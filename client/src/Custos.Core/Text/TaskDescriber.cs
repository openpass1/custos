using System.Globalization;
using Custos.Core.Model;
using Custos.Core.Scheduling;

namespace Custos.Core.Text;

/// <summary>
/// Describes a task in plain words: "Every day at 00:00, copies changed files from D:\Data to \\nas01\backup. Every
/// 7th run is a full backup. The last 12 full backups are kept, with their incremental backups." (the Cobian import
/// window), and pieces of that for the rows of the task card (docs/client-ui.md, 2.3). In the language of
/// <see cref="Messages"/>.
/// </summary>
public static class TaskDescriber
{
    private const int MaxListedPaths = 3;

    public static string Describe(TaskDefinition task)
    {
        ArgumentNullException.ThrowIfNull(task);
        var sentences = new List<string>
        {
            Messages.Format(Messages.DescSentence, Capitalize(Schedule(task.Schedule)), Action(task.BackupType), Sources(task), Destinations(task)),
        };

        if (task.DbServers.Count > 0)
        {
            var servers = task.DbServers.Select(d => string.Concat(DbEngines.Title(d.Engine), " ", d.Host, ":",
                (d.Port ?? DbEngines.DefaultPort(d.Engine)).ToString(CultureInfo.InvariantCulture)));
            sentences.Add(Messages.Format(Messages.DescDatabases, JoinWords([.. servers])));
        }

        if (task.Sources.FirstOrDefault(s => s.Kind == SourceKinds.Ssh) is { Helper.DbEngines: { Count: > 0 } engines } remote)
        {
            string titles = JoinWords([.. engines.Distinct(StringComparer.Ordinal).Select(DbEngines.Title)]);
            sentences.Add(remote.Databases switch
            {
                { AllDatabases: false } only => Messages.Format(Messages.DescRemoteDatabasesOnly, titles, RemoteDatabaseChoice.Describe(only.Include)),
                { Exclude.Count: > 0 } except => Messages.Format(Messages.DescRemoteDatabasesExcept, titles, RemoteDatabaseChoice.Describe(except.Exclude)),
                _ => Messages.Format(Messages.DescRemoteDatabases, titles),
            });
        }

        if (task.BackupType is BackupTypes.Incremental or BackupTypes.Differential && ForceFull(task.ForceFull) is { } force)
        {
            sentences.Add(force);
        }

        sentences.Add(Retention(task));
        if (task.Events.Pre.Count + task.Events.Post.Count > 0)
        {
            sentences.Add(Messages.Format(Messages.DescEvents, task.Events.Pre.Count, task.Events.Post.Count));
        }

        if (task.Events.After is AfterActions.Shutdown or AfterActions.Restart or AfterActions.Sleep or AfterActions.Hibernate)
        {
            sentences.Add(Messages.Format(Messages.DescAfter, Words.PowerAction(task.Events.After)));
        }

        return string.Join(" ", sentences);
    }

    /// <summary>"every day at 00:00", "on Mon and Fri at 22:00", "every 30 min", "on request".</summary>
    public static string Schedule(ScheduleOptions schedule)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        var times = schedule.Times.Select(ScheduleCalculator.ParseTime).OfType<TimeSpan>().Distinct().Order()
            .Select(t => t.ToString(@"hh\:mm", CultureInfo.InvariantCulture)).ToList();
        string at = JoinWords(times);
        switch (schedule.Type)
        {
            case ScheduleTypes.Daily:
            case ScheduleTypes.Weekly:
            {
                if (!ScheduleCalculator.HasSlots(schedule))
                {
                    return Messages.DescScheduleNever;
                }

                var days = schedule.DaysOfWeek.Where(d => d is >= 1 and <= 7).Distinct().Order().ToList();
                return days.Count is 0 or 7
                    ? Messages.Format(Messages.DescEveryDay, at)
                    : Messages.Format(Messages.DescOnDays, Days(days), at);
            }

            case ScheduleTypes.Monthly:
            {
                if (!ScheduleCalculator.HasSlots(schedule))
                {
                    return Messages.DescScheduleNever;
                }

                // The 31st is always the last day of its month (ScheduleCalculator.IsMonthDay), so it is said so.
                var monthDays = schedule.DaysOfMonth.Where(d => d is (>= 1 and <= 31) or (-1)).Select(d => d == 31 ? -1 : d).Distinct().ToList();
                var days = monthDays.Where(d => d > 0).Order().Select(d => Messages.Format(Messages.DescMonthDay, d, Ordinal(d))).ToList();
                bool lastDay = monthDays.Contains(-1);
                if (lastDay)
                {
                    days.Add(Messages.DescLastDay);
                }

                string text = Messages.Format(Messages.DescMonthly, JoinWords(days), at);
                return !lastDay && monthDays.Any(d => d >= 29) ? text + " " + Messages.DescMonthlyFebruary : text;
            }

            case ScheduleTypes.Interval:
            {
                if (!ScheduleCalculator.HasSlots(schedule))
                {
                    return Messages.DescScheduleNever;
                }

                string every = Messages.Format(Messages.DescInterval, schedule.IntervalMinutes);
                var days = schedule.DaysOfWeek.Where(d => d is >= 1 and <= 7).Distinct().Order().ToList();
                return days.Count is 0 or 7 ? every : every + " " + Messages.Format(Messages.DescOnDaysSuffix, Days(days));
            }

            default:
                return Messages.DescManual;
        }
    }

    /// <summary><see cref="Schedule"/> plus whether a missed run is made up: for the card and the editor.</summary>
    public static string ScheduleWithCatchUp(ScheduleOptions schedule)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        string text = Capitalize(Schedule(schedule));
        return ScheduleCalculator.HasSlots(schedule) && schedule.CatchUp ? text + Messages.DescCatchUp : text;
    }

    /// <summary>"D:\Data, E:\Docs and 2 more".</summary>
    public static string Paths(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count <= MaxListedPaths)
        {
            return JoinWords(paths);
        }

        var shown = paths.Take(MaxListedPaths - 1).Append(Messages.Format(Messages.DescMore, paths.Count - MaxListedPaths + 1)).ToList();
        return JoinWords(shown);
    }

    /// <summary>A destination in words: its path, "USB disk «LABEL»" with the folder on it, or the address of a
    /// server ("sftp://backup.example.org/office", "s3://bucket/office").</summary>
    public static string Destination(TaskDestination destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (DestinationKinds.IsSupported(destination.Kind) && !DestinationKinds.IsFileSystem(destination.Kind))
        {
            return Custos.Core.Destinations.DestinationStores.Label(destination);
        }

        if (destination.Kind == DestinationKinds.Usb)
        {
            string label = Messages.Format(Messages.UsbDiskLabel, destination.UsbLabel ?? "?");
            string folder = DestinationKinds.UsbFolder(destination.Path);
            return folder.Length == 0 ? label : label + " \\" + folder;
        }

        return destination.Path;
    }

    /// <summary>"a, b and c".</summary>
    public static string JoinWords(IReadOnlyList<string> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return items.Count switch
        {
            0 => string.Empty,
            1 => items[0],
            _ => string.Join(", ", items.Take(items.Count - 1)) + " " + Messages.DescAnd + " " + items[^1],
        };
    }

    private static string Action(string backupType) => backupType switch
    {
        BackupTypes.Full => Messages.DescActionFull,
        BackupTypes.Differential => Messages.DescActionDifferential,
        BackupTypes.Mirror => Messages.DescActionMirror,
        _ => Messages.DescActionIncremental,
    };

    private static string Sources(TaskDefinition task)
    {
        if (task.Sources.FirstOrDefault(s => s.Kind == SourceKinds.Ssh) is { } remote)
        {
            return Messages.Format(Messages.DescFromServer, remote.Host);
        }

        var paths = task.Sources.Select(s => s.Path).Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        return paths.Count == 0 ? Messages.DescNoSources : Messages.Format(Messages.DescFrom, Paths(paths));
    }

    private static string Destinations(TaskDefinition task)
    {
        var parts = new List<string>();
        foreach (var destination in task.Destinations.Take(MaxListedPaths))
        {
            parts.Add(destination.Kind == DestinationKinds.Usb
                ? Messages.Format(Messages.DescToUsb, destination.UsbLabel ?? "?")
                : Messages.Format(Messages.DescTo, destination.Path));
        }

        if (task.Destinations.Count > MaxListedPaths)
        {
            parts[^1] = Messages.Format(Messages.DescMore, task.Destinations.Count - MaxListedPaths + 1);
        }

        return parts.Count == 0 ? Messages.DescNoDestinations : JoinWords(parts);
    }

    /// <summary>
    /// When an incremental or differential task makes a full backup, for the card: "Every 7th run, on Sundays", "The
    /// first run of every week (grandfather-father-son)", or "Only the first one, and when a destination has none".
    /// Null for full backups and mirrors.
    /// </summary>
    public static string? FullBackups(TaskDefinition task)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (task.BackupType is not (BackupTypes.Incremental or BackupTypes.Differential))
        {
            return null;
        }

        var force = task.ForceFull;
        var parts = new List<string>();
        if (force.EveryRuns > 0)
        {
            parts.Add(Messages.Format(Messages.DescFullListEveryRuns, force.EveryRuns, Ordinal(force.EveryRuns)));
        }

        if (force.Weekday is >= 1 and <= 7)
        {
            parts.Add(Messages.DescFullListWeekdays.Split(';')[force.Weekday.Value - 1].Trim());
        }

        if (force.MonthDay is -1 or 31)
        {
            parts.Add(Messages.DescFullListLastDay);
        }
        else if (force.MonthDay is >= 1 and <= 30)
        {
            parts.Add(Messages.Format(Messages.DescFullListMonthDay, force.MonthDay.Value, Ordinal(force.MonthDay.Value)));
        }

        if (task.Retention.Mode == RetentionModes.Gfs && KeepsSets(task))
        {
            parts.Add(Messages.DescFullListGfsWeek);
        }

        return Capitalize(parts.Count == 0 ? Messages.DescFullOnlyFirst : string.Join(", ", parts));
    }

    private static string? ForceFull(ForceFullOptions force)
    {
        var parts = new List<string>();
        if (force.EveryRuns > 0)
        {
            parts.Add(Messages.Format(Messages.DescFullEveryRuns, force.EveryRuns, Ordinal(force.EveryRuns)));
        }

        if (force.Weekday is >= 1 and <= 7)
        {
            parts.Add(Messages.Format(Messages.DescFullWeekday, Messages.DescWeekdaysPlural.Split(';')[force.Weekday.Value - 1].Trim()));
        }

        if (force.MonthDay is -1 or 31)
        {
            parts.Add(Messages.DescFullLastDay);
        }
        else if (force.MonthDay is >= 1 and <= 30)
        {
            parts.Add(Messages.Format(Messages.DescFullMonthDay, force.MonthDay.Value, Ordinal(force.MonthDay.Value)));
        }

        return parts.Count == 0 ? null : string.Join(" ", parts);
    }

    /// <summary>What happens to old backups, as one sentence: "The last 12 backups are kept."</summary>
    public static string Retention(TaskDefinition task)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (task.BackupType == BackupTypes.Mirror)
        {
            int keep = Math.Max(1, task.Retention.SafeMirrorDays);
            return task.MirrorMode == MirrorModes.Safe
                ? Messages.Format(Sizes.Plural(keep, Messages.DescMirrorSafeOne, Messages.DescMirrorSafeFew, Messages.DescMirrorSafeMany), keep)
                : Messages.DescMirrorExact;
        }

        if (!KeepsSets(task))
        {
            return Messages.DescOneFolder;
        }

        var retention = task.Retention;
        switch (retention.Mode)
        {
            case RetentionModes.None:
                return Messages.DescKeepAll;
            case RetentionModes.Count:
                int keep = Math.Max(1, retention.KeepFull);
                if (task.BackupType == BackupTypes.Full)
                {
                    return Messages.Format(Sizes.Plural(keep, Messages.DescKeepSetsOne, Messages.DescKeepSetsFew, Messages.DescKeepSetsMany), keep);
                }

                string kind = task.BackupType == BackupTypes.Differential ? Messages.DescKindDifferential : Messages.DescKindIncremental;
                return Messages.Format(Sizes.Plural(keep, Messages.DescKeepChainsOne, Messages.DescKeepChainsFew, Messages.DescKeepChainsMany), keep, kind);
            case RetentionModes.Age:
                int days = Math.Max(1, retention.MaxAgeDays);
                return Messages.Format(Sizes.Plural(days, Messages.DescKeepAgeOne, Messages.DescKeepAgeFew, Messages.DescKeepAgeMany), days);
            case RetentionModes.Gfs:
                return Messages.Format(Messages.DescKeepGfs, retention.Gfs.Daily, retention.Gfs.Weekly, retention.Gfs.Monthly);
            default:
                return Messages.DescKeepAll;
        }
    }

    /// <summary>Whether backups are kept as separate sets (archives, a folder per backup, a Linux server), so that
    /// retention has something to delete.</summary>
    private static bool KeepsSets(TaskDefinition task) =>
        task.Layout.SeparateFolders || task.Archive.Format != ArchiveFormats.None || task.Sources.Any(s => s.Kind == SourceKinds.Ssh);

    private static string Days(List<int> days)
    {
        if (days.SequenceEqual([1, 2, 3, 4, 5]))
        {
            return Messages.DescWeekdaysWork;
        }

        if (days.SequenceEqual([6, 7]))
        {
            return Messages.DescWeekdaysWeekend;
        }

        string[] names = Messages.DescWeekdaysShort.Split(',');
        return JoinWords([.. days.Select(d => names[d - 1].Trim())]);
    }

    /// <summary>English ordinal suffix: st, nd, rd, th. Other languages put theirs in the text.</summary>
    private static string Ordinal(int number)
    {
        if (Messages.IsRussian)
        {
            return string.Empty;
        }

        int lastTwo = number % 100;
        return lastTwo is >= 11 and <= 13 ? "th" : (number % 10) switch
        {
            1 => "st",
            2 => "nd",
            3 => "rd",
            _ => "th",
        };
    }

    private static string Capitalize(string text) =>
        text.Length == 0 ? text : char.ToUpper(text[0], Messages.UiCulture) + text[1..];
}
