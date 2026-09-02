namespace FolderVault.UI;

/// <summary>
/// One place for the handful of colours, fonts and control factories the UI uses, so the prompt,
/// the manager window and the dialogs look like parts of the same app.
/// </summary>
public static class Theme
{
    public const string UiFontFamily = "Segoe UI";

    public static readonly Color Surface = Color.FromArgb(250, 250, 251);
    public static readonly Color Panel = Color.FromArgb(255, 255, 255);
    public static readonly Color Border = Color.FromArgb(206, 208, 214);
    public static readonly Color Text = Color.FromArgb(24, 26, 31);
    public static readonly Color Muted = Color.FromArgb(104, 110, 122);
    public static readonly Color Accent = Color.FromArgb(38, 96, 208);
    public static readonly Color Danger = Color.FromArgb(190, 44, 44);
    public static readonly Color Success = Color.FromArgb(28, 128, 74);

    public static Font Body(float size = 9f, FontStyle style = FontStyle.Regular) =>
        new(UiFontFamily, size, style);

    /// <summary>
    /// A label that wraps to <paramref name="width"/> and grows as tall as its text needs.
    ///
    /// WinForms defaults <see cref="Label.AutoSize"/> to true, which makes any Size you assign be
    /// ignored: the label lays its text out on a single unwrapped line and whatever does not fit
    /// is simply clipped by the form edge. Pairing AutoSize with a MaximumSize whose height is 0
    /// is the fix - it wraps at that width and measures its own height - so every piece of
    /// explanatory prose in this app is built through here rather than by hand.
    /// </summary>
    public static Label Wrapping(string text, int width, float size = 8.25f, Color? color = null,
        FontStyle style = FontStyle.Regular) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(width, 0),
        Font = Body(size, style),
        ForeColor = color ?? Muted,
        Margin = new Padding(0, 2, 0, 8),
    };

    /// <summary>A section heading, e.g. "Protection".</summary>
    public static Label Heading(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = Body(9f, FontStyle.Bold),
        ForeColor = Text,
        Margin = new Padding(0, 10, 0, 4),
    };

    /// <summary>
    /// A checkbox whose caption wraps instead of being cut off.
    ///
    /// CheckBox is worse than Label here: with AutoSize on it lays the caption out on one line
    /// and MaximumSize does not make it wrap, so a sentence-length caption is simply truncated.
    /// The only reliable fix is to turn AutoSize off and measure the wrapped text ourselves.
    /// The font is set explicitly because measuring has to use the same font the control will
    /// actually paint with, and inherited fonts are not applied until the control has a parent.
    /// </summary>
    public static CheckBox WrappingCheckBox(string text, int width, bool isChecked = false)
    {
        var font = Body();
        const int glyphWidth = 22; // box plus the gap before the caption

        var measured = TextRenderer.MeasureText(
            text, font, new Size(width - glyphWidth, 0), TextFormatFlags.WordBreak);

        return new CheckBox
        {
            Text = text,
            Checked = isChecked,
            Font = font,
            AutoSize = false,
            Width = width,
            Height = Math.Max(20, measured.Height + 6),
            Margin = new Padding(0, 6, 0, 4),
        };
    }

    /// <summary>A button that is always at least as wide as its own caption.</summary>
    public static Button Action(string text, bool primary = false)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(88, 30),
            Padding = new Padding(10, 0, 10, 0),
            Margin = new Padding(0, 0, 8, 0),
            FlatStyle = primary ? FlatStyle.Flat : FlatStyle.System,
        };

        if (primary)
        {
            button.BackColor = Accent;
            button.ForeColor = Color.White;
            button.Font = Body(9f, FontStyle.Bold);
            button.FlatAppearance.BorderSize = 0;
        }

        return button;
    }

    /// <summary>A single-line password box.</summary>
    public static TextBox Secret(string placeholder, int width) => new()
    {
        UseSystemPasswordChar = true,
        PlaceholderText = placeholder,
        Width = width,
        BorderStyle = BorderStyle.FixedSingle,
        Margin = new Padding(0, 0, 0, 6),
    };
}
