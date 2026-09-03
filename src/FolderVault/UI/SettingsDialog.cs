using FolderVault.Core.Model;
using FolderVault.Core.Shell;

namespace FolderVault.UI;

/// <summary>
/// Settings: when a folder re-locks, set per folder and labelled as such because the scope is
/// easy to mistake for machine-wide.
///
/// <para>It used to carry a second, machine-wide section for hiding the Windows shortcut arrow.
/// That is gone - see <see cref="ShellTweakRepair"/> for why - and all that is left of it is a
/// repair button, which only appears on a PC an older version already damaged.</para>
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
    private readonly Button _repair;
    private readonly Label _repairState;

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

        // ---- repairing an older FolderVault's machine-wide damage ----
        // Only shown when there is something to repair. FolderVault no longer changes anything
        // outside its own files, so on a healthy PC this section would be a heading over a
        // sentence saying nothing is wrong - noise in a dialog that is otherwise all choices.
        _repair = Theme.Action("Repair");
        _repair.Click += (_, _) => Repair();
        _repair.Margin = new Padding(0, 4, 8, 0);

        _repairState = Theme.Wrapping(string.Empty, ContentWidth, color: Theme.Danger);

        if (ShellTweakRepair.NeedsRepair())
        {
            rows.Add(Theme.Heading("Repair Windows shortcuts"));
            rows.Add(_repairState);
            rows.Add(_repair);
        }

        // ---- buttons ----
        // With no folder selected there is nothing to save: repair applies the moment it is
        // clicked. Offering "Save" there would promise something this dialog cannot do.
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
        UpdateRepairState();
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

    /// <summary>
    /// Names the damage in the terms the user is actually seeing it in - a black square, or a pin
    /// that will not launch - rather than as a registry value. Both can be present at once, on a
    /// PC that ran both of the old implementations.
    /// </summary>
    private void UpdateRepairState()
    {
        var blacked = ShellTweakRepair.ShortcutIconsAreBlacked();
        var pins = ShellTweakRepair.TaskbarPinsAreBroken();

        var problems = new List<string>();

        if (blacked)
            problems.Add(
                "every shortcut on this PC has a black square where its arrow should be");

        if (pins)
            problems.Add(
                "taskbar pins do not launch, reporting that the file has no app associated with it");

        if (problems.Count == 0)
        {
            _repairState.Text = "Nothing to repair.";
            _repair.Enabled = false;
            return;
        }

        _repairState.Text =
            $"An older FolderVault changed Windows itself, and {string.Join(", and ", problems)}. " +
            "Repairing puts Windows back to its default. It needs administrator approval and " +
            "restarts Explorer.";
    }

    private void Repair()
    {
        if (!ShellTweakRepair.TryRepair())
        {
            MessageBox.Show(this, "The repair was not applied.", "Repair Windows shortcuts",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        UpdateRepairState();

        // The registry is already correct at this point, but Explorer draws icons from a cache
        // that still holds the black squares. Saying so is the difference between the user
        // believing the repair worked and believing it did nothing.
        if (MessageBox.Show(this,
                "Repaired. Restart Explorer now to clear the icons already on screen?",
                "Repair Windows shortcuts",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK)
            ShellTweakRepair.RestartExplorer();
    }
}
