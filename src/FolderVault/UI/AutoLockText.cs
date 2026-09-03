using FolderVault.Core.Model;

namespace FolderVault.UI;

/// <summary>
/// Puts an auto-lock policy into words, for the settings dialog and for the manager's list.
///
/// Separate from both because the wording is the feature. The rule a user picked and the rule
/// their folder actually follows have to be the same thing, and the gap between those two is
/// where the last bug lived: the dialog promised a fifteen-minute timeout that an open Explorer
/// window quietly cancelled forever.
/// </summary>
public static class AutoLockText
{
    /// <summary>"45 minutes", "8 hours", "1 hour 30 minutes".</summary>
    public static string Duration(int minutes)
    {
        if (minutes < 60) return Plural(minutes, "minute");
        if (minutes % 60 == 0) return Plural(minutes / 60, "hour");
        return $"{Plural(minutes / 60, "hour")} {Plural(minutes % 60, "minute")}";
    }

    /// <summary>The short form for the manager's Auto-lock column.</summary>
    public static string Summary(Vault vault)
    {
        var rule = vault.RuleFor() switch
        {
            AutoLockRule.OnClose => "Window closed",
            AutoLockRule.OnTimer => $"{Duration(vault.IdleLockMinutes)} idle",
            AutoLockRule.Either => $"Closed or {Duration(vault.IdleLockMinutes)}",
            _ => "Stays open",
        };

        // The Windows-locks rule composes with all four, so it is appended rather than folded in.
        return vault.LockOnSessionLock ? $"{rule}, Windows locked" : rule;
    }

    /// <summary>
    /// The sentence shown under the choices. Returns null when nothing will re-lock the folder,
    /// which the caller renders as a warning rather than as a statement of fact.
    /// </summary>
    public static string? Sentence(string folderName, AutoLockRule rule, int idleMinutes,
        bool onSessionLock)
    {
        var clause = rule switch
        {
            AutoLockRule.OnClose => "you close its Explorer window",
            AutoLockRule.OnTimer => $"{Duration(idleMinutes)} pass without anything inside it changing",
            AutoLockRule.Either =>
                $"you close its Explorer window, or {Duration(idleMinutes)} pass without anything " +
                "inside it changing",
            _ => null,
        };

        if (clause is null)
        {
            return onSessionLock
                ? $"“{folderName}” re-locks only when Windows locks, or when you lock it yourself."
                : null;
        }

        if (onSessionLock) clause += ", or Windows locks";

        return $"“{folderName}” re-locks as soon as {clause}.";
    }

    /// <summary>The warning shown in place of <see cref="Sentence"/> when no rule is set.</summary>
    public static string NothingWillLockIt(string folderName) =>
        $"“{folderName}” will stay unlocked until you lock it yourself or sign out of " +
        "Windows - including while the screen is locked. Its files are readable on disk the whole time.";

    private static string Plural(int count, string noun) =>
        $"{count} {noun}{(count == 1 ? string.Empty : "s")}";
}
