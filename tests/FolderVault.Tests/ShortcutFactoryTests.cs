using FolderVault.Core.Model;
using FolderVault.Core.Shell;
using Xunit;

namespace FolderVault.Tests;

/// <summary>
/// Covers the IShellLink COM interop. The vtable order in those interface declarations has to be
/// exact, and getting it wrong fails at runtime rather than at compile time - so it is worth a
/// test that actually round-trips a shortcut through the shell.
/// </summary>
public class ShortcutFactoryTests
{
    [Fact]
    public void CreatesAShortcutThatRoundTripsItsArguments()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fv-lnk", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var path = Path.Combine(directory, "Photos.lnk");
            var vaultId = Guid.NewGuid();

            ShortcutFactory.Create(path, Environment.ProcessPath!, $"--unlock {vaultId:N}", "Photos (locked)");

            Assert.True(File.Exists(path));
            Assert.Contains(vaultId.ToString("N"), ShortcutFactory.TryReadArguments(path));
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void DecoyIsNamedSoExplorerRendersItAsTheFolder()
    {
        var vault = new Vault { OriginalPath = @"D:\Stuff\Photos" };

        // Explorer registers NeverShowExt for lnkfile, so "Photos.lnk" displays as "Photos".
        Assert.Equal(@"D:\Stuff\Photos.lnk", vault.ShortcutPath);
        Assert.Equal("Photos", vault.DisplayName);
        Assert.Equal("Photos", Path.GetFileNameWithoutExtension(vault.ShortcutPath));
    }

    [Fact]
    public void DisplayNameSurvivesATrailingSeparator()
    {
        var vault = new Vault { OriginalPath = @"D:\Stuff\Photos\" };
        Assert.Equal("Photos", vault.DisplayName);
    }

    [Fact]
    public void FolderIconComesFromTheShellsOwnResource()
    {
        // Using imageres.dll rather than a bundled icon means the decoy matches whatever folder
        // icon the current Windows theme uses.
        var iconPath = Environment.ExpandEnvironmentVariables(ShortcutFactory.FolderIconLocation);
        Assert.True(File.Exists(iconPath), $"Expected the shell icon resource at {iconPath}.");
    }
}
