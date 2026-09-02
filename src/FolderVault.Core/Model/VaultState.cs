namespace FolderVault.Core.Model;

/// <summary>
/// Lifecycle of a vault. The transitional states exist so that a crash mid-operation
/// is detectable on the next launch and can be driven to completion by the journal.
/// </summary>
public enum VaultState
{
    /// <summary>Payload lives in the store; a decoy shortcut stands at the original path.</summary>
    Locked = 0,

    /// <summary>Payload lives at the original path and is browsable in Explorer.</summary>
    Unlocked = 1,

    /// <summary>Mid-lock. Recovery required before the vault may be used again.</summary>
    Locking = 2,

    /// <summary>Mid-unlock. Recovery required before the vault may be used again.</summary>
    Unlocking = 3,
}
