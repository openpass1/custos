using System.Globalization;

namespace Custos.Service.Platform;

/// <summary>A logical volume of LVM as <c>lvs</c> reports it.</summary>
/// <param name="Attributes">lv_attr: the first letter is the kind ('-' an ordinary volume, 'o' one with snapshots, 's'
/// a snapshot, 'V' a thin volume), the fifth the state ('a' active, 'I' an invalid snapshot, one that ran out of room).</param>
internal sealed record LogicalVolume(
    string Group, string Name, int Major, int Minor, long Size, string Attributes, long GroupFree, long ExtentSize, string? Path)
{
    /// <summary>"ubuntu-vg/ubuntu-lv", as the LVM tools take it.</summary>
    public string FullName => Group + "/" + Name;

    public bool IsThin => Attributes.Length > 0 && Attributes[0] == 'V';

    public bool IsSnapshot => Attributes.Length > 0 && Attributes[0] is 's' or 'S';

    public bool IsInvalid => Attributes.Length > 4 && Attributes[4] == 'I';
}

/// <summary>
/// The tools of LVM (lvs, lvcreate, lvremove), for the snapshots of the volumes the sources lie on. A snapshot keeps
/// the blocks the volume changes while it exists, in the free room of the same volume group: without free room there
/// is no snapshot. Every snapshot of CUSTOS is tagged «custos», so that one left by a crash is found again.
/// </summary>
internal static class Lvm
{
    public const string Tag = "custos";

    /// <summary>The prefix of the names of the snapshots of CUSTOS ("custos-snap-1a2b3c4d-0").</summary>
    public const string SnapshotPrefix = "custos-snap-";

    private static readonly string[] Columns =
        ["vg_name", "lv_name", "lv_kernel_major", "lv_kernel_minor", "lv_size", "lv_attr", "vg_free", "vg_extent_size", "lv_dm_path"];

    /// <summary>Whether the tools of LVM are installed.</summary>
    public static bool Installed => Tool.Find("lvs") is not null;

    /// <summary>Every logical volume of this computer; empty without LVM. The problem, if lvs failed, in words.</summary>
    public static List<LogicalVolume> List(out string? problem, string? select = null)
    {
        problem = null;
        if (!Installed)
        {
            return [];
        }

        List<string> arguments = ["--noheadings", "--nosuffix", "--units", "b", "--separator", "|", "-o", string.Join(",", Columns)];
        if (select is not null)
        {
            arguments.Add(select);
        }

        var (code, output) = Tool.Run("lvs", arguments, out string lines, TimeSpan.FromMinutes(1));
        if (code != 0)
        {
            problem = Tool.Reason(output, "lvs", code);
            return [];
        }

        return [.. lines.Split('\n').Select(Parse).OfType<LogicalVolume>()];
    }

    /// <summary>One line of <see cref="List"/>.</summary>
    internal static LogicalVolume? Parse(string line)
    {
        string[] fields = line.Trim().Split('|');
        if (fields.Length < Columns.Length - 1)
        {
            return null;
        }

        return new LogicalVolume(
            fields[0].Trim(),
            fields[1].Trim(),
            Int(fields[2]),
            Int(fields[3]),
            Long(fields[4]),
            fields[5].Trim(),
            Long(fields[6]),
            Math.Max(1, Long(fields[7])),
            fields.Length > 8 && fields[8].Trim().Length > 0 ? fields[8].Trim() : null);
    }

    /// <summary>The logical volume of a mounted device (by its device numbers), or null if it is none.</summary>
    public static LogicalVolume? Find(IEnumerable<LogicalVolume> volumes, MountEntry mount) =>
        volumes.FirstOrDefault(v => v.Major == mount.Major && v.Minor == mount.Minor && v.Major >= 0);

    /// <summary>Creates a snapshot of <paramref name="origin"/> with this many extents of its group; null or the reason.</summary>
    public static string? CreateSnapshot(LogicalVolume origin, string name, long extents)
    {
        var (code, output) = Tool.Run("lvcreate",
            ["--snapshot", "--name", name, "--extents", extents.ToString(CultureInfo.InvariantCulture), "--addtag", Tag, origin.FullName],
            TimeSpan.FromMinutes(5));
        return code == 0 ? null : Tool.Reason(output, "lvcreate", code);
    }

    /// <summary>Creates a snapshot of a thin volume: it takes room in the thin pool as the volume changes, not in the
    /// group. Thin snapshots are not activated by default; this one is (-kn).</summary>
    public static string? CreateThinSnapshot(LogicalVolume origin, string name)
    {
        var (code, output) = Tool.Run("lvcreate",
            ["--snapshot", "--setactivationskip", "n", "--name", name, "--addtag", Tag, origin.FullName],
            TimeSpan.FromMinutes(5));
        return code == 0 ? null : Tool.Reason(output, "lvcreate", code);
    }

    /// <summary>The snapshot just made, read again (its device and state).</summary>
    public static LogicalVolume? Get(string group, string name) => List(out _, group + "/" + name).FirstOrDefault();

    /// <summary>How full a snapshot is, in percent of its size (lvs snap_percent), or null.</summary>
    public static double? UsedPercent(string fullName)
    {
        var (code, _) = Tool.Run("lvs", ["--noheadings", "-o", "snap_percent", fullName], out string output, TimeSpan.FromMinutes(1));
        return code == 0 && double.TryParse(output.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double percent) ? percent : null;
    }

    /// <summary>Removes a snapshot; true when it is gone (or was never there).</summary>
    public static bool Remove(string fullName)
    {
        var (code, output) = Tool.Run("lvremove", ["--force", "--yes", fullName], TimeSpan.FromMinutes(5));
        return code == 0 || output.Contains("not found", StringComparison.OrdinalIgnoreCase)
            || output.Contains("Failed to find logical volume", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The snapshots of CUSTOS still there ("vg/custos-snap-…"): left by a run that never ended.</summary>
    public static List<string> Leftovers() =>
        [.. List(out _, "@" + Tag).Where(v => v.Name.StartsWith(SnapshotPrefix, StringComparison.Ordinal)).Select(v => v.FullName)];

    private static int Int(string text) => int.TryParse(text.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int value) ? value : -1;

    private static long Long(string text) =>
        long.TryParse(text.Trim().Split('.')[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long value) ? value : 0;
}
