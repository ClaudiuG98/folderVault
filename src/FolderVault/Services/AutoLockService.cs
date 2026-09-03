using FolderVault.App;
using FolderVault.Core.Model;
using FolderVault.Core.Shell;

namespace FolderVault.Services;

/// <summary>Why a vault is being re-locked, for the notification shown to the user.</summary>
public enum AutoLockReason
{
    IdleTimeout,
    ExplorerClosed,

    /// <summary>The screen was locked or the user switched away. A vault can opt out of this.</summary>
    SessionLocked,

    /// <summary>Windows is signing out or shutting down. No vault may opt out of this.</summary>
    WindowsClosing,
}

/// <summary>
/// Watches open vaults and asks for them to be re-locked when they go idle or when the Explorer
/// window they were opened with is closed.
///
/// It raises <see cref="LockRequested"/> rather than locking directly, so all the actual work
/// stays on the UI thread in one place and this class can be reasoned about on its own.
/// </summary>
public sealed class AutoLockService : IDisposable
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    /// <summary>
    /// How long after unlocking before the Explorer-closed rule applies. Without this the vault
    /// would re-lock in the instant between unlocking and the window actually appearing.
    /// </summary>
    public static readonly TimeSpan ExplorerGracePeriod = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Consecutive polls that must agree no window is open before locking. The shell briefly
    /// stops answering while Explorer restarts, and a single empty reading there would otherwise
    /// re-lock every open vault at once.
    /// </summary>
    private const int RequiredConsecutiveMisses = 3;

    private readonly Func<IReadOnlyCollection<UnlockedSession>> _sessions;
    private readonly System.Threading.Timer _timer;
    private readonly Dictionary<Guid, int> _missedWindowChecks = [];
    private readonly Dictionary<Guid, FileSystemWatcher> _activityWatchers = [];
    private readonly object _gate = new();

    public AutoLockService(Func<IReadOnlyCollection<UnlockedSession>> sessions)
    {
        _sessions = sessions;
        _timer = new System.Threading.Timer(_ => Poll(), null, PollInterval, PollInterval);
    }

    /// <summary>Raised when a vault should be locked. Handled on the UI thread by the caller.</summary>
    public event Action<UnlockedSession, AutoLockReason>? LockRequested;

    /// <summary>
    /// Starts treating writes under the vault folder as activity, so editing a file inside an
    /// open vault keeps postponing the idle timeout.
    /// </summary>
    public void TrackActivity(UnlockedSession session)
    {
        lock (_gate)
        {
            StopTrackingActivity(session.Vault.Id);

            try
            {
                var watcher = new FileSystemWatcher(session.Vault.OriginalPath)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName
                                   | NotifyFilters.DirectoryName | NotifyFilters.Size,
                };

                watcher.Changed += (_, _) => session.Touch();
                watcher.Created += (_, _) => session.Touch();
                watcher.Deleted += (_, _) => session.Touch();
                watcher.Renamed += (_, _) => session.Touch();
                watcher.EnableRaisingEvents = true;

                _activityWatchers[session.Vault.Id] = watcher;
            }
            catch (Exception ex) when (ex is ArgumentException or FileNotFoundException or IOException)
            {
                // Without a watcher the idle timer simply runs from the unlock time, which is a
                // safe fallback: it locks sooner, never later.
            }
        }
    }

    public void StopTrackingActivity(Guid vaultId)
    {
        lock (_gate)
        {
            if (!_activityWatchers.Remove(vaultId, out var watcher)) return;

            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
            _missedWindowChecks.Remove(vaultId);
        }
    }

    /// <summary>
    /// Re-locks everything now, used when the Windows session locks or ends.
    ///
    /// <see cref="AutoLockReason.SessionLocked"/> honours each vault's
    /// <see cref="Vault.LockOnSessionLock"/>; <see cref="AutoLockReason.WindowsClosing"/> does
    /// not, because there is no "later" to defer to once Windows is gone.
    /// </summary>
    public void LockAll(AutoLockReason reason)
    {
        foreach (var session in _sessions())
        {
            if (reason == AutoLockReason.SessionLocked && !session.Vault.LockOnSessionLock) continue;
            LockRequested?.Invoke(session, reason);
        }
    }

    private void Poll()
    {
        var sessions = _sessions();
        if (sessions.Count == 0) return;

        // Query the shell once per tick rather than once per vault.
        var openPaths = ExplorerWatcher.GetOpenFolderPaths();

        foreach (var session in sessions)
        {
            if (session.IdleTimeoutExpired)
            {
                LockRequested?.Invoke(session, AutoLockReason.IdleTimeout);
                continue;
            }

            if (!session.Vault.LockOnExplorerClose) continue;
            if (DateTimeOffset.UtcNow - session.UnlockedAt < ExplorerGracePeriod) continue;

            var isOpen = ExplorerWatcher.IsAnyWindowOpenUnder(session.Vault.OriginalPath, openPaths);

            lock (_gate)
            {
                if (isOpen)
                {
                    // Deliberately does NOT count as activity. An open window used to call
                    // Touch() here, which reset the idle clock every three seconds and meant a
                    // folder left open on screen never timed out - not after fifteen minutes, not
                    // ever. A window sitting open is not someone using the folder; it is the exact
                    // case the idle timeout exists to catch.
                    _missedWindowChecks.Remove(session.Vault.Id);
                    continue;
                }

                var misses = _missedWindowChecks.GetValueOrDefault(session.Vault.Id) + 1;
                _missedWindowChecks[session.Vault.Id] = misses;

                if (misses < RequiredConsecutiveMisses) continue;
                _missedWindowChecks.Remove(session.Vault.Id);
            }

            LockRequested?.Invoke(session, AutoLockReason.ExplorerClosed);
        }
    }

    public void Dispose()
    {
        _timer.Dispose();

        lock (_gate)
        {
            foreach (var watcher in _activityWatchers.Values)
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            _activityWatchers.Clear();
        }
    }
}
