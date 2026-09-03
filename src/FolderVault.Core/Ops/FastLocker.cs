using FolderVault.Core.Shell;
using FolderVault.Core.Store;

namespace FolderVault.Core.Ops;

/// <summary>
/// Fast mode: the payload is moved, never copied.
///
/// Because the store lives on the same volume as the folder, both directions are a single
/// <see cref="Directory.Move"/> - an NTFS metadata rename that is atomic and completes in
/// milliseconds whether the folder holds one file or a hundred gigabytes. Nothing is ever
/// duplicated, so there is no window in which two divergent copies exist.
///
/// Secure mode reuses the same move to stage its plaintext, passing <c>applyAcl: false</c>
/// because that staged copy is transient and gets deleted once encryption verifies.
/// </summary>
public static class FastLocker
{
    /// <summary>Moves the folder from its original path into the store, optionally denying access.</summary>
    public static void Stage(string originalPath, string vaultStore, bool applyAcl = true,
        IProgress<OperationProgress>? progress = null)
    {
        var destination = VaultLayout.Plain(vaultStore);
        if (Directory.Exists(destination))
            throw new VaultOperationException(
                "The vault store already holds a payload. Run recovery before locking again.");

        progress?.Report(new OperationProgress("Moving folder into the vault store"));
        MoveDirectory(originalPath, destination);

        if (!applyAcl) return;

        progress?.Report(new OperationProgress("Applying access restrictions"));
        try
        {
            Acl.Deny(destination);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException)
        {
            // The move already succeeded and is what actually hides the folder. Losing the Deny
            // ACE weakens an already-obfuscation-grade mode; it is not worth undoing the lock.
            progress?.Report(new OperationProgress(
                "Locked, but access restrictions could not be applied: " + ex.Message));
        }
    }

    /// <summary>Restores access and moves the folder back to its original path.</summary>
    public static void Restore(string originalPath, string vaultStore, bool removeAcl = true,
        IProgress<OperationProgress>? progress = null)
    {
        var source = VaultLayout.Plain(vaultStore);
        if (!Directory.Exists(source))
            throw new VaultOperationException("The vault store does not contain a payload to restore.");

        if (Directory.Exists(originalPath))
            throw new VaultOperationException(
                $"Cannot restore: something already exists at '{originalPath}'. Move it aside and try again.");

        if (removeAcl)
        {
            progress?.Report(new OperationProgress("Restoring access"));
            try
            {
                Acl.RemoveDeny(source);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException)
            {
                throw new VaultOperationException(
                    "The access restriction on the stored folder could not be removed, so it cannot be " +
                    "moved back. The vault is still locked and your files are intact. See the README " +
                    "section \"Recovering a folder by hand\" for the icacls command that fixes this.", ex);
            }
        }

        progress?.Report(new OperationProgress("Moving folder back"));
        MoveDirectory(source, originalPath);
    }

    /// <summary>Same-volume directory rename, with the common failures turned into clear messages.</summary>
    public static void MoveDirectory(string source, string destination)
    {
        var parent = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

        try
        {
            Directory.Move(source, destination);

            // Directory.Move is plain file I/O and announces nothing. Explorer refreshes a view
            // when it is told to, so without this the folder keeps showing at the path it just
            // left, and the one that just arrived cannot be found in a view at all.
            ShellChange.DirectoryRemoved(source);
            ShellChange.DirectoryCreated(destination);
        }
        catch (IOException ex) when (IsSharingViolation(ex))
        {
            throw new PayloadInUseException(
                "A file in this folder is open in another program, so the folder cannot be moved. " +
                "Close anything using it and try again.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new VaultOperationException(
                "Windows refused to move the folder. It may be open in another program, or its " +
                "permissions may have been changed outside FolderVault.", ex);
        }
    }

    /// <summary>ERROR_SHARING_VIOLATION (32) and ERROR_LOCK_VIOLATION (33).</summary>
    private static bool IsSharingViolation(IOException ex) =>
        (ex.HResult & 0xFFFF) is 32 or 33;
}
