using Microsoft.Win32;

namespace FolderVault.App;

/// <summary>
/// Bridges the Windows session-change notifications FolderVault cares about: locking the screen
/// or signing out should re-lock every open vault, since the user has clearly stepped away.
/// </summary>
public static class SystemEvents
{
    private static Action? _handler;

    public static event Action SessionLockOrLogoff
    {
        add
        {
            if (_handler is null)
            {
                Microsoft.Win32.SystemEvents.SessionSwitch += OnSessionSwitch;
                Microsoft.Win32.SystemEvents.SessionEnding += OnSessionEnding;
            }
            _handler += value;
        }
        remove => _handler -= value;
    }

    private static void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.SessionLogoff)
            _handler?.Invoke();
    }

    private static void OnSessionEnding(object sender, SessionEndingEventArgs e) => _handler?.Invoke();
}
