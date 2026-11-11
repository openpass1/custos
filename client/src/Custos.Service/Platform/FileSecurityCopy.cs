using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using Custos.Core.Backup;

namespace Custos.Service.Platform;

/// <summary>
/// Copies NTFS permissions, owner and group with plain copies (section 6.8). Setting another owner needs
/// SeRestorePrivilege, which the service account holds but has off. A share that does not take the owner (another
/// domain, a NAS) gets the permissions alone.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class FileSecurityCopy
{
    private const AccessControlSections All = AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group;

    private static readonly Lazy<bool> RestorePrivilege = new(() =>
    {
        try
        {
            return Privileges.Enable(Privileges.Restore);
        }
        catch (Win32Exception)
        {
            return false;
        }
    });

    /// <summary>Whether a volume (by its root) keeps permissions; asked once per volume.</summary>
    private static readonly ConcurrentDictionary<string, bool> Persistent = new(StringComparer.OrdinalIgnoreCase);

    public static byte[]? Read(string path, bool folder)
    {
        try
        {
            NativeObjectSecurity security = folder
                ? new DirectoryInfo(path).GetAccessControl(All)
                : new FileInfo(path).GetAccessControl(All);
            return security.GetSecurityDescriptorBinaryForm();
        }
        catch (Exception ex) when (IsSecurityError(ex))
        {
            return null;
        }
    }

    public static bool Write(string path, bool folder, byte[] descriptor)
    {
        if (!KeepsPermissions(path))
        {
            return false;
        }

        _ = RestorePrivilege.Value;
        try
        {
            Apply(path, folder, descriptor, All);
        }
        catch (Exception ex) when (IsSecurityError(ex))
        {
            try
            {
                Apply(path, folder, descriptor, AccessControlSections.Access);
            }
            catch (Exception again) when (IsSecurityError(again))
            {
                throw new BackupException(again.Message, again);
            }
        }

        return true;
    }

    private static void Apply(string path, bool folder, byte[] descriptor, AccessControlSections sections)
    {
        if (folder)
        {
            var security = new DirectorySecurity();
            security.SetSecurityDescriptorBinaryForm(descriptor, sections);
            new DirectoryInfo(path).SetAccessControl(security);
        }
        else
        {
            var security = new FileSecurity();
            security.SetSecurityDescriptorBinaryForm(descriptor, sections);
            new FileInfo(path).SetAccessControl(security);
        }
    }

    private static bool KeepsPermissions(string path)
    {
        char[] root = new char[1024];
        if (!NativeMethods.GetVolumePathName(path, root, root.Length))
        {
            return true;
        }

        string volume = new string(root).TrimEnd('\0');
        return Persistent.GetOrAdd(volume, static v =>
            !NativeMethods.GetVolumeInformation(v, IntPtr.Zero, 0, out _, out _, out uint flags, IntPtr.Zero, 0)
            || (flags & NativeMethods.FILE_PERSISTENT_ACLS) != 0);
    }

    private static bool IsSecurityError(Exception ex) =>
        ex is UnauthorizedAccessException or IOException or InvalidOperationException or NotSupportedException
            or ArgumentException or PrivilegeNotHeldException;
}
