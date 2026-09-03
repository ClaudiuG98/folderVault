namespace FolderVault.Core.Model;

/// <summary>
/// When a folder re-locks itself, as one choice rather than as a set of flags.
///
/// <para>The stored form is still two fields - <see cref="Vault.IdleLockMinutes"/> and
/// <see cref="Vault.LockOnExplorerClose"/> - because that is what the auto-lock poller actually
/// reads, and every combination of them is meaningful. But "a timer is on" and "the close rule is
/// on" are not two questions a person is holding in their head; they are one question with four
/// answers, and two independent tickboxes made the interesting answer - both at once - look like
/// an accident rather than a choice.</para>
///
/// <para>Locking when Windows locks is deliberately not in here. It is a different kind of rule:
/// an event that happens to the machine rather than a judgement about when you are finished with
/// the folder, and it composes with all four of these.</para>
/// </summary>
public enum AutoLockRule
{
    /// <summary>Nothing re-locks it while Windows stays signed in.</summary>
    Never,

    /// <summary>Shortly after the last Explorer window on the folder is closed.</summary>
    OnClose,

    /// <summary>After a stretch with nothing written inside it.</summary>
    OnTimer,

    /// <summary>Both of the above, whichever happens first. The safest, and the default.</summary>
    Either,
}

/// <summary>Translates between the stored fields and the choice the user actually made.</summary>
public static class AutoLockRules
{
    /// <summary>Used when a rule that has no timer of its own is switched back to one that does.</summary>
    public const int DefaultIdleMinutes = 15;

    public static AutoLockRule RuleFor(this Vault vault) =>
        Classify(vault.IdleLockMinutes, vault.LockOnExplorerClose);

    public static AutoLockRule Classify(int idleMinutes, bool onExplorerClose) =>
        (idleMinutes > 0, onExplorerClose) switch
        {
            (true, true) => AutoLockRule.Either,
            (true, false) => AutoLockRule.OnTimer,
            (false, true) => AutoLockRule.OnClose,
            (false, false) => AutoLockRule.Never,
        };

    /// <summary>
    /// The two stored fields a rule corresponds to. <paramref name="idleMinutes"/> is carried
    /// through even by rules that ignore it, so switching to "when I close it" and back does not
    /// silently forget the number the user typed.
    /// </summary>
    public static (int IdleMinutes, bool OnExplorerClose) Fields(AutoLockRule rule, int idleMinutes) =>
        rule switch
        {
            AutoLockRule.Either => (Sane(idleMinutes), true),
            AutoLockRule.OnTimer => (Sane(idleMinutes), false),
            AutoLockRule.OnClose => (0, true),
            _ => (0, false),
        };

    private static int Sane(int idleMinutes) => idleMinutes > 0 ? idleMinutes : DefaultIdleMinutes;
}
