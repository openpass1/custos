using System.Text.RegularExpressions;
using Custos.Core;
using Custos.Core.Backup;

namespace Custos.Service.Platform;

/// <summary>
/// The macOS counterpart of the shadow copies of Windows: a local APFS snapshot of the startup disk, made the way Time
/// Machine makes them (<c>tmutil localsnapshot</c>), mounted read-only into <see cref="DataFolders.Snapshots"/> for
/// the run and deleted when it ends. Only the Data volume of the startup disk gets one: it holds everything people
/// back up (/Users, /Applications, /Library, /opt, /private …). Other volumes (/Volumes/…) are read as they are.
/// </summary>
/// <remarks>
/// A snapshot outlives the process, so the ones made are written to a file and any left there by a crash are
/// deleted when the service starts again. Mounting a snapshot needs Full Disk Access for the service.
/// </remarks>
internal sealed partial class ShadowCopies(string pendingFile)
{
    private const string DataVolume = "/System/Volumes/Data";
    private const string Tmutil = "/usr/bin/tmutil";

    /// <summary>Folders of the root that are the Data volume (/usr/share/firmlinks lists them on every Mac).</summary>
    private static readonly Lazy<string[]> Firmlinks = new(ReadFirmlinks);

    private readonly Lock _fileLock = new();

    public ISnapshotSet Create(IReadOnlyCollection<string> paths)
    {
        if (!OperatingSystem.IsMacOS() || !paths.Any(IsOnDataVolume))
        {
            return NoSnapshots.Instance;
        }

        var (code, output) = Tool.Run(Tmutil, ["localsnapshot"], TimeSpan.FromMinutes(2));
        var match = CreatedLine().Match(output);
        if (code != 0 || !match.Success)
        {
            throw new BackupException(Messages.Format(Messages.MacSnapshotFailed, output.Length > 0 ? output : $"tmutil: {code}"));
        }

        string date = match.Groups[1].Value;
        Remember(date);
        string mountPoint = Path.Combine(DataFolders.Snapshots, date);
        try
        {
            Directory.CreateDirectory(mountPoint);
            string device = DataDevice() ?? throw new BackupException(Messages.Format(Messages.MacSnapshotFailed, "no device of " + DataVolume));
            var (mounted, mountOutput) = Tool.Run("/sbin/mount_apfs", ["-o", "nobrowse", "-o", "rdonly", "-s", SnapshotName(date), device, mountPoint], TimeSpan.FromMinutes(1));
            if (mounted != 0)
            {
                throw new BackupException(Messages.Format(Messages.MacSnapshotFailed, mountOutput.Length > 0 ? mountOutput : $"mount_apfs: {mounted}"));
            }
        }
        catch
        {
            Delete(date);
            throw;
        }

        return new Set(this, date, mountPoint);
    }

    /// <summary>Deletes the snapshots a crashed run left behind.</summary>
    public int DeleteLeftovers()
    {
        List<string> dates;
        lock (_fileLock)
        {
            if (!File.Exists(pendingFile))
            {
                return 0;
            }

            dates = [.. File.ReadAllLines(pendingFile).Select(l => l.Trim()).Where(l => l.Length > 0)];
        }

        foreach (string date in dates)
        {
            Delete(date);
        }

        return dates.Count;
    }

    /// <summary>Whether the file is on the Data volume of the startup disk.</summary>
    internal static bool IsOnDataVolume(string path)
    {
        string full = WithoutSystemLinks(path);
        if (full.StartsWith(DataVolume + "/", StringComparison.Ordinal))
        {
            return true;
        }

        if (full.StartsWith("/Volumes/", StringComparison.Ordinal))
        {
            return false;
        }

        return Firmlinks.Value.Any(root => full == root || full.StartsWith(root + "/", StringComparison.Ordinal));
    }

    /// <summary>Where a file of the Data volume is inside its mounted snapshot.</summary>
    internal static string InSnapshot(string mountPoint, string path)
    {
        string full = WithoutSystemLinks(path);
        string inVolume = full.StartsWith(DataVolume + "/", StringComparison.Ordinal) ? full[DataVolume.Length..] : full;
        return mountPoint + inVolume;
    }

    /// <summary>/tmp, /var and /etc are links of the system to /private/…; the snapshot only has /private.</summary>
    private static string WithoutSystemLinks(string path)
    {
        string full = Path.GetFullPath(path);
        foreach (string link in new[] { "/tmp", "/var", "/etc" })
        {
            if (full == link || full.StartsWith(link + "/", StringComparison.Ordinal))
            {
                return "/private" + full;
            }
        }

        return full;
    }

    private static string[] ReadFirmlinks()
    {
        try
        {
            if (File.Exists("/usr/share/firmlinks"))
            {
                return [.. File.ReadAllLines("/usr/share/firmlinks")
                    .Select(l => l.Split('\t')[0].Trim())
                    .Where(l => l.StartsWith('/') && l != "/Volumes")];
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The usual list below.
        }

        return ["/Applications", "/Library", "/Users", "/opt", "/private", "/usr/local", "/cores", "/System/Library/Caches"];
    }

    /// <summary>The device of the Data volume ("/dev/disk3s5") from the list of mounted volumes.</summary>
    private static string? DataDevice()
    {
        var (code, output) = Tool.Run("/sbin/mount", [], TimeSpan.FromSeconds(30));
        if (code != 0)
        {
            return null;
        }

        foreach (string line in output.Split('\n'))
        {
            int on = line.IndexOf(" on " + DataVolume + " (", StringComparison.Ordinal);
            if (on > 0)
            {
                return line[..on];
            }
        }

        return null;
    }

    private static string SnapshotName(string date) => $"com.apple.TimeMachine.{date}.local";

    private void Remember(string date)
    {
        lock (_fileLock)
        {
            File.AppendAllLines(pendingFile, [date]);
        }
    }

    private void Delete(string date)
    {
        string mountPoint = Path.Combine(DataFolders.Snapshots, date);
        if (Directory.Exists(mountPoint))
        {
            Tool.Run("/sbin/umount", ["-f", mountPoint], TimeSpan.FromMinutes(1));
            try
            {
                Directory.Delete(mountPoint);
            }
            catch (IOException)
            {
                // Still mounted: tried again at the next start.
            }
        }

        var (code, output) = Tool.Run(Tmutil, ["deletelocalsnapshots", date], TimeSpan.FromMinutes(2));
        bool gone = code == 0 || output.Contains("No local snapshot", StringComparison.OrdinalIgnoreCase)
            || output.Contains("not found", StringComparison.OrdinalIgnoreCase);
        if (!gone)
        {
            return;
        }

        lock (_fileLock)
        {
            if (File.Exists(pendingFile))
            {
                var left = File.ReadAllLines(pendingFile).Select(l => l.Trim()).Where(l => l.Length > 0 && l != date).ToList();
                if (left.Count == 0)
                {
                    File.Delete(pendingFile);
                }
                else
                {
                    File.WriteAllLines(pendingFile, left);
                }
            }
        }
    }

    [GeneratedRegex(@"(\d{4}-\d{2}-\d{2}-\d{6})")]
    private static partial Regex CreatedLine();

    private sealed class Set(ShadowCopies owner, string date, string mountPoint) : ISnapshotSet
    {
        private bool _disposed;

        public string Map(string path) => IsOnDataVolume(path) ? InSnapshot(mountPoint, path) : path;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            owner.Delete(date);
        }
    }
}
