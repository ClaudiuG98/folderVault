using System.Diagnostics;
using Microsoft.Win32;

namespace FolderVault.Core.Shell;

/// <summary>
/// Optional cosmetic tweak: hides the small arrow Explorer draws over every shortcut, leaving a
/// locked folder's decoy showing nothing but the folder icon and its padlock badge (see
/// <see cref="DecoyIcon"/>).
///
/// <para><b>How, and why not the obvious way.</b> The widely-copied recipe for this deletes
/// <c>IsShortcut</c> from <c>HKLM\Software\Classes\lnkfile</c>, and FolderVault used to do the
/// same. That value is what tells the shell to resolve a <c>.lnk</c> to its target instead of
/// opening it as a document, and removing it breaks every taskbar pin on the machine: Explorer's
/// own views resolve links through the shell namespace and keep working, but the taskbar launches
/// a pin by ShellExecute-ing the <c>.lnk</c>, and <c>lnkfile</c> has no <c>shell\open\command</c>
/// of its own, so the click fails with "This file does not have an app associated with it".</para>
///
/// <para>Instead this points shell icon override <c>29</c> - the shortcut overlay - at a blank
/// icon that Windows already ships (<c>shell32.dll</c> index 50). The overlay is still drawn;
/// there is simply nothing in it. <c>IsShortcut</c> stays exactly where it was, so shortcuts keep
/// behaving like shortcuts everywhere, taskbar included. Applying the tweak also restores
/// <c>IsShortcut</c> if an older FolderVault removed it.</para>
///
/// <para>It remains a <b>system-wide</b> change - every shortcut on the PC loses its arrow, not
/// just FolderVault's - it lives under HKLM so it needs elevation, and it only takes effect once
/// the icon cache is refreshed and Explorer restarts. FolderVault itself runs without elevation,
/// so applying it shells out to an elevated <c>reg.exe</c> and the user sees a UAC prompt.</para>
/// </summary>
public static class ShellArrowOverlay
{
    private const string ShellIconsKey =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Shell Icons";

    /// <summary>Shell icon override 29 is the shortcut arrow.</summary>
    private const string ShortcutOverlayValue = "29";

    /// <summary>A blank icon in the box, so nothing has to be written to disk for this to work.</summary>
    private const string BlankIcon = @"%SystemRoot%\System32\shell32.dll,50";

    private const string LnkFileKey = @"Software\Classes\lnkfile";

    private const string IsShortcutValue = "IsShortcut";

    /// <summary>True when the arrow is currently hidden.</summary>
    public static bool IsSuppressed()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(ShellIconsKey);
            return key?.GetValue(ShortcutOverlayValue) is string value && value.Length > 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// True if <c>IsShortcut</c> is missing, which is the fingerprint of the old implementation
    /// and the reason a machine's taskbar pins stop working. <see cref="TrySetSuppressed"/>
    /// repairs it in either direction.
    /// </summary>
    public static bool TaskbarPinsAreBroken()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(LnkFileKey);
            return key is not null && key.GetValue(IsShortcutValue) is null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Applies or reverts the tweak, and repairs <c>IsShortcut</c> either way. Returns false if
    /// the user dismissed the UAC prompt or the change did not take. The caller should refresh
    /// the shell afterwards with <see cref="RestartExplorer"/>.
    /// </summary>
    public static bool TrySetSuppressed(bool suppressed)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("cmd.exe", BuildCommand(suppressed))
            {
                UseShellExecute = true,
                Verb = "runas", // triggers the UAC prompt
                WindowStyle = ProcessWindowStyle.Hidden,
            });

            process?.WaitForExit();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // ERROR_CANCELLED: the user declined elevation.
            return false;
        }

        return IsSuppressed() == suppressed;
    }

    /// <summary>
    /// The elevated command line, split out so the taskbar regression is covered by a test rather
    /// than by remembering. Both edits go in one invocation so the user sees a single UAC prompt.
    ///
    /// The steps are joined with <c>&amp;</c> rather than <c>&amp;&amp;</c> because deleting a
    /// value that is not there returns a failure code, and that case is a success as far as this
    /// is concerned - the outcome is verified by reading the registry back afterwards instead of
    /// by trusting an exit code.
    /// </summary>
    internal static string BuildCommand(bool suppressed)
    {
        var overlay = suppressed
            ? $@"reg add ""HKLM\{ShellIconsKey}"" /v {ShortcutOverlayValue} /t REG_SZ /d ""{BlankIcon}"" /f"
            : $@"reg delete ""HKLM\{ShellIconsKey}"" /v {ShortcutOverlayValue} /f";

        // No /d on this one: reg.exe then writes empty data, which is the Windows default.
        var repair = $@"reg add ""HKLM\{LnkFileKey}"" /v {IsShortcutValue} /t REG_SZ /f";

        return $"/c {repair} & {overlay}";
    }

    /// <summary>
    /// Makes the change visible: clears the per-user icon cache, then restarts Explorer.
    ///
    /// The cache flush matters. Explorer keeps rendered icons keyed by source, and without
    /// <c>ie4uinit -show</c> a restart alone can leave the old arrow painted on items that were
    /// already cached, which reads as the tweak having silently failed.
    /// </summary>
    public static void RestartExplorer()
    {
        try
        {
            using var refresh = Process.Start(new ProcessStartInfo("ie4uinit.exe", "-show")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            refresh?.WaitForExit(5000);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Not present on every build. The restart below still applies the change; at worst a
            // few already-cached icons keep their arrow until the cache turns over on its own.
        }

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
