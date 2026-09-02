namespace FolderVault.Core.Store;

/// <summary>Where a vault's files physically are right now, as observed on disk.</summary>
public enum PayloadLocation
{
    /// <summary>At the original path: the folder is unlocked and browsable.</summary>
    AtOriginal,

    /// <summary>In the store, unencrypted. The locked state for Fast mode; transient for Secure.</summary>
    PlainInStore,

    /// <summary>In the store, encrypted. The locked state for Secure mode.</summary>
    EncryptedInStore,

    /// <summary>Both at the original path and in the store. Needs a human decision.</summary>
    Ambiguous,

    /// <summary>Nowhere to be found. Something outside FolderVault removed it.</summary>
    Missing,
}

/// <summary>
/// The naming rule that makes FolderVault crash-safe.
///
/// A payload directory carries its final name (<c>plain</c> / <c>enc</c>) only once it is
/// complete and verified. While being written it is named <c>*.partial</c>, and a partial
/// directory is garbage by definition - recovery deletes it without inspection. Promotion from
/// partial to final is a same-directory rename, which NTFS performs atomically, so there is no
/// instant at which a directory holds its final name while still incomplete.
///
/// The other half of the rule: <b>never delete a source until the destination is promoted</b>.
/// Together these guarantee that at every point in a lock or unlock, at least one complete copy
/// of the data exists, and its name says so.
/// </summary>
public static class VaultLayout
{
    public static string Plain(string vaultStore) => Path.Combine(vaultStore, VolumeStore.PlainDirectoryName);

    public static string Encrypted(string vaultStore) => Path.Combine(vaultStore, VolumeStore.EncryptedDirectoryName);

    public static string PlainPartial(string vaultStore) => Plain(vaultStore) + ".partial";

    public static string EncryptedPartial(string vaultStore) => Encrypted(vaultStore) + ".partial";

    /// <summary>
    /// Atomically publishes a finished payload directory. Call only after the contents are
    /// written, flushed and verified.
    /// </summary>
    public static void Promote(string partialPath, string finalPath)
    {
        if (Directory.Exists(finalPath))
            throw new IOException($"Cannot promote '{partialPath}': '{finalPath}' already exists.");

        Directory.Move(partialPath, finalPath);
    }

    /// <summary>Removes incomplete payload directories left behind by an interrupted run.</summary>
    public static void DiscardPartials(string vaultStore)
    {
        AtomicFile.DeleteDirectory(PlainPartial(vaultStore));
        AtomicFile.DeleteDirectory(EncryptedPartial(vaultStore));
    }

    /// <summary>
    /// Observes where the payload actually is. Partial directories are ignored: they never count
    /// as a copy of the data.
    /// </summary>
    public static PayloadLocation Locate(string originalPath, string vaultStore)
    {
        var atOriginal = Directory.Exists(originalPath);
        var plain = Directory.Exists(Plain(vaultStore));
        var encrypted = Directory.Exists(Encrypted(vaultStore));

        // The original path only ever receives a payload by atomic rename of a verified
        // directory, so if it exists it is complete and authoritative.
        if (atOriginal) return plain || encrypted ? PayloadLocation.Ambiguous : PayloadLocation.AtOriginal;

        // Plain outranks encrypted: plain is only deleted after enc has been promoted, so both
        // being present means that deletion had not happened yet and plain is the safe copy.
        if (plain) return PayloadLocation.PlainInStore;
        if (encrypted) return PayloadLocation.EncryptedInStore;

        return PayloadLocation.Missing;
    }
}
