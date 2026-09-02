using FolderVault.Core.Model;
using FolderVault.Core.Ops;
using FolderVault.Core.Store;
using Xunit;

namespace FolderVault.Tests;

public class RemoveProtectionTests
{
    private const string Password = "correct horse battery staple";

    [Theory]
    [InlineData(VaultMode.Fast)]
    [InlineData(VaultMode.Secure)]
    public void RemovingProtection_LeavesAnOrdinaryFolderAndForgetsTheVault(VaultMode mode)
    {
        using var ctx = new VaultTestContext();
        VaultTestContext.BuildSampleTree(ctx.FolderPath);
        var before = VaultTestContext.Snapshot(ctx.FolderPath);

        var (vault, _) = ctx.Service.Create(ctx.FolderPath, mode, Password);
        ctx.Track(vault.Id);

        ctx.Service.Unlock(vault, VaultService.DeriveDek(vault, Password));
        ctx.Service.RemoveProtection(vault);

        Assert.True(Directory.Exists(ctx.FolderPath), "The folder itself must survive.");
        Assert.Equal(before.Files, VaultTestContext.Snapshot(ctx.FolderPath).Files);
        Assert.False(File.Exists(vault.ShortcutPath));
        Assert.False(Directory.Exists(VolumeStore.GetVaultStore(ctx.Root, vault.Id)));
        Assert.DoesNotContain(ctx.Registry.Load(), v => v.Id == vault.Id);
    }

    [Fact]
    public void RemovingProtection_WhileLocked_IsRefusedRatherThanLosingTheStore()
    {
        using var ctx = new VaultTestContext();
        VaultTestContext.BuildSampleTree(ctx.FolderPath);

        var (vault, _) = ctx.Service.Create(ctx.FolderPath, VaultMode.Fast, Password);
        ctx.Track(vault.Id);

        // Deleting the store here would destroy the only copy of the data.
        Assert.Throws<VaultOperationException>(() => ctx.Service.RemoveProtection(vault));
        Assert.True(Directory.Exists(VaultLayout.Plain(VolumeStore.GetVaultStore(ctx.Root, vault.Id))));
    }

    [Fact]
    public void RemovingProtection_AfterAnUnlockThatLeftAStaleJournal_StillSucceeds()
    {
        using var ctx = new VaultTestContext();
        VaultTestContext.BuildSampleTree(ctx.FolderPath);

        var (vault, _) = ctx.Service.Create(ctx.FolderPath, VaultMode.Fast, Password);
        ctx.Track(vault.Id);
        ctx.Service.Unlock(vault, VaultService.DeriveDek(vault, Password));

        var store = VolumeStore.GetVaultStore(ctx.Root, vault.Id);
        Journal.Write(store, new JournalEntry { VaultId = vault.Id, OriginalPath = vault.OriginalPath });

        ctx.Service.RemoveProtection(vault);
        Assert.False(Directory.Exists(store));
    }

    /// <summary>
    /// The manager window hands back a Vault deserialized from the index, not the instance the
    /// service last touched. Removal must key off the id, not object identity.
    /// </summary>
    [Fact]
    public void RemovingProtection_WorksOnAFreshlyLoadedVaultInstance()
    {
        using var ctx = new VaultTestContext();
        VaultTestContext.BuildSampleTree(ctx.FolderPath);

        var (created, _) = ctx.Service.Create(ctx.FolderPath, VaultMode.Fast, Password);
        ctx.Track(created.Id);
        ctx.Service.Unlock(created, VaultService.DeriveDek(created, Password));

        var reloaded = ctx.Registry.Load().Single(v => v.Id == created.Id);
        Assert.Equal(VaultState.Unlocked, reloaded.State);

        ctx.Service.RemoveProtection(reloaded);
        Assert.Empty(ctx.Registry.Load());
    }
}
