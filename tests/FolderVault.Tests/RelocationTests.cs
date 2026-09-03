using FolderVault.Core.Model;
using FolderVault.Core.Ops;
using FolderVault.Core.Shell;
using Xunit;

namespace FolderVault.Tests;

/// <summary>
/// FolderVault has to survive being moved.
///
/// A decoy is a .lnk, and a .lnk stores an absolute path to its target. Rename the folder the
/// app lives in - or move it to Program Files, or reinstall it elsewhere - and every locked
/// folder is left standing in front of an executable that is no longer there: it still looks
/// like a folder, but double-clicking does nothing at all.
/// </summary>
public class RelocationTests
{
    private const string Password = "correct horse battery staple";

    [Fact]
    public void ADecoyPointingAtAMovedExe_IsRepaired()
    {
        using var ctx = new VaultTestContext();
        VaultTestContext.BuildSampleTree(ctx.FolderPath);

        var (vault, _) = ctx.Service.Create(ctx.FolderPath, VaultMode.Fast, Password);
        ctx.Track(vault.Id);

        // Stand in for the app having lived somewhere else when the folder was locked.
        var stalePath = Path.Combine(ctx.Root, "old-location", "FolderVault.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(stalePath)!);
        File.WriteAllBytes(stalePath, [0]);
        ShortcutFactory.Create(vault.ShortcutPath, stalePath, $"--unlock {vault.Id:N}", "stale");

        Assert.Equal(stalePath, ShortcutFactory.TryReadTarget(vault.ShortcutPath));

        Assert.Equal(DecoyRepair.Retargeted, ctx.Service.RepairDecoy(vault));
        Assert.Equal(VaultService.LauncherPath, ShortcutFactory.TryReadTarget(vault.ShortcutPath));

        // It still opens the right vault afterwards.
        Assert.Contains(vault.Id.ToString("N"), ShortcutFactory.TryReadArguments(vault.ShortcutPath));
    }

    [Fact]
    public void AnUpToDateDecoy_IsLeftAlone()
    {
        using var ctx = new VaultTestContext();
        VaultTestContext.BuildSampleTree(ctx.FolderPath);

        var (vault, _) = ctx.Service.Create(ctx.FolderPath, VaultMode.Fast, Password);
        ctx.Track(vault.Id);

        Assert.Equal(DecoyRepair.None, ctx.Service.RepairDecoy(vault));
    }

    [Fact]
    public void ADecoyDeletedByHand_IsRecreated()
    {
        using var ctx = new VaultTestContext();
        VaultTestContext.BuildSampleTree(ctx.FolderPath);

        var (vault, _) = ctx.Service.Create(ctx.FolderPath, VaultMode.Fast, Password);
        ctx.Track(vault.Id);

        File.Delete(vault.ShortcutPath);

        Assert.Equal(DecoyRepair.Retargeted, ctx.Service.RepairDecoy(vault));
        Assert.True(File.Exists(vault.ShortcutPath));
    }

    [Fact]
    public void UnlockedVaults_AreNotGivenADecoy()
    {
        using var ctx = new VaultTestContext();
        VaultTestContext.BuildSampleTree(ctx.FolderPath);

        var (vault, _) = ctx.Service.Create(ctx.FolderPath, VaultMode.Fast, Password);
        ctx.Track(vault.Id);
        ctx.Service.Unlock(vault, VaultService.DeriveDek(vault, Password));

        Assert.Equal(DecoyRepair.None, ctx.Service.RepairDecoy(vault));
        Assert.False(File.Exists(vault.ShortcutPath), "An unlocked folder must not sprout a shortcut.");
    }

    [Fact]
    public void ADecoyFromAnOlderBuild_GetsTheCurrentIcon()
    {
        using var ctx = new VaultTestContext();
        VaultTestContext.BuildSampleTree(ctx.FolderPath);

        var (vault, _) = ctx.Service.Create(ctx.FolderPath, VaultMode.Fast, Password);
        ctx.Track(vault.Id);

        // What an older build wrote: the right target, but the bare stock folder icon.
        ShortcutFactory.CreateWithIcon(vault.ShortcutPath, VaultService.LauncherPath,
            $"--unlock {vault.Id:N}", "old build",
            Environment.ExpandEnvironmentVariables(DecoyIcon.StockFolderIconLocation),
            DecoyIcon.StockFolderIconIndex);

        Assert.Equal(DecoyRepair.Reiconed, ctx.Service.RepairDecoy(vault));

        var icon = ShortcutFactory.TryReadIconLocation(vault.ShortcutPath);
        Assert.Equal(DecoyIcon.Path, icon!.Value.Location, ignoreCase: true);

        // Re-pointing an executable is worth a notification; a refreshed icon is not.
        Assert.DoesNotContain(vault.Id, ctx.Service.RepairAllDecoys().Select(v => v.Id));
    }

    [Fact]
    public void RepairAllDecoys_FixesEveryLockedVaultAtOnce()
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

        var stale = Path.Combine(ctx.Root, "elsewhere.exe");
        File.WriteAllBytes(stale, [0]);
        ShortcutFactory.Create(a.ShortcutPath, stale, $"--unlock {a.Id:N}", "stale");
        ShortcutFactory.Create(b.ShortcutPath, stale, $"--unlock {b.Id:N}", "stale");

        var repaired = ctx.Service.RepairAllDecoys();

        Assert.Equal(2, repaired.Count);
        Assert.Equal(VaultService.LauncherPath, ShortcutFactory.TryReadTarget(a.ShortcutPath));
        Assert.Equal(VaultService.LauncherPath, ShortcutFactory.TryReadTarget(b.ShortcutPath));
    }
}
