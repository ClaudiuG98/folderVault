using FolderVault.Core.Shell;
using Xunit;

namespace FolderVault.Tests;

/// <summary>
/// Guards the only two places FolderVault reaches outside its own files: repairing the
/// machine-wide damage older versions did, and desktop icon positions.
/// </summary>
public class ShellIntegrationTests
{
    [Fact]
    public void RepairRestoresIsShortcutAndNeverDeletesIt()
    {
        // The first regression this exists for: FolderVault used to hide the shortcut arrow by
        // deleting IsShortcut from lnkfile, which stops the shell resolving a .lnk to its target
        // and breaks every taskbar pin on the machine with "this file does not have an app
        // associated with it". lnkfile is only ever written to, never deleted from.
        var command = ShellTweakRepair.BuildCommand();

        Assert.Contains(@"reg add ""HKLM\Software\Classes\lnkfile"" /v IsShortcut", command);
        Assert.DoesNotContain(@"reg delete ""HKLM\Software\Classes\lnkfile""", command);
    }

    [Fact]
    public void RepairRemovesTheShortcutOverlayOverride()
    {
        // The second: pointing shell icon override 29 at a blank icon, which Explorer draws as a
        // solid black block over every shortcut on the PC rather than as nothing.
        var command = ShellTweakRepair.BuildCommand();

        Assert.Contains(
            @"reg delete ""HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Shell Icons"" /v 29",
            command);
    }

    [Fact]
    public void RepairNeverInstallsAnOverlayIconOfItsOwn()
    {
        // The whole point of the rewrite: there is no longer any code path that sets override 29.
        // A blank icon cannot be made to work - Explorer blackens the slot at the small icon size
        // whatever the file contains - so writing one is always a regression.
        var command = ShellTweakRepair.BuildCommand();

        Assert.DoesNotContain(@"Shell Icons"" /v 29 /t", command);
        Assert.DoesNotContain(".ico", command, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("shell32.dll", command, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadingTheCurrentStateNeverThrows()
    {
        // All three read HKLM, which is readable but not writable without elevation; they must
        // cope with a locked-down machine rather than taking the manager window down with them.
        _ = ShellTweakRepair.ShortcutIconsAreBlacked();
        _ = ShellTweakRepair.TaskbarPinsAreBroken();
        _ = ShellTweakRepair.NeedsRepair();
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
