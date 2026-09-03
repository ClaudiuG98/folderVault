using FolderVault.App;
using FolderVault.Core.Model;
using FolderVault.Core.Ops;
using FolderVault.Core.Shell;
using FolderVault.Core.Store;

namespace FolderVault.UI;

/// <summary>
/// The main window: every protected folder, its state, and the actions that apply to it.
/// Day to day the app is driven by double-clicking folders, so this is deliberately plain.
///
/// The action buttons live in a <see cref="FlowLayoutPanel"/> and size themselves to their own
/// captions. Hand-placing them meant a caption longer than its allotted width was silently
/// clipped by the neighbour drawn on top of it.
/// </summary>
public sealed class ManagerForm : Form
{
    private readonly FolderVaultContext _context;
    private readonly ListView _list;
    private readonly Button _open;
    private readonly Button _lock;
    private readonly Button _changePassword;
    private readonly Button _remove;
    private readonly Label _status;
    private string? _registryProblem;

    public ManagerForm(FolderVaultContext context)
    {
        _context = context;

        Text = "FolderVault";
        Icon = AppIcon.Get();
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Surface;
        Font = Theme.Body();
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(900, 470);
        MinimumSize = new Size(880, 420);

        _list = new ListView
        {
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            HideSelection = false,
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
        };
        _list.Columns.Add("Folder", 180);
        _list.Columns.Add("Location", 250);
        _list.Columns.Add("Protection", 100);
        _list.Columns.Add("State", 90);
        _list.Columns.Add("Auto-lock", 140);
        _list.SelectedIndexChanged += (_, _) => UpdateButtons();
        _list.DoubleClick += (_, _) => OpenSelected();

        _status = new Label
        {
            AutoSize = false,
            Height = 26,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Theme.Muted,
            Font = Theme.Body(8.25f),
            AutoEllipsis = true,
        };

        var add = Theme.Action("Protect a folder", primary: true);
        add.Click += (_, _) => AddVault();

        _open = Theme.Action("Open");
        _open.Click += (_, _) => OpenSelected();

        _lock = Theme.Action("Lock");
        _lock.Click += (_, _) => LockSelected();

        _changePassword = Theme.Action("Change password");
        _changePassword.Click += (_, _) => ChangePasswordForSelected();

        _remove = Theme.Action("Remove protection");
        _remove.Click += (_, _) => RemoveSelected();

        var settings = Theme.Action("Settings");
        settings.Click += (_, _) => ShowSettings();

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Bottom,
            // Never wrap: a wrapped second row would be pushed off the window. The minimum
            // window width is set so the whole bar fits on one line.
            WrapContents = false,
            Margin = new Padding(0),
        };
        buttons.Controls.AddRange([add, _open, _lock, _changePassword, _remove, settings]);

        _status.Dock = DockStyle.Bottom;

        // Docking rather than table rows: a docked bottom strip is always given its full height
        // and the list simply takes what is left, with no row arithmetic to get wrong. Order
        // matters - WinForms positions the last-added control first, so the Fill control is
        // added first and the bottom strips after it.
        var root = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16) };
        root.Controls.Add(_list);
        root.Controls.Add(_status);
        root.Controls.Add(buttons);

        Controls.Add(root);

        // A right-click menu on the row itself, so the actions are discoverable without first
        // spotting that the buttons apply to the selection.
        _list.ContextMenuStrip = BuildRowMenu();

        ReloadVaults();
    }

    private ContextMenuStrip BuildRowMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => OpenSelected());
        menu.Items.Add("Lock", null, (_, _) => LockSelected());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Change password", null, (_, _) => ChangePasswordForSelected());
        menu.Items.Add("Auto-lock...", null, (_, _) => ShowSettings());
        menu.Items.Add("Remove protection", null, (_, _) => RemoveSelected());
        menu.Opening += (_, e) => e.Cancel = SelectedVault() is null;
        return menu;
    }

    /// <summary>Reloads the list from the registry, preserving the current selection.</summary>
    public void ReloadVaults()
    {
        var selectedId = SelectedVault()?.Id;

        _list.BeginUpdate();
        _list.Items.Clear();

        List<Vault> vaults;
        try
        {
            vaults = _context.Registry.Load();
        }
        catch (VaultRegistryUnreadableException ex)
        {
            _list.EndUpdate();
            _registryProblem = ex.Message;
            UpdateButtons();
            return;
        }

        _registryProblem = null;

        foreach (var vault in vaults.OrderBy(v => v.DisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            var unlocked = _context.IsUnlocked(vault.Id);
            var busy = _context.IsLockingInBackground(vault.Id);
            var item = new ListViewItem(vault.DisplayName) { Tag = vault };

            item.SubItems.Add(Path.GetDirectoryName(vault.OriginalPath) ?? vault.OriginalPath);
            item.SubItems.Add(vault.Mode == VaultMode.Secure ? "Encrypted" : "Hidden");
            item.SubItems.Add(DescribeState(vault, unlocked, busy));
            item.SubItems.Add(AutoLockText.Summary(vault));

            // Red is for a vault that needs a human. One that is mid-encryption on purpose does not.
            item.ForeColor = vault.NeedsRecovery && !busy ? Theme.Danger : Theme.Text;

            _list.Items.Add(item);
            if (vault.Id == selectedId) item.Selected = true;
        }

        // Preselect so the buttons are live straight away; with nothing selected they are all
        // disabled, which reads as the app being broken rather than as "pick a row first".
        if (_list.SelectedItems.Count == 0 && _list.Items.Count > 0)
            _list.Items[0].Selected = true;

        _list.EndUpdate();
        UpdateButtons();
    }

    private static string DescribeState(Vault vault, bool unlocked, bool busy) => vault.State switch
    {
        // A background auto-lock leaves the vault in the same transitional state a crash would,
        // so the in-flight flag is the only thing that tells the two apart.
        _ when busy => "Locking",
        VaultState.Locked => "Locked",
        VaultState.Unlocked when unlocked => "Open",
        VaultState.Unlocked => "Unlocked",
        _ => "Interrupted",
    };

    private Vault? SelectedVault() =>
        _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as Vault : null;

    private void UpdateButtons()
    {
        var vault = SelectedVault();
        var unlocked = vault is not null && vault.State == VaultState.Unlocked;
        var busy = vault is not null && _context.IsLockingInBackground(vault.Id);

        // Nothing may act on a folder that is halfway through being encrypted - Open in
        // particular would try to decrypt a payload that is still being written.
        _open.Enabled = vault is not null && !unlocked && !busy;
        _lock.Enabled = unlocked && !busy;
        _changePassword.Enabled = vault is not null && !busy;
        _remove.Enabled = vault is not null && !busy;

        _status.Text = _registryProblem is not null
            ? _registryProblem
            : _list.Items.Count == 0
                ? "No folders are protected yet. Choose \"Protect a folder\" to add one."
            : vault is null
            ? "Select a folder to act on it."
            : unlocked
                ? $"{vault.DisplayName} is unlocked. \"Remove protection\" turns it back into an ordinary folder."
                : $"{vault.DisplayName} is locked. Opening it needs the password.";
    }

    // ---- actions ----

    private void AddVault()
    {
        using var dialog = new AddVaultDialog();
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            Vault? vault = null;
            string? recoveryKey = null;

            if (dialog.Mode == VaultMode.Secure)
            {
                // Encrypting can take a while, so it runs behind the progress window. Run
                // rethrows on this thread if it fails, so reaching the next line means success.
                ProgressDialog.Run(this, $"Encrypting {Path.GetFileName(dialog.FolderPath)}",
                    progress => (vault, recoveryKey) = _context.Service.Create(
                        dialog.FolderPath, dialog.Mode, dialog.Password, dialog.IssueRecoveryKey, progress));
            }
            else
            {
                (vault, recoveryKey) = _context.Service.Create(
                    dialog.FolderPath, dialog.Mode, dialog.Password, dialog.IssueRecoveryKey);
            }

            if (vault is not null && recoveryKey is not null)
            {
                using var keyDialog = new RecoveryKeyDialog(vault.DisplayName, recoveryKey);
                keyDialog.ShowDialog(this);
            }

            ReloadVaults();
        }
        catch (Exception ex) when (ex is VaultOperationException or PayloadInUseException or IOException)
        {
            Warn(ex.Message, "Could not protect that folder");
        }
    }

    private void OpenSelected()
    {
        if (SelectedVault() is not { } vault) return;

        if (vault.State == VaultState.Unlocked)
        {
            OpenInExplorer(vault.OriginalPath);
            return;
        }

        _context.Unlock(vault, this);
        ReloadVaults();
    }

    private void LockSelected()
    {
        if (SelectedVault() is not { } vault) return;

        _context.Lock(vault, this);
        ReloadVaults();
    }

    private void ChangePasswordForSelected()
    {
        if (SelectedVault() is not { } vault) return;

        using var dialog = new ChangePasswordDialog(vault.DisplayName);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            _context.Service.ChangePassword(vault, dialog.CurrentPassword, dialog.NewPassword);
            MessageBox.Show(this,
                "Password changed. The files themselves were not re-encrypted - only the key that " +
                "protects them was re-wrapped, which is why this was instant.",
                "Password changed", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            Warn("The current password is not right.", "Password not changed");
        }
    }

    /// <summary>
    /// Turns the folder back into an ordinary unprotected folder. A locked vault has to be
    /// unlocked first, because removing protection while the payload is still in the store would
    /// delete the only copy of the data.
    /// </summary>
    private void RemoveSelected()
    {
        if (SelectedVault() is not { } vault) return;

        var confirm = MessageBox.Show(this,
            $"Stop protecting {vault.DisplayName}?\r\n\r\n" +
            "The password is removed and the folder goes back to being an ordinary folder in " +
            "Explorer. Nothing is deleted." +
            (vault.State == VaultState.Unlocked ? "" : "\r\n\r\nYou will be asked for the password first."),
            "Remove protection", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);

        if (confirm != DialogResult.OK) return;

        try
        {
            // Ask Unlock directly whether it worked. Re-reading vault.State here was unreliable:
            // a successful unlock refreshes the list, which replaces the Vault instances behind
            // this local, and a stale one reads as still locked.
            if (vault.State != VaultState.Unlocked && !_context.Unlock(vault, this))
            {
                // Cancelled at the prompt, or the unlock itself failed and already reported why.
                return;
            }

            _context.RemoveProtection(vault, this);
            ReloadVaults();

            MessageBox.Show(this,
                $"{vault.DisplayName} is no longer protected. It is an ordinary folder again at:\r\n\r\n" +
                vault.OriginalPath,
                "Protection removed", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) when (ex is VaultOperationException or PayloadInUseException or IOException
                                       or UnauthorizedAccessException)
        {
            Warn(ex.Message, "Could not remove protection");
        }
    }

    private void ShowSettings()
    {
        var vault = SelectedVault();

        using var dialog = new SettingsDialog(vault, vault is not null && _context.IsUnlocked(vault.Id));
        if (dialog.ShowDialog(this) != DialogResult.OK || vault is null) return;

        _context.UpdateAutoLockPolicy(vault.Id, dialog.IdleLockMinutes,
            dialog.LockOnExplorerClose, dialog.LockOnSessionLock);

        ReloadVaults();
    }

    private void Warn(string message, string title) =>
        MessageBox.Show(this, message, title, MessageBoxButtons.OK, MessageBoxIcon.Warning);

    private static void OpenInExplorer(string path)
    {
        if (!Directory.Exists(path)) return;
        System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }
}
