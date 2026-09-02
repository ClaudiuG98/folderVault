namespace FolderVault.Core.Model;

/// <summary>How a vault protects its contents while locked.</summary>
public enum VaultMode
{
    /// <summary>
    /// Move the folder into the per-volume store and deny access via NTFS ACLs.
    /// Instant regardless of size, but obfuscation only: the owner or an administrator
    /// can always take the files back. See the threat model in README.md.
    /// </summary>
    Fast = 0,

    /// <summary>
    /// Encrypt every file with AES-256-GCM under a key derived from the password.
    /// Real at-rest encryption; cost scales with folder size.
    /// </summary>
    Secure = 1,
}
