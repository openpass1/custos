using System.Globalization;
using System.Text.RegularExpressions;
using Custos.Core.Model;

namespace Custos.Core.Backup;

/// <summary>
/// Variables in the paths of a task (section 6.2): environment variables like <c>%SystemDrive%</c> and
/// <c>%USERPROFILE%</c>, and the tokens <c>{task}</c>, <c>{computer}</c>, <c>{type}</c>, <c>{yyyy}</c>, <c>{MM}</c>,
/// <c>{dd}</c>, <c>{HH}</c>, <c>{mm}</c>. The service runs as SYSTEM, so the variables of a user profile come from the
/// profile of the task (<see cref="TaskDefinition.ProfileSid"/>), not from the environment of the service.
/// </summary>
public sealed partial class PathVariables
{
    /// <summary>Variables that belong to a user profile: without a profile they are not filled in at all.</summary>
    public static readonly IReadOnlySet<string> ProfileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "USERPROFILE", "APPDATA", "LOCALAPPDATA", "TEMP", "TMP", "HOMEDRIVE", "HOMEPATH", "HOMESHARE",
        "USERNAME", "USERDOMAIN", "OneDrive", "OneDriveCommercial", "OneDriveConsumer",
    };

    private readonly Func<string, string?> _lookup;
    private readonly bool _hasProfile;
    private readonly (string Token, string Value)[] _tokens;

    /// <param name="lookup">The value of an environment variable, or null if it has none.</param>
    /// <param name="hasProfile">The task has a user profile; otherwise <see cref="ProfileNames"/> stay unknown.</param>
    public PathVariables(Func<string, string?> lookup, bool hasProfile, string taskName, string computer, string backupType, DateTime localTime)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        _lookup = lookup;
        _hasProfile = hasProfile;
        _tokens =
        [
            ("{task}", SetNaming.CleanName(taskName)),
            ("{computer}", SetNaming.CleanName(computer)),
            ("{type}", SetNaming.TypeWord(backupType)),
            ("{yyyy}", localTime.ToString("yyyy", CultureInfo.InvariantCulture)),
            ("{MM}", localTime.ToString("MM", CultureInfo.InvariantCulture)),
            ("{dd}", localTime.ToString("dd", CultureInfo.InvariantCulture)),
            ("{HH}", localTime.ToString("HH", CultureInfo.InvariantCulture)),
            ("{mm}", localTime.ToString("mm", CultureInfo.InvariantCulture)),
        ];
    }

    /// <summary>The variables of a task for a run that starts at <paramref name="localTime"/>.</summary>
    public static PathVariables For(IBackupPlatform platform, TaskDefinition task, DateTime localTime)
    {
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(task);
        return new PathVariables(platform.Variables(task.ProfileSid), task.ProfileSid is not null, task.Name, platform.ComputerName, task.BackupType, localTime);
    }

    /// <summary>True if the text has something to fill in.</summary>
    public static bool HasVariables(string? text) =>
        text is not null && (EnvironmentVariable().IsMatch(text) || text.Contains('{', StringComparison.Ordinal));

    /// <summary>
    /// A full path, or one that starts with an environment variable (<c>%USERPROFILE%\Documents</c>) which the
    /// service turns into one.
    /// </summary>
    public static bool LooksAbsolute(string? path) =>
        !string.IsNullOrWhiteSpace(path) && (Path.IsPathFullyQualified(path) || LeadingVariable().IsMatch(path));

    /// <summary>Fills in what it can; an unknown variable stays as it is.</summary>
    public string Expand(string text) => Expand(text, out _);

    /// <summary>Fills in only the environment variables <c>%NAME%</c> that <paramref name="lookup"/> knows.</summary>
    public static string ExpandEnvironment(string text, Func<string, string?> lookup)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(lookup);
        return EnvironmentVariable().Replace(text, match => lookup(match.Groups[1].Value) ?? match.Value);
    }

    /// <summary>Fills in what it can.</summary>
    /// <param name="problem">Why a variable stayed as it is, for the run log; null if all were filled in.</param>
    public string Expand(string text, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(text);
        string? first = null;
        bool needsProfile = false;
        string result = EnvironmentVariable().Replace(text, match =>
        {
            string name = match.Groups[1].Value;
            bool profile = ProfileNames.Contains(name);
            string? value = profile && !_hasProfile ? null : _lookup(name);
            if (value is null)
            {
                first ??= match.Value;
                needsProfile |= profile && !_hasProfile;
                return match.Value;
            }

            return value;
        });

        foreach (var (token, value) in _tokens)
        {
            result = result.Replace(token, value, StringComparison.Ordinal);
        }

        problem = first is null ? null
            : Messages.Format(needsProfile ? Messages.PathVariableNoProfile : Messages.PathVariableUnknown, first, text);
        return result;
    }

    /// <summary>A copy of the task with the paths of its sources, destinations and database programs filled in.</summary>
    /// <exception cref="BackupException">A variable is unknown.</exception>
    public TaskDefinition ExpandPaths(TaskDefinition task)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (!task.Sources.Any(s => HasVariables(s.Path)) && !task.Destinations.Any(d => HasVariables(d.Path))
            && !task.DbServers.Any(d => HasVariables(d.BinDir)))
        {
            return task;
        }

        var copy = task.Clone();
        foreach (var source in copy.Sources)
        {
            source.Path = Checked(source.Path);
        }

        foreach (var destination in copy.Destinations)
        {
            destination.Path = Checked(destination.Path);
        }

        foreach (var server in copy.DbServers)
        {
            server.BinDir = Checked(server.BinDir);
        }

        return copy;
    }

    private string Checked(string path)
    {
        string result = Expand(path, out string? problem);
        return problem is null ? result : throw new BackupException(problem);
    }

    private static readonly Regex EnvironmentVariableRegex = new(@"%([^%\\/:*?""<>|\r\n]+)%", RegexOptions.CultureInvariant);

    private static Regex EnvironmentVariable() => EnvironmentVariableRegex;

    private static readonly Regex LeadingVariableRegex = new(@"^%[^%\\/:*?""<>|\r\n]+%([\\/]|$)", RegexOptions.CultureInvariant);

    private static Regex LeadingVariable() => LeadingVariableRegex;
}
