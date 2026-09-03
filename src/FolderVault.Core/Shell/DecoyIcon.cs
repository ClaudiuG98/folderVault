using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using FolderVault.Core.Store;

namespace FolderVault.Core.Shell;

/// <summary>
/// The icon a locked folder's decoy wears: the stock Windows folder with a padlock badge on it.
///
/// Explorer draws the shortcut arrow itself, over the bottom-left corner of whatever icon an item
/// carries, and nothing a <c>.lnk</c> can say will stop it. So the badge sits bottom-<i>right</i>,
/// where the two never collide - and where it reads as "this folder is locked" rather than as a
/// rendering accident. <see cref="ShellArrowOverlay"/> can additionally replace the arrow itself,
/// but that is a system-wide change and stays opt-in; this badge costs nothing and is always on.
///
/// The file is composited at run time from the user's own <c>imageres.dll</c> and cached under
/// <c>%LOCALAPPDATA%\FolderVault</c>. Generating rather than shipping it keeps the project free of
/// binary assets, matches whatever folder art the installed Windows build uses, and means the
/// padlock is drawn at every size Explorer asks for instead of being scaled from one.
/// </summary>
public static class DecoyIcon
{
    /// <summary>The stock closed-folder icon Explorer itself uses, and its index.</summary>
    public const string StockFolderIconLocation = @"%SystemRoot%\System32\imageres.dll";

    public const int StockFolderIconIndex = 3;

    /// <summary>
    /// Bumped whenever the artwork changes. It is part of the file name, so an upgraded
    /// FolderVault writes a new file instead of trying to decide whether a cached one is stale -
    /// and Explorer, which caches icons by path, sees a path it has never rendered before.
    /// </summary>
    private const int ArtworkVersion = 2;

    private static readonly object Gate = new();

    /// <summary>Where the generated icon lives once <see cref="Ensure"/> has run.</summary>
    public static string Path => System.IO.Path.Combine(
        VaultRegistry.DefaultDirectory, $"folder-locked.v{ArtworkVersion}.ico");

    /// <summary>
    /// The icon a decoy should use, as a (location, index) pair for <c>IShellLink</c>.
    ///
    /// Falls back to the stock folder icon if the badged one cannot be written - a decoy that
    /// looks like a plain folder is a cosmetic loss, and never worth failing a lock over.
    /// </summary>
    public static (string Location, int Index) ForShortcut()
    {
        try
        {
            Ensure();
            return (Path, 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                       ExternalException or InvalidOperationException)
        {
            return (Environment.ExpandEnvironmentVariables(StockFolderIconLocation), StockFolderIconIndex);
        }
    }

    /// <summary>Writes the icon file if it is not already there. Cheap to call repeatedly.</summary>
    public static void Ensure()
    {
        if (File.Exists(Path)) return;

        lock (Gate)
        {
            if (File.Exists(Path)) return;

            Directory.CreateDirectory(VaultRegistry.DefaultDirectory);

            var frames = new List<Bitmap>();
            try
            {
                foreach (var size in IconFile.StandardSizes) frames.Add(RenderBadgedFolder(size));

                // Write via a temporary file: a half-written .ico that Explorer has already cached
                // would stay broken until the artwork version changed.
                var temporary = Path + ".partial";
                IconFile.Write(temporary, frames);
                File.Move(temporary, Path, overwrite: true);
            }
            finally
            {
                foreach (var frame in frames) frame.Dispose();
            }
        }
    }

    /// <summary>The stock folder at <paramref name="size"/> with the padlock badge composited on.</summary>
    private static Bitmap RenderBadgedFolder(int size)
    {
        var bitmap = new Bitmap(size, size);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.Clear(Color.Transparent);

        DrawStockFolder(g, size);

        // The badge occupies the bottom-right quadrant, a little proud of the edge so it reads as
        // applied to the folder rather than contained by it.
        var badge = size * 0.56f;
        DrawPadlock(g, new RectangleF(size - badge, size - badge, badge, badge));

        return bitmap;
    }

    /// <summary>
    /// Draws Explorer's own folder icon, asked for at exactly the size being rendered so Windows
    /// picks its hand-tuned frame rather than scaling a larger one.
    /// </summary>
    private static void DrawStockFolder(Graphics g, int size)
    {
        var source = Environment.ExpandEnvironmentVariables(StockFolderIconLocation);
        var handles = new nint[1];
        var ids = new int[1];

        var extracted = PrivateExtractIcons(source, StockFolderIconIndex, size, size, handles, ids, 1, 0);
        if (extracted <= 0 || handles[0] == nint.Zero)
            throw new InvalidOperationException(
                $"Could not read the stock folder icon from '{source}'.");

        try
        {
            using var icon = Icon.FromHandle(handles[0]);
            using var bitmap = icon.ToBitmap();
            g.DrawImage(bitmap, new Rectangle(0, 0, size, size));
        }
        finally
        {
            DestroyIcon(handles[0]);
        }
    }

    /// <summary>
    /// A closed padlock, drawn to fill <paramref name="bounds"/>. Shares its proportions with the
    /// tray icon so the app reads as one thing, but carries a light outline: this one has to stay
    /// legible against the gold of the folder behind it, and at 16 pixels the badge is barely
    /// nine pixels across.
    /// </summary>
    public static void DrawPadlock(Graphics g, RectangleF bounds)
    {
        var scale = bounds.Width / 32f;
        var body = new RectangleF(
            bounds.X + (7 * scale), bounds.Y + (14 * scale), 18 * scale, 14 * scale);

        var shackle = new RectangleF(
            bounds.X + (11 * scale), bounds.Y + (5.5f * scale), 10 * scale, 12 * scale);

        // Halo first, so the whole badge separates from the folder no matter what it sits on.
        using var halo = new Pen(Color.FromArgb(235, 255, 255, 255), 5f * scale)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };
        g.DrawArc(halo, shackle, 180, 180);
        using (var haloPath = RoundedRect(body, 3.5f * scale))
        {
            g.DrawPath(halo, haloPath);
        }

        using var shacklePen = new Pen(Steel, 3.2f * scale)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        g.DrawArc(shacklePen, shackle, 180, 180);

        using var bodyBrush = new SolidBrush(Steel);
        using var path = RoundedRect(body, 3.5f * scale);
        g.FillPath(bodyBrush, path);

        // The keyhole is dropped below roughly 24 pixels of badge: at that size it stops being a
        // keyhole and starts being a stray pale pixel that makes the body look damaged.
        if (bounds.Width < 24) return;

        using var holeBrush = new SolidBrush(Color.White);
        var hole = 4.2f * scale;
        g.FillEllipse(holeBrush, body.X + ((body.Width - hole) / 2), body.Y + (3.4f * scale), hole, hole);
    }

    /// <summary>Matches Theme.Accent in the UI layer; duplicated because Core owns no palette.</summary>
    private static readonly Color Steel = Color.FromArgb(38, 96, 208);

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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int PrivateExtractIcons(string szFileName, int nIconIndex, int cxIcon,
        int cyIcon, nint[] phicon, int[] piconid, int nIcons, uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint hIcon);
}
