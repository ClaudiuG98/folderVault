using System.Security.Cryptography;
using System.Text.Json;
using FolderVault.Core.Crypto;
using FolderVault.Core.Store;

namespace FolderVault.Core.Ops;

/// <summary>
/// Secure mode: every file is encrypted with AES-256-GCM under the vault's data key.
///
/// Both directions build into a <c>*.partial</c> directory and promote it by atomic rename only
/// after a full verification pass, and the source is never deleted before that promotion. So at
/// every instant there is at least one complete copy of the data under a name that says it is
/// complete - which is what makes an interrupted run recoverable rather than destructive.
/// </summary>
public static class SecureLocker
{
    public const string ManifestName = "manifest.bin";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    /// <summary>
    /// Encrypts the staged plaintext tree into the store. Leaves the plaintext untouched: the
    /// caller deletes it only after this returns successfully.
    /// </summary>
    public static void Encrypt(string vaultStore, byte[] dek,
        IProgress<OperationProgress>? progress = null, CancellationToken ct = default)
    {
        var plain = VaultLayout.Plain(vaultStore);
        var target = VaultLayout.EncryptedPartial(vaultStore);

        if (!Directory.Exists(plain))
            throw new VaultOperationException("There is no staged folder to encrypt.");

        AtomicFile.DeleteDirectory(target);
        Directory.CreateDirectory(target);

        var manifest = new Manifest();
        long index = 0, bytesDone = 0;

        var files = Directory.GetFiles(plain, "*", SearchOption.AllDirectories);
        var totalBytes = files.Sum(f => new FileInfo(f).Length);
        manifest.TotalBytes = totalBytes;

        // Directories are recorded explicitly so empty ones survive a round trip.
        foreach (var dir in Directory.GetDirectories(plain, "*", SearchOption.AllDirectories))
        {
            var info = new DirectoryInfo(dir);
            manifest.Entries.Add(new ManifestEntry
            {
                IsDirectory = true,
                RelativePath = Path.GetRelativePath(plain, dir),
                LastWriteUtc = info.LastWriteTimeUtc,
                CreationUtc = info.CreationTimeUtc,
                Attributes = (int)info.Attributes,
            });
        }

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            var info = new FileInfo(file);
            var entry = new ManifestEntry
            {
                Index = index,
                RelativePath = Path.GetRelativePath(plain, file),
                Length = info.Length,
                LastWriteUtc = info.LastWriteTimeUtc,
                CreationUtc = info.CreationTimeUtc,
                Attributes = (int)info.Attributes,
            };

            progress?.Report(new OperationProgress($"Encrypting {entry.RelativePath}", bytesDone, totalBytes));

            using (var source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var hashing = new HashingReadStream(source))
            using (var destination = new FileStream(Path.Combine(target, entry.BlobName),
                       FileMode.Create, FileAccess.Write, FileShare.None))
            {
                FileCrypto.Encrypt(hashing, destination, dek, info.Length, ct: ct);
                destination.Flush(flushToDisk: true);
                entry.Sha256 = hashing.Finish();
            }

            manifest.Entries.Add(entry);
            bytesDone += info.Length;
            index++;
        }

        WriteManifest(target, manifest, dek);

        progress?.Report(new OperationProgress("Verifying encrypted copy", 0, totalBytes));
        Verify(target, manifest, dek, progress, ct);

        VaultLayout.Promote(target, VaultLayout.Encrypted(vaultStore));
    }

    /// <summary>
    /// Decrypts the store back into a staged plaintext tree. Leaves the ciphertext untouched:
    /// the caller removes it only after the payload has been moved back into place.
    /// </summary>
    public static void Decrypt(string vaultStore, byte[] dek,
        IProgress<OperationProgress>? progress = null, CancellationToken ct = default)
    {
        var encrypted = VaultLayout.Encrypted(vaultStore);
        var target = VaultLayout.PlainPartial(vaultStore);

        if (!Directory.Exists(encrypted))
            throw new VaultOperationException("There is no encrypted payload to decrypt.");

        var manifest = ReadManifest(encrypted, dek);

        AtomicFile.DeleteDirectory(target);
        Directory.CreateDirectory(target);

        // Directories first, so files always have a parent to land in.
        foreach (var entry in manifest.Entries.Where(e => e.IsDirectory))
            Directory.CreateDirectory(SafeCombine(target, entry.RelativePath));

        long bytesDone = 0;
        foreach (var entry in manifest.Entries.Where(e => !e.IsDirectory))
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new OperationProgress(
                $"Decrypting {entry.RelativePath}", bytesDone, manifest.TotalBytes));

            var destinationPath = SafeCombine(target, entry.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

            var blob = Path.Combine(encrypted, entry.BlobName);
            if (!File.Exists(blob))
                throw new VaultOperationException(
                    $"The vault is missing data for '{entry.RelativePath}'. The store may be damaged.");

            string actualHash;
            using (var source = new FileStream(blob, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var destination = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var hashing = new HashingSinkStream())
            {
                using var tee = new TeeStream(destination, hashing);
                FileCrypto.Decrypt(source, tee, dek, ct);
                destination.Flush(flushToDisk: true);
                actualHash = hashing.Finish();
            }

            if (!string.Equals(actualHash, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new VaultOperationException(
                    $"'{entry.RelativePath}' failed its integrity check after decryption. " +
                    "The vault has not been unlocked and the encrypted copy is untouched.");

            RestoreMetadata(destinationPath, entry);
            bytesDone += entry.Length;
        }

        // Directory timestamps last: writing files into a directory updates them.
        foreach (var entry in manifest.Entries.Where(e => e.IsDirectory))
            RestoreMetadata(SafeCombine(target, entry.RelativePath), entry, isDirectory: true);

        VaultLayout.Promote(target, VaultLayout.Plain(vaultStore));
    }

    /// <summary>
    /// Decrypts every blob and checks it against the hash recorded at encryption time. This is
    /// what licenses the caller to delete the plaintext: a bad sector or a short write is caught
    /// here, while the original still exists.
    /// </summary>
    private static void Verify(string encryptedDir, Manifest manifest, byte[] dek,
        IProgress<OperationProgress>? progress, CancellationToken ct)
    {
        long bytesDone = 0;
        foreach (var entry in manifest.Entries.Where(e => !e.IsDirectory))
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new OperationProgress(
                $"Verifying {entry.RelativePath}", bytesDone, manifest.TotalBytes));

            var blob = Path.Combine(encryptedDir, entry.BlobName);
            using var source = new FileStream(blob, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var sink = new HashingSinkStream();

            try
            {
                FileCrypto.Decrypt(source, sink, dek, ct);
            }
            catch (CryptographicException ex)
            {
                throw new VaultOperationException(
                    $"The encrypted copy of '{entry.RelativePath}' did not verify. Your original " +
                    "files have not been touched.", ex);
            }

            if (sink.Length != entry.Length ||
                !string.Equals(sink.Finish(), entry.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new VaultOperationException(
                    $"The encrypted copy of '{entry.RelativePath}' does not match the original. " +
                    "Your original files have not been touched.");

            bytesDone += entry.Length;
        }
    }

    private static void WriteManifest(string encryptedDir, Manifest manifest, byte[] dek)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        using var output = new FileStream(Path.Combine(encryptedDir, ManifestName),
            FileMode.Create, FileAccess.Write, FileShare.None);
        FileCrypto.Encrypt(new MemoryStream(json), output, dek, json.Length);
        output.Flush(flushToDisk: true);
    }

    public static Manifest ReadManifest(string encryptedDir, byte[] dek)
    {
        var path = Path.Combine(encryptedDir, ManifestName);
        if (!File.Exists(path))
            throw new VaultOperationException("The vault store is missing its manifest and cannot be opened.");

        var buffer = new MemoryStream();
        using (var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            FileCrypto.Decrypt(source, buffer, dek);

        return JsonSerializer.Deserialize<Manifest>(buffer.ToArray())
               ?? throw new VaultOperationException("The vault manifest could not be read.");
    }

    private static void RestoreMetadata(string path, ManifestEntry entry, bool isDirectory = false)
    {
        try
        {
            if (isDirectory)
            {
                Directory.SetLastWriteTimeUtc(path, entry.LastWriteUtc.UtcDateTime);
                Directory.SetCreationTimeUtc(path, entry.CreationUtc.UtcDateTime);
            }
            else
            {
                File.SetLastWriteTimeUtc(path, entry.LastWriteUtc.UtcDateTime);
                File.SetCreationTimeUtc(path, entry.CreationUtc.UtcDateTime);
                var attributes = (FileAttributes)entry.Attributes;
                if (attributes != 0) File.SetAttributes(path, attributes);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
        {
            // Timestamps are cosmetic; never fail an unlock over them.
        }
    }

    /// <summary>
    /// Joins a manifest-supplied relative path to the target root, refusing anything that escapes
    /// it. The manifest is encrypted and authenticated, so this is defence against a corrupt
    /// store rather than an attacker, but a path traversal here would write outside the vault.
    /// </summary>
    private static string SafeCombine(string root, string relative)
    {
        var combined = Path.GetFullPath(Path.Combine(root, relative));
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        if (!combined.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            throw new VaultOperationException($"The manifest contains an unsafe path: '{relative}'.");

        return combined;
    }
}

/// <summary>Writes to two streams at once, so decryption can hash and save in one pass.</summary>
internal sealed class TeeStream(Stream first, Stream second) : Stream
{
    public override void Write(byte[] buffer, int offset, int count)
    {
        first.Write(buffer, offset, count);
        second.Write(buffer, offset, count);
    }

    public override void Flush()
    {
        first.Flush();
        second.Flush();
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => first.Length;
    public override long Position { get => first.Position; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
