using FolderVault.Core.Crypto;
using FolderVault.Core.Model;
using FolderVault.UI;
using Xunit;

namespace FolderVault.Tests;

/// <summary>
/// Regression tests for the password prompt.
///
/// These exist because of a bug that made every correct password look wrong. The prompt used to
/// close itself in OnDeactivate to feel like a context menu. But deactivation is part of the
/// normal window-close sequence, so the moment a correct password set DialogResult.OK and the
/// form began closing, the deactivation handler overwrote it with Cancel. The caller saw a
/// cancelled prompt and reported that the folder "was not unlocked".
/// </summary>
public class PasswordPromptTests
{
    private static Vault SampleVault() => new()
    {
        OriginalPath = @"C:\Users\someone\Desktop\Holiday Photos",
        Mode = VaultMode.Fast,
        Salt = KeyDerivation.NewSalt(),
        Iterations = 1000,
    };

    /// <summary>WinForms needs an STA thread; xunit runs tests on MTA ones.</summary>
    private static T OnStaThread<T>(Func<T> work)
    {
        T result = default!;
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try { result = work(); }
            catch (Exception ex) { failure = ex; }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "The prompt did not finish in time.");
        if (failure is not null) throw failure;
        return result;
    }

    /// <summary>Shows the prompt and drives it with the given steps, returning how it closed.</summary>
    private static (DialogResult Result, string Entered) Drive(
        Func<string, bool, string?> validate, params string[] attempts)
    {
        return OnStaThread(() =>
        {
            using var prompt = new PasswordPrompt(SampleVault())
            {
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-4000, -4000), // off-screen: never in the way
            };
            prompt.ValidateSecret = validate;

            var secret = Descendants(prompt).OfType<TextBox>().First();
            var submit = Descendants(prompt).OfType<Button>().First(b => b.Text is "Open" or "Working");

            var index = 0;
            var ticks = 0;
            var driver = new System.Windows.Forms.Timer { Interval = 120 };
            driver.Tick += (_, _) =>
            {
                // Wait out the background validation between attempts.
                if (!secret.Enabled) return;

                if (index < attempts.Length)
                {
                    secret.Text = attempts[index++];
                    submit.PerformClick();
                    return;
                }

                if (++ticks > 8)
                {
                    driver.Stop();
                    if (!prompt.IsDisposed && prompt.Visible) prompt.DialogResult = DialogResult.Abort;
                }
            };
            driver.Start();

            var result = prompt.ShowDialog();
            driver.Stop();
            return (result, prompt.EnteredSecret);
        });
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    [Fact]
    public void CorrectPassword_ClosesWithOk_AndKeepsThatResult()
    {
        var (result, entered) = Drive((secret, _) => secret == "right" ? null : "Wrong password.", "right");

        Assert.Equal(DialogResult.OK, result);
        Assert.Equal("right", entered);
    }

    [Fact]
    public void CorrectPasswordAfterAWrongOne_StillSucceeds()
    {
        var (result, entered) = Drive((secret, _) => secret == "right" ? null : "Wrong password.",
            "wrong", "right");

        Assert.Equal(DialogResult.OK, result);
        Assert.Equal("right", entered);
    }

    [Fact]
    public void WrongPassword_LeavesTheBoxUsableInsteadOfClosing()
    {
        // "Abort" is what the driver sets once it has waited without the prompt closing itself,
        // so seeing it here means the prompt stayed open and editable after a rejected attempt.
        var (result, _) = Drive((_, _) => "Wrong password.", "wrong");

        Assert.Equal(DialogResult.Abort, result);
    }

    [Fact]
    public void RecoveryKeyEntry_IsOfferedOnlyWhenTheVaultHasOne()
    {
        var withKey = SampleVault();
        withKey.RecoveryWrappedDek = new byte[60];

        // The forms have to be shown: Control.Visible reports false for any child of a form
        // that has not been displayed, whatever the control's own setting.
        var (without, with) = OnStaThread(() =>
        {
            static bool LinkShown(Vault vault)
            {
                using var prompt = new PasswordPrompt(vault)
                {
                    StartPosition = FormStartPosition.Manual,
                    Location = new Point(-4000, -4000),
                };
                prompt.Show();
                Application.DoEvents();
                var visible = Descendants(prompt).OfType<LinkLabel>().Single().Visible;
                prompt.Close();
                return visible;
            }

            return (LinkShown(SampleVault()), LinkShown(withKey));
        });

        Assert.False(without);
        Assert.True(with);
    }
}
