namespace FolderVault.Core.Store;

/// <summary>
/// Resolves where a vault's payload lives while locked.
///
/// The store always sits on the <b>same volume</b> as the protected folder, at
/// <c>&lt;Volume&gt;:\.FolderVault\&lt;guid&gt;</c>. This is the single most important performance
/// decision in the app: a same-volume <see cref="Directory.Move"/> is a metadata rename that
/// completes instantly no matter how large the folder, whereas moving across volumes copies every
/// byte. A 100 GB folder on D: must never be staged through C:.
/// </summary>
public static class VolumeStore
{
    public const string StoreDirectoryName = ".FolderVault";

    /// <summary>Payload subdirectory holding readable files mid-operation (Secure mode staging).</summary>
    public const string PlainDirectoryName = "plain";

    /// <summary>Payload subdirectory holding encrypted blobs (Secure mode at rest).</summary>
    public const string EncryptedDirectoryName = "enc";

    /// <summary>The <c>.FolderVault</c> root on the volume that holds <paramref name="anyPath"/>.</summary>
    public static string GetStoreRoot(string anyPath)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(anyPath))
                   ?? throw new ArgumentException($"Cannot determine the volume for '{anyPath}'.", nameof(anyPath));
        return Path.Combine(root, StoreDirectoryName);
    }

    public static string GetVaultStore(string anyPath, Guid vaultId) =>
        Path.Combine(GetStoreRoot(anyPath), vaultId.ToString("N"));

    /// <summary>
    /// Creates the vault's store directory, marking the root Hidden + System so it does not
    /// clutter the drive root in Explorer.
    /// </summary>
    public static string EnsureVaultStore(string anyPath, Guid vaultId)
    {
        var storeRoot = GetStoreRoot(anyPath);
        if (!Directory.Exists(storeRoot))
        {
            Directory.CreateDirectory(storeRoot);
            try
            {
                File.SetAttributes(storeRoot, FileAttributes.Hidden | FileAttributes.System | FileAttributes.Directory);
            }
            catch (IOException)
            {
                // Cosmetic only - a visible store still works correctly.
            }
        }

        var vaultStore = Path.Combine(storeRoot, vaultId.ToString("N"));
        Directory.CreateDirectory(vaultStore);
        return vaultStore;
    }

    public static bool AreOnSameVolume(string a, string b) =>
        string.Equals(Path.GetPathRoot(Path.GetFullPath(a)),
                      Path.GetPathRoot(Path.GetFullPath(b)),
                      StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Rejects folders FolderVault must not manage. Returns null when the folder is acceptable,
    /// otherwise a message suitable for showing to the user.
    /// </summary>
    public static string? ValidateProtectable(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
            return "No folder was given.";

        string full;
        try
        {
            full = Path.GetFullPath(folderPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "That path is not valid.";
        }

        if (!Directory.Exists(full))
            return "That folder does not exist.";

        if (full.StartsWith(@"\\", StringComparison.Ordinal))
            return "Network and UNC paths are not supported: locking relies on same-volume moves and NTFS permissions.";

        var root = Path.GetPathRoot(full);
        if (string.Equals(full.TrimEnd(Path.DirectorySeparatorChar), root?.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
            return "A drive root cannot be locked.";

        // Locking a system folder would break the running OS, and locking the app's own store
        // would corrupt every other vault on the drive.
        if (IsUnderAny(full,
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)))
            return "System folders cannot be locked.";

        if (full.Contains(Path.DirectorySeparatorChar + StoreDirectoryName, StringComparison.OrdinalIgnoreCase))
            return "That folder is inside the FolderVault store and cannot itself be locked.";

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.Equals(full.TrimEnd(Path.DirectorySeparatorChar), profile.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
            return "Your whole user profile folder cannot be locked. Pick a folder inside it instead.";

        return null;
    }

    /// <summary>
    /// True when <paramref name="path"/> is one of <paramref name="roots"/> or sits inside one.
    /// Matching the root itself matters: without it, C:\Windows would be accepted while
    /// C:\Windows\System32 was refused.
    /// </summary>
    private static bool IsUnderAny(string path, params string[] roots)
    {
        var normalized = path.TrimEnd(Path.DirectorySeparatorChar);

        return roots.Where(r => !string.IsNullOrEmpty(r))
            .Select(r => r.TrimEnd(Path.DirectorySeparatorChar))
            .Any(r => normalized.Equals(r, StringComparison.OrdinalIgnoreCase)
                      || normalized.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }
}
