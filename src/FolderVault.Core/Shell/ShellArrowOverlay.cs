using System.Diagnostics;
using Microsoft.Win32;

namespace FolderVault.Core.Shell;

/// <summary>
/// Optional cosmetic tweak: hides the small arrow overlay Explorer draws on every shortcut, which
/// is the last visual difference between a decoy and a real folder.
///
/// This is deliberately opt-in and off by default. It is a <b>system-wide</b> change - every
/// shortcut on the machine loses its arrow, not just FolderVault's - it lives under HKLM so it
/// needs elevation, and it only takes effect once Explorer restarts. FolderVault itself runs
/// without elevation, so applying it shells out to an elevated <c>reg.exe</c> and the user sees a
/// UAC prompt.
/// </summary>
public static class ShellArrowOverlay
{
    private const string KeyPath = @"Software\Classes\lnkfile";
    private const string ValueName = "IsShortcut";

    /// <summary>True when the arrow is currently hidden.</summary>
    public static bool IsSuppressed()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(KeyPath);
            return key?.GetValue(ValueName) is null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Applies or reverts the tweak by launching an elevated reg.exe. Returns false if the user
    /// dismissed the UAC prompt. The caller should offer to restart Explorer afterwards.
    /// </summary>
    public static bool TrySetSuppressed(bool suppressed)
    {
        var arguments = suppressed
            ? $@"delete HKLM\{KeyPath} /v {ValueName} /f"
            : $@"add HKLM\{KeyPath} /v {ValueName} /t REG_SZ /d "" "" /f";

        try
        {
            using var process = Process.Start(new ProcessStartInfo("reg.exe", arguments)
            {
                UseShellExecute = true,
                Verb = "runas", // triggers the UAC prompt
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });

            process?.WaitForExit();
            return process?.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // ERROR_CANCELLED: the user declined elevation.
            return false;
        }
    }

    /// <summary>Restarts Explorer so the change takes effect without a sign-out.</summary>
    public static void RestartExplorer()
    {
        foreach (var process in Process.GetProcessesByName("explorer"))
        {
            try
            {
                process.Kill();
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Already gone, or protected; Windows restarts the shell on its own either way.
            }
        }
        // Windows normally relaunches the shell automatically; this covers the case where it does not.
        Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
    }
}
