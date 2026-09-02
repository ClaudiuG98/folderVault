using System.Runtime.InteropServices;

namespace FolderVault.Core.Shell;

/// <summary>
/// Reports which folders currently have an Explorer window open on them, so a vault can re-lock
/// once the user closes the window they opened it with.
///
/// There is no notification API for "an Explorer window closed", so this polls the shell's
/// window collection via the <c>Shell.Application</c> COM object. Polling every few seconds is
/// cheap - the collection is normally a handful of windows - and it is the same source Explorer
/// itself exposes to scripting.
/// </summary>
public static class ExplorerWatcher
{
    /// <summary>
    /// Local filesystem paths of every open Explorer window, including any folder navigated to
    /// inside one. Returns an empty set if the shell cannot be queried.
    /// </summary>
    public static HashSet<string> GetOpenFolderPaths()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var shellType = Type.GetTypeFromProgID("Shell.Application");
        if (shellType is null) return paths;

        object? shell = null;
        try
        {
            shell = Activator.CreateInstance(shellType);
            if (shell is null) return paths;

            dynamic windows = ((dynamic)shell).Windows();
            foreach (dynamic window in windows)
            {
                try
                {
                    string? url = window.LocationURL;
                    if (!string.IsNullOrEmpty(url) && TryConvertFileUri(url, out var path))
                        paths.Add(path);
                }
                catch (COMException)
                {
                    // A window closed mid-enumeration, or is an Internet Explorer-style window
                    // with no filesystem location. Neither is interesting here.
                }
                finally
                {
                    if (window is not null && Marshal.IsComObject(window))
                        Marshal.ReleaseComObject(window);
                }
            }
        }
        catch (COMException)
        {
            // Explorer is restarting or the shell is not scriptable right now. Treat as unknown;
            // callers must not lock on an empty result alone.
        }
        finally
        {
            if (shell is not null && Marshal.IsComObject(shell))
                Marshal.ReleaseComObject(shell);
        }

        return paths;
    }

    /// <summary>True when a window is open on <paramref name="folderPath"/> or anywhere beneath it.</summary>
    public static bool IsAnyWindowOpenUnder(string folderPath, HashSet<string> openPaths)
    {
        var target = Path.GetFullPath(folderPath).TrimEnd(Path.DirectorySeparatorChar);

        return openPaths.Any(open =>
            open.Equals(target, StringComparison.OrdinalIgnoreCase) ||
            open.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Turns a <c>file:///</c> URL into a normal path. Explorer percent-encodes non-ASCII names,
    /// so a vault called "Café" would never match without unescaping first.
    /// </summary>
    private static bool TryConvertFileUri(string url, out string path)
    {
        path = string.Empty;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.IsFile)
            return false;

        try
        {
            path = Path.GetFullPath(uri.LocalPath).TrimEnd(Path.DirectorySeparatorChar);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
