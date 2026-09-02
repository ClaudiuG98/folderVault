using FolderVault.Core.Ops;

namespace FolderVault.UI;

/// <summary>
/// Shown while a Secure vault is being encrypted or decrypted, which is the one part of the app
/// that can take minutes rather than milliseconds.
///
/// The work runs on a background thread and the dialog closes itself when it finishes. There is
/// no cancel button once encryption has begun: stopping midway is safe for the data, but the
/// resulting state needs a recovery pass, and offering a button that leads there would be a trap.
/// </summary>
public sealed class ProgressDialog : Form
{
    private readonly Label _step;
    private readonly ProgressBar _bar;
    private Exception? _failure;

    private ProgressDialog(string title)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = MinimizeBox = false;
        ControlBox = false;
        ShowInTaskbar = false;
        BackColor = Theme.Surface;
        Font = Theme.Body();
        ClientSize = new Size(420, 116);

        var heading = new Label
        {
            Text = title,
            Location = new Point(20, 18),
            Size = new Size(380, 22),
            Font = Theme.Body(10.5f, FontStyle.Bold),
        };

        _step = new Label
        {
            Location = new Point(20, 44),
            Size = new Size(380, 20),
            ForeColor = Theme.Muted,
            Font = Theme.Body(8.5f),
            AutoEllipsis = true,
            Text = "Starting",
        };

        _bar = new ProgressBar
        {
            Location = new Point(20, 72),
            Size = new Size(380, 12),
            Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 24,
        };

        Controls.AddRange([heading, _step, _bar]);
    }

    /// <summary>
    /// Runs <paramref name="work"/> on a background thread behind a modal progress window,
    /// rethrowing on the calling thread if it fails so callers handle errors normally.
    /// </summary>
    public static void Run(IWin32Window? owner, string title, Action<IProgress<OperationProgress>> work)
    {
        using var dialog = new ProgressDialog(title);

        var progress = new Progress<OperationProgress>(dialog.Report);

        dialog.Shown += async (_, _) =>
        {
            try
            {
                await Task.Run(() => work(progress));
            }
            catch (Exception ex)
            {
                dialog._failure = ex;
            }
            finally
            {
                dialog.Close();
            }
        };

        dialog.ShowDialog(owner);

        if (dialog._failure is not null)
            throw dialog._failure;
    }

    private void Report(OperationProgress progress)
    {
        _step.Text = progress.Step;

        if (progress.Fraction is not { } fraction)
        {
            if (_bar.Style != ProgressBarStyle.Marquee) _bar.Style = ProgressBarStyle.Marquee;
            return;
        }

        if (_bar.Style != ProgressBarStyle.Continuous)
        {
            _bar.Style = ProgressBarStyle.Continuous;
            _bar.Maximum = 1000;
        }

        _bar.Value = (int)Math.Round(fraction * 1000);
    }
}
