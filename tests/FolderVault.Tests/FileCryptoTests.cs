using System.Security.Cryptography;
using FolderVault.Core.Crypto;
using Xunit;

namespace FolderVault.Tests;

public class FileCryptoTests
{
    private const int SmallChunk = 64; // exercises the multi-chunk path without large buffers

    private static byte[] Key() => RandomNumberGenerator.GetBytes(KeyDerivation.KeySize);

    private static byte[] RoundTrip(byte[] plaintext, byte[] key, int chunkSize = SmallChunk)
    {
        var enc = new MemoryStream();
        FileCrypto.Encrypt(new MemoryStream(plaintext), enc, key, plaintext.Length, chunkSize);
        var dec = new MemoryStream();
        FileCrypto.Decrypt(new MemoryStream(enc.ToArray()), dec, key);
        return dec.ToArray();
    }

    [Theory]
    [InlineData(0)]      // empty file
    [InlineData(1)]
    [InlineData(63)]     // just under one chunk
    [InlineData(64)]     // exactly one chunk
    [InlineData(65)]     // just over: forces a short final chunk
    [InlineData(640)]    // exact multiple of chunk size
    [InlineData(1000)]
    public void RoundTrips_PreservesContentExactly(int size)
    {
        var key = Key();
        var plaintext = RandomNumberGenerator.GetBytes(size);
        Assert.Equal(plaintext, RoundTrip(plaintext, key));
    }

    [Fact]
    public void RoundTrips_AtDefaultChunkSize()
    {
        var key = Key();
        var plaintext = RandomNumberGenerator.GetBytes(3 * (1 << 20) + 12345);
        var enc = new MemoryStream();
        FileCrypto.Encrypt(new MemoryStream(plaintext), enc, key, plaintext.Length);
        var dec = new MemoryStream();
        FileCrypto.Decrypt(new MemoryStream(enc.ToArray()), dec, key);
        Assert.Equal(plaintext, dec.ToArray());
    }

    [Fact]
    public void WrongKey_IsRejected()
    {
        var plaintext = RandomNumberGenerator.GetBytes(500);
        var enc = new MemoryStream();
        FileCrypto.Encrypt(new MemoryStream(plaintext), enc, Key(), plaintext.Length, SmallChunk);

        Assert.ThrowsAny<CryptographicException>(() =>
            FileCrypto.Decrypt(new MemoryStream(enc.ToArray()), new MemoryStream(), Key()));
    }

    [Fact]
    public void FlippedCiphertextByte_FailsAuthentication()
    {
        var key = Key();
        var plaintext = RandomNumberGenerator.GetBytes(500);
        var enc = new MemoryStream();
        FileCrypto.Encrypt(new MemoryStream(plaintext), enc, key, plaintext.Length, SmallChunk);

        var corrupted = enc.ToArray();
        corrupted[40] ^= 0xFF; // lands inside the ciphertext of the first chunk

        Assert.ThrowsAny<CryptographicException>(() =>
            FileCrypto.Decrypt(new MemoryStream(corrupted), new MemoryStream(), key));
    }

    [Fact]
    public void TruncatedFile_IsDetected()
    {
        var key = Key();
        var plaintext = RandomNumberGenerator.GetBytes(500);
        var enc = new MemoryStream();
        FileCrypto.Encrypt(new MemoryStream(plaintext), enc, key, plaintext.Length, SmallChunk);

        var truncated = enc.ToArray()[..^100];

        Assert.ThrowsAny<CryptographicException>(() =>
            FileCrypto.Decrypt(new MemoryStream(truncated), new MemoryStream(), key));
    }

    [Fact]
    public void ReorderedChunks_AreDetected()
    {
        // The whole point of binding the chunk index into the AAD: swapping two
        // ciphertext+tag blocks must not silently decrypt to shuffled plaintext.
        var key = Key();
        var plaintext = RandomNumberGenerator.GetBytes(SmallChunk * 4);
        var enc = new MemoryStream();
        FileCrypto.Encrypt(new MemoryStream(plaintext), enc, key, plaintext.Length, SmallChunk);

        var bytes = enc.ToArray();
        const int header = 29;
        const int block = SmallChunk + KeyDerivation.TagSize;
        var first = bytes[header..(header + block)].ToArray();
        var second = bytes[(header + block)..(header + 2 * block)].ToArray();
        second.CopyTo(bytes, header);
        first.CopyTo(bytes, header + block);

        Assert.ThrowsAny<CryptographicException>(() =>
            FileCrypto.Decrypt(new MemoryStream(bytes), new MemoryStream(), key));
    }

    [Fact]
    public void ForeignData_IsRejectedByMagic()
    {
        Assert.ThrowsAny<CryptographicException>(() =>
            FileCrypto.Decrypt(new MemoryStream(new byte[200]), new MemoryStream(), Key()));
    }
}
