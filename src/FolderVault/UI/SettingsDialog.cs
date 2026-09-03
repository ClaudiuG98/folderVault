using FolderVault.Core.Model;
using FolderVault.Core.Shell;

namespace FolderVault.UI;

/// <summary>
/// Settings, in two scopes that are labelled as such because they are easy to confuse: when a
/// folder re-locks goes per folder, and the shortcut arrow is a change to Windows itself.
///
/// <para>The re-lock rules are radio buttons, not tickboxes. They were tickboxes, and two of them
/// ticked - which is the default, and the safest setting - read as a mistake rather than as a
/// choice: nothing on screen said which of the two would win, or that they were even meant to
/// coexist. "Whichever comes first" is now a thing you pick, and the sentence underneath reads the
/// choice back in full.</para>
///
/// <para>Locking when Windows locks stays a separate tickbox on purpose. It is a different kind of
/// rule - something that happens to the machine, rather than a judgement about when you are
/// finished with the folder - and it composes with all four of the others.</para>
/// </summary>
public sealed class SettingsDialog : Form
{
    private const int ContentWidth = 470;

    private readonly Vault? _vault;
    private readonly bool _isOpenNow;

    private readonly RadioButton _onClose;
    private readonly RadioButton _onTimer;
    private readonly RadioButton _either;
    private readonly RadioButton _never;
    private readonly NumericUpDown _minutes;
    private readonly CheckBox _sessionLock;
    private readonly Label _summary;
    private readonly Button _arrow;
    private readonly Label _arrowState;

    /// <summary>The rule the user settled on. Only meaningful when the dialog returns OK.</summary>
    public AutoLockRule Rule =>
        _onClose.Checked ? AutoLockRule.OnClose
        : _onTimer.Checked ? AutoLockRule.OnTimer
        : _either.Checked ? AutoLockRule.Either
        : AutoLockRule.Never;

    public int IdleLockMinutes => AutoLockRules.Fields(Rule, (int)_minutes.Value).IdleMinutes;

    public bool LockOnExplorerClose => AutoLockRules.Fields(Rule, (int)_minutes.Value).OnExplorerClose;

    public bool LockOnSessionLock => _sessionLock.Checked;

    /// <param name="vault">The selected folder, or null to show only the machine-wide settings.</param>
    /// <param name="isOpenNow">Whether that folder is unlocked, which changes when a change bites.</param>
    public SettingsDialog(Vault? vault, bool isOpenNow)
    {
        _vault = vault;
        _isOpenNow = isOpenNow;

        Text = "Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = Theme.Surface;
        Font = Theme.Body();
        AutoScaleMode = AutoScaleMode.Font;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var current = vault?.RuleFor() ?? AutoLockRule.Either;

        _onClose = Choice("When I close its Explorer window", current == AutoLockRule.OnClose);
        _onTimer = Choice("After a period without activity", current == AutoLockRule.OnTimer);
        // Caption is filled in by UpdateSummary, which keeps the minutes in it matching the
        // spinner. "Whichever comes first" was accurate and meant nothing on its own - naming
        // the two rules it combines is what makes it readable without the note underneath.
        _either = Choice(string.Empty, current == AutoLockRule.Either);
        _never = Choice("Never - only when I lock it myself", current == AutoLockRule.Never);

        _minutes = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 1440, // a day; past that "Never" is the honest setting
            Value = Math.Clamp(vault?.IdleLockMinutes ?? AutoLockRules.DefaultIdleMinutes, 1, 1440),
            Width = 60,
            Margin = new Padding(20, 2, 6, 4),
        };

        _sessionLock = Theme.WrappingCheckBox(
            "Also lock it when Windows locks or I switch user", ContentWidth,
            isChecked: vault?.LockOnSessionLock ?? true);

        _summary = Theme.Wrapping(string.Empty, ContentWidth, color: Theme.Text);

        var rows = new List<Control>();

        if (vault is not null)
        {
            rows.Add(Theme.Heading($"When to re-lock “{vault.DisplayName}”"));

            rows.Add(_onClose);
            rows.Add(Note("A few seconds after the last Explorer window on it is closed."));

            rows.Add(_onTimer);
            rows.Add(MinutesRow());
            rows.Add(Note(
                "Saving, renaming or deleting anything inside restarts the clock. Simply having " +
                "the folder open on screen does not - that is the case this is meant to catch."));

            rows.Add(_either);
            rows.Add(Note("Covers both: closing it when you are done, and catching it if you walk " +
                          "away with it open."));

            rows.Add(_never);

            rows.Add(_sessionLock);
            rows.Add(_summary);
            rows.Add(Theme.Wrapping(
                "These apply to this folder only, and only while FolderVault is running. Signing " +
                "out or shutting down always locks every open folder, whatever is set here.",
                ContentWidth));
        }
        else
        {
            rows.Add(Theme.Heading("When to re-lock"));
            rows.Add(Theme.Wrapping(
                "Set per folder. Select one in the manager window and open Settings again to " +
                "change when it re-locks.", ContentWidth));
        }

        // ---- shortcut arrow, machine-wide ----
        _arrowState = Theme.Wrapping(string.Empty, ContentWidth);

        _arrow = Theme.Action("Hide the arrow");
        _arrow.Click += (_, _) => ToggleArrow();
        _arrow.Margin = new Padding(0, 4, 8, 0);

        rows.Add(Theme.Heading("Shortcut arrows"));
        rows.Add(Theme.Wrapping(
            "A locked folder is stood in for by a shortcut wearing the folder icon with a padlock " +
            "badge. Windows draws its own small arrow over every shortcut, which is the remaining " +
            "visual difference from a real folder. Hiding it affects every shortcut on the PC, " +
            "needs administrator approval, and restarts Explorer.", ContentWidth));
        rows.Add(_arrowState);
        rows.Add(_arrow);

        // ---- buttons ----
        // With no folder selected there is nothing to save: the arrow toggle applies the moment
        // it is clicked. Offering "Save" there would promise something this dialog cannot do.
        var ok = Theme.Action(vault is null ? "Close" : "Save", primary: true);
        ok.DialogResult = vault is null ? DialogResult.Cancel : DialogResult.OK;

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Width = ContentWidth,
            Margin = new Padding(0, 14, 0, 0),
        };
        buttons.Controls.Add(ok);

        Button? cancel = null;
        if (vault is not null)
        {
            cancel = Theme.Action("Cancel");
            cancel.DialogResult = DialogResult.Cancel;
            cancel.Margin = new Padding(0);
            buttons.Controls.Add(cancel);
        }

        rows.Add(buttons);

        var root = new TableLayoutPanel
        {
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Padding = new Padding(20, 16, 20, 16),
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        foreach (var row in rows) root.Controls.Add(row);

        Controls.Add(root);
        AcceptButton = ok;
        CancelButton = cancel ?? ok;

        foreach (var choice in new[] { _onClose, _onTimer, _either, _never })
            choice.CheckedChanged += (_, _) => UpdateSummary();
        _minutes.ValueChanged += (_, _) => UpdateSummary();
        _sessionLock.CheckedChanged += (_, _) => UpdateSummary();

        UpdateSummary();
        UpdateArrowState();
    }

    private static RadioButton Choice(string text, bool selected) => new()
    {
        Text = text,
        Checked = selected,
        AutoSize = true,
        Font = Theme.Body(9f, FontStyle.Bold),
        Margin = new Padding(0, 8, 0, 2),
    };

    private static Label Note(string text)
    {
        var label = Theme.Wrapping(text, ContentWidth - 20);
        label.Margin = new Padding(20, 0, 0, 4);
        return label;
    }

    /// <summary>The spinner, indented under the timer choice it belongs to.</summary>
    private Control MinutesRow()
    {
        var row = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = new Padding(0),
        };
        row.Controls.Add(_minutes);
        row.Controls.Add(new Label
        {
            Text = "minutes",
            AutoSize = true,
            Margin = new Padding(0, 6, 0, 0),
            ForeColor = Theme.Text,
        });
        return row;
    }

    /// <summary>
    /// Says back, in one sentence, what the current choice actually means. The rule that matters
    /// most is "Never", and it is the one that looks least alarming on screen.
    /// </summary>
    private void UpdateSummary()
    {
        // The number only governs two of the four rules; greying it out elsewhere says so without
        // discarding whatever the user typed.
        _minutes.Enabled = _onTimer.Checked || _either.Checked;

        // Spell the combined rule out in its own caption, tracking the spinner, so the option
        // says what it does rather than referring to the two above it.
        _either.Text = $"When I close it, or after {AutoLockText.Duration((int)_minutes.Value)}";

        if (_vault is null) return;

        var name = _vault.DisplayName;
        var sentence = AutoLockText.Sentence(name, Rule, (int)_minutes.Value, _sessionLock.Checked);

        if (sentence is null)
        {
            _summary.ForeColor = Theme.Danger;
            _summary.Text = AutoLockText.NothingWillLockIt(name);
            return;
        }

        _summary.ForeColor = Rule == AutoLockRule.Never ? Theme.Danger : Theme.Text;
        _summary.Text = sentence +
                        (_isOpenNow ? " It is open now, so this takes effect immediately." : string.Empty);
    }

    private void UpdateArrowState()
    {
        if (ShellArrowOverlay.TaskbarPinsAreBroken())
        {
            _arrowState.ForeColor = Theme.Danger;
            _arrowState.Text =
                "Taskbar pins on this PC are broken - clicking one reports that the file has no app " +
                "associated with it. An older FolderVault caused it. Use the button to repair.";
            _arrow.Text = "Repair taskbar pins";
            return;
        }

        var suppressed = ShellArrowOverlay.IsSuppressed();
        _arrowState.ForeColor = Theme.Muted;
        _arrowState.Text = suppressed ? "The arrow is currently hidden." : "The arrow is currently shown.";
        _arrow.Text = suppressed ? "Show the arrow" : "Hide the arrow";
    }

    private void ToggleArrow()
    {
        var broken = ShellArrowOverlay.TaskbarPinsAreBroken();

        // Repairing means writing the arrow setting that is already in effect: the same call
        // rewrites IsShortcut either way, so nothing else visibly changes.
        var wanted = broken ? ShellArrowOverlay.IsSuppressed() : !ShellArrowOverlay.IsSuppressed();

        if (!ShellArrowOverlay.TrySetSuppressed(wanted))
        {
            MessageBox.Show(this, "The change was not applied.", "Shortcut arrows",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        UpdateArrowState();

        if (MessageBox.Show(this, "Restart Explorer now to apply it?", "Shortcut arrows",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK)
            ShellArrowOverlay.RestartExplorer();
    }
}
