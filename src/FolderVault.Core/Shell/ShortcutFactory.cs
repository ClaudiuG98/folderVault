using System.Runtime.InteropServices;
using System.Text;

namespace FolderVault.Core.Shell;

/// <summary>
/// Creates the decoy that stands in for a folder while it is locked.
///
/// A locked folder is replaced by <c>&lt;name&gt;.lnk</c> pointing at FolderVault. Explorer
/// registers <c>NeverShowExt</c> for the <c>lnkfile</c> type, so the <c>.lnk</c> suffix is never
/// rendered - even with "show file extensions" enabled - and a shortcut named <c>Photos.lnk</c>
/// displays as exactly <c>Photos</c>. Given the standard folder icon from imageres.dll it reads
/// as a folder; the only tell is the shortcut arrow overlay, which
/// <see cref="ShellArrowOverlay"/> can optionally suppress.
/// </summary>
public static class ShortcutFactory
{
    /// <summary>The stock closed-folder icon that Explorer itself uses.</summary>
    public const string FolderIconLocation = @"%SystemRoot%\System32\imageres.dll";

    public const int FolderIconIndex = 3;

    public static void Create(string shortcutPath, string targetExe, string arguments, string description)
    {
        var link = (IShellLinkW)new ShellLink();

        link.SetPath(targetExe);
        link.SetArguments(arguments);
        link.SetDescription(description);
        link.SetIconLocation(Environment.ExpandEnvironmentVariables(FolderIconLocation), FolderIconIndex);

        // Run from the exe's own directory: the shortcut stands where the locked folder was, and
        // that path does not exist while locked.
        link.SetWorkingDirectory(Path.GetDirectoryName(targetExe) ?? Environment.CurrentDirectory);

        ((IPersistFile)link).Save(shortcutPath, fRemember: true);
        Marshal.ReleaseComObject(link);
    }

    /// <summary>Reads a shortcut's arguments, used to confirm a decoy belongs to a given vault.</summary>
    public static string? TryReadArguments(string shortcutPath)
    {
        if (!File.Exists(shortcutPath)) return null;

        try
        {
            var link = (IShellLinkW)new ShellLink();
            ((IPersistFile)link).Load(shortcutPath, 0);
            var buffer = new StringBuilder(1024);
            link.GetArguments(buffer, buffer.Capacity);
            Marshal.ReleaseComObject(link);
            return buffer.ToString();
        }
        catch (COMException)
        {
            return null;
        }
    }

    /// <summary>The executable a shortcut points at, or null if it cannot be read.</summary>
    public static string? TryReadTarget(string shortcutPath)
    {
        if (!File.Exists(shortcutPath)) return null;

        try
        {
            var link = (IShellLinkW)new ShellLink();
            ((IPersistFile)link).Load(shortcutPath, 0);
            var buffer = new StringBuilder(1024);
            link.GetPath(buffer, buffer.Capacity, nint.Zero, 0);
            Marshal.ReleaseComObject(link);
            return buffer.ToString();
        }
        catch (COMException)
        {
            return null;
        }
    }

    public static void Delete(string shortcutPath)
    {
        if (File.Exists(shortcutPath)) File.Delete(shortcutPath);
    }

    // ---- COM interop. Method order below must match the vtable exactly. ----

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink;

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch,
            nint pfd, uint fFlags);
        void GetIDList(out nint ppidl);
        void SetIDList(nint pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out ushort pwHotkey);
        void SetHotkey(ushort wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch,
            out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(nint hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("0000010B-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName,
            [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }
}
