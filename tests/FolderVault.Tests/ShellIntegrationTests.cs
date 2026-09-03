using FolderVault.Core.Shell;
using Xunit;

namespace FolderVault.Tests;

/// <summary>
/// Guards the two shell tweaks that reach outside FolderVault's own files: the system-wide
/// shortcut arrow override, and desktop icon positions.
/// </summary>
public class ShellIntegrationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HidingTheArrowNeverRemovesIsShortcut(bool suppressed)
    {
        // The regression this exists for: FolderVault used to hide the arrow by deleting
        // IsShortcut from lnkfile, which stops the shell resolving a .lnk to its target and
        // breaks every taskbar pin on the machine with "this file does not have an app
        // associated with it". The arrow is now blanked through the Shell Icons override
        // instead, and lnkfile is only ever written to, never deleted from.
        var command = ShellArrowOverlay.BuildCommand(suppressed);

        Assert.DoesNotContain("delete", command[..command.IndexOf('&')], StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"reg add ""HKLM\Software\Classes\lnkfile"" /v IsShortcut", command);
        Assert.DoesNotContain(@"reg delete ""HKLM\Software\Classes\lnkfile""", command);
    }

    [Fact]
    public void HidingTheArrowPointsTheOverlayAtABlankIcon()
    {
        var command = ShellArrowOverlay.BuildCommand(suppressed: true);

        Assert.Contains(@"Explorer\Shell Icons"" /v 29", command);
        Assert.Contains(@"shell32.dll,50", command);
    }

    [Fact]
    public void ShowingTheArrowRemovesOnlyTheOverride()
    {
        var command = ShellArrowOverlay.BuildCommand(suppressed: false);

        Assert.Contains(@"reg delete ""HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Shell Icons"" /v 29",
            command);
        Assert.Contains("IsShortcut", command);
    }

    [Fact]
    public void ReadingTheCurrentStateNeverThrows()
    {
        // Both read HKLM, which is readable but not writable without elevation; they must cope
        // with a locked-down machine rather than taking the manager window down with them.
        _ = ShellArrowOverlay.IsSuppressed();
        _ = ShellArrowOverlay.TaskbarPinsAreBroken();
    }

    [Fact]
    public void DesktopItemsAreRecognisedByTheirParent()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

        Assert.True(DesktopIcons.IsOnDesktop(Path.Combine(desktop, "Photos")));
        Assert.True(DesktopIcons.IsOnDesktop(Path.Combine(desktop, "Photos.lnk")));

        // A folder nested below the desktop is an ordinary folder item, not a desktop icon.
        Assert.False(DesktopIcons.IsOnDesktop(Path.Combine(desktop, "Album", "Photos")));
        Assert.False(DesktopIcons.IsOnDesktop(@"C:\Windows"));
    }

    [Fact]
    public void PositioningIsASilentNoOpAwayFromTheDesktop()
    {
        // Position preservation is a cosmetic nicety layered onto lock and unlock. If it ever
        // threw, it would fail an operation that had already succeeded on disk.
        var elsewhere = Path.Combine(Path.GetTempPath(), "fv-not-a-desktop-item");

        Assert.Null(DesktopIcons.TryGetPosition(elsewhere));
        Assert.False(DesktopIcons.TryPlaceAt(elsewhere, new System.Drawing.Point(10, 10)));
    }
}
