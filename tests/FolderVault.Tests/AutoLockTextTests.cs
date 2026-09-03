using FolderVault.Core.Model;
using FolderVault.UI;
using Xunit;

namespace FolderVault.Tests;

/// <summary>
/// The settings dialog reads the chosen rule back in plain words, and that sentence is the only
/// place its meaning is ever stated. If it is wrong, it is wrong in the most expensive way:
/// reassuring someone their folder re-locks when it does not.
/// </summary>
public class AutoLockTextTests
{
    [Theory]
    [InlineData(1, "1 minute")]
    [InlineData(15, "15 minutes")]
    [InlineData(59, "59 minutes")]
    [InlineData(60, "1 hour")]
    [InlineData(120, "2 hours")]
    [InlineData(480, "8 hours")]
    [InlineData(90, "1 hour 30 minutes")]
    [InlineData(125, "2 hours 5 minutes")]
    public void DurationsReadAsSomeoneWouldSayThem(int minutes, string expected) =>
        Assert.Equal(expected, AutoLockText.Duration(minutes));

    [Fact]
    public void NeverWithNothingElseProducesNoSentence()
    {
        // Null is the signal for "warn instead of describe". Returning a cheerful sentence here
        // would be the worst possible outcome.
        Assert.Null(AutoLockText.Sentence("Photos", AutoLockRule.Never, 15, onSessionLock: false));
        Assert.Contains("stay unlocked", AutoLockText.NothingWillLockIt("Photos"));
    }

    [Fact]
    public void NeverStillSaysSomethingWhenWindowsLockingIsOn()
    {
        Assert.Equal("“Photos” re-locks only when Windows locks, or when you lock it yourself.",
            AutoLockText.Sentence("Photos", AutoLockRule.Never, 15, onSessionLock: true));
    }

    [Fact]
    public void OnCloseReadsWithoutMentioningTime()
    {
        Assert.Equal("“Photos” re-locks as soon as you close its Explorer window.",
            AutoLockText.Sentence("Photos", AutoLockRule.OnClose, 15, onSessionLock: false));
    }

    [Fact]
    public void TheTimerSaysWhatCountsAsActivity()
    {
        // "without you touching it" was the old wording and it was a lie: reading a file inside
        // the folder does not reset anything, only writing does.
        Assert.Equal(
            "“Photos” re-locks as soon as 15 minutes pass without anything inside it changing.",
            AutoLockText.Sentence("Photos", AutoLockRule.OnTimer, 15, onSessionLock: false));
    }

    [Fact]
    public void EitherNamesBothHalves()
    {
        Assert.Equal(
            "“Photos” re-locks as soon as you close its Explorer window, or 30 minutes pass " +
            "without anything inside it changing.",
            AutoLockText.Sentence("Photos", AutoLockRule.Either, 30, onSessionLock: false));
    }

    [Fact]
    public void WindowsLockingIsAppendedToWhicheverRuleIsChosen()
    {
        Assert.Equal(
            "“Photos” re-locks as soon as you close its Explorer window, or Windows locks.",
            AutoLockText.Sentence("Photos", AutoLockRule.OnClose, 15, onSessionLock: true));
    }

    [Theory]
    [InlineData(0, false, false, "Stays open")]
    [InlineData(0, true, false, "Window closed")]
    [InlineData(120, false, false, "2 hours idle")]
    [InlineData(15, true, false, "Closed or 15 minutes")]
    [InlineData(15, true, true, "Closed or 15 minutes, Windows locked")]
    [InlineData(0, false, true, "Stays open, Windows locked")]
    public void TheListColumnFitsTheRuleIntoAColumn(int idle, bool onClose, bool onSession,
        string expected)
    {
        var vault = new Vault
        {
            OriginalPath = @"D:\A",
            IdleLockMinutes = idle,
            LockOnExplorerClose = onClose,
            LockOnSessionLock = onSession,
        };

        Assert.Equal(expected, AutoLockText.Summary(vault));
    }
}
