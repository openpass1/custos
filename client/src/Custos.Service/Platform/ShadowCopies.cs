using System.ComponentModel;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Custos.Core.Backup;

namespace Custos.Service.Platform;

/// <summary>
/// Shadow copies (VSS) of the source volumes, made through WMI (<c>Win32_ShadowCopy</c>, context "ClientAccessible":
/// no writers, the provider of Windows). A shadow copy is read through its device path
/// (<c>\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopyN\…</c>) and deleted when the run ends.
/// </summary>
/// <remarks>
/// Such a shadow copy outlives the process, so the ids of the ones made are written to a file and any left there by a
/// crash are deleted when the service starts again.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class ShadowCopies(string pendingFile)
{
    private readonly Lock _fileLock = new();

    public ISnapshotSet Create(IReadOnlyCollection<string> paths)
    {
        var volumes = paths.Select(VolumeRoot).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var made = new List<Shadow>();
        try
        {
            foreach (string volume in volumes)
            {
                made.Add(CreateOne(volume));
            }
        }
        catch
        {
            foreach (var shadow in made)
            {
                Delete(shadow.Id);
            }

            throw;
        }

        return new Set(this, made);
    }

    /// <summary>Deletes the shadow copies a crashed run left behind.</summary>
    public int DeleteLeftovers()
    {
        List<string> ids;
        lock (_fileLock)
        {
            if (!File.Exists(pendingFile))
            {
                return 0;
            }

            ids = [.. File.ReadAllLines(pendingFile).Select(l => l.Trim()).Where(l => l.Length > 0)];
        }

        int deleted = 0;
        foreach (string id in ids)
        {
            if (Delete(id))
            {
                deleted++;
            }
        }

        return deleted;
    }

    private Shadow CreateOne(string volume)
    {
        var scope = Scope();
        using var shadowClass = new ManagementClass(scope, new ManagementPath("Win32_ShadowCopy"), new ObjectGetOptions(Context()));
        using var parameters = shadowClass.GetMethodParameters("Create");
        parameters["Volume"] = VolumeName(volume);
        parameters["Context"] = "ClientAccessible";
        uint code;
        string? id;
        try
        {
            using var result = shadowClass.InvokeMethod("Create", parameters, new InvokeMethodOptions(Context(), TimeSpan.MaxValue));
            code = Convert.ToUInt32(result["ReturnValue"], System.Globalization.CultureInfo.InvariantCulture);
            id = result["ShadowID"] as string;
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
        {
            throw new BackupException(ex.Message, ex);
        }

        if (code != 0 || string.IsNullOrEmpty(id))
        {
            throw new BackupException($"{volume}: {DescribeCode(code)}");
        }

        Remember(id, add: true);
        string? device = null;
        using (var searcher = new ManagementObjectSearcher(scope, new ObjectQuery($"SELECT ID, DeviceObject FROM Win32_ShadowCopy WHERE ID = '{id}'"), new System.Management.EnumerationOptions { Context = Context() }))
        using (var found = searcher.Get())
        {
            foreach (var item in found)
            {
                using (item)
                {
                    device ??= item["DeviceObject"] as string;
                }
            }
        }

        if (string.IsNullOrEmpty(device))
        {
            Delete(id);
            throw new BackupException($"{volume}: the shadow copy {id} has no device.");
        }

        return new Shadow(volume, id, device.TrimEnd('\\'));
    }

    private bool Delete(string id)
    {
        bool deleted = false;
        try
        {
            using var searcher = new ManagementObjectSearcher(Scope(), new ObjectQuery($"SELECT * FROM Win32_ShadowCopy WHERE ID = '{id}'"), new System.Management.EnumerationOptions { Context = Context() });
            using var found = searcher.Get();
            foreach (ManagementObject item in found)
            {
                using (item)
                {
                    item.Delete(new DeleteOptions(Context(), TimeSpan.MaxValue));
                    deleted = true;
                }
            }

            // Gone already counts as deleted.
            Remember(id, add: false);
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
        {
            // Stays in the file; the next start tries again.
        }

        return deleted;
    }

    private void Remember(string id, bool add)
    {
        lock (_fileLock)
        {
            var ids = File.Exists(pendingFile) ? File.ReadAllLines(pendingFile).Select(l => l.Trim()).Where(l => l.Length > 0).ToList() : [];
            ids.RemoveAll(i => string.Equals(i, id, StringComparison.OrdinalIgnoreCase));
            if (add)
            {
                ids.Add(id);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(pendingFile)!);
            File.WriteAllLines(pendingFile, ids);
        }
    }

    /// <summary>The WMI namespace of the shadow copies, asking for the provider that matches Windows (see <see cref="Context"/>).</summary>
    private static ManagementScope Scope() => new(@"\\.\root\cimv2", new ConnectionOptions { Context = Context() });

    /// <summary>
    /// A 32-bit program on 64-bit Windows (the legacy edition) gets the 32-bit WMI providers, and the 32-bit shadow copy
    /// provider cannot make shadow copies there. These values ask WMI for the 64-bit one; for a 64-bit program, or on
    /// 32-bit Windows, the context is empty and nothing changes.
    /// </summary>
    private static ManagementNamedValueCollection Context()
    {
        var context = new ManagementNamedValueCollection();
        if (Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess)
        {
            context.Add("__ProviderArchitecture", 64);
            context.Add("__RequiredArchitecture", true);
        }

        return context;
    }

    /// <summary>The root of the volume a path is on: <c>C:\</c>, or a mount point folder.</summary>
    private static string VolumeRoot(string path)
    {
        var buffer = new char[1024];
        if (!NativeMethods.GetVolumePathName(Path.GetFullPath(path), buffer, buffer.Length))
        {
            throw new BackupException($"{path}: {new Win32Exception().Message}");
        }

        string root = new string(buffer).TrimEnd('\0');
        return root.EndsWith('\\') ? root : root + "\\";
    }

    /// <summary>The volume GUID path (<c>\\?\Volume{…}\</c>) of a volume root, which WMI takes for any volume.</summary>
    private static string VolumeName(string volumeRoot)
    {
        var buffer = new char[64];
        return NativeMethods.GetVolumeNameForVolumeMountPoint(volumeRoot, buffer, buffer.Length)
            ? new string(buffer).TrimEnd('\0')
            : volumeRoot;
    }

    private static string DescribeCode(uint code) => code switch
    {
        1 => "access denied (1)",
        2 => "invalid argument (2)",
        3 => "the volume was not found (3)",
        4 => "the volume does not support shadow copies (4)",
        5 => "this shadow copy context is not supported (5)",
        6 => "not enough storage for the shadow copy (6)",
        7 => "the volume is in use (7)",
        8 => "the maximum number of shadow copies is reached (8)",
        9 => "another shadow copy operation is in progress (9)",
        10 => "the shadow copy provider refused (10)",
        11 => "the shadow copy provider is not registered (11)",
        12 => "the shadow copy provider failed (12)",
        _ => $"unknown error ({code})",
    };

    private sealed record Shadow(string VolumeRoot, string Id, string Device);

    private sealed class Set(ShadowCopies owner, List<Shadow> shadows) : ISnapshotSet
    {
        private bool _disposed;

        public string Map(string path)
        {
            string full = Path.GetFullPath(path);
            foreach (var shadow in shadows.OrderByDescending(s => s.VolumeRoot.Length))
            {
                if (full.StartsWith(shadow.VolumeRoot, StringComparison.OrdinalIgnoreCase))
                {
                    return shadow.Device + "\\" + full[shadow.VolumeRoot.Length..];
                }

                // "D:\" given as "D:" or the volume root itself.
                if (string.Equals(full + "\\", shadow.VolumeRoot, StringComparison.OrdinalIgnoreCase))
                {
                    return shadow.Device + "\\";
                }
            }

            return Wow64Paths.Native(path);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var shadow in shadows)
            {
                owner.Delete(shadow.Id);
            }
        }
    }
}
