using FolderVault.Core.Model;
using Xunit;

namespace FolderVault.Tests;

/// <summary>
/// The rule the user picks and the two fields the poller reads have to mean the same thing in
/// both directions. A mistranslation here would show one rule in the dialog while the folder
/// followed another.
/// </summary>
public class AutoLockRuleTests
{
    [Theory]
    [InlineData(0, false, AutoLockRule.Never)]
    [InlineData(0, true, AutoLockRule.OnClose)]
    [InlineData(15, false, AutoLockRule.OnTimer)]
    [InlineData(15, true, AutoLockRule.Either)]
    public void EveryFieldCombinationHasAName(int idle, bool onClose, AutoLockRule expected) =>
        Assert.Equal(expected, AutoLockRules.Classify(idle, onClose));

    [Theory]
    [InlineData(AutoLockRule.Never)]
    [InlineData(AutoLockRule.OnClose)]
    [InlineData(AutoLockRule.OnTimer)]
    [InlineData(AutoLockRule.Either)]
    public void ARuleSurvivesBeingWrittenToFieldsAndReadBack(AutoLockRule rule)
    {
        var (idle, onClose) = AutoLockRules.Fields(rule, 42);
        Assert.Equal(rule, AutoLockRules.Classify(idle, onClose));
    }

    [Fact]
    public void SwitchingAwayFromATimerAndBackKeepsTheNumber()
    {
        // The spinner keeps its value while greyed out, so the round trip has to carry it too -
        // otherwise picking "when I close it" silently resets a carefully chosen 8 hours to 15.
        var (idle, _) = AutoLockRules.Fields(AutoLockRule.OnClose, 480);
        Assert.Equal(0, idle); // not stored while the rule ignores it

        var (restored, _) = AutoLockRules.Fields(AutoLockRule.OnTimer, 480);
        Assert.Equal(480, restored);
    }

    [Theory]
    [InlineData(AutoLockRule.OnTimer)]
    [InlineData(AutoLockRule.Either)]
    public void ATimerRuleNeverEndsUpWithoutATimer(AutoLockRule rule)
    {
        // Guards the path where a vault stored with 0 minutes is switched to a rule that needs
        // one: without the fallback it would classify straight back to Never or OnClose.
        var (idle, _) = AutoLockRules.Fields(rule, 0);

        Assert.Equal(AutoLockRules.DefaultIdleMinutes, idle);
        Assert.Equal(rule, AutoLockRules.Classify(idle, AutoLockRules.Fields(rule, 0).OnExplorerClose));
    }

    [Fact]
    public void TheDefaultsForANewVaultAreTheSafestRule()
    {
        var fresh = new Vault { OriginalPath = @"D:\A" };

        Assert.Equal(AutoLockRule.Either, fresh.RuleFor());
        Assert.True(fresh.LockOnSessionLock);
        Assert.False(fresh.StaysUnlocked);
    }
}
