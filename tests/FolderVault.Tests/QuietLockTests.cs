using FolderVault.App;
using FolderVault.Core.Model;
using FolderVault.Services;
using Xunit;

namespace FolderVault.Tests;

/// <summary>
/// Encrypting a folder is the one operation here that takes minutes, so where its progress window
/// appears is a real decision. An auto-lock the user did not ask for must not put a modal window
/// over whatever they are doing; a lock they pressed the button for must show them something.
/// </summary>
public class QuietLockTests
{
    [Theory]
    [InlineData(AutoLockReason.IdleTimeout)]
    [InlineData(AutoLockReason.ExplorerClosed)]
    [InlineData(AutoLockReason.SessionLocked)]
    public void AnEncryptedFolderAutoLocksWithoutAWindow(AutoLockReason reason) =>
        Assert.True(FolderVaultContext.ShouldLockQuietly(VaultMode.Secure, reason));

    [Fact]
    public void PressingLockShowsProgress()
    {
        // No reason means the user asked for it and is waiting on it.
        Assert.False(FolderVaultContext.ShouldLockQuietly(VaultMode.Secure, null));
    }

    [Fact]
    public void SigningOutDoesNotGoToTheBackground()
    {
        // Handing this to a worker thread and returning would let Windows tear the process down
        // mid-encryption. It is recoverable, but finishing is better, and nobody is watching the
        // screen during a shutdown to be interrupted by the window.
        Assert.False(FolderVaultContext.ShouldLockQuietly(VaultMode.Secure, AutoLockReason.WindowsClosing));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(AutoLockReason.IdleTimeout)]
    [InlineData(AutoLockReason.WindowsClosing)]
    public void FastModeNeverNeedsEither(AutoLockReason? reason)
    {
        // A same-volume rename finishes in milliseconds: nothing to show, nothing to offload.
        Assert.False(FolderVaultContext.ShouldLockQuietly(VaultMode.Fast, reason));
    }
}
