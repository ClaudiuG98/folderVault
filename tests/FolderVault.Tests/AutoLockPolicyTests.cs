using FolderVault.App;
using FolderVault.Core.Model;
using FolderVault.Services;
using Xunit;

namespace FolderVault.Tests;

/// <summary>
/// The auto-lock policy decides when a folder stops being readable. Every bug in here fails in
/// the same direction - a folder staying open longer than the user asked - and does so silently,
/// so the combinations are pinned down rather than left to the UI to get right.
/// </summary>
public class AutoLockPolicyTests
{
    private static UnlockedSession Session(Vault vault) => new(vault, new byte[32]);

    private static (AutoLockService Service, List<(Vault Vault, AutoLockReason Reason)> Locked)
        ServiceFor(params Vault[] vaults)
    {
        var sessions = vaults.Select(Session).ToList();
        var locked = new List<(Vault, AutoLockReason)>();

        var service = new AutoLockService(() => sessions);
        service.LockRequested += (session, reason) => locked.Add((session.Vault, reason));

        return (service, locked);
    }

    [Fact]
    public void LockingWindowsSkipsFoldersThatOptedOut()
    {
        var optedIn = new Vault { OriginalPath = @"D:\A", LockOnSessionLock = true };
        var optedOut = new Vault { OriginalPath = @"D:\B", LockOnSessionLock = false };

        var (service, locked) = ServiceFor(optedIn, optedOut);
        using (service) service.LockAll(AutoLockReason.SessionLocked);

        Assert.Equal([optedIn], locked.Select(l => l.Vault));
    }

    [Fact]
    public void SigningOutLocksEverythingRegardlessOfPolicy()
    {
        // There is no "later" once Windows is gone, so this rule is not opt-out. Getting it wrong
        // would leave a folder's files sitting in plaintext on a powered-off disk.
        var optedOut = new Vault { OriginalPath = @"D:\B", LockOnSessionLock = false };
        var neverLocks = new Vault
        {
            OriginalPath = @"D:\C",
            IdleLockMinutes = 0,
            LockOnExplorerClose = false,
            LockOnSessionLock = false,
        };

        var (service, locked) = ServiceFor(optedOut, neverLocks);
        using (service) service.LockAll(AutoLockReason.WindowsClosing);

        Assert.Equal(2, locked.Count);
        Assert.All(locked, entry => Assert.Equal(AutoLockReason.WindowsClosing, entry.Reason));
    }

    [Theory]
    [InlineData(0, false)]   // disabled
    [InlineData(-5, false)]  // nonsense value must not mean "lock constantly"
    public void AnIdleTimeoutOfZeroOrLessNeverExpires(int minutes, bool expected)
    {
        var session = Session(new Vault { OriginalPath = @"D:\A", IdleLockMinutes = minutes });
        Assert.Equal(expected, session.IdleTimeoutExpired);
    }

    [Fact]
    public void AFreshlyOpenedVaultIsNotAlreadyIdle()
    {
        var session = Session(new Vault { OriginalPath = @"D:\A", IdleLockMinutes = 1 });
        Assert.False(session.IdleTimeoutExpired);
    }

    [Fact]
    public void StaysUnlockedIsOnlyTrueWhenEveryRuleIsOff()
    {
        Assert.True(new Vault
        {
            IdleLockMinutes = 0,
            LockOnExplorerClose = false,
            LockOnSessionLock = false,
        }.StaysUnlocked);

        Assert.False(new Vault { IdleLockMinutes = 5, LockOnExplorerClose = false, LockOnSessionLock = false }.StaysUnlocked);
        Assert.False(new Vault { IdleLockMinutes = 0, LockOnExplorerClose = true, LockOnSessionLock = false }.StaysUnlocked);
        Assert.False(new Vault { IdleLockMinutes = 0, LockOnExplorerClose = false, LockOnSessionLock = true }.StaysUnlocked);
    }

    [Fact]
    public void TheDefaultsForANewVaultAreTheCautiousOnes()
    {
        // A folder nobody configured must not be one that stays readable indefinitely.
        var fresh = new Vault { OriginalPath = @"D:\A" };

        Assert.False(fresh.StaysUnlocked);
        Assert.True(fresh.IdleLockMinutes > 0);
        Assert.True(fresh.LockOnExplorerClose);
        Assert.True(fresh.LockOnSessionLock);
    }

    [Fact]
    public void ThePolicySurvivesBeingSavedAndReloaded()
    {
        using var ctx = new VaultTestContext();
        VaultTestContext.BuildSampleTree(ctx.FolderPath);

        var (vault, _) = ctx.Service.Create(ctx.FolderPath, VaultMode.Fast, "correct horse battery staple");
        ctx.Track(vault.Id);

        vault.IdleLockMinutes = 240;
        vault.LockOnExplorerClose = false;
        vault.LockOnSessionLock = false;
        ctx.Registry.Upsert(vault);

        var reloaded = ctx.Registry.Load().Single(v => v.Id == vault.Id);
        Assert.Equal(240, reloaded.IdleLockMinutes);
        Assert.False(reloaded.LockOnExplorerClose);
        Assert.False(reloaded.LockOnSessionLock);
    }
}
