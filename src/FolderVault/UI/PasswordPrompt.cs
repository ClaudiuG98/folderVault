using System.Runtime.InteropServices;
using FolderVault.Core.Model;

namespace FolderVault.UI;

/// <summary>
/// The small password box that appears when a locked folder is double-clicked.
///
/// It is borderless, always on top, and positioned at the mouse pointer, so it surfaces right
/// where the user just clicked rather than in the middle of the screen or behind another window.
/// Enter submits and Escape dismisses.
///
/// It deliberately does <b>not</b> close when it loses focus. An earlier version did, to feel like
/// a context menu, and that was a bad idea twice over: deactivation is part of the normal
/// close sequence, so it overwrote a successful <see cref="DialogResult.OK"/> with
/// <see cref="DialogResult.Cancel"/> and made correct passwords look wrong; and any transient
/// focus change - a message box finishing its teardown, the shell stealing focus - silently
/// dismissed the prompt before the user could type. A password box that vanishes on a focus
/// wobble is far worse than one that waits to be told to go away.
/// </summary>
public sealed class PasswordPrompt : Form
{
    private readonly TextBox _secret;
    private readonly Label _hint;
    private readonly Label _error;
    private readonly ProgressBar _progress;
    private readonly Button _submit;
    private readonly Button _cancel;
    private readonly LinkLabel _useRecoveryKey;

    private readonly System.Windows.Forms.Timer _shakeTimer = new() { Interval = 18 };
    private int _shakeStep;
    private Point _restPosition;
    private bool _shaking;
    private bool _busy;

    /// <summary>What the user typed. Valid once the dialog returns OK.</summary>
    public string EnteredSecret { get; private set; } = string.Empty;

    /// <summary>True when the user switched to entering a recovery key instead of a password.</summary>
    public bool UsingRecoveryKey { get; private set; }

    /// <summary>
    /// Checks the secret. Returns null to accept and close, or a message to show as an error.
    /// Invoked on a background thread, because key derivation is deliberately slow.
    /// </summary>
    public Func<string, bool, string?>? ValidateSecret { get; set; }

    public PasswordPrompt(Vault vault)
    {
        // This is the one hand-positioned form in the app, so it needs to be told how to scale.
        // The baseline is Segoe UI 9pt at 96 DPI; WinForms scales every coordinate below by the
        // ratio between that and the font on the monitor the prompt actually opens on.
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;
        BackColor = Theme.Surface;
        ClientSize = new Size(360, 182);

        var title = new Label
        {
            Text = vault.DisplayName,
            Font = Theme.Body(11f, FontStyle.Bold),
            ForeColor = Theme.Text,
            // AutoSize must be off for both AutoEllipsis and the fixed Size to take effect.
            AutoSize = false,
            AutoEllipsis = true,
            Location = new Point(18, 13),
            Size = new Size(324, 26),
        };

        _hint = new Label
        {
            Text = vault.Mode == VaultMode.Secure
                ? "Encrypted folder. Enter the password to open it."
                : "Locked folder. Enter the password to open it.",
            Font = Theme.Body(8.25f),
            ForeColor = Theme.Muted,
            AutoSize = false,
            Location = new Point(18, 41),
            Size = new Size(324, 18),
        };

        _secret = new TextBox
        {
            UseSystemPasswordChar = true,
            Font = Theme.Body(11f),
            Location = new Point(18, 65),
            Size = new Size(324, 28),
            BorderStyle = BorderStyle.FixedSingle,
        };

        _error = new Label
        {
            ForeColor = Theme.Danger,
            Font = Theme.Body(8.25f),
            AutoSize = false,
            Location = new Point(18, 98),
            Size = new Size(324, 32),
            Visible = false,
        };

        _progress = new ProgressBar
        {
            Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 24,
            Location = new Point(18, 100),
            Size = new Size(324, 6),
            Visible = false,
        };

        _submit = new Button
        {
            Text = "Open",
            Location = new Point(266, 136),
            Size = new Size(76, 28),
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.Accent,
            ForeColor = Color.White,
            Font = Theme.Body(9f, FontStyle.Bold),
        };
        _submit.FlatAppearance.BorderSize = 0;
        _submit.Click += (_, _) => Submit();

        _cancel = new Button
        {
            Text = "Cancel",
            Location = new Point(182, 136),
            Size = new Size(76, 28),
            FlatStyle = FlatStyle.System,
            DialogResult = DialogResult.Cancel,
        };

        _useRecoveryKey = new LinkLabel
        {
            Text = "Use recovery key",
            Font = Theme.Body(8.25f),
            LinkColor = Theme.Muted,
            ActiveLinkColor = Theme.Accent,
            AutoSize = false,
            Location = new Point(18, 142),
            Size = new Size(150, 18),
            Visible = vault.RecoveryWrappedDek is not null,
        };
        _useRecoveryKey.LinkClicked += (_, _) => SwitchToRecoveryKey();

        Controls.AddRange([title, _hint, _secret, _error, _progress, _submit, _cancel, _useRecoveryKey]);

        AcceptButton = _submit;
        CancelButton = _cancel;
        _shakeTimer.Tick += OnShakeTick;
    }

    /// <summary>
    /// Places the prompt just below and right of the pointer, clamped into whichever monitor it
    /// lands on so it is never clipped by a screen edge or a secondary display.
    /// </summary>
    public void PositionAtCursor()
    {
        var cursor = Cursor.Position;
        var screen = Screen.FromPoint(cursor).WorkingArea;

        var x = Math.Clamp(cursor.X - 24, screen.Left + 8, Math.Max(screen.Left + 8, screen.Right - Width - 8));
        var y = Math.Clamp(cursor.Y + 18, screen.Top + 8, Math.Max(screen.Top + 8, screen.Bottom - Height - 8));

        Location = _restPosition = new Point(x, y);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _restPosition = Location;
        BringToFront();
        TakeForeground();
        _secret.Focus();
    }

    /// <summary>
    /// Claims the foreground so the box can actually be typed into.
    ///
    /// <para><see cref="Form.Activate"/> alone is not enough. A double-click on a locked folder is
    /// served by the long-running instance, which is usually not the foreground process at that
    /// moment - Explorer, or whatever the user was last in, is - and Windows refuses
    /// <c>SetForegroundWindow</c> from a background process without a word. The window still
    /// appears, because it is topmost, so the prompt looks ready while every keystroke goes to the
    /// window behind it. That was the bug: typeable when FolderVault happened to already be in
    /// front, dead otherwise.</para>
    ///
    /// <para>The courier passes on its right to take the foreground before it exits (see
    /// <c>SingleInstance.SendToPrimary</c>), which covers the ordinary double-click. It is not
    /// enough on its own though - the courier only has that right if whatever launched it had it -
    /// so this also briefly attaches to the foreground window's input queue. Two threads sharing
    /// an input queue share the right to set focus within it, which is the documented way to take
    /// the foreground when asking politely is refused. The attachment is undone immediately;
    /// leaving it in place would tie this app's message loop to another process's.</para>
    /// </summary>
    private void TakeForeground()
    {
        var foreground = GetForegroundWindow();
        var thisThread = GetCurrentThreadId();
        var foregroundThread = foreground == nint.Zero ? thisThread : GetWindowThreadProcessId(foreground, out _);

        var attached = foregroundThread != thisThread
                       && AttachThreadInput(thisThread, foregroundThread, true);
        try
        {
            Activate();
            BringWindowToTop(Handle);
            SetForegroundWindow(Handle);
        }
        finally
        {
            if (attached) AttachThreadInput(thisThread, foregroundThread, false);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape && !_busy)
        {
            DialogResult = DialogResult.Cancel;
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Theme.Border);
        e.Graphics.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
    }

    private void SwitchToRecoveryKey()
    {
        UsingRecoveryKey = true;
        _hint.Text = "Enter your recovery key.";
        _secret.UseSystemPasswordChar = false;
        _secret.Text = string.Empty;
        _secret.Focus();
        _useRecoveryKey.Visible = false;
        ShowError(null);
    }

    private async void Submit()
    {
        if (_busy) return;

        var entered = _secret.Text;
        if (string.IsNullOrEmpty(entered))
        {
            Shake();
            return;
        }

        if (ValidateSecret is null)
        {
            Accept(entered);
            return;
        }

        SetBusy(true);
        ShowError(null);

        string? failure;
        try
        {
            var usingRecoveryKey = UsingRecoveryKey;
            failure = await Task.Run(() => ValidateSecret(entered, usingRecoveryKey));
        }
        catch (Exception ex)
        {
            failure = ex.Message;
        }

        if (IsDisposed) return;
        SetBusy(false);

        if (failure is null)
        {
            Accept(entered);
            return;
        }

        ShowError(failure);

        // Put the caret back in the box and make sure the window still owns the keyboard:
        // disabling the focused control while validating hands focus away, and without this the
        // next keystrokes go to whatever window picked it up.
        TakeForeground();
        _secret.SelectAll();
        _secret.Focus();
        Shake();
    }

    private void Accept(string entered)
    {
        EnteredSecret = entered;
        DialogResult = DialogResult.OK;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _secret.Enabled = !busy;
        _submit.Enabled = !busy;
        _cancel.Enabled = !busy;
        _submit.Text = busy ? "Working" : "Open";
        _progress.Visible = busy;
        if (busy) _error.Visible = false;
    }

    private void ShowError(string? message)
    {
        _error.Text = message ?? string.Empty;
        _error.Visible = message is not null;
    }

    // ---- wrong-password shake ----

    private void Shake()
    {
        // Only capture the resting place when not already shaking; re-capturing mid-animation
        // would bake in the current offset and walk the window across the screen.
        if (!_shaking)
        {
            _restPosition = Location;
            _shaking = true;
        }

        _shakeStep = 0;
        _shakeTimer.Start();
    }

    private void OnShakeTick(object? sender, EventArgs e)
    {
        // A short damped wobble that settles exactly back on the rest position.
        const int steps = 10;
        if (_shakeStep >= steps)
        {
            _shakeTimer.Stop();
            Location = _restPosition;
            _shaking = false;
            return;
        }

        var amplitude = 7 * (steps - _shakeStep) / steps;
        var offset = _shakeStep % 2 == 0 ? amplitude : -amplitude;
        Location = _restPosition with { X = _restPosition.X + offset };
        _shakeStep++;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _shakeTimer.Dispose();
        base.Dispose(disposing);
    }

    /// <summary>Keeps the prompt out of Alt+Tab: it is a transient popup, not a window.</summary>
    protected override CreateParams CreateParams
    {
        get
        {
            const int WsExToolWindow = 0x80;
            var parameters = base.CreateParams;
            parameters.ExStyle |= WsExToolWindow;
            return parameters;
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(nint hWnd);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint attachTo, uint attachFrom,
        [MarshalAs(UnmanagedType.Bool)] bool attach);
}
