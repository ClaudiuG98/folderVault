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
        // Compositing over imageres.dll rather than bundling artwork means the decoy matches
        // whatever folder icon the current Windows build uses.
        var iconPath = Environment.ExpandEnvironmentVariables(DecoyIcon.StockFolderIconLocation);
        Assert.True(File.Exists(iconPath), $"Expected the shell icon resource at {iconPath}.");
    }

    [Fact]
    public void DecoyIconIsGeneratedWithEverySizeExplorerAsksFor()
    {
        DecoyIcon.Ensure();
        Assert.True(File.Exists(DecoyIcon.Path), $"Expected a generated icon at {DecoyIcon.Path}.");

        // Read the ICONDIR back rather than trusting the writer: a malformed directory is the one
        // failure mode that leaves Explorer showing a blank decoy with no other symptom.
        using var stream = File.OpenRead(DecoyIcon.Path);
        using var reader = new BinaryReader(stream);

        Assert.Equal(0, reader.ReadUInt16());          // reserved
        Assert.Equal(1, reader.ReadUInt16());          // type: icon
        var count = reader.ReadUInt16();
        Assert.Equal(IconFile.StandardSizes.Length, count);

        foreach (var expected in IconFile.StandardSizes)
        {
            var width = reader.ReadByte();
            Assert.Equal(expected == 256 ? 0 : expected, width); // 0 is how the format spells 256
            reader.ReadBytes(3);                                 // height, palette, reserved
            Assert.Equal(1, reader.ReadUInt16());                // planes
            Assert.Equal(32, reader.ReadUInt16());               // bits per pixel
            Assert.True(reader.ReadInt32() > 0);                 // bytes in resource
            Assert.True(reader.ReadInt32() >= 6 + (16 * count)); // offset past the directory
        }
    }

    [Fact]
    public void LargeIconFramesAreCompressedAndSmallOnesAreNot()
    {
        // The 128 and 256 frames go in as PNGs; everything below stays a raw DIB, where support
        // is universal. Uncompressed, those two alone were nine tenths of the file - which is why
        // the application icon was once larger than the executable it is embedded in.
        DecoyIcon.Ensure();

        using var stream = File.OpenRead(DecoyIcon.Path);
        using var reader = new BinaryReader(stream);

        reader.ReadBytes(4);                       // reserved + type
        var count = reader.ReadUInt16();

        var frames = new List<(int Size, int Length, int Offset)>();
        for (var i = 0; i < count; i++)
        {
            var width = reader.ReadByte();
            reader.ReadBytes(7);                   // height, palette, reserved, planes, bpp
            frames.Add((width == 0 ? 256 : width, reader.ReadInt32(), reader.ReadInt32()));
        }

        byte[] PngMagic = [0x89, (byte)'P', (byte)'N', (byte)'G'];

        foreach (var (size, _, offset) in frames)
        {
            stream.Position = offset;
            var head = reader.ReadBytes(4);

            if (size >= 128)
            {
                Assert.True(head.SequenceEqual(PngMagic), $"The {size}px frame should be a PNG.");
            }
            else
            {
                // A DIB frame opens with BITMAPINFOHEADER.biSize, which is always 40.
                Assert.Equal(40, BitConverter.ToInt32(head));
            }
        }
    }

    [Fact]
    public void DecoyShortcutWearsTheGeneratedIcon()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fv-lnk", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var shortcut = Path.Combine(directory, "Photos.lnk");
            ShortcutFactory.Create(shortcut, Environment.ProcessPath!, "--unlock test", "Photos");

            var icon = ShortcutFactory.TryReadIconLocation(shortcut);
            Assert.NotNull(icon);
            Assert.Equal(DecoyIcon.Path, icon.Value.Location, ignoreCase: true);
            Assert.Equal(0, icon.Value.Index);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }
}
