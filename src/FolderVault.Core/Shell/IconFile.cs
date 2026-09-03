using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace FolderVault.Core.Shell;

/// <summary>
/// Writes a multi-resolution <c>.ico</c> from a set of bitmaps.
///
/// Explorer picks the frame nearest the size it is drawing, so an icon that ships only one size
/// looks soft in every view that is not that size. Supplying the whole ladder from 16 to 256 keeps
/// the decoy crisp in the list, tile and extra-large-icon views alike.
///
/// Small frames are written as 32-bit BGRA DIBs. PNG-compressed frames are legal from Vista
/// onwards, but support for them at the small sizes is patchy across shell surfaces and a DIB is
/// understood everywhere.
///
/// The two large frames are the exception and go in as PNGs. Those sizes exist because
/// PNG-in-ICO was introduced for them, and every shell on a supported Windows reads them - while
/// as DIBs they are 256 KB and 64 KB, together nine tenths of the file. That mattered once this
/// artwork became the executable's embedded icon: uncompressed, it was larger than the
/// executable carrying it.
/// </summary>
public static class IconFile
{
    /// <summary>The sizes Explorer asks for across its view modes.</summary>
    public static readonly int[] StandardSizes = [16, 20, 24, 32, 48, 64, 128, 256];

    /// <summary>At and above this, frames are PNG-compressed rather than stored as raw DIBs.</summary>
    private const int PngFromSize = 128;

    /// <summary>
    /// Writes <paramref name="frames"/> to <paramref name="path"/>, smallest first. Each bitmap
    /// must be square and no larger than 256x256, which is the largest size the format can name.
    /// </summary>
    public static void Write(string path, IReadOnlyList<Bitmap> frames)
    {
        if (frames.Count is 0 or > ushort.MaxValue)
            throw new ArgumentException("An icon needs between 1 and 65535 frames.", nameof(frames));

        var images = frames.Select(Encode).ToArray();

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(stream);

        // ICONDIR
        writer.Write((ushort)0);             // reserved
        writer.Write((ushort)1);             // type: 1 = icon
        writer.Write((ushort)frames.Count);

        // ICONDIRENTRY per frame. The directory has a fixed size, so every offset is known up front.
        var offset = 6 + (16 * frames.Count);
        for (var i = 0; i < frames.Count; i++)
        {
            var size = frames[i].Width;
            writer.Write((byte)(size == 256 ? 0 : size)); // 0 means 256: the field is one byte
            writer.Write((byte)(size == 256 ? 0 : size));
            writer.Write((byte)0);           // palette entries: none, this is a true-colour frame
            writer.Write((byte)0);           // reserved
            writer.Write((ushort)1);         // colour planes
            writer.Write((ushort)32);        // bits per pixel
            writer.Write(images[i].Length);
            writer.Write(offset);
            offset += images[i].Length;
        }

        foreach (var image in images) writer.Write(image);
    }

    /// <summary>
    /// One frame: a PNG at the large sizes, and below them a bottom-up 32-bit DIB -
    /// BITMAPINFOHEADER, then the BGRA pixels, then the legacy 1-bit AND mask.
    /// </summary>
    private static byte[] Encode(Bitmap frame)
    {
        if (frame.Width != frame.Height)
            throw new ArgumentException($"Icon frames must be square; got {frame.Width}x{frame.Height}.");
        if (frame.Width > 256)
            throw new ArgumentException($"Icon frames cannot exceed 256 pixels; got {frame.Width}.");

        if (frame.Width >= PngFromSize)
        {
            using var png = new MemoryStream();
            frame.Save(png, ImageFormat.Png);
            return png.ToArray();
        }

        var size = frame.Width;
        var pixels = ReadBgra(frame);

        // Each mask row is padded to a 4-byte boundary, as every DIB row is.
        var maskStride = ((size + 31) / 32) * 4;

        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer);

        // BITMAPINFOHEADER. biHeight is doubled because it spans the colour data and the mask.
        writer.Write(40);                    // biSize
        writer.Write(size);                  // biWidth
        writer.Write(size * 2);              // biHeight
        writer.Write((ushort)1);             // biPlanes
        writer.Write((ushort)32);            // biBitCount
        writer.Write(0);                     // biCompression: BI_RGB
        writer.Write((size * size * 4) + (maskStride * size)); // biSizeImage
        writer.Write(0);                     // biXPelsPerMeter
        writer.Write(0);                     // biYPelsPerMeter
        writer.Write(0);                     // biClrUsed
        writer.Write(0);                     // biClrImportant

        // Colour data, bottom-up.
        for (var y = size - 1; y >= 0; y--)
            writer.Write(pixels, y * size * 4, size * 4);

        // AND mask, bottom-up. Modern Windows composites from the alpha channel and ignores this,
        // but the format requires it and older code paths still consult it, so derive it from
        // alpha: a set bit means "leave the background alone here".
        var maskRow = new byte[maskStride];
        for (var y = size - 1; y >= 0; y--)
        {
            Array.Clear(maskRow);
            for (var x = 0; x < size; x++)
            {
                var alpha = pixels[((y * size) + x) * 4 + 3];
                if (alpha == 0) maskRow[x / 8] |= (byte)(0x80 >> (x % 8));
            }
            writer.Write(maskRow);
        }

        writer.Flush();
        return buffer.ToArray();
    }

    /// <summary>The frame's pixels as top-down BGRA, whatever pixel format it arrived in.</summary>
    private static byte[] ReadBgra(Bitmap frame)
    {
        using var normalised = frame.PixelFormat == PixelFormat.Format32bppArgb
            ? null
            : new Bitmap(frame.Width, frame.Height, PixelFormat.Format32bppArgb);

        var source = frame;
        if (normalised is not null)
        {
            using var g = Graphics.FromImage(normalised);
            g.Clear(Color.Transparent);
            g.DrawImage(frame, 0, 0, frame.Width, frame.Height);
            source = normalised;
        }

        var rect = new Rectangle(0, 0, source.Width, source.Height);
        var data = source.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var bytes = new byte[source.Width * source.Height * 4];
            for (var y = 0; y < source.Height; y++)
                Marshal.Copy(data.Scan0 + (y * data.Stride), bytes, y * source.Width * 4, source.Width * 4);
            return bytes;
        }
        finally
        {
            source.UnlockBits(data);
        }
    }
}
