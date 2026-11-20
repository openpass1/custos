using System.Globalization;
using System.Text;

namespace Custos.Service.Platform;

/// <summary>One mounted file system, a line of /proc/self/mountinfo.</summary>
/// <param name="Root">The folder of the file system that is mounted ("/" for the whole of it, "/@home" for a btrfs subvolume).</param>
internal sealed record MountEntry(uint Major, uint Minor, string Root, string MountPoint, string FileSystem, string Source);

/// <summary>
/// The mounted file systems of this computer, as the kernel lists them for the service (/proc/self/mountinfo): what a
/// path lies on, what is mounted below a folder, and which mounts hold no data of their own (/proc, /sys, /run …) or
/// belong to another computer (NFS, SMB).
/// </summary>
internal sealed class MountTable
{
    /// <summary>File systems of the kernel and the memory, never data to back up.</summary>
    private static readonly HashSet<string> SystemTypes = new(StringComparer.Ordinal)
    {
        "proc", "sysfs", "devtmpfs", "devpts", "tmpfs", "ramfs", "securityfs", "cgroup", "cgroup2", "pstore", "bpf",
        "debugfs", "tracefs", "configfs", "fusectl", "mqueue", "hugetlbfs", "autofs", "binfmt_misc", "efivarfs", "nsfs",
        "rpc_pipefs", "nfsd", "selinuxfs", "overlay", "squashfs", "fuse.lxcfs", "fuse.gvfsd-fuse", "fuse.portal",
        "fuse.snapfuse", "devfs", "usbfs", "xenfs", "sockfs", "pipefs", "anon_inodefs", "aufs",
    };

    /// <summary>File systems of other computers.</summary>
    private static readonly HashSet<string> NetworkTypes = new(StringComparer.Ordinal)
    {
        "nfs", "nfs4", "cifs", "smb3", "smbfs", "9p", "ceph", "glusterfs", "fuse.glusterfs", "fuse.sshfs", "sshfs", "davfs",
        "fuse.davfs", "afs", "lustre", "fuse.rclone", "fuse.s3fs", "ocfs2", "gfs2",
    };

    private readonly Dictionary<string, MountEntry> _byPoint;

    public MountTable(IReadOnlyList<MountEntry> entries)
    {
        Entries = entries;
        _byPoint = new Dictionary<string, MountEntry>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            // Several file systems on one point: the last one mounted is the one seen there.
            _byPoint[entry.MountPoint] = entry;
        }
    }

    public IReadOnlyList<MountEntry> Entries { get; }

    /// <summary>The mounts of this process now.</summary>
    public static MountTable Read(string file = "/proc/self/mountinfo")
    {
        var entries = new List<MountEntry>();
        try
        {
            foreach (string line in File.ReadLines(file))
            {
                if (Parse(line) is { } entry)
                {
                    entries.Add(entry);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // No mount table (not Linux): nothing is mounted anywhere.
        }

        return new MountTable(entries);
    }

    /// <summary>"36 35 98:0 /mnt1 /mnt/parent rw,noatime master:1 - ext3 /dev/root rw,errors=continue".</summary>
    internal static MountEntry? Parse(string line)
    {
        string[] fields = line.Split(' ');
        int dash = fields.Length < 10 ? -1 : Array.IndexOf(fields, "-", 6);
        if (dash < 0 || dash + 2 >= fields.Length)
        {
            return null;
        }

        string[] device = fields[2].Split(':');
        if (device.Length != 2
            || !uint.TryParse(device[0], NumberStyles.None, CultureInfo.InvariantCulture, out uint major)
            || !uint.TryParse(device[1], NumberStyles.None, CultureInfo.InvariantCulture, out uint minor))
        {
            return null;
        }

        return new MountEntry(major, minor, Unescape(fields[3]), Unescape(fields[4]), fields[dash + 1], Unescape(fields[dash + 2]));
    }

    /// <summary>The kernel writes space, tab, new line and backslash as \040, \011, \012 and \134.</summary>
    internal static string Unescape(string text)
    {
        if (!text.Contains('\\', StringComparison.Ordinal))
        {
            return text;
        }

        var result = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\' && i + 3 < text.Length && TryOctal(text.AsSpan(i + 1, 3), out char decoded))
            {
                result.Append(decoded);
                i += 3;
            }
            else
            {
                result.Append(text[i]);
            }
        }

        return result.ToString();
    }

    private static bool TryOctal(ReadOnlySpan<char> digits, out char value)
    {
        int number = 0;
        foreach (char digit in digits)
        {
            if (digit is < '0' or > '7')
            {
                value = '\0';
                return false;
            }

            number = (number * 8) + (digit - '0');
        }

        value = (char)number;
        return true;
    }

    /// <summary>Whether the file system holds no data of its own (/proc, /sys, /run, snaps, overlays of containers).</summary>
    public static bool IsSystem(string fileSystem) => SystemTypes.Contains(fileSystem);

    /// <summary>Whether the file system is another computer's (NFS, SMB, sshfs).</summary>
    public static bool IsNetwork(string fileSystem) => NetworkTypes.Contains(fileSystem) || fileSystem.StartsWith("fuse.", StringComparison.Ordinal) && fileSystem.Contains("ssh", StringComparison.Ordinal);

    /// <summary>The mount seen at exactly this mount point, or null.</summary>
    public MountEntry? At(string mountPoint) => _byPoint.GetValueOrDefault(Trim(mountPoint));

    /// <summary>The mount a path lies on: the one with the longest mount point that is the path or a folder above it.</summary>
    public MountEntry? Of(string path)
    {
        string full = Trim(Path.GetFullPath(path));
        for (string? folder = full; folder is not null; folder = Parent(folder))
        {
            if (_byPoint.TryGetValue(folder, out var entry))
            {
                return entry;
            }
        }

        return null;
    }

    /// <summary>The mounts seen strictly below a folder, shallow ones first.</summary>
    public IEnumerable<MountEntry> Below(string folder)
    {
        string full = Trim(Path.GetFullPath(folder));
        string prefix = full == "/" ? "/" : full + "/";
        return _byPoint.Values
            .Where(e => e.MountPoint != full && e.MountPoint.StartsWith(prefix, StringComparison.Ordinal))
            .OrderBy(e => e.MountPoint.Count(c => c == '/'))
            .ThenBy(e => e.MountPoint, StringComparer.Ordinal);
    }

    /// <summary>"/var/www/" → "/var/www"; "/" stays.</summary>
    internal static string Trim(string path) => path.Length > 1 ? path.TrimEnd('/') is { Length: > 0 } trimmed ? trimmed : "/" : path;

    private static string? Parent(string path)
    {
        if (path == "/")
        {
            return null;
        }

        int slash = path.LastIndexOf('/');
        return slash <= 0 ? "/" : path[..slash];
    }
}
