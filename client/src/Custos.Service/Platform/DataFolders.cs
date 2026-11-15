using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Custos.Core;
using Custos.Core.Agent;

namespace Custos.Service.Platform;

/// <summary>Where the service keeps its files (section 3.3).</summary>
internal static class DataFolders
{
    public static string Config { get; } = Path.Combine(CustosInfo.DataDirectory, "config");

    public static string Database { get; } = Path.Combine(CustosInfo.DataDirectory, "db");

    public static string RunLogs { get; } = Path.Combine(CustosInfo.LogsDirectory, "runs");

    /// <summary>The journal of each day (<see cref="JournalFile"/>).</summary>
    public static string Journal => CustosInfo.JournalDirectory;

    /// <summary>Downloaded 7-Zip installers and the 7z.dll unpacked from them: the service loads a DLL from here, so
    /// it is closed like <c>config</c>.</summary>
    public static string Updates { get; } = Path.Combine(CustosInfo.DataDirectory, "updates");

    public static string DatabaseFile { get; } = Path.Combine(Database, "custos.db");

    /// <summary>Ids of shadow copies made and not yet deleted (see <see cref="ShadowCopies"/>).</summary>
    public static string PendingShadowsFile { get; } = Path.Combine(Database, "shadow-copies.txt");

    /// <summary>
    /// Creates the folders and closes <c>config</c>, <c>db</c> and <c>updates</c> to everyone but SYSTEM and Administrators (section 3.3).
    /// Without that a user could give himself administrator rights by editing tasks.json: the service runs what it says.
    /// Files a user may have put there beforehand get the same protection and lose their owner.
    /// </summary>
    /// <returns>What could not be protected, for the service log.</returns>
    [SupportedOSPlatform("windows")]
    public static List<string> Prepare()
    {
        var problems = new List<string>();
        Directory.CreateDirectory(CustosInfo.LogsDirectory);
        Directory.CreateDirectory(RunLogs);
        Directory.CreateDirectory(Journal);
        foreach (string folder in new[] { Config, Database, Updates })
        {
            Directory.CreateDirectory(folder);
            Protect(folder, problems);
        }

        return problems;
    }

    [SupportedOSPlatform("windows")]
    private static void Protect(string folder, List<string> problems)
    {
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

        try
        {
            var security = new DirectorySecurity();
            security.SetOwner(administrators);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(administrators, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(folder).SetAccessControl(security);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException or PrivilegeNotHeldException)
        {
            problems.Add($"{folder}: {ex.Message}");
            return;
        }

        // Everything inside: own rules dropped, rights only from the folder.
        foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
        {
            try
            {
                if (entry is DirectoryInfo directory)
                {
                    var child = new DirectorySecurity();
                    child.SetOwner(administrators);
                    child.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
                    directory.SetAccessControl(child);
                }
                else if (entry is FileInfo file)
                {
                    var child = new FileSecurity();
                    child.SetOwner(administrators);
                    child.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
                    file.SetAccessControl(child);
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException or PrivilegeNotHeldException)
            {
                problems.Add($"{entry.FullName}: {ex.Message}");
            }
        }
    }
}

/// <summary>DPAPI with the key of the account the service runs as (SYSTEM): a copied file is useless elsewhere.</summary>
[SupportedOSPlatform("windows")]
internal sealed class DpapiProtector : ISecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("CUSTOS credentials v1");

    public byte[] Protect(byte[] data) => ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] data) => ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);
}
