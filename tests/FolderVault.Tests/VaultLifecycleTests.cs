using System.Security.Cryptography;
using FolderVault.Core.Model;
using FolderVault.Core.Ops;
using FolderVault.Core.Store;
using Xunit;

namespace FolderVault.Tests;

public class VaultLifecycleTests
{
    private const string Password = "correct horse battery staple";

    [Theory]
    [InlineData(VaultMode.Fast)]
    [InlineData(VaultMode.Secure)]
    public void LockThenUnlock_RoundTripsEveryFileByteForByte(VaultMode mode)
    {
        using var ctx = new VaultTestContext();
        VaultTestContext.BuildSampleTree(ctx.FolderPath);
        var before = VaultTestContext.Snapshot(ctx.FolderPath);

        var (vault, _) = ctx.Service.Create(ctx.FolderPath, mode, Password);
        ctx.Track(vault.Id);

        Assert.Equal(VaultState.Locked, vault.State);
        Assert.False(Directory.Exists(ctx.FolderPath));
        Assert.True(File.Exists(vault.ShortcutPath));

        var dek = VaultService.DeriveDek(vault, Password);
        ctx.Service.Unlock(vault, dek);

        Assert.Equal(VaultState.Unlocked, vault.State);
        Assert.True(Directory.Exists(ctx.FolderPath));
        Assert.False(File.Exists(vault.ShortcutPath));

        var after = VaultTestContext.Snapshot(ctx.FolderPath);
        Assert.Equal(before.Files, after.Files);
        Assert.Equal(before.Directories, after.Directories);
    }

    [Theory]
    [InlineData(VaultMode.Fast)]
    [InlineData(VaultMode.Secure)]
    public void SurvivesRepeatedLockUnlockCycles(VaultMode mode)
    {
        using var ctx = new VaultTestContext();
        VaultTestContext.BuildSampleTree(ctx.FolderPath);
        var before = VaultTestContext.Snapshot(ctx.FolderPath);

        var (vault, _) = ctx.Service.Create(ctx.FolderPath, mode, Password);
        ctx.Track(vault.Id);

        for (var i = 0; i < 3; i++)
        {
            var dek = VaultService.DeriveDek(vault, Password);
            ctx.Service.Unlock(vault, dek);
            Assert.Equal(before.Files, VaultTestContext.Snapshot(ctx.FolderPath).Files);
            ctx.Service.Lock(vault, dek);
        }

        var finalDek = VaultService.DeriveDek(vault, Password);
        ctx.Service.Unlock(vault, finalDek);
        Assert.Equal(before.Files, VaultTestContext.Snapshot(ctx.FolderPath).Files);
    }

    [Fact]
    public void WrongPassword_CannotDeriveTheKey()
    {
        using var ctx = new VaultTestContext();
        VaultTestContext.BuildSampleTree(ctx.FolderPath);

        var (vault, _) = ctx.Service.Create(ctx.FolderPath, VaultMode.Secure, Password);
        ctx.Track(vault.Id);

        Assert.ThrowsAny<CryptographicException>(() => VaultService.DeriveDek(vault, "not the password"));
    }

    [Fact]
    public void RecoveryKey_UnlocksWithoutThePassword()
    {
        using var ctx = new VaultTestContext();
        VaultTestContext.BuildSampleTree(ctx.FolderPath);
        var before = VaultTestContext.Snapshot(ctx.FolderPath);

        var (vault, recoveryKey) = ctx.Service.Create(ctx.FolderPath, VaultMode.Secure, Password);
        ctx.Track(vault.Id);
        Assert.NotNull(recoveryKey);

        // Typed back the way a person would: lower case, dashes dropped.
        var typed = recoveryKey!.Replace("-", "").ToLowerInvariant();
        var dek = VaultService.DeriveDekFromRecoveryKey(vault, typed);
        ctx.Service.Unlock(vault, dek);

        Assert.Equal(before.Files, VaultTestContext.Snapshot(ctx.FolderPath).Files);
    }

    [Fact]
    public void ChangingPassword_KeepsTheDataReadable_AndRetiresTheOldPassword()
    {
        using var ctx = new VaultTestContext();
        VaultTestContext.BuildSampleTree(ctx.FolderPath);
        var before = VaultTestContext.Snapshot(ctx.FolderPath);

        var (vault, _) = ctx.Service.Create(ctx.FolderPath, VaultMode.Secure, Password);
        ctx.Track(vault.Id);

        ctx.Service.ChangePassword(vault, Password, "a brand new password");

        Assert.ThrowsAny<CryptographicException>(() => VaultService.DeriveDek(vault, Password));

        ctx.Service.Unlock(vault, VaultService.DeriveDek(vault, "a brand new password"));
        Assert.Equal(before.Files, VaultTestContext.Snapshot(ctx.FolderPath).Files);
    }

    [Fact]
    public void SecureStore_LeaksNeitherFilenamesNorContent()
    {
        using var ctx = new VaultTestContext();
        VaultTestContext.BuildSampleTree(ctx.FolderPath);

        var (vault, _) = ctx.Service.Create(ctx.FolderPath, VaultMode.Secure, Password);
        ctx.Track(vault.Id);

        var encrypted = VaultLayout.Encrypted(VolumeStore.GetVaultStore(ctx.Root, vault.Id));
        var names = Directory.GetFiles(encrypted).Select(Path.GetFileName).ToList();

        // Only opaque blobs and the encrypted manifest.
        Assert.Contains(SecureLocker.ManifestName, names);
        Assert.All(names.Where(n => n != SecureLocker.ManifestName),
            n => Assert.Matches(@"^\d{8}\.bin$", n!));

        // No original filename or file content appears anywhere in the store, in either encoding.
        var haystack = Directory.GetFiles(encrypted).SelectMany(File.ReadAllBytes).ToArray();
        foreach (var needle in new[] { "notes.txt", "hello vault", "café-résumé-日本語" })
        {
            Assert.False(ContainsSequence(haystack, System.Text.Encoding.UTF8.GetBytes(needle)),
                $"The store leaks '{needle}' as UTF-8.");
            Assert.False(ContainsSequence(haystack, System.Text.Encoding.Unicode.GetBytes(needle)),
                $"The store leaks '{needle}' as UTF-16.");
        }
    }

    [Fact]
    public void FastMode_LocksInstantly_BecauseItOnlyRenames()
    {
        using var ctx = new VaultTestContext();
        Directory.CreateDirectory(ctx.FolderPath);
        // 64 MiB: a copy would take meaningfully longer than a metadata rename.
        File.WriteAllBytes(Path.Combine(ctx.FolderPath, "big.bin"), RandomNumberGenerator.GetBytes(64 << 20));

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var (vault, _) = ctx.Service.Create(ctx.FolderPath, VaultMode.Fast, Password);
        stopwatch.Stop();
        ctx.Track(vault.Id);

        // Generous bound: the point is that it is not proportional to folder size. PBKDF2 at
        // 600k iterations dominates this number, not the move.
        Assert.True(stopwatch.ElapsedMilliseconds < 5000,
            $"Fast lock took {stopwatch.ElapsedMilliseconds} ms; it should be a rename, not a copy.");
    }

    [Fact]
    public void SystemFoldersAndDriveRoots_AreRefused()
    {
        using var ctx = new VaultTestContext();

        Assert.NotNull(VolumeStore.ValidateProtectable(@"C:\"));
        Assert.NotNull(VolumeStore.ValidateProtectable(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows)));
        Assert.NotNull(VolumeStore.ValidateProtectable(@"\\server\share\folder"));
        Assert.NotNull(VolumeStore.ValidateProtectable(Path.Combine(ctx.Root, "does-not-exist")));

        Directory.CreateDirectory(ctx.FolderPath);
        Assert.Null(VolumeStore.ValidateProtectable(ctx.FolderPath));
    }

    /// <summary>Plain subsequence search: does <paramref name="haystack"/> contain the needle?</summary>
    private static bool ContainsSequence(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length) return false;

        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle)) return true;
        }
        return false;
    }
}
