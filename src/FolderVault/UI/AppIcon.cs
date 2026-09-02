using System.Drawing.Drawing2D;

namespace FolderVault.UI;

/// <summary>
/// Draws the tray and window icon at runtime, so the project ships without binary assets and the
/// icon stays crisp at whatever size Windows asks for.
/// </summary>
public static class AppIcon
{
    private static readonly Dictionary<(int Size, bool Open), Icon> Cache = [];

    /// <summary>A padlock, shown open while any vault is unlocked so the tray reflects state.</summary>
    public static Icon Get(int size = 32, bool open = false)
    {
        if (Cache.TryGetValue((size, open), out var cached)) return cached;

        using var bitmap = new Bitmap(size, size);
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

        var icon = Icon.FromHandle(bitmap.GetHicon());
        Cache[(size, open)] = icon;
        return icon;
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
