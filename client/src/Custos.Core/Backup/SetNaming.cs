using System.Globalization;
using System.Text;
using Custos.Core.Model;

namespace Custos.Core.Backup;

/// <summary>Names of sets from the template of the task (section 6.4).</summary>
public static class SetNaming
{
    /// <summary>The word <c>{type}</c> stands for: Full, Incremental, Differential.</summary>
    public static string TypeWord(string backupType) => backupType switch
    {
        BackupTypes.Full => "Full",
        BackupTypes.Incremental => "Incremental",
        BackupTypes.Differential => "Differential",
        BackupTypes.Mirror => "Mirror",
        _ => backupType,
    };

    /// <summary>
    /// Fills in <c>{task}</c>, <c>{yyyy}</c>, <c>{MM}</c>, <c>{dd}</c>, <c>{HH}</c>, <c>{mm}</c>, <c>{computer}</c> and
    /// <c>{type}</c>; other text stays. Characters Windows does not allow in names become "_".
    /// </summary>
    public static string Expand(string template, string taskName, DateTime localTime, string computer, string backupType)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (string.IsNullOrWhiteSpace(template))
        {
            template = LayoutOptions.DefaultNameTemplate;
        }

        string result = template
            .Replace("{task}", taskName, StringComparison.Ordinal)
            .Replace("{yyyy}", localTime.ToString("yyyy", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{MM}", localTime.ToString("MM", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{dd}", localTime.ToString("dd", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{HH}", localTime.ToString("HH", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{mm}", localTime.ToString("mm", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{computer}", computer, StringComparison.Ordinal)
            .Replace("{type}", TypeWord(backupType), StringComparison.Ordinal);
        return CleanName(result);
    }

    /// <summary>A valid folder name: invalid characters replaced, no trailing dots or spaces, not empty.</summary>
    public static string CleanName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var builder = new StringBuilder(name.Length);
        var invalid = new HashSet<char>(Path.GetInvalidFileNameChars()) { '\\', '/', ':', '*', '?', '"', '<', '>', '|' };
        foreach (char c in name)
        {
            builder.Append(invalid.Contains(c) || char.IsControl(c) ? '_' : c);
        }

        string clean = builder.ToString().Trim().TrimEnd('.');
        return clean.Length == 0 ? "CUSTOS" : clean;
    }

    /// <summary><paramref name="name"/>, or "name (2)", "name (3)" … if a set with it exists in any form.</summary>
    public static string MakeUnique(string root, string name)
    {
        string candidate = name;
        for (int i = 2; IsTaken(root, candidate); i++)
        {
            candidate = Numbered(name, i);
        }

        return candidate;
    }

    /// <summary>"name (i)".</summary>
    public static string Numbered(string name, int i) => string.Create(CultureInfo.InvariantCulture, $"{name} ({i})");

    /// <summary>Whether <paramref name="root"/> holds a set called <paramref name="name"/>: a folder, an archive or
    /// its volumes, finished or still partial.</summary>
    public static bool IsTaken(string root, string name)
    {
        string path = Path.Combine(root, name);
        if (Directory.Exists(path) || Directory.Exists(path + SetCatalog.PartialSuffix))
        {
            return true;
        }

        foreach (string extension in (string[])[".7z", ".zip", ".7z.001"])
        {
            if (File.Exists(path + extension) || File.Exists(path + extension + SetCatalog.PartialSuffix))
            {
                return true;
            }
        }

        return false;
    }
}
