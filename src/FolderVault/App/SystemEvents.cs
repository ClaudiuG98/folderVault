using Microsoft.Win32;

namespace FolderVault.App;

/// <summary>
/// Bridges the Windows session-change notifications FolderVault cares about.
///
/// The two are deliberately separate. <b>Locking the screen or switching user</b> is a guess that
/// the user stepped away - a good guess, and the default, but one a vault can opt out of when the
/// point is to stay open across a coffee break. <b>Signing out or shutting down</b> is not a
/// guess, and is the last moment anything can re-lock a folder before the machine is gone, so it
/// applies to every open vault regardless of policy.
/// </summary>
public static class SystemEvents
{
    private static Action? _locked;
    private static Action? _ending;
    private static bool _subscribed;

    /// <summary>The screen was locked, or the user switched to another account.</summary>
    public static event Action SessionLocked
    {
        add { Subscribe(); _locked += value; }
        remove => _locked -= value;
    }

    /// <summary>Windows is signing the user out or shutting down.</summary>
    public static event Action SessionEnding
    {
        add { Subscribe(); _ending += value; }
        remove => _ending -= value;
    }

    private static void Subscribe()
    {
        if (_subscribed) return;
        _subscribed = true;

        Microsoft.Win32.SystemEvents.SessionSwitch += OnSessionSwitch;
        Microsoft.Win32.SystemEvents.SessionEnding += OnSessionEnding;
    }

    private static void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        switch (e.Reason)
        {
            // Signing out arrives here as well as through SessionEnding, and it is the
            // stronger of the two meanings, so route it to the stronger handler.
            case SessionSwitchReason.SessionLogoff:
                _ending?.Invoke();
                break;

            case SessionSwitchReason.SessionLock:
            case SessionSwitchReason.ConsoleDisconnect:
            case SessionSwitchReason.RemoteDisconnect:
                _locked?.Invoke();
                break;
        }
    }

    private static void OnSessionEnding(object sender, SessionEndingEventArgs e) => _ending?.Invoke();
}
