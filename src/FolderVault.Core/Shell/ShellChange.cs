using System.Runtime.InteropServices;

namespace FolderVault.Core.Shell;

/// <summary>
/// Tells Explorer that FolderVault changed something on disk.
///
/// <para>Explorer does not re-read a folder because the bytes changed; it re-reads because it was
/// told to. Shell APIs raise that notification themselves - creating the decoy through
/// <c>IPersistFile::Save</c> announces itself, which is why a lock always appeared instantly - but
/// plain file I/O does not, and <see cref="Directory.Move"/> is plain file I/O. The moves that
/// lock and unlock a folder went through unannounced, leaving an open Explorer window showing a
/// folder that is no longer there and the desktop unable to find the one that just arrived.</para>
///
/// <para>That asymmetry is what stopped an unlocked folder returning to its old spot on the
/// desktop: the item could not be positioned because, as far as the view was concerned, it did not
/// exist yet.</para>
/// </summary>
public static class ShellChange
{
    public static void DirectoryCreated(string path) => Notify(ShcneMkdir, path);

    public static void DirectoryRemoved(string path) => Notify(ShcneRmdir, path);

    public static void FileRemoved(string path) => Notify(ShcneDelete, path);

    /// <summary>
    /// Announces one change and waits for the shell to have distributed it, so a caller that is
    /// about to look for the item in a view does not race the notification it just sent.
    /// </summary>
    private static void Notify(uint eventId, string path)
    {
        try
        {
            SHChangeNotify(eventId, ShcnfPathW | ShcnfFlush, path, nint.Zero);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Nothing here is load-bearing: without it Explorer refreshes on its own schedule.
        }
    }

    private const uint ShcneDelete = 0x00000004;
    private const uint ShcneMkdir = 0x00000008;
    private const uint ShcneRmdir = 0x00000010;

    private const uint ShcnfPathW = 0x0005;

    /// <summary>Deliver synchronously rather than queueing, so the change is visible on return.</summary>
    private const uint ShcnfFlush = 0x1000;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(uint wEventId, uint uFlags,
        [MarshalAs(UnmanagedType.LPWStr)] string dwItem1, nint dwItem2);
}
