using System.Buffers.Binary;
using System.Security.Cryptography;

namespace FolderVault.Core.Crypto;

/// <summary>
/// Streaming AES-256-GCM for a single file.
///
/// GCM has a hard ~64 GiB limit per (key, nonce) and .NET's one-shot API needs the whole
/// payload in memory, so files are encrypted in 1 MiB chunks, each with its own nonce and tag.
/// Chunking naively would let an attacker reorder, drop, duplicate or truncate chunks
/// undetectably, so the fixed header - which pins the exact plaintext length - plus the chunk
/// index are fed to every chunk as associated data. Any such edit fails the tag check.
///
/// The header records the plaintext length rather than a chunk count so the decoder knows the
/// exact payload size of the final, short chunk. Reading a chunk greedily instead would consume
/// the trailing tag bytes along with it.
/// </summary>
public static class FileCrypto
{
    private static readonly byte[] Magic = "FVLT"u8.ToArray();
    private const byte Version = 1;
    private const int DefaultChunkSize = 1 << 20; // 1 MiB
    private const int HeaderSize = 4 + 1 + 4 + KeyDerivation.NonceSize + 8; // 29 bytes

    /// <summary>Bytes this format adds on top of the plaintext, for space-check purposes.</summary>
    public static long OverheadFor(long plaintextLength, int chunkSize = DefaultChunkSize) =>
        HeaderSize + ChunkCount(plaintextLength, chunkSize) * KeyDerivation.TagSize;

    private static long ChunkCount(long length, int chunkSize) =>
        length == 0 ? 1 : (length + chunkSize - 1) / chunkSize;

    public static void Encrypt(Stream input, Stream output, byte[] key, long inputLength,
        int chunkSize = DefaultChunkSize, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(inputLength);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunkSize);

        var baseNonce = RandomNumberGenerator.GetBytes(KeyDerivation.NonceSize);
        // An empty file still writes one empty chunk, keeping the decrypt loop uniform.
        var totalChunks = ChunkCount(inputLength, chunkSize);

        var header = new byte[HeaderSize];
        Magic.CopyTo(header, 0);
        header[4] = Version;
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(5, 4), chunkSize);
        baseNonce.CopyTo(header, 9);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(21, 8), inputLength);
        output.Write(header);

        var plain = new byte[chunkSize];
        var cipher = new byte[chunkSize];
        var tag = new byte[KeyDerivation.TagSize];
        var aad = new byte[HeaderSize + 8];
        header.CopyTo(aad, 0);

        using var aes = new AesGcm(key, KeyDerivation.TagSize);
        for (long index = 0; index < totalChunks; index++)
        {
            ct.ThrowIfCancellationRequested();

            var expected = PayloadLength(index, totalChunks, inputLength, chunkSize);
            if (ReadFully(input, plain, expected) != expected)
                throw new IOException("Source file shrank while it was being encrypted.");

            BinaryPrimitives.WriteInt64LittleEndian(aad.AsSpan(HeaderSize, 8), index);
            aes.Encrypt(ChunkNonce(baseNonce, index), plain.AsSpan(0, expected),
                cipher.AsSpan(0, expected), tag, aad);

            output.Write(cipher, 0, expected);
            output.Write(tag);
        }

        // If bytes remain, the file grew mid-encrypt and the ciphertext would silently lose the
        // tail. Fail loudly so the caller keeps the plaintext and retries.
        if (input.ReadByte() != -1)
            throw new IOException("Source file grew while it was being encrypted.");
    }

    public static void Decrypt(Stream input, Stream output, byte[] key, CancellationToken ct = default)
    {
        var header = new byte[HeaderSize];
        if (ReadFully(input, header, HeaderSize) != HeaderSize)
            throw new CryptographicException("Encrypted file is truncated: header incomplete.");
        if (!header.AsSpan(0, 4).SequenceEqual(Magic))
            throw new CryptographicException("Not a FolderVault encrypted file.");
        if (header[4] != Version)
            throw new CryptographicException($"Unsupported file format version {header[4]}.");

        var chunkSize = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(5, 4));
        if (chunkSize is <= 0 or > (1 << 26))
            throw new CryptographicException("Encrypted file declares an implausible chunk size.");
        var baseNonce = header.AsSpan(9, KeyDerivation.NonceSize).ToArray();
        var plaintextLength = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(21, 8));
        if (plaintextLength < 0)
            throw new CryptographicException("Encrypted file declares a negative length.");

        var totalChunks = ChunkCount(plaintextLength, chunkSize);
        var cipher = new byte[chunkSize];
        var plain = new byte[chunkSize];
        var tag = new byte[KeyDerivation.TagSize];
        var aad = new byte[HeaderSize + 8];
        header.CopyTo(aad, 0);

        using var aes = new AesGcm(key, KeyDerivation.TagSize);
        for (long index = 0; index < totalChunks; index++)
        {
            ct.ThrowIfCancellationRequested();

            // Exact, header-derived length: never read greedily or the tag gets eaten.
            var payload = PayloadLength(index, totalChunks, plaintextLength, chunkSize);
            if (ReadFully(input, cipher, payload) != payload)
                throw new CryptographicException("Encrypted file is truncated.");
            if (ReadFully(input, tag, KeyDerivation.TagSize) != KeyDerivation.TagSize)
                throw new CryptographicException("Encrypted file is truncated: missing tag.");

            BinaryPrimitives.WriteInt64LittleEndian(aad.AsSpan(HeaderSize, 8), index);
            // Throws AuthenticationTagMismatchException (a CryptographicException) on a wrong
            // key or any tampering, including a reordered or spliced chunk.
            aes.Decrypt(ChunkNonce(baseNonce, index), cipher.AsSpan(0, payload),
                tag, plain.AsSpan(0, payload), aad);

            output.Write(plain, 0, payload);
        }
    }

    /// <summary>Plaintext bytes carried by chunk <paramref name="index"/>; the last one is short.</summary>
    private static int PayloadLength(long index, long totalChunks, long totalLength, int chunkSize) =>
        index == totalChunks - 1 ? (int)(totalLength - index * chunkSize) : chunkSize;

    /// <summary>
    /// Nonce for one chunk: the file's random base nonce with the chunk index folded into its
    /// low 8 bytes. Unique per chunk within a file, and the random base keeps it unique across
    /// files under the same key.
    /// </summary>
    private static byte[] ChunkNonce(byte[] baseNonce, long index)
    {
        var nonce = (byte[])baseNonce.Clone();
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, index);
        for (var i = 0; i < 8; i++) nonce[4 + i] ^= counter[i];
        return nonce;
    }

    /// <summary>Streams may return short reads; loop until <paramref name="count"/> or the source ends.</summary>
    private static int ReadFully(Stream stream, byte[] buffer, int count)
    {
        var total = 0;
        while (total < count)
        {
            var read = stream.Read(buffer, total, count - total);
            if (read == 0) break;
            total += read;
        }
        return total;
    }
}
