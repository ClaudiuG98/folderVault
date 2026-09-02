using System.Security.Cryptography;
using System.Text;

namespace FolderVault.Core.Crypto;

/// <summary>
/// Password -> key-encryption-key (KEK), and wrapping of the per-vault data key (DEK).
///
/// The DEK is generated once and never changes. The password only ever wraps it, so changing
/// the password re-wraps 32 bytes instead of re-encrypting the whole folder. The same DEK can
/// carry a second wrapping under a recovery key.
/// </summary>
public static class KeyDerivation
{
    public const int KeySize = 32;    // AES-256
    public const int SaltSize = 32;
    public const int NonceSize = 12;  // GCM standard
    public const int TagSize = 16;

    /// <summary>
    /// PBKDF2-HMAC-SHA256 iterations for new vaults (OWASP guidance for SHA-256).
    /// Stored per vault so this can be raised later without breaking existing vaults.
    /// .NET 8 has no built-in Argon2id or scrypt; see README for the upgrade path.
    /// </summary>
    public const int DefaultIterations = 600_000;

    private static readonly byte[] WrapAad = "FolderVault/dek-wrap/v1"u8.ToArray();

    public static byte[] NewSalt() => RandomNumberGenerator.GetBytes(SaltSize);

    public static byte[] NewDek() => RandomNumberGenerator.GetBytes(KeySize);

    public static byte[] DeriveKek(string password, byte[] salt, int iterations)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        return Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, KeySize);
    }

    /// <summary>Wraps the DEK under the KEK. Layout: nonce || ciphertext || tag.</summary>
    public static byte[] WrapDek(byte[] kek, byte[] dek)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[dek.Length];
        var tag = new byte[TagSize];

        using (var aes = new AesGcm(kek, TagSize))
            aes.Encrypt(nonce, dek, ciphertext, tag, WrapAad);

        var result = new byte[NonceSize + ciphertext.Length + TagSize];
        nonce.CopyTo(result, 0);
        ciphertext.CopyTo(result, NonceSize);
        tag.CopyTo(result, NonceSize + ciphertext.Length);
        return result;
    }

    /// <summary>
    /// Unwraps the DEK. A wrong password derives a wrong KEK and the GCM tag check fails,
    /// so this throwing <b>is</b> the password check.
    /// </summary>
    /// <exception cref="CryptographicException">The key is wrong or the blob was tampered with.</exception>
    public static byte[] UnwrapDek(byte[] kek, byte[] wrapped)
    {
        if (wrapped.Length != NonceSize + KeySize + TagSize)
            throw new CryptographicException("Wrapped key blob is malformed.");

        var nonce = wrapped.AsSpan(0, NonceSize);
        var ciphertext = wrapped.AsSpan(NonceSize, KeySize);
        var tag = wrapped.AsSpan(NonceSize + KeySize, TagSize);
        var dek = new byte[KeySize];

        using var aes = new AesGcm(kek, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, dek, WrapAad);
        return dek;
    }

    /// <summary>
    /// A fresh recovery key as Crockford-style base32 in 8 groups of 5, e.g.
    /// <c>K3M9P-2XQ7R-...</c>. Shown to the user exactly once.
    /// </summary>
    public static string NewRecoveryKey()
    {
        const string alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"; // no I, L, O, U
        // One random byte per character: 40 chars x 5 bits = 200 bits of key material.
        // 256 is an exact multiple of 32, so the modulo introduces no bias.
        var raw = RandomNumberGenerator.GetBytes(40);
        var sb = new StringBuilder(47);
        for (var i = 0; i < raw.Length; i++)
        {
            if (i > 0 && i % 5 == 0) sb.Append('-');
            sb.Append(alphabet[raw[i] % 32]);
        }
        return sb.ToString();
    }

    /// <summary>Normalises user-typed recovery keys so case and dashes do not matter.</summary>
    public static string NormalizeRecoveryKey(string input) =>
        new(input.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    public static void Wipe(byte[]? key)
    {
        if (key is not null) CryptographicOperations.ZeroMemory(key);
    }
}
