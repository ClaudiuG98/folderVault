using System.Diagnostics;
using Microsoft.Win32;

namespace FolderVault.Core.Shell;

/// <summary>
/// Undoes the machine-wide shell tweaks older versions of FolderVault applied. It applies none of
/// its own, and there is deliberately no way to switch either of them back on.
///
/// <para><b>What used to be here.</b> A locked folder is stood in for by a shortcut wearing the
/// folder icon, and Windows draws its own small arrow over every shortcut. That arrow is the last
/// visual difference between a decoy and a real folder, so FolderVault offered to hide it. Two
/// implementations were tried and both broke the whole machine.</para>
///
/// <para>The first deleted <c>IsShortcut</c> from <c>lnkfile</c>. That value is what tells the
/// shell to resolve a <c>.lnk</c> to its target instead of opening it as a document, and without
/// it every taskbar pin on the PC stops working: Explorer's own views resolve links through the
/// shell namespace and carry on, but the taskbar launches a pin by ShellExecute-ing the
/// <c>.lnk</c>, and <c>lnkfile</c> has no <c>shell\open\command</c> of its own, so the click fails
/// with "This file does not have an app associated with it".</para>
///
/// <para>The second pointed shell icon override <c>29</c> - the shortcut overlay - at a blank
/// icon, on the theory that the overlay would still be drawn with nothing in it. Explorer instead
/// filled the slot with a solid black block, so every shortcut on the PC wore a black square where
/// its arrow had been. The icon was not at fault: a generated blank <c>.ico</c> and
/// <c>shell32.dll,50</c> both load fully transparent at every size, and both stay invisible in an
/// ordinary image list. The blackening happens inside the shell's own overlay image list and only
/// at the small size - the same override renders correctly at 48 and 256 pixels. Nothing about the
/// icon file can steer that, so there is no version of this that can be made to work.
/// </para>
///
/// <para>Neither was worth it for a cosmetic gain. <see cref="DecoyIcon"/> puts its padlock badge
/// on the bottom-<i>right</i> corner precisely so it never collides with the arrow on the
/// bottom-left, so a decoy reads correctly whether the arrow is there or not - which is why
/// dropping this costs nothing.</para>
///
/// <para>What remains is repair. A machine that ran either version still carries the damage after
/// an upgrade, so FolderVault detects both and offers to put them back. Repair edits HKLM, so it
/// needs elevation; FolderVault itself runs unelevated and shells out to <c>reg.exe</c>, which is
/// where the UAC prompt comes from.</para>
/// </summary>
public static class ShellTweakRepair
{
    private const string ShellIconsKey =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Shell Icons";

    /// <summary>Shell icon override 29 is the shortcut arrow.</summary>
    private const string ShortcutOverlayValue = "29";

    private const string LnkFileKey = @"Software\Classes\lnkfile";

    private const string IsShortcutValue = "IsShortcut";

    /// <summary>
    /// True when override 29 is set, which is what paints a black square over every shortcut on
    /// the PC. Anything in the value counts: FolderVault no longer writes one, so whatever is
    /// there is either its own leftovers or another tool's, and either way the black square is
    /// what the user is looking at.
    /// </summary>
    public static bool ShortcutIconsAreBlacked() =>
        Read(ShellIconsKey, ShortcutOverlayValue) is not null;

    /// <summary>
    /// True if <c>IsShortcut</c> is missing, which is the fingerprint of the older implementation
    /// and the reason a machine's taskbar pins stop working.
    /// </summary>
    public static bool TaskbarPinsAreBroken() =>
        Exists(LnkFileKey) && Read(LnkFileKey, IsShortcutValue) is null;

    /// <summary>True when this PC is carrying either kind of damage.</summary>
    public static bool NeedsRepair() => ShortcutIconsAreBlacked() || TaskbarPinsAreBroken();

    /// <summary>
    /// Puts both settings back to the Windows default. Returns false if the user dismissed the
    /// UAC prompt or the change did not take. The caller should refresh the shell afterwards with
    /// <see cref="RestartExplorer"/>, without which the black squares stay on screen from the icon
    /// cache even once the registry is correct.
    /// </summary>
    public static bool TryRepair()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("cmd.exe", BuildCommand())
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

        return !NeedsRepair();
    }

    /// <summary>
    /// The elevated command line, split out so the two regressions it repairs stay covered by
    /// tests rather than by remembering. Both edits go in one invocation so the user sees a
    /// single UAC prompt.
    ///
    /// The steps are joined with <c>&amp;</c> rather than <c>&amp;&amp;</c> because deleting a
    /// value that is not there returns a failure code, and that case is a success as far as this
    /// is concerned - the outcome is verified by reading the registry back afterwards instead of
    /// by trusting an exit code.
    /// </summary>
    internal static string BuildCommand()
    {
        // No /d on this one: reg.exe then writes empty data, which is the Windows default.
        var pins = $@"reg add ""HKLM\{LnkFileKey}"" /v {IsShortcutValue} /t REG_SZ /f";

        var overlay = $@"reg delete ""HKLM\{ShellIconsKey}"" /v {ShortcutOverlayValue} /f";

        return $"/c {pins} & {overlay}";
    }

    /// <summary>
    /// Makes the repair visible: clears the per-user icon cache, then restarts Explorer.
    ///
    /// The cache flush matters. Explorer keeps rendered icons keyed by source, and without
    /// <c>ie4uinit -show</c> a restart alone leaves the black squares painted on every item that
    /// was already cached, which reads as the repair having silently failed.
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
            // Not present on every build. The restart below still applies the repair; at worst a
            // few already-cached icons keep their black square until the cache turns over.
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

        // Windows relaunches the shell by itself, and starting a second one opens a stray folder
        // window over the desktop. So this only steps in when the shell has not come back.
        if (ShellIsBack()) return;

        Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
    }

    private static bool ShellIsBack()
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (Process.GetProcessesByName("explorer").Length > 0) return true;
            Thread.Sleep(250);
        }

        return false;
    }

    /// <summary>
    /// HKLM is readable but not writable without elevation, and can be locked down altogether.
    /// Reads therefore report "nothing there" rather than taking the caller down with them.
    /// </summary>
    private static object? Read(string key, string value)
    {
        try
        {
            using var handle = Registry.LocalMachine.OpenSubKey(key);
            return handle?.GetValue(value);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool Exists(string key)
    {
        try
        {
            using var handle = Registry.LocalMachine.OpenSubKey(key);
            return handle is not null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
