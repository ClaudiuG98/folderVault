using System.Diagnostics;
using System.Security.Cryptography;
using FolderVault.Core.Model;
using FolderVault.Core.Ops;
using FolderVault.Core.Store;
using FolderVault.Services;
using FolderVault.UI;

namespace FolderVault.App;

/// <summary>
/// The running application: owns the tray icon, the open vault sessions and their data keys, and
/// the auto-lock watchers.
///
/// Every launch after the first forwards its command line here over the named pipe rather than
/// starting a second copy, so all unlock requests are served by the one process that holds the
/// session state.
/// </summary>
public sealed class FolderVaultContext : ApplicationContext
{
    private readonly VaultRegistry _registry = new();
    private readonly VaultService _service;
    private readonly Dictionary<Guid, UnlockedSession> _sessions = [];

    /// <summary>Vaults being encrypted on a worker thread right now. UI thread only.</summary>
    private readonly HashSet<Guid> _lockingInBackground = [];
    private readonly AutoLockService _autoLock;
    private readonly NotifyIcon _tray;
    private readonly Control _uiMarshal;
    private ManagerForm? _manager;

    public FolderVaultContext(string[] args)
    {
        _service = new VaultService(_registry);

        // Everything that arrives from another thread - a forwarded command line on the named
        // pipe, an auto-lock tick from a timer - has to be bounced onto the UI thread before it
        // touches a window. This hidden control is the anchor for that.
        //
        // It exists because the obvious candidate does not work: asking the tray menu whether it
        // needs an Invoke returns false, because InvokeRequired is false for any control without
        // a handle, and a ContextMenuStrip has none until it is first shown. Forwarded unlock
        // requests were therefore building the password prompt on the pipe thread, where it had
        // no message pump and never became visible. Touching Handle forces the handle into
        // existence here, on the UI thread, so InvokeRequired answers truthfully from then on.
        _uiMarshal = new Control();
        _ = _uiMarshal.Handle;

        _tray = new NotifyIcon
        {
            Icon = AppIcon.Get(),
            Text = "FolderVault",
            Visible = true,
            ContextMenuStrip = BuildTrayMenu(),
        };
        _tray.DoubleClick += (_, _) => ShowManager();

        _autoLock = new AutoLockService(() => _sessions.Values.ToList());
        _autoLock.LockRequested += OnAutoLockRequested;

        SystemEvents.SessionLocked += () => _autoLock.LockAll(AutoLockReason.SessionLocked);
        SystemEvents.SessionEnding += () => _autoLock.LockAll(AutoLockReason.WindowsClosing);

        RunStartupRecovery();
        RepairMovedShortcuts();
        HandleArgs(args);
    }

    public VaultService Service => _service;

    public VaultRegistry Registry => _registry;

    public bool IsUnlocked(Guid vaultId) => _sessions.ContainsKey(vaultId);

    /// <summary>
    /// True while a folder is being re-encrypted quietly in the background. The manager uses this
    /// to say "Locking" rather than "Interrupted" - the vault really is mid-operation, but this
    /// one is deliberate and under way, not the wreckage of a crash.
    /// </summary>
    public bool IsLockingInBackground(Guid vaultId) => _lockingInBackground.Contains(vaultId);

    /// <summary>Acts on a command line, whether this process's own or one forwarded to it.</summary>
    public void HandleArgs(string[] args)
    {
        var unlockIndex = Array.FindIndex(args, a => a.Equals("--unlock", StringComparison.OrdinalIgnoreCase));

        if (unlockIndex >= 0 && unlockIndex + 1 < args.Length && Guid.TryParse(args[unlockIndex + 1], out var id))
        {
            UnlockById(id);
            return;
        }

        if (args.Any(a => a.Equals("--lock-all", StringComparison.OrdinalIgnoreCase)))
        {
            LockAll();
            return;
        }

        ShowManager();
    }

    /// <summary>Runs <paramref name="action"/> on the UI thread, hopping only if needed.</summary>
    private void OnUiThread(Action action)
    {
        if (_uiMarshal.IsDisposed) return;

        if (_uiMarshal.InvokeRequired) _uiMarshal.BeginInvoke(action);
        else action();
    }

    /// <summary>Marshals a forwarded command line onto the UI thread.</summary>
    public void HandleForwardedArgs(string[] args) => OnUiThread(() => HandleArgs(args));

    // ---- Unlock ----

    private void UnlockById(Guid vaultId)
    {
        var vault = FindVault(vaultId);
        if (vault is null)
        {
            Notify("Folder not found",
                "FolderVault has no record of that folder. Open the manager to re-import it from its drive.",
                ToolTipIcon.Warning);
            return;
        }

        if (_sessions.ContainsKey(vault.Id))
        {
            OpenInExplorer(vault.OriginalPath);
            return;
        }

        if (vault.NeedsRecovery && !TryRecover(vault)) return;

        if (vault.State == VaultState.Unlocked)
        {
            OpenInExplorer(vault.OriginalPath);
            return;
        }

        // Parent to the manager when it is showing, so the prompt sits above it rather than
        // fighting it for z-order.
        Unlock(vault, _manager is { IsDisposed: false } ? _manager : null);
    }

    /// <summary>
    /// Prompts for the password at the cursor, then unlocks and opens the folder. Returns whether
    /// the folder actually ended up unlocked, so callers never have to infer that from vault
    /// state that another refresh may have replaced underneath them.
    /// </summary>
    public bool Unlock(Vault vault, IWin32Window? owner = null)
    {
        byte[]? dek = null;

        using (var prompt = new PasswordPrompt(vault))
        {
            // Key derivation is the slow part and runs on a worker thread inside the prompt, so
            // the box stays responsive while it works.
            prompt.ValidateSecret = (secret, usingRecoveryKey) =>
            {
                try
                {
                    dek = usingRecoveryKey
                        ? VaultService.DeriveDekFromRecoveryKey(vault, secret)
                        : VaultService.DeriveDek(vault, secret);
                    return null;
                }
                catch (CryptographicException)
                {
                    return usingRecoveryKey ? "That recovery key is not right." : "Wrong password.";
                }
                catch (VaultOperationException ex)
                {
                    return ex.Message;
                }
            };

            prompt.PositionAtCursor();
            if (prompt.ShowDialog(owner) != DialogResult.OK || dek is null) return false;
        }

        try
        {
            if (vault.Mode == VaultMode.Secure)
            {
                ProgressDialog.Run(owner, $"Opening {vault.DisplayName}",
                    progress => _service.Unlock(vault, dek, progress));
            }
            else
            {
                _service.Unlock(vault, dek);
            }

            var session = new UnlockedSession(vault, dek);
            _sessions[vault.Id] = session;
            _autoLock.TrackActivity(session);

            OpenInExplorer(vault.OriginalPath);
            RefreshUi();
            return true;
        }
        catch (Exception ex) when (ex is VaultOperationException or PayloadInUseException or IOException)
        {
            KeyDerivation_Wipe(dek);
            ShowProblem("Could not open the folder", ex.Message);
            return false;
        }
    }

    // ---- Lock ----

    public void Lock(Vault vault, IWin32Window? owner = null, AutoLockReason? reason = null)
    {
        _sessions.TryGetValue(vault.Id, out var session);

        // The vault may have been unprotected since this call was scheduled.
        if (FindKnownVault(vault.Id) is null)
        {
            ReleaseSession(vault.Id);
            return;
        }

        if (vault.Mode == VaultMode.Secure && session is null)
        {
            ShowProblem("Cannot lock this folder",
                $"{vault.DisplayName} is encrypted, and its key is not held in memory - that happens " +
                "when FolderVault was restarted while the folder was open. Open it once more, then " +
                "lock it from the manager.");
            return;
        }

        if (ShouldLockQuietly(vault.Mode, reason))
        {
            LockQuietly(vault, session!, reason!.Value);
            return;
        }

        try
        {
            if (vault.Mode == VaultMode.Secure)
            {
                ProgressDialog.Run(owner, $"Locking {vault.DisplayName}",
                    progress => _service.Lock(vault, session!.Dek, progress));
            }
            else
            {
                _service.Lock(vault, session?.Dek);
            }

            ReleaseSession(vault.Id);
            RefreshUi();

            if (reason is not null) NotifyLocked(vault, reason.Value);
        }
        catch (PayloadInUseException ex)
        {
            // Very common and entirely recoverable: something inside is still open. Say so plainly
            // and leave the vault unlocked rather than half-moved.
            Notify($"{vault.DisplayName} is still in use", ex.Message, ToolTipIcon.Warning);
        }
        catch (Exception ex) when (ex is VaultOperationException or IOException)
        {
            ShowProblem("Could not lock the folder", ex.Message);
        }
    }

    /// <summary>
    /// Whether a lock should run quietly in the background instead of behind a progress dialog.
    ///
    /// <para>Three conditions, and each carries its weight. <b>Secure only</b>: Fast mode is a
    /// directory rename measured in milliseconds, so there is nothing to show progress for and
    /// nothing to move off the UI thread. <b>A reason is set</b>: that is what distinguishes an
    /// auto-lock from the user pressing Lock, and someone who pressed Lock is waiting for it and
    /// should see it happening. <b>Not shutting down</b>: at sign-out, getting the encryption
    /// finished beats staying out of the way, and no one is looking at the screen to be
    /// interrupted.</para>
    /// </summary>
    internal static bool ShouldLockQuietly(VaultMode mode, AutoLockReason? reason) =>
        mode == VaultMode.Secure && reason is not null and not AutoLockReason.WindowsClosing;

    /// <summary>
    /// Re-encrypts a folder in the background, with no window and no progress bar - just the tray
    /// icon and, when it finishes, the same notification every auto-lock gives.
    ///
    /// <para>An idle timeout or a closed Explorer window fires while the user is busy with
    /// something else entirely. A modal progress dialog there lands on top of whatever they were
    /// doing, cannot be dismissed, and steals the keyboard for as long as encryption takes. They
    /// did not ask for it, so it should not interrupt them.</para>
    ///
    /// <para>It cannot simply run on the UI thread without the dialog either: encryption is the
    /// one operation here measured in minutes, and that would freeze the tray into "Not
    /// Responding". So it goes to a worker thread, and everything that touches state comes back to
    /// the UI thread afterwards.</para>
    /// </summary>
    private void LockQuietly(Vault vault, UnlockedSession session, AutoLockReason reason)
    {
        // The auto-lock poller keeps ticking while this runs and would happily ask again a few
        // seconds later, starting a second encryption over the first.
        if (!_lockingInBackground.Add(vault.Id)) return;

        _autoLock.StopTrackingActivity(vault.Id);
        RefreshUi();

        _ = Task.Run(() =>
        {
            try
            {
                _service.Lock(vault, session.Dek);
                return null as Exception;
            }
            catch (Exception ex)
            {
                // Every exception is caught, not just the expected three. This is a worker thread
                // with no progress dialog to rethrow through and no UI handler above it, so
                // anything that escaped here would leave the folder open and say nothing at all.
                return ex;
            }
        }).ContinueWith(finished => OnUiThread(() =>
        {
            _lockingInBackground.Remove(vault.Id);

            if (finished.Result is { } failure)
            {
                // The folder is still open and still tracked, so the next poll will try again -
                // which is what should happen when the reason was a file being in use.
                _autoLock.TrackActivity(session);
                RefreshUi();
                Notify($"Could not lock {vault.DisplayName}", failure.Message, ToolTipIcon.Warning);
                return;
            }

            ReleaseSession(vault.Id);
            RefreshUi();
            NotifyLocked(vault, reason);
        }), TaskScheduler.Default);
    }

    public void LockAll()
    {
        foreach (var vault in _sessions.Values.Select(s => s.Vault).ToList())
            Lock(vault);
    }

    /// <summary>
    /// Saves a vault's auto-lock policy and applies it to the folder if it is open right now.
    ///
    /// Both halves are needed. An open vault's <see cref="Vault"/> lives inside its
    /// <see cref="UnlockedSession"/>, and that is the instance the idle timer and the
    /// Explorer-close watcher actually read; the manager window holds a separate copy reloaded
    /// from the registry. Writing only the registry would leave a folder the user just set to
    /// "stay open" locking itself a minute later on the old timeout.
    /// </summary>
    public void UpdateAutoLockPolicy(Guid vaultId, int idleMinutes, bool onExplorerClose, bool onSessionLock)
    {
        var stored = FindKnownVault(vaultId);
        if (stored is null) return;

        Apply(stored);
        _registry.Upsert(stored);

        if (_sessions.TryGetValue(vaultId, out var session)) Apply(session.Vault);

        RefreshUi();
        return;

        void Apply(Vault vault)
        {
            vault.IdleLockMinutes = idleMinutes;
            vault.LockOnExplorerClose = onExplorerClose;
            vault.LockOnSessionLock = onSessionLock;
        }
    }

    private void OnAutoLockRequested(UnlockedSession session, AutoLockReason reason) => OnUiThread(() =>
    {
        if (!_sessions.ContainsKey(session.Vault.Id)) return;
        Lock(session.Vault, reason: reason);
    });

    private void NotifyLocked(Vault vault, AutoLockReason reason) => Notify(
        $"{vault.DisplayName} locked",
        reason switch
        {
            AutoLockReason.IdleTimeout => $"No activity for {vault.IdleLockMinutes} minutes.",
            AutoLockReason.ExplorerClosed => "The Explorer window was closed.",
            AutoLockReason.WindowsClosing => "Windows is signing out.",
            _ => "You locked Windows.",
        },
        ToolTipIcon.Info);

    private void ReleaseSession(Guid vaultId)
    {
        _autoLock.StopTrackingActivity(vaultId);

        if (_sessions.Remove(vaultId, out var session))
            session.Dispose(); // zeroes the key
    }

    /// <summary>
    /// Stops protecting a folder, releasing its in-memory session first.
    ///
    /// Releasing the session is the important half. An unlocked vault leaves a data key and an
    /// auto-lock timer behind, and those outlive the registry entry: with the session still
    /// present, the idle timer or the Explorer-closed watcher would fire minutes later and
    /// dutifully re-lock a folder the user had just unprotected, recreating the store and the
    /// decoy shortcut out of nowhere.
    /// </summary>
    public void RemoveProtection(Vault vault, IWin32Window? owner = null)
    {
        ReleaseSession(vault.Id);

        _service.RemoveProtection(vault);
        RefreshUi();
    }

    // ---- Recovery ----

    private void RunStartupRecovery()
    {
        foreach (var (vault, result) in _service.RecoverAll())
        {
            Notify(result.NeedsUserDecision ? $"{vault.DisplayName} needs attention" : $"{vault.DisplayName} recovered",
                result.Summary,
                result.NeedsUserDecision ? ToolTipIcon.Warning : ToolTipIcon.Info);
        }
    }

    /// <summary>
    /// Re-points locked folders' decoys at this executable, so renaming or moving the FolderVault
    /// folder does not leave them pointing at an exe that no longer exists.
    /// </summary>
    private void RepairMovedShortcuts()
    {
        try
        {
            var repaired = _service.RepairAllDecoys();
            if (repaired.Count == 0) return;

            Notify("FolderVault has moved",
                repaired.Count == 1
                    ? $"Updated the shortcut for {repaired[0].DisplayName} to point at the new location."
                    : $"Updated {repaired.Count} locked folders to point at the new location.",
                ToolTipIcon.Info);
        }
        catch (Exception ex) when (ex is VaultRegistryUnreadableException or IOException)
        {
            // Nothing to repair if the index cannot be read; recovery already reported that.
        }
    }

    private bool TryRecover(Vault vault)
    {
        try
        {
            var result = _service.Recover(vault);
            if (!result.NeedsUserDecision) return true;

            ShowProblem($"{vault.DisplayName} needs attention", result.Summary);
            return false;
        }
        catch (Exception ex) when (ex is VaultOperationException or IOException)
        {
            ShowProblem($"{vault.DisplayName} could not be recovered", ex.Message);
            return false;
        }
    }

    // ---- Vault lookup ----

    /// <summary>
    /// Finds a vault by id, falling back to scanning the drives. The fallback matters after a
    /// Windows profile reset, when the DPAPI-wrapped index is unreadable but the self-describing
    /// copy beside each payload still is.
    /// </summary>
    /// <summary>The vault as the registry currently knows it, or null if it was removed.</summary>
    private Vault? FindKnownVault(Guid vaultId)
    {
        try
        {
            return _registry.Load().FirstOrDefault(v => v.Id == vaultId);
        }
        catch (VaultRegistryUnreadableException)
        {
            return null;
        }
    }

    public Vault? FindVault(Guid vaultId)
    {
        try
        {
            if (_registry.Load().FirstOrDefault(v => v.Id == vaultId) is { } known) return known;
        }
        catch (VaultRegistryUnreadableException)
        {
            // Fall through to the drive scan.
        }

        foreach (var drive in DriveInfo.GetDrives().Where(d => d is { IsReady: true, DriveType: DriveType.Fixed }))
        {
            try
            {
                if (VaultRegistry.DiscoverOnVolume(drive.RootDirectory.FullName)
                        .FirstOrDefault(v => v.Id == vaultId) is { } found)
                {
                    _registry.Upsert(found);
                    return found;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Skip drives we cannot read.
            }
        }

        return null;
    }

    // ---- UI plumbing ----

    private ContextMenuStrip BuildTrayMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open FolderVault", null, (_, _) => ShowManager());
        menu.Items.Add("Lock everything now", null, (_, _) => LockAll());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());
        return menu;
    }

    public void ShowManager()
    {
        if (_manager is { IsDisposed: false })
        {
            _manager.Activate();
            _manager.Refresh();
            return;
        }

        _manager = new ManagerForm(this);
        _manager.FormClosed += (_, _) => _manager = null;
        _manager.Show();
    }

    private void RefreshUi()
    {
        _tray.Icon = AppIcon.Get(open: _sessions.Count > 0);
        _tray.Text = _sessions.Count switch
        {
            0 => "FolderVault - everything locked",
            1 => "FolderVault - 1 folder open",
            var n => $"FolderVault - {n} folders open",
        };

        if (_manager is { IsDisposed: false }) _manager.ReloadVaults();
    }

    private void Notify(string title, string message, ToolTipIcon icon)
    {
        _tray.BalloonTipTitle = title;
        _tray.BalloonTipText = message;
        _tray.BalloonTipIcon = icon;
        _tray.ShowBalloonTip(6000);
    }

    private static void ShowProblem(string title, string message) =>
        MessageBox.Show(message, title, MessageBoxButtons.OK, MessageBoxIcon.Warning);

    private static void OpenInExplorer(string path)
    {
        if (!Directory.Exists(path)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    private static void KeyDerivation_Wipe(byte[] key) => Core.Crypto.KeyDerivation.Wipe(key);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _autoLock.Dispose();
            foreach (var session in _sessions.Values) session.Dispose();
            _sessions.Clear();

            _tray.Visible = false;
            _tray.Dispose();
            _uiMarshal.Dispose();
        }

        base.Dispose(disposing);
    }
}
