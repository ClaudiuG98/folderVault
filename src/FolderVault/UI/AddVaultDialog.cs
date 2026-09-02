using FolderVault.Core.Model;
using FolderVault.Core.Store;

namespace FolderVault.UI;

/// <summary>
/// Collects everything needed to protect a folder for the first time: which folder, which mode,
/// and a password. The mode descriptions are blunt on purpose - Fast mode is obfuscation, and a
/// user choosing it should know that before they rely on it.
///
/// Laid out with a single-column <see cref="TableLayoutPanel"/> of auto-sizing rows so that
/// wrapped explanatory text can never be clipped, whatever the font or display scaling.
/// </summary>
public sealed class AddVaultDialog : Form
{
    private const int ContentWidth = 460;

    private readonly TextBox _folder;
    private readonly RadioButton _fast;
    private readonly RadioButton _secure;
    private readonly TextBox _password;
    private readonly TextBox _confirm;
    private readonly CheckBox _recoveryKey;
    private readonly Label _warning;
    private readonly Label _error;

    public string FolderPath => _folder.Text.Trim();
    public VaultMode Mode => _secure.Checked ? VaultMode.Secure : VaultMode.Fast;
    public string Password => _password.Text;
    public bool IssueRecoveryKey => _recoveryKey.Checked;

    public AddVaultDialog(string? initialFolder = null)
    {
        Text = "Protect a folder";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = Theme.Surface;
        Font = Theme.Body();
        AutoScaleMode = AutoScaleMode.Font;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        // ---- folder row ----
        _folder = new TextBox
        {
            Text = initialFolder ?? string.Empty,
            Width = ContentWidth - 100,
            BorderStyle = BorderStyle.FixedSingle,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
        };

        var browse = new Button
        {
            Text = "Browse",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(88, 26),
            FlatStyle = FlatStyle.System,
            Margin = new Padding(8, 0, 0, 0),
        };
        browse.Click += (_, _) => Browse();

        var folderRow = new TableLayoutPanel
        {
            ColumnCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 0, 0, 4),
            Width = ContentWidth,
        };
        folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        folderRow.Controls.Add(_folder, 0, 0);
        folderRow.Controls.Add(browse, 1, 0);

        // ---- protection ----
        _fast = Mode0("Fast - hide and restrict", checkedByDefault: true);
        var fastNote = Theme.Wrapping(
            "Locks instantly whatever the folder size, because nothing is copied. Stops someone " +
            "casually browsing your PC, but an administrator can still get the files back. " +
            "This is not encryption.",
            ContentWidth - 20);
        fastNote.Margin = new Padding(20, 0, 0, 8);

        _secure = Mode0("Secure - encrypt with AES-256", checkedByDefault: false);
        var secureNote = Theme.Wrapping(
            "Every file is encrypted with your password, so the contents are genuinely unreadable " +
            "without it. Locking and unlocking take time proportional to the size of the folder.",
            ContentWidth - 20);
        secureNote.Margin = new Padding(20, 0, 0, 8);

        // ---- password ----
        _password = Theme.Secret("Password", ContentWidth);
        _confirm = Theme.Secret("Confirm password", ContentWidth);

        _recoveryKey = Theme.WrappingCheckBox(
            "Give me a recovery key, which opens the folder if I forget the password",
            ContentWidth, isChecked: true);

        _warning = Theme.Wrapping(string.Empty, ContentWidth, color: Theme.Danger);
        _error = Theme.Wrapping(string.Empty, ContentWidth, color: Theme.Danger);
        _error.Visible = false;

        // ---- buttons ----
        var ok = Theme.Action("Protect and lock", primary: true);
        ok.Click += (_, _) => Confirm();

        var cancel = Theme.Action("Cancel");
        cancel.DialogResult = DialogResult.Cancel;
        cancel.Margin = new Padding(0);

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Width = ContentWidth,
            Margin = new Padding(0, 10, 0, 0),
        };
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);

        // ---- assemble ----
        var root = new TableLayoutPanel
        {
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Padding = new Padding(20, 16, 20, 16),
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        foreach (Control control in new Control[]
                 {
                     Theme.Heading("Folder"), folderRow,
                     Theme.Heading("Protection"), _fast, fastNote, _secure, secureNote,
                     Theme.Heading("Password"), _password, _confirm,
                     _recoveryKey, _warning, _error, buttons,
                 })
        {
            root.Controls.Add(control);
        }

        Controls.Add(root);
        AcceptButton = ok;
        CancelButton = cancel;

        _secure.CheckedChanged += (_, _) => UpdateWarning();
        _fast.CheckedChanged += (_, _) => UpdateWarning();
        UpdateWarning();
    }

    private static RadioButton Mode0(string text, bool checkedByDefault) => new()
    {
        Text = text,
        Checked = checkedByDefault,
        AutoSize = true,
        Font = Theme.Body(9f, FontStyle.Bold),
        Margin = new Padding(0, 4, 0, 2),
    };

    private void Browse()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose a folder to protect",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
        };

        if (Directory.Exists(FolderPath)) dialog.InitialDirectory = FolderPath;
        if (dialog.ShowDialog(this) == DialogResult.OK) _folder.Text = dialog.SelectedPath;
    }

    private void UpdateWarning()
    {
        _warning.Text = _secure.Checked
            ? "If you lose this password and have no recovery key, the files are gone for good. " +
              "Nobody can reset it - that is exactly what makes the encryption worth having."
            : string.Empty;
        _warning.Visible = _secure.Checked;
    }

    private void Confirm()
    {
        if (VolumeStore.ValidateProtectable(FolderPath) is { } problem)
        {
            ShowError(problem);
            return;
        }

        if (_password.Text.Length < 8)
        {
            ShowError("Use a password of at least 8 characters.");
            return;
        }

        if (_password.Text != _confirm.Text)
        {
            ShowError("The two passwords do not match.");
            return;
        }

        DialogResult = DialogResult.OK;
    }

    private void ShowError(string message)
    {
        _error.Text = message;
        _error.Visible = true;
    }
}
