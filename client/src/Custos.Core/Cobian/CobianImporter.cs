using System.Globalization;
using System.Text.RegularExpressions;
using Custos.Core.Model;

namespace Custos.Core.Cobian;

/// <summary>Something the administrator should look at before enabling an imported task. The interface turns
/// <see cref="Code"/> into a sentence; <see cref="Argument"/> is the value it is about.</summary>
public sealed record ImportWarning(string Code, string? Argument = null);

/// <summary>Codes of <see cref="ImportWarning"/>.</summary>
public static class ImportWarningCodes
{
    /// <summary>The schedule code is unknown; the task was made manual.</summary>
    public const string ScheduleUnknown = "schedule_unknown";

    /// <summary>"Once" and "yearly" schedules do not exist in CUSTOS; the task was made manual.</summary>
    public const string ScheduleNotSupported = "schedule_not_supported";

    /// <summary>The schedule code was interpreted by assumption (monthly, timer, manual).</summary>
    public const string ScheduleAssumed = "schedule_assumed";

    /// <summary>No time of day could be read; 00:00 was used.</summary>
    public const string TimeUnreadable = "time_unreadable";

    /// <summary>Days of the week: Cobian's numbering is assumed to start on Monday. Argument: the days as read.</summary>
    public const string WeekdaysAssumed = "weekdays_assumed";

    /// <summary>The backup type code was interpreted by assumption (differential).</summary>
    public const string BackupTypeAssumed = "backup_type_assumed";

    /// <summary>The backup type is unknown or "dummy"; the task copies nothing until it is changed.</summary>
    public const string BackupTypeNotSupported = "backup_type_not_supported";

    /// <summary>The task compresses (zip or 7z); compression arrives in a later version. Argument: the format.</summary>

    /// <summary>The compression code was interpreted by assumption (zip).</summary>
    public const string CompressionAssumed = "compression_assumed";

    /// <summary>Archive encryption: the password has to be entered in the task again.</summary>
    public const string EncryptionPassword = "encryption_password";

    /// <summary>Splitting into volumes was not carried over.</summary>
    public const string SplitIgnored = "split_ignored";

    /// <summary>Events before/after were put into the comment. Argument: their text.</summary>
    public const string EventsInComment = "events_in_comment";

    /// <summary>A source of an unknown type. Argument: the entry.</summary>
    public const string SourceUnknown = "source_unknown";

    /// <summary>An FTP source or destination; CUSTOS writes to FTP in a later version, it never reads from FTP.
    /// Argument: the entry.</summary>
    public const string FtpNotSupported = "ftp_not_supported";

    /// <summary>The task has no source.</summary>
    public const string NoSources = "no_sources";

    /// <summary>The task has no destination.</summary>
    public const string NoDestinations = "no_destinations";

    /// <summary>"Fixed day" of full copies read as a weekday by assumption. Argument: the value.</summary>
    public const string FixedDayAssumed = "fixed_day_assumed";

    /// <summary>Exclusions and inclusions were imported; check them. Argument: the masks.</summary>
    public const string FiltersImported = "filters_imported";

    /// <summary>The task runs as another user; its password is asked during the import. Argument: the account.</summary>
    public const string RunAsAccount = "run_as_account";

    /// <summary>"Run as" is set for a task without network paths: CUSTOS reads local files as SYSTEM anyway.</summary>
    public const string RunAsLocalOnly = "run_as_local_only";
}

/// <summary>A task made from one Cobian block, with what the administrator should check.</summary>
public sealed class ImportedTask
{
    public required TaskDefinition Task { get; init; }

    /// <summary>"Enabled" in Cobian. The imported task itself is always created disabled (section 9).</summary>
    public required bool EnabledInCobian { get; init; }

    public required List<ImportWarning> Warnings { get; init; }

    /// <summary>Id of the "run as" account, if the task uses one.</summary>
    public string? AccountId { get; init; }
}

/// <summary>A distinct "run as" account found in the list. Its password is asked once (section 9).</summary>
public sealed class ImportedAccount
{
    public required Credential Credential { get; init; }

    public required List<string> TaskNames { get; init; }
}

public sealed class CobianImportResult
{
    public required List<ImportedTask> Tasks { get; init; }

    public required List<ImportedAccount> Accounts { get; init; }
}

/// <summary>
/// Converts Cobian Backup 11 tasks into CUSTOS tasks by the table in section 9 of the client spec. Values the table
/// marks as unverified are interpreted by the most likely meaning and reported as warnings.
/// </summary>
public static partial class CobianImporter
{
    public static CobianImportResult Convert(IReadOnlyList<CobianTaskBlock> blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        var accounts = new Dictionary<string, ImportedAccount>(StringComparer.OrdinalIgnoreCase);
        var tasks = new List<ImportedTask>();
        foreach (var block in blocks)
        {
            tasks.Add(ConvertTask(block, accounts));
        }

        return new CobianImportResult { Tasks = tasks, Accounts = [.. accounts.Values] };
    }

    private static ImportedTask ConvertTask(CobianTaskBlock block, Dictionary<string, ImportedAccount> accounts)
    {
        var warnings = new List<ImportWarning>();
        var task = new TaskDefinition
        {
            Name = block.Get("Name") is { Length: > 0 } name ? name : "Cobian " + block.LineNumber.ToString(CultureInfo.InvariantCulture),
            Enabled = false,
            Priority = GetInt(block, "Priority") ?? 0,
        };

        ReadBackupType(block, task, warnings);
        bool recursive = GetBool(block, "Include subdirectories") ?? true;
        ReadSources(block, task, recursive, warnings);
        ReadDestinations(block, task, warnings);
        ReadSchedule(block, task, warnings);
        ReadRetention(block, task, warnings);
        ReadArchive(block, task, warnings);
        ReadFilters(block, task, warnings);
        ReadLayout(block, task);
        ReadEvents(block, task, warnings);

        task.Vss.Enabled = GetBool(block, "Use VSC") ?? false;
        // Cobian carries on without the shadow copy when it fails; keep that behaviour for imported tasks.
        task.Vss.OnFailure = VssFailureActions.Continue;

        string? accountId = ReadAccount(block, task, accounts, warnings);

        if (task.Sources.Count == 0)
        {
            warnings.Add(new ImportWarning(ImportWarningCodes.NoSources));
        }

        if (task.Destinations.Count == 0)
        {
            warnings.Add(new ImportWarning(ImportWarningCodes.NoDestinations));
        }

        return new ImportedTask
        {
            Task = task,
            EnabledInCobian = GetBool(block, "Enabled") ?? true,
            Warnings = warnings,
            AccountId = accountId,
        };
    }

    private static void ReadBackupType(CobianTaskBlock block, TaskDefinition task, List<ImportWarning> warnings)
    {
        if (GetBool(block, "Mirror") == true)
        {
            task.BackupType = BackupTypes.Mirror;
            task.MirrorMode = MirrorModes.Exact;
            return;
        }

        switch (GetInt(block, "Backup type"))
        {
            case null or 0:
                task.BackupType = BackupTypes.Full;
                break;
            case 1:
                task.BackupType = BackupTypes.Incremental;
                break;
            case 2:
                task.BackupType = BackupTypes.Differential;
                warnings.Add(new ImportWarning(ImportWarningCodes.BackupTypeAssumed));
                break;
            case int other:
                // 3 is "dummy" in Cobian: runs only the events.
                task.BackupType = BackupTypes.Full;
                warnings.Add(new ImportWarning(ImportWarningCodes.BackupTypeNotSupported, other.ToString(CultureInfo.InvariantCulture)));
                break;
        }
    }

    private static void ReadSources(CobianTaskBlock block, TaskDefinition task, bool recursive, List<ImportWarning> warnings)
    {
        foreach (var (type, path) in ReadPathList(block.Get("Source")))
        {
            if (type == 2 || IsUrl(path))
            {
                warnings.Add(new ImportWarning(ImportWarningCodes.FtpNotSupported, path));
                continue;
            }

            string? kind = type switch
            {
                0 => SourceKinds.File,
                1 or 3 or null => IsUnc(path) ? SourceKinds.Unc : SourceKinds.Folder,
                _ => null,
            };
            if (kind is null || !IsAbsolute(path))
            {
                warnings.Add(new ImportWarning(ImportWarningCodes.SourceUnknown, $"{type},{path}"));
                continue;
            }

            task.Sources.Add(new TaskSource { Kind = kind, Path = path, Recursive = recursive });
        }
    }

    private static void ReadDestinations(CobianTaskBlock block, TaskDefinition task, List<ImportWarning> warnings)
    {
        foreach (var (type, path) in ReadPathList(block.Get("Destination")))
        {
            if (type == 2 || IsUrl(path))
            {
                warnings.Add(new ImportWarning(ImportWarningCodes.FtpNotSupported, path));
                continue;
            }

            if (!IsAbsolute(path))
            {
                warnings.Add(new ImportWarning(ImportWarningCodes.SourceUnknown, $"{type},{path}"));
                continue;
            }

            task.Destinations.Add(new TaskDestination
            {
                Kind = IsUnc(path) ? DestinationKinds.Unc : DestinationKinds.Local,
                Path = path,
            });
        }
    }

    private static void ReadSchedule(CobianTaskBlock block, TaskDefinition task, List<ImportWarning> warnings)
    {
        var schedule = task.Schedule;
        var time = CobianListParser.DecodeDateTimeBits(block.Get("Date/Time")) is { } dateTime
            ? CobianListParser.TimeOfDay(dateTime)
            : (TimeSpan?)null;
        schedule.Times = [(time ?? TimeSpan.Zero).ToString(@"hh\:mm", CultureInfo.InvariantCulture)];
        schedule.DaysOfWeek = [1, 2, 3, 4, 5, 6, 7];
        schedule.CatchUp = true;

        int? code = GetInt(block, "Schedule type");
        bool needsTime = true;
        switch (code)
        {
            case 1:
                schedule.Type = ScheduleTypes.Daily;
                break;
            case 2:
                schedule.Type = ScheduleTypes.Weekly;
                schedule.DaysOfWeek = ReadNumbers(block.Get("Day of the week")).Where(d => d is >= 1 and <= 7).Distinct().Order().ToList();
                warnings.Add(new ImportWarning(ImportWarningCodes.WeekdaysAssumed, string.Join(",", schedule.DaysOfWeek)));
                break;
            case 3:
                schedule.Type = ScheduleTypes.Monthly;
                schedule.DaysOfMonth = ReadNumbers(block.Get("Day of the month")).Where(d => (d >= 1 && d <= 31) || d == -1).Distinct().Order().ToList();
                if (schedule.DaysOfMonth.Count == 0)
                {
                    schedule.DaysOfMonth = [1];
                }

                warnings.Add(new ImportWarning(ImportWarningCodes.ScheduleAssumed, "3"));
                break;
            case 5:
                schedule.Type = ScheduleTypes.Interval;
                schedule.IntervalMinutes = Math.Clamp(GetInt(block, "Timer") ?? 60, 1, 24 * 60);
                warnings.Add(new ImportWarning(ImportWarningCodes.ScheduleAssumed, "5"));
                needsTime = false;
                break;
            case 6:
                schedule.Type = ScheduleTypes.Manual;
                warnings.Add(new ImportWarning(ImportWarningCodes.ScheduleAssumed, "6"));
                needsTime = false;
                break;
            case 0 or 4:
                // Once and yearly do not exist in CUSTOS (section 13).
                schedule.Type = ScheduleTypes.Manual;
                warnings.Add(new ImportWarning(ImportWarningCodes.ScheduleNotSupported, code.Value.ToString(CultureInfo.InvariantCulture)));
                needsTime = false;
                break;
            default:
                schedule.Type = ScheduleTypes.Manual;
                warnings.Add(new ImportWarning(ImportWarningCodes.ScheduleUnknown, block.Get("Schedule type") ?? string.Empty));
                needsTime = false;
                break;
        }

        if (needsTime && time is null)
        {
            warnings.Add(new ImportWarning(ImportWarningCodes.TimeUnreadable));
        }
    }

    private static void ReadRetention(CobianTaskBlock block, TaskDefinition task, List<ImportWarning> warnings)
    {
        int keep = GetInt(block, "Full copies") ?? 0;
        if (keep > 0)
        {
            task.Retention.Mode = RetentionModes.Count;
            task.Retention.KeepFull = keep;
        }
        else
        {
            task.Retention.Mode = RetentionModes.None;
        }

        if (task.BackupType is BackupTypes.Incremental or BackupTypes.Differential)
        {
            task.ForceFull.EveryRuns = Math.Max(0, GetInt(block, "One full every") ?? 0);
            if (GetBool(block, "Use fixed day") == true && GetInt(block, "Fixed day") is { } day)
            {
                if (day is >= 1 and <= 7)
                {
                    task.ForceFull.Weekday = day;
                }

                warnings.Add(new ImportWarning(ImportWarningCodes.FixedDayAssumed, day.ToString(CultureInfo.InvariantCulture)));
            }
        }
    }

    private static void ReadArchive(CobianTaskBlock block, TaskDefinition task, List<ImportWarning> warnings)
    {
        if (task.BackupType == BackupTypes.Mirror)
        {
            // A mirror keeps plain files.
            task.Archive.Format = ArchiveFormats.None;
            return;
        }

        switch (GetInt(block, "Compression"))
        {
            case null or 0:
                task.Archive.Format = ArchiveFormats.None;
                break;
            case 1:
                task.Archive.Format = ArchiveFormats.Zip;
                warnings.Add(new ImportWarning(ImportWarningCodes.CompressionAssumed));
                break;
            default:
                task.Archive.Format = ArchiveFormats.SevenZip;
                break;
        }

        if ((GetInt(block, "Encryption") ?? 0) != 0)
        {
            task.Archive.Encrypt = true;
            warnings.Add(new ImportWarning(ImportWarningCodes.EncryptionPassword));
        }

        if (GetBool(block, "Split") == true || (GetInt(block, "Split") ?? 0) > 0)
        {
            warnings.Add(new ImportWarning(ImportWarningCodes.SplitIgnored));
        }
    }

    private static void ReadFilters(CobianTaskBlock block, TaskDefinition task, List<ImportWarning> warnings)
    {
        var excluded = ReadMasks(block.Get("Exclusions"));
        var included = ReadMasks(block.Get("Inclusions"));
        foreach (string mask in excluded)
        {
            // A path without wildcards may be a folder or a file: the scanner checks folders against ExcludeDirs and
            // files against Exclude, so it goes into both.
            if (!task.Filters.Exclude.Contains(mask, StringComparer.OrdinalIgnoreCase))
            {
                task.Filters.Exclude.Add(mask);
            }

            if ((mask.Contains('\\', StringComparison.Ordinal) && !HasWildcard(mask)) || !mask.Contains('.', StringComparison.Ordinal))
            {
                task.Filters.ExcludeDirs.Add(mask);
            }
        }

        task.Filters.Include.AddRange(included);
        if (excluded.Count + included.Count > 0)
        {
            warnings.Add(new ImportWarning(ImportWarningCodes.FiltersImported, string.Join("; ", included.Concat(excluded.Select(m => "-" + m)))));
        }
    }

    private static void ReadLayout(CobianTaskBlock block, TaskDefinition task)
    {
        task.Layout.SeparateFolders = GetBool(block, "Create separated backups") ?? true;
        task.Layout.KeepTopDir = GetBool(block, "Always create top directory") ?? true;
        task.Layout.DeleteEmptyDirs = GetBool(block, "Delete empty directories") ?? false;
        if (GetBool(block, "Include backup type") == false)
        {
            task.Layout.NameTemplate = "{task} {yyyy}-{MM}-{dd} {HH}-{mm}";
        }
    }

    private static void ReadEvents(CobianTaskBlock block, TaskDefinition task, List<ImportWarning> warnings)
    {
        var events = block.Values
            .Where(kv => kv.Key.Contains("event", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(kv.Value)
                && kv.Value.Trim('"').Length > 0)
            .Select(kv => $"{kv.Key}: {kv.Value}")
            .ToList();
        if (events.Count > 0)
        {
            string text = string.Join(Environment.NewLine, events);
            task.Comment = string.IsNullOrEmpty(task.Comment) ? text : task.Comment + Environment.NewLine + text;
            warnings.Add(new ImportWarning(ImportWarningCodes.EventsInComment, string.Join("; ", events)));
        }
    }

    private static string? ReadAccount(CobianTaskBlock block, TaskDefinition task, Dictionary<string, ImportedAccount> accounts, List<ImportWarning> warnings)
    {
        string? user = block.Get("Run as User name")?.Trim('"');
        if (GetBool(block, "Impersonate") != true || string.IsNullOrWhiteSpace(user))
        {
            return null;
        }

        string? domain = block.Get("Run as Domain")?.Trim('"');
        if (string.IsNullOrWhiteSpace(domain))
        {
            domain = ".";
        }

        string key = domain + "\\" + user;
        if (!accounts.TryGetValue(key, out var account))
        {
            account = new ImportedAccount
            {
                Credential = new Credential { Kind = CredentialKinds.Windows, User = user, Domain = domain },
                TaskNames = [],
            };
            accounts.Add(key, account);
        }

        account.TaskNames.Add(task.Name);
        bool usesNetwork = false;
        foreach (var source in task.Sources.Where(s => IsUnc(s.Path)))
        {
            source.CredentialId = account.Credential.Id;
            usesNetwork = true;
        }

        foreach (var destination in task.Destinations.Where(d => d.Kind == DestinationKinds.Unc))
        {
            destination.CredentialId = account.Credential.Id;
            usesNetwork = true;
        }

        warnings.Add(new ImportWarning(usesNetwork ? ImportWarningCodes.RunAsAccount : ImportWarningCodes.RunAsLocalOnly, key));
        return account.Credential.Id;
    }

    /// <summary>Reads Cobian's <c>"N,path"</c> list: N is the entry type (0 file, 1 folder, 2 FTP, 3 typed by hand).</summary>
    internal static List<(int? Type, string Path)> ReadPathList(string? value)
    {
        var result = new List<(int?, string)>();
        if (string.IsNullOrWhiteSpace(value))
        {
            return result;
        }

        IEnumerable<string> items;
        if (!value.Contains('"', StringComparison.Ordinal) && TypedEntry().IsMatch(value))
        {
            // Not quoted at all: one entry, possibly with spaces in the path.
            items = [value];
        }
        else
        {
            items = Rejoin(CobianListParser.SplitCommaText(value));
        }

        foreach (string item in items)
        {
            var match = TypedEntry().Match(item);
            if (match.Success)
            {
                result.Add((int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), match.Groups[2].Value.Trim()));
            }
            else if (item.Trim().Length > 0)
            {
                result.Add((null, item.Trim()));
            }
        }

        return result;
    }

    /// <summary>Puts "1" and "D:\Data" back together when an unquoted "1,D:\Data" was split at its comma.</summary>
    private static IEnumerable<string> Rejoin(IReadOnlyList<string> items)
    {
        for (int i = 0; i < items.Count; i++)
        {
            if (Number().IsMatch(items[i]) && i + 1 < items.Count && !Number().IsMatch(items[i + 1]))
            {
                yield return items[i] + "," + items[i + 1];
                i++;
            }
            else
            {
                yield return items[i];
            }
        }
    }

    private static List<string> ReadMasks(string? value)
    {
        var result = new List<string>();
        foreach (var (_, mask) in ReadPathList(value))
        {
            if (mask.Length > 0 && !result.Contains(mask, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(mask);
            }
        }

        return result;
    }

    private static List<int> ReadNumbers(string? value)
    {
        var result = new List<int>();
        foreach (string part in (value ?? string.Empty).Trim('"').Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(part, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int number))
            {
                result.Add(number);
            }
        }

        return result;
    }

    private static int? GetInt(CobianTaskBlock block, string key) =>
        int.TryParse(block.Get(key)?.Trim('"'), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int value) ? value : null;

    private static bool? GetBool(CobianTaskBlock block, string key) => block.Get(key)?.Trim('"').ToUpperInvariant() switch
    {
        "TRUE" or "1" or "YES" or "-1" => true,
        "FALSE" or "0" or "NO" => false,
        _ => null,
    };

    private static bool IsUnc(string path) => path.StartsWith(@"\\", StringComparison.Ordinal);

    private static bool IsUrl(string path) => path.Contains("://", StringComparison.Ordinal);

    private static bool IsAbsolute(string path) => IsUnc(path) || DriveRooted().IsMatch(path);

    private static bool HasWildcard(string mask) => mask.Contains('*', StringComparison.Ordinal) || mask.Contains('?', StringComparison.Ordinal);

    [GeneratedRegex(@"^\s*(\d+)\s*,(.*)$", RegexOptions.Singleline)]
    private static partial Regex TypedEntry();

    [GeneratedRegex(@"^\d+$")]
    private static partial Regex Number();

    [GeneratedRegex(@"^[A-Za-z]:\\")]
    private static partial Regex DriveRooted();
}
