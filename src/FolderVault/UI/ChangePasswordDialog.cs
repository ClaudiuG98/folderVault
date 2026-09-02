namespace FolderVault.UI;

/// <summary>
/// Collects the current and new password. The change itself only re-wraps the vault key, so it
/// is instant even for a large encrypted folder.
/// </summary>
public sealed class ChangePasswordDialog : Form
{
    private const int ContentWidth = 380;

    private readonly TextBox _current;
    private readonly TextBox _next;
    private readonly TextBox _confirm;
    private readonly Label _error;

    public string CurrentPassword => _current.Text;
    public string NewPassword => _next.Text;

    public ChangePasswordDialog(string folderName)
    {
        Text = "Change password";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = Theme.Surface;
        Font = Theme.Body();
        AutoScaleMode = AutoScaleMode.Font;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var heading = Theme.Wrapping(folderName, ContentWidth, 10.5f, Theme.Text, FontStyle.Bold);

        _current = Theme.Secret("Current password", ContentWidth);
        _current.Margin = new Padding(0, 4, 0, 14);

        _next = Theme.Secret("New password", ContentWidth);
        _confirm = Theme.Secret("Confirm new password", ContentWidth);

        var note = Theme.Wrapping(
            "Any recovery key you already have keeps working: the folder key itself does not " +
            "change, only the password that unwraps it.",
            ContentWidth);

        _error = Theme.Wrapping(string.Empty, ContentWidth, color: Theme.Danger);
        _error.Visible = false;

        var ok = Theme.Action("Change", primary: true);
        ok.Click += (_, _) => Confirm();

        var cancel = Theme.Action("Cancel");
        cancel.DialogResult = DialogResult.Cancel;
        cancel.Margin = new Padding(0);

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Width = ContentWidth,
            Margin = new Padding(0, 8, 0, 0),
        };
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);

        var root = new TableLayoutPanel
        {
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Padding = new Padding(20, 16, 20, 16),
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        foreach (Control control in new Control[] { heading, _current, _next, _confirm, note, _error, buttons })
            root.Controls.Add(control);

        Controls.Add(root);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    private void Confirm()
    {
        if (_next.Text.Length < 8)
        {
            Fail("Use a new password of at least 8 characters.");
            return;
        }

        if (_next.Text != _confirm.Text)
        {
            Fail("The two new passwords do not match.");
            return;
        }

        DialogResult = DialogResult.OK;
    }

    private void Fail(string message)
    {
        _error.Text = message;
        _error.Visible = true;
    }
}
