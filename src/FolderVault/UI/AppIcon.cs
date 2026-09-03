using System.Drawing.Drawing2D;
using FolderVault.Core.Shell;

namespace FolderVault.UI;

/// <summary>
/// Draws the tray and window icon at runtime, so the icon stays crisp at whatever size Windows
/// asks for and there is one definition of what the app looks like.
///
/// <para>The executable's own icon - the one Explorer shows for <c>FolderVault.exe</c>, and the
/// one a taskbar pin keeps - cannot be drawn at runtime: Windows reads it out of the file's
/// resources before anything runs. That icon is a real <c>.ico</c> committed at
/// <c>src/FolderVault/app.ico</c> and named by <c>ApplicationIcon</c> in the project file. It is
/// this same drawing, written out by <see cref="WriteIcoFile"/>, so the two cannot drift; if the
/// artwork below changes, regenerate it.</para>
/// </summary>
public static class AppIcon
{
    private static readonly Dictionary<(int Size, bool Open), Icon> Cache = [];

    /// <summary>A padlock, shown open while any vault is unlocked so the tray reflects state.</summary>
    public static Icon Get(int size = 32, bool open = false)
    {
        if (Cache.TryGetValue((size, open), out var cached)) return cached;

        using var bitmap = Render(size, open);
        var icon = Icon.FromHandle(bitmap.GetHicon());
        Cache[(size, open)] = icon;
        return icon;
    }

    /// <summary>
    /// The padlock as a bitmap at one size. Separate from <see cref="Get"/> because an
    /// <see cref="Icon"/> built from an HICON cannot be read back as pixels, and writing an
    /// <c>.ico</c> file needs the pixels.
    /// </summary>
    public static Bitmap Render(int size, bool open = false)
    {
        var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            var scale = size / 32f;
            var body = new RectangleF(7 * scale, 14 * scale, 18 * scale, 14 * scale);

            // Shackle: a half-ring above the body, tilted up on one side when open.
            using var shacklePen = new Pen(open ? Theme.Success : Theme.Accent, 3.2f * scale)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
            };
            var shackle = new RectangleF(11 * scale, 5 * scale, 10 * scale, 12 * scale);
            if (open) shackle.Offset(5.5f * scale, -1.5f * scale);
            g.DrawArc(shacklePen, shackle, 180, 180);

            using var bodyBrush = new SolidBrush(open ? Theme.Success : Theme.Accent);
            using var path = RoundedRect(body, 3.5f * scale);
            g.FillPath(bodyBrush, path);

            // Keyhole.
            using var holeBrush = new SolidBrush(Color.White);
            var hole = 4.2f * scale;
            g.FillEllipse(holeBrush, body.X + (body.Width - hole) / 2, body.Y + 3.4f * scale, hole, hole);
        }

        return bitmap;
    }

    /// <summary>
    /// Writes the closed padlock to <paramref name="path"/> as a multi-resolution <c>.ico</c>, for
    /// use as the executable's <c>ApplicationIcon</c>.
    ///
    /// Run this after changing the artwork above, then rebuild:
    /// <code>AppIcon.WriteIcoFile(@"src\FolderVault\app.ico");</code>
    /// The closed padlock is deliberate - the file icon says what the app is, not what state some
    /// folder happens to be in.
    /// </summary>
    public static void WriteIcoFile(string path)
    {
        var frames = IconFile.StandardSizes.Select(size => Render(size)).ToList();
        try
        {
            IconFile.Write(path, frames);
        }
        finally
        {
            foreach (var frame in frames) frame.Dispose();
        }
    }

    private static GraphicsPath RoundedRect(RectangleF bounds, float radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();

        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();

        return path;
    }
}
