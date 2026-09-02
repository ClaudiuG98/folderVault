using FolderVault.Core.Model;
using FolderVault.Core.Ops;
using FolderVault.Core.Store;
using Xunit;

namespace FolderVault.Tests;

/// <summary>
/// Crash-recovery tests. Each one reproduces the on-disk state a real crash would leave at a
/// specific point in a lock or unlock, then asserts that recovery reaches a consistent state
/// with every byte still present.
///
/// The invariant under test: a payload directory carries its final name only once complete, and
/// a source is never deleted before its replacement is promoted. So at every interruption point
/// there is at least one complete copy.
/// </summary>
public class RecoveryTests
{
    private const string Password = "correct horse battery staple";

    [Theory]
    [InlineData(VaultMode.Fast)]
    [InlineData(VaultMode.Secure)]
    public void CrashAfterStaging_BeforeShortcut_RecoversWithoutDataLoss(VaultMode mode)
    {
        using var ctx = new VaultTestContext();
        VaultTestContext.BuildSampleTree(ctx.FolderPath);
        var before = VaultTestContext.Snapshot(ctx.FolderPath);

        var (vault, _) = ctx.Service.Create(ctx.FolderPath, mode, Password);
        ctx.Track(vault.Id);

        // Simulate dying between the payload move and the shortcut being written.
        ShortcutFactoryDeleteDecoy(vault);
        MarkInterrupted(ctx, vault, VaultState.Locking, JournalOperation.Lock);

        var result = ctx.Service.Recover(vault);

        Assert.False(result.NeedsUserDecision);
        Assert.Equal(VaultState.Locked, result.State);
        Assert.True(File.Exists(vault.ShortcutPath), "Recovery should have restored the decoy shortcut.");

        // And the data is still intact underneath.
        ctx.Service.Unlock(vault, VaultService.DeriveDek(vault, Password));
        Assert.Equal(before.Files, VaultTestContext.Snapshot(ctx.FolderPath).Files);
    }

    [Fact]
    public void CrashMidEncryption_RestoresTheFolderIntact()
    {
        using var ctx = new VaultTestContext();
        VaultTestContext.BuildSampleTree(ctx.FolderPath);
        var before = VaultTestContext.Snapshot(ctx.FolderPath);

        var (vault, _) = ctx.Service.Create(ctx.FolderPath, VaultMode.Secure, Password);
        ctx.Track(vault.Id);
        var store = VolumeStore.GetVaultStore(ctx.Root, vault.Id);

        // Rebuild the state a crash midway through encryption leaves: the staged plaintext is
        // back, and a half-written enc.partial sits beside it.
        ctx.Service.Unlock(vault, VaultService.DeriveDek(vault, Password));
        FastLocker.Stage(ctx.FolderPath, store, applyAcl: false);
        Directory.CreateDirectory(VaultLayout.EncryptedPartial(store));
        File.WriteAllBytes(Path.Combine(VaultLayout.EncryptedPartial(store), "00000000.bin"), [1, 2, 3]);
        MarkInterrupted(ctx, vault, VaultState.Locking, JournalOperation.Lock);

        var result = ctx.Service.Recover(vault);

        // The partial ciphertext is garbage and must be gone; the plaintext is authoritative.
        Assert.False(Directory.Exists(VaultLayout.EncryptedPartial(store)));
        Assert.Equal(VaultState.Unlocked, result.State);
        Assert.Equal(before.Files, VaultTestContext.Snapshot(ctx.FolderPath).Files);
    }

    [Fact]
    public void CrashAfterEncryptingButBeforeDeletingPlaintext_PrefersThePlaintext()
    {
        using var ctx = new VaultTestContext();
        VaultTestContext.BuildSampleTree(ctx.FolderPath);
        var before = VaultTestContext.Snapshot(ctx.FolderPath);

        var (vault, _) = ctx.Service.Create(ctx.FolderPath, VaultMode.Secure, Password);
        ctx.Track(vault.Id);
        var store = VolumeStore.GetVaultStore(ctx.Root, vault.Id);

        // Both copies present: enc was promoted, plain not yet deleted. Plain outranks enc,
        // because its survival proves the deletion step had not run.
        var dek = VaultService.DeriveDek(vault, Password);
        SecureLocker.Decrypt(store, dek);   // rebuilds plain from enc
        Assert.True(Directory.Exists(VaultLayout.Plain(store)));
        Assert.True(Directory.Exists(VaultLayout.Encrypted(store)));
        MarkInterrupted(ctx, vault, VaultState.Locking, JournalOperation.Lock);

        var result = ctx.Service.Recover(vault);

        Assert.Equal(VaultState.Unlocked, result.State);
        Assert.Equal(before.Files, VaultTestContext.Snapshot(ctx.FolderPath).Files);
    }

    [Fact]
    public void CrashMidDecryption_LeavesTheEncryptedCopyAuthoritative()
    {
        using var ctx = new VaultTestContext();
        VaultTestContext.BuildSampleTree(ctx.FolderPath);
        var before = VaultTestContext.Snapshot(ctx.FolderPath);

        var (vault, _) = ctx.Service.Create(ctx.FolderPath, VaultMode.Secure, Password);
        ctx.Track(vault.Id);
        var store = VolumeStore.GetVaultStore(ctx.Root, vault.Id);

        // A crash partway through unlocking: a truncated plain.partial next to an intact enc.
        Directory.CreateDirectory(VaultLayout.PlainPartial(store));
        File.WriteAllText(Path.Combine(VaultLayout.PlainPartial(store), "notes.txt"), "half-writ");
        MarkInterrupted(ctx, vault, VaultState.Unlocking, JournalOperation.Unlock);

        var result = ctx.Service.Recover(vault);

        Assert.False(Directory.Exists(VaultLayout.PlainPartial(store)),
            "A partial directory is garbage by definition and must be discarded.");
        Assert.Equal(VaultState.Locked, result.State);

        ctx.Service.Unlock(vault, VaultService.DeriveDek(vault, Password));
        Assert.Equal(before.Files, VaultTestContext.Snapshot(ctx.FolderPath).Files);
    }

    [Fact]
    public void CrashAfterMovingBack_SettlesAsUnlocked()
    {
        using var ctx = new VaultTestContext();
        VaultTestContext.BuildSampleTree(ctx.FolderPath);
        var before = VaultTestContext.Snapshot(ctx.FolderPath);

        var (vault, _) = ctx.Service.Create(ctx.FolderPath, VaultMode.Fast, Password);
        ctx.Track(vault.Id);

        // The folder is back but the shortcut was never cleaned up.
        FastLocker.Restore(ctx.FolderPath, VolumeStore.GetVaultStore(ctx.Root, vault.Id));
        MarkInterrupted(ctx, vault, VaultState.Unlocking, JournalOperation.Unlock);

        var result = ctx.Service.Recover(vault);

        Assert.Equal(VaultState.Unlocked, result.State);
        Assert.False(File.Exists(vault.ShortcutPath), "The stale decoy should have been removed.");
        Assert.Equal(before.Files, VaultTestContext.Snapshot(ctx.FolderPath).Files);
    }

    [Fact]
    public void TwoCopiesPresent_RefusesToGuessAndAsksTheUser()
    {
        using var ctx = new VaultTestContext();
        VaultTestContext.BuildSampleTree(ctx.FolderPath);

        var (vault, _) = ctx.Service.Create(ctx.FolderPath, VaultMode.Fast, Password);
        ctx.Track(vault.Id);

        // Someone recreated the folder by hand while the real payload sits in the store.
        Directory.CreateDirectory(ctx.FolderPath);
        File.WriteAllText(Path.Combine(ctx.FolderPath, "different.txt"), "not the original");
        MarkInterrupted(ctx, vault, VaultState.Locking, JournalOperation.Lock);

        var result = ctx.Service.Recover(vault);

        Assert.True(result.NeedsUserDecision);
        Assert.Contains("will not guess", result.Summary);
        // Neither copy was touched.
        Assert.True(File.Exists(Path.Combine(ctx.FolderPath, "different.txt")));
        Assert.True(Directory.Exists(VaultLayout.Plain(VolumeStore.GetVaultStore(ctx.Root, vault.Id))));
    }

    [Fact]
    public void CorruptJournal_DoesNotPreventRecovery()
    {
        using var ctx = new VaultTestContext();
        VaultTestContext.BuildSampleTree(ctx.FolderPath);
        var before = VaultTestContext.Snapshot(ctx.FolderPath);

        var (vault, _) = ctx.Service.Create(ctx.FolderPath, VaultMode.Fast, Password);
        ctx.Track(vault.Id);
        var store = VolumeStore.GetVaultStore(ctx.Root, vault.Id);

        // Recovery reads the filesystem, so even unparseable garbage here is survivable.
        File.WriteAllText(Journal.PathFor(store), "{ this is not json");
        vault.State = VaultState.Locking;

        var result = ctx.Service.Recover(vault);

        Assert.Equal(VaultState.Locked, result.State);
        ctx.Service.Unlock(vault, VaultService.DeriveDek(vault, Password));
        Assert.Equal(before.Files, VaultTestContext.Snapshot(ctx.FolderPath).Files);
    }

    [Fact]
    public void StoreCopyOfMetadata_SurvivesLosingTheIndex()
    {
        using var ctx = new VaultTestContext();
        VaultTestContext.BuildSampleTree(ctx.FolderPath);
        var before = VaultTestContext.Snapshot(ctx.FolderPath);

        var (vault, _) = ctx.Service.Create(ctx.FolderPath, VaultMode.Secure, Password);
        ctx.Track(vault.Id);

        // Simulate a lost Windows profile: the DPAPI-wrapped index is gone entirely.
        File.Delete(ctx.Registry.IndexPath);
        Assert.Empty(ctx.Registry.Load());

        // The vault is still discoverable and openable from the drive alone.
        var discovered = VaultRegistry.DiscoverOnVolume(ctx.Root).Single(v => v.Id == vault.Id);
        Assert.Equal(vault.OriginalPath, discovered.OriginalPath);

        ctx.Service.Unlock(discovered, VaultService.DeriveDek(discovered, Password));
        Assert.Equal(before.Files, VaultTestContext.Snapshot(ctx.FolderPath).Files);
    }

    // ---- helpers ----

    private static void ShortcutFactoryDeleteDecoy(Vault vault) =>
        FolderVault.Core.Shell.ShortcutFactory.Delete(vault.ShortcutPath);

    /// <summary>Puts the vault into the state a crash would leave: transitional, journal present.</summary>
    private static void MarkInterrupted(VaultTestContext ctx, Vault vault, VaultState state,
        JournalOperation operation)
    {
        var store = VolumeStore.GetVaultStore(ctx.Root, vault.Id);
        Journal.Write(store, new JournalEntry
        {
            VaultId = vault.Id,
            Operation = operation,
            Mode = vault.Mode,
            OriginalPath = vault.OriginalPath,
            Step = "interrupted by test",
        });

        vault.State = state;
        ctx.Registry.Upsert(vault);
    }
}
