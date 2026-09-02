namespace FolderVault.Core.Ops;

/// <summary>
/// One entry in a Secure vault's manifest.
///
/// The manifest is itself encrypted, which is why blobs on disk are named by an opaque index
/// rather than by filename. Storing names in the clear would leak most of what a vault exists to
/// hide - "tax-return-2024.pdf" is nearly as revealing as its contents - and the directory tree
/// would leak the rest.
/// </summary>
public sealed class ManifestEntry
{
    /// <summary>Blob number, or -1 for a directory, which has no blob.</summary>
    public long Index { get; set; } = -1;

    /// <summary>Path relative to the vault root, using the platform separator.</summary>
    public string RelativePath { get; set; } = string.Empty;

    public bool IsDirectory { get; set; }

    public long Length { get; set; }

    /// <summary>Hex SHA-256 of the plaintext, checked on both the verify pass and unlock.</summary>
    public string Sha256 { get; set; } = string.Empty;

    public DateTimeOffset LastWriteUtc { get; set; }

    public DateTimeOffset CreationUtc { get; set; }

    /// <summary><see cref="FileAttributes"/> as an int so the JSON stays stable across runtimes.</summary>
    public int Attributes { get; set; }

    public string BlobName => Index.ToString("D8") + ".bin";
}

/// <summary>The decrypted contents of a Secure vault's <c>manifest.bin</c>.</summary>
public sealed class Manifest
{
    public int Version { get; set; } = 1;
    public List<ManifestEntry> Entries { get; set; } = [];
    public long TotalBytes { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
}
