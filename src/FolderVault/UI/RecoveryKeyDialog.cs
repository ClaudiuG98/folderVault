namespace FolderVault.UI;

/// <summary>
/// Shows a freshly generated recovery key. This is the only time it is ever displayed: only a
/// key-wrapping blob is stored, so FolderVault genuinely cannot show it again later.
///
/// The dialog therefore refuses to close until the user confirms they have saved it - the one
/// place in the app where nagging is justified, because the alternative is unrecoverable data.
/// </summary>
public sealed class RecoveryKeyDialog : Form
{
    private const int ContentWidth = 440;

    public RecoveryKeyDialog(string folderName, string recoveryKey)
    {
        Text = "Save your recovery key";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = Theme.Surface;
        Font = Theme.Body();
        AutoScaleMode = AutoScaleMode.Font;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var heading = Theme.Wrapping($"Recovery key for {folderName}", ContentWidth, 11f, Theme.Text, FontStyle.Bold);

        var explanation = Theme.Wrapping(
            "This opens the folder if you ever forget the password. It is shown once and cannot be " +
            "shown again - FolderVault keeps no copy it can read. Store it somewhere separate from " +
            "the folder itself.",
            ContentWidth);

        var keyBox = new TextBox
        {
            Text = recoveryKey,
            ReadOnly = true,
            Multiline = true,
            Width = ContentWidth,
            Height = 52,
            Font = new Font("Consolas", 11.5f),
            TextAlign = HorizontalAlignment.Center,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Theme.Panel,
            Margin = new Padding(0, 4, 0, 8),
        };
        keyBox.Select(0, 0);

        var copy = Theme.Action("Copy to clipboard");
        copy.Click += (_, _) =>
        {
            Clipboard.SetText(recoveryKey);
            copy.Text = "Copied";
        };

        var save = Theme.Action("Save to a file");
        save.Click += (_, _) => SaveToFile(folderName, recoveryKey);

        var tools = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Width = ContentWidth,
            Margin = new Padding(0, 0, 0, 8),
        };
        tools.Controls.AddRange([copy, save]);

        var confirmed = Theme.WrappingCheckBox(
            "I have saved this recovery key somewhere safe", ContentWidth);

        var close = Theme.Action("Done", primary: true);
        close.Enabled = false;
        close.DialogResult = DialogResult.OK;
        close.Margin = new Padding(0);
        confirmed.CheckedChanged += (_, _) => close.Enabled = confirmed.Checked;

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Width = ContentWidth,
            Margin = new Padding(0, 8, 0, 0),
        };
        buttons.Controls.Add(close);

        var root = new TableLayoutPanel
        {
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Padding = new Padding(20, 16, 20, 16),
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        foreach (Control control in new Control[] { heading, explanation, keyBox, tools, confirmed, buttons })
            root.Controls.Add(control);

        Controls.Add(root);
        AcceptButton = close;
    }

    private void SaveToFile(string folderName, string recoveryKey)
    {
        using var dialog = new SaveFileDialog
        {
            FileName = $"FolderVault recovery key - {Sanitize(folderName)}.txt",
            Filter = "Text file (*.txt)|*.txt",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        };

        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        File.WriteAllText(dialog.FileName,
            $"""
             FolderVault recovery key
             Folder: {folderName}
             Created: {DateTimeOffset.Now:yyyy-MM-dd HH:mm}

             {recoveryKey}

             Anyone holding this key can open the folder. Keep it somewhere separate
             from the drive the folder lives on.
             """);
    }

    private static string Sanitize(string name) =>
        string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
