using System.Security.AccessControl;
using System.Security.Principal;

namespace FolderVault.Core.Ops;

/// <summary>
/// The NTFS half of Fast mode: a Deny ACE on the stored payload so that browsing to it with
/// hidden files shown still yields "Access denied".
///
/// This is defence in depth, not the protection itself. Windows grants a file's owner implicit
/// WRITE_DAC, so the owner - and any administrator - can always strip this ACE and read the
/// files. That is inherent to NTFS and is exactly why Fast mode is documented as obfuscation
/// rather than encryption. Users who need real protection should choose Secure mode.
/// </summary>
public static class Acl
{
    /// <summary>Denies the current user all access to <paramref name="path"/>.</summary>
    public static void Deny(string path)
    {
        var directory = new DirectoryInfo(path);
        var security = directory.GetAccessControl();

        // Protect the DACL so inherited Allow rules from the drive root cannot override the Deny.
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            CurrentUser(),
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Deny));

        directory.SetAccessControl(security);
    }

    /// <summary>
    /// Removes the Deny ACE and restores inheritance. Must succeed before the payload can be
    /// moved back; if it throws, the caller aborts the unlock and leaves the vault locked, which
    /// is recoverable, rather than proceeding into a half-restored state.
    /// </summary>
    public static void RemoveDeny(string path)
    {
        var directory = new DirectoryInfo(path);
        var security = directory.GetAccessControl();

        var user = CurrentUser();
        foreach (FileSystemAccessRule rule in security.GetAccessRules(
                     includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType == AccessControlType.Deny && rule.IdentityReference.Equals(user))
                security.RemoveAccessRuleSpecific(rule);
        }

        security.SetAccessRuleProtection(isProtected: false, preserveInheritance: true);
        directory.SetAccessControl(security);
    }

    /// <summary>True if the current user can still enumerate the directory.</summary>
    public static bool IsReadable(string path)
    {
        try
        {
            Directory.EnumerateFileSystemEntries(path).FirstOrDefault();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static SecurityIdentifier CurrentUser() =>
        WindowsIdentity.GetCurrent().User
        ?? throw new InvalidOperationException("Cannot determine the current Windows user.");
}
