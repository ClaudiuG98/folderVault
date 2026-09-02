using System.Security.Cryptography;
using FolderVault.Core.Crypto;
using Xunit;

namespace FolderVault.Tests;

public class KeyDerivationTests
{
    // Real vaults use 600k iterations; tests use a low count purely for speed.
    private const int FastIterations = 1000;

    [Fact]
    public void CorrectPassword_UnwrapsTheSameDek()
    {
        var salt = KeyDerivation.NewSalt();
        var dek = KeyDerivation.NewDek();
        var wrapped = KeyDerivation.WrapDek(
            KeyDerivation.DeriveKek("correct horse", salt, FastIterations), dek);

        var unwrapped = KeyDerivation.UnwrapDek(
            KeyDerivation.DeriveKek("correct horse", salt, FastIterations), wrapped);

        Assert.Equal(dek, unwrapped);
    }

    [Fact]
    public void WrongPassword_ThrowsRatherThanReturningGarbage()
    {
        var salt = KeyDerivation.NewSalt();
        var wrapped = KeyDerivation.WrapDek(
            KeyDerivation.DeriveKek("correct horse", salt, FastIterations), KeyDerivation.NewDek());

        Assert.ThrowsAny<CryptographicException>(() => KeyDerivation.UnwrapDek(
            KeyDerivation.DeriveKek("wrong horse", salt, FastIterations), wrapped));
    }

    [Fact]
    public void PasswordChange_RewrapsSameDek_SoExistingFilesStillDecrypt()
    {
        var salt = KeyDerivation.NewSalt();
        var dek = KeyDerivation.NewDek();
        var wrapped = KeyDerivation.WrapDek(KeyDerivation.DeriveKek("old", salt, FastIterations), dek);

        // Change password: unwrap with the old, re-wrap with the new. The DEK is untouched,
        // which is why a password change does not re-encrypt the folder.
        var recovered = KeyDerivation.UnwrapDek(
            KeyDerivation.DeriveKek("old", salt, FastIterations), wrapped);
        var newSalt = KeyDerivation.NewSalt();
        var rewrapped = KeyDerivation.WrapDek(
            KeyDerivation.DeriveKek("new", newSalt, FastIterations), recovered);

        var afterChange = KeyDerivation.UnwrapDek(
            KeyDerivation.DeriveKek("new", newSalt, FastIterations), rewrapped);
        Assert.Equal(dek, afterChange);
    }

    [Fact]
    public void TamperedWrappedKey_IsRejected()
    {
        var salt = KeyDerivation.NewSalt();
        var wrapped = KeyDerivation.WrapDek(
            KeyDerivation.DeriveKek("pw", salt, FastIterations), KeyDerivation.NewDek());
        wrapped[20] ^= 0xFF;

        Assert.ThrowsAny<CryptographicException>(() => KeyDerivation.UnwrapDek(
            KeyDerivation.DeriveKek("pw", salt, FastIterations), wrapped));
    }

    [Fact]
    public void DifferentSalts_ProduceDifferentKeys()
    {
        var a = KeyDerivation.DeriveKek("same password", KeyDerivation.NewSalt(), FastIterations);
        var b = KeyDerivation.DeriveKek("same password", KeyDerivation.NewSalt(), FastIterations);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void RecoveryKey_IsGrouped_Unambiguous_AndUnique()
    {
        var key = KeyDerivation.NewRecoveryKey();

        Assert.Equal(47, key.Length); // 40 chars + 7 dashes
        Assert.Equal(8, key.Split('-').Length);
        Assert.All(key.Split('-'), g => Assert.Equal(5, g.Length));
        // I, L, O and U are excluded to avoid transcription errors off a screen or on paper.
        Assert.DoesNotContain(key, c => c is 'I' or 'L' or 'O' or 'U');
        Assert.NotEqual(key, KeyDerivation.NewRecoveryKey());
    }

    [Fact]
    public void RecoveryKey_NormalizationIgnoresCaseAndDashes()
    {
        Assert.Equal(
            KeyDerivation.NormalizeRecoveryKey("K3M9P-2XQ7R"),
            KeyDerivation.NormalizeRecoveryKey("k3m9p2xq7r"));
    }
}
