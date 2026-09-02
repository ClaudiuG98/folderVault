using FolderVault.Core.Crypto;
using FolderVault.Core.Model;

namespace FolderVault.App;

/// <summary>
/// An open vault. Holds the data key in memory for as long as the folder is unlocked, which is
/// what lets auto-lock re-encrypt a Secure vault without asking for the password again.
///
/// The key is zeroed on <see cref="Dispose"/>. A key in RAM while the vault is open is inherent
/// to the design and is called out in the threat model: FolderVault protects data at rest, not
/// against someone with control of the running machine.
/// </summary>
public sealed class UnlockedSession : IDisposable
{
    public UnlockedSession(Vault vault, byte[] dek)
    {
        Vault = vault;
        Dek = dek;
        UnlockedAt = DateTimeOffset.UtcNow;
        Touch();
    }

    public Vault Vault { get; }

    public byte[] Dek { get; }

    public DateTimeOffset UnlockedAt { get; }

    public DateTimeOffset LastActivity { get; private set; }

    /// <summary>Marks the vault as active, postponing the idle auto-lock.</summary>
    public void Touch() => LastActivity = DateTimeOffset.UtcNow;

    public TimeSpan IdleFor => DateTimeOffset.UtcNow - LastActivity;

    public bool IdleTimeoutExpired =>
        Vault.IdleLockMinutes > 0 && IdleFor > TimeSpan.FromMinutes(Vault.IdleLockMinutes);

    public void Dispose() => KeyDerivation.Wipe(Dek);
}
