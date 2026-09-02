namespace FolderVault.Core.Store;

/// <summary>
/// Small helpers for writes that must not leave a half-written file behind if the process dies.
/// </summary>
public static class AtomicFile
{
    /// <summary>
    /// Writes to a sibling temp file, flushes it to the physical disk, then renames over the
    /// destination. A crash therefore leaves either the old file or the new one, never a
    /// truncated mix. The explicit flush matters: without it the rename can reach the disk
    /// before the contents do, and a power loss yields an empty file.
    /// </summary>
    public static void WriteAllBytes(string path, byte[] contents)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(contents);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, path, overwrite: true);
    }

    public static byte[]? ReadAllBytesOrNull(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Deletes a directory tree, tolerating read-only and hidden entries.</summary>
    public static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path)) return;

        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            try
            {
                var attributes = File.GetAttributes(file);
                if (attributes.HasFlag(FileAttributes.ReadOnly))
                    File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
            }
            catch (IOException) { /* best effort; the delete below will report the real problem */ }
        }

        Directory.Delete(path, recursive: true);
    }
}
