using FolderVault.Core.Model;
using FolderVault.Core.Ops;
using FolderVault.Core.Store;
using Xunit;

namespace FolderVault.Tests;

/// <summary>
/// Removing protection has to be permanent.
///
/// It was not: the manager called the service directly, so the in-memory session created when the
/// folder was unlocked was never released. Its auto-lock timer kept running, and roughly half a
/// minute after the Explorer window closed it re-locked a folder the user had just unprotected -
/// recreating the store and the decoy shortcut with no interaction at all.
///
/// The session leak is fixed in the app layer; these tests pin the last-resort guard in the core,
/// so nothing holding a stale reference can resurrect a vault the registry has forgotten.
/// </summary>
public class RemovalStaysRemovedTests
{
    private const string Password = "correct horse battery staple";

    [Theory]
    [InlineData(VaultMode.Fast)]
    [InlineData(VaultMode.Secure)]
    public void AfterRemoval_ARelockAttemptIsRefused(VaultMode mode)
    {
        using var ctx = new VaultTestContext();
        VaultTestContext.BuildSampleTree(ctx.FolderPath);
        var before = VaultTestContext.Snapshot(ctx.FolderPath);

        var (vault, _) = ctx.Service.Create(ctx.FolderPath, mode, Password);
        ctx.Track(vault.Id);

        var dek = VaultService.DeriveDek(vault, Password);
        ctx.Service.Unlock(vault, dek);
        ctx.Service.RemoveProtection(vault);

        // This is the call a leaked auto-lock timer would make.
        var refused = Assert.Throws<VaultOperationException>(() => ctx.Service.Lock(vault, dek));
        Assert.Contains("no longer protected", refused.Message);

        // The folder is still an ordinary folder, with everything in it.
        Assert.True(Directory.Exists(ctx.FolderPath));
        Assert.False(File.Exists(vault.ShortcutPath), "No decoy shortcut should reappear.");
        Assert.False(Directory.Exists(VolumeStore.GetVaultStore(ctx.Root, vault.Id)),
            "No vault store should be recreated.");
        Assert.Equal(before.Files, VaultTestContext.Snapshot(ctx.FolderPath).Files);
    }

    [Fact]
    public void RemovingOneVault_DoesNotStopAnotherFromLocking()
    {
        using var ctx = new VaultTestContext();
        var first = Path.Combine(ctx.Root, "First");
        var second = Path.Combine(ctx.Root, "Second");
        VaultTestContext.BuildSampleTree(first);
        VaultTestContext.BuildSampleTree(second);

        var (a, _) = ctx.Service.Create(first, VaultMode.Fast, Password);
        var (b, _) = ctx.Service.Create(second, VaultMode.Fast, Password);
        ctx.Track(a.Id);
        ctx.Track(b.Id);

        ctx.Service.Unlock(a, VaultService.DeriveDek(a, Password));
        ctx.Service.RemoveProtection(a);

        // The guard keys off the vault id, so the surviving vault is unaffected.
        ctx.Service.Unlock(b, VaultService.DeriveDek(b, Password));
        ctx.Service.Lock(b);

        Assert.True(File.Exists(b.ShortcutPath));
        Assert.True(Directory.Exists(first), "The removed folder stays an ordinary folder.");
    }
}
