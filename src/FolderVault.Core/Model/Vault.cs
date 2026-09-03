using System.Text.Json.Serialization;

namespace FolderVault.Core.Model;

/// <summary>
/// One protected folder. Persisted (DPAPI-wrapped) in vaults.json.
///
/// No password and no unwrapped key is ever stored here. Correctness of a password is
/// established by successfully AES-GCM-unwrapping <see cref="WrappedDek"/>: a wrong password
/// derives a wrong KEK, the GCM tag fails, and unwrapping throws. That authenticated unwrap
/// is the password check, so there is no separate verifier hash to keep in sync.
/// </summary>
public sealed class Vault
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Where the folder lives when unlocked, e.g. <c>D:\Stuff\Photos</c>.</summary>
    public string OriginalPath { get; set; } = string.Empty;

    public VaultMode Mode { get; set; } = VaultMode.Fast;

    public VaultState State { get; set; } = VaultState.Unlocked;

    // ---- Key derivation (see Crypto/KeyDerivation.cs) ----

    /// <summary>Random per-vault PBKDF2 salt.</summary>
    public byte[] Salt { get; set; } = [];

    /// <summary>PBKDF2 iteration count, recorded so it can be raised for new vaults later.</summary>
    public int Iterations { get; set; }

    /// <summary>The data key, wrapped by the password-derived KEK. Nonce + ciphertext + tag.</summary>
    public byte[] WrappedDek { get; set; } = [];

    // ---- Recovery key (optional second wrapping of the same DEK) ----

    public byte[]? RecoverySalt { get; set; }

    /// <summary>The same DEK wrapped by the recovery-key-derived KEK, if a recovery key was issued.</summary>
    public byte[]? RecoveryWrappedDek { get; set; }

    // ---- Auto-lock policy ----

    /// <summary>Re-lock after this many minutes without activity. Zero disables the idle timer.</summary>
    public int IdleLockMinutes { get; set; } = 15;

    /// <summary>Re-lock once no Explorer window remains open under the folder.</summary>
    public bool LockOnExplorerClose { get; set; } = true;

    /// <summary>
    /// Re-lock when Windows locks or the user switches away. Turning all three rules off is what
    /// "leave it open until I say otherwise" means; signing out and shutting down still lock,
    /// because that is the last moment anything can.
    /// </summary>
    public bool LockOnSessionLock { get; set; } = true;

    /// <summary>True when nothing will re-lock this vault on its own while Windows stays signed in.</summary>
    [JsonIgnore]
    public bool StaysUnlocked => IdleLockMinutes <= 0 && !LockOnExplorerClose && !LockOnSessionLock;

    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? UnlockedAtUtc { get; set; }

    /// <summary>Leaf name shown in the UI and used for the decoy shortcut, e.g. <c>Photos</c>.</summary>
    [JsonIgnore]
    public string DisplayName =>
        Path.GetFileName(OriginalPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    /// <summary>Full path of the decoy that stands in for the folder while locked.</summary>
    [JsonIgnore]
    public string ShortcutPath => OriginalPath + ".lnk";

    /// <summary>True while a lock or unlock was interrupted and recovery has not yet run.</summary>
    [JsonIgnore]
    public bool NeedsRecovery => State is VaultState.Locking or VaultState.Unlocking;
}
