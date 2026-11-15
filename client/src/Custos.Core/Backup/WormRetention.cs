using Custos.Core.Destinations;
using Custos.Core.Model;
using Custos.Core.Text;

namespace Custos.Core.Backup;

/// <summary>
/// Whether the retention of a task fits the protection from deletion of its destinations: WORM of a NAS, S3 Object
/// Lock (section 6.6). A rule that deletes backups before the storage lets them go does not delete them: they stay
/// until their protection is over, and the destination holds more backups than the rule says. The editor and the site
/// do not save such a task and tell what to change.
/// </summary>
/// <remarks>
/// The times come from the schedule, as an average; a task run by hand only has no such time, and only the rules
/// that do not depend on it are checked then.
/// </remarks>
public static class WormRetention
{
    /// <summary>A full backup forced on a day of the month comes at least this often.</summary>
    private static readonly TimeSpan Month = TimeSpan.FromDays(28);

    private static readonly TimeSpan Day = TimeSpan.FromDays(1);

    private static readonly TimeSpan Week = TimeSpan.FromDays(7);

    /// <summary>What is wrong, with what to change, in words; null if the retention fits (or nothing is protected).</summary>
    public static string? Problem(TaskDefinition task)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (task.BackupType == BackupTypes.Mirror)
        {
            return null;
        }

        var (destination, protection) = task.Destinations
            .Select(d => (Destination: d, Protection: Protection(d)))
            .Where(p => p.Protection > TimeSpan.Zero)
            .OrderByDescending(p => p.Protection)
            .FirstOrDefault();
        if (destination is null)
        {
            return null;
        }

        var interval = Interval(task.Schedule);
        TimeSpan youngest;
        string fix;
        switch (task.Retention.Mode)
        {
            case RetentionModes.Age when task.Retention.MaxAgeDays > 0:
                youngest = TimeSpan.FromDays(task.Retention.MaxAgeDays);
                fix = Messages.Format(Messages.WormRetentionFixAge, WholeDays(protection));
                break;

            case RetentionModes.Count:
                // N full backups are kept: when the next full one comes, the oldest chain goes, and its newest set
                // was made just before the full backup that is now the oldest one kept.
                if (interval is not { } run || FullEvery(task, run) is not { } full)
                {
                    return null;
                }

                youngest = ((Math.Max(1, task.Retention.KeepFull) - 1) * full) + run;
                int keep = (int)Math.Ceiling((protection - run) / full) + 1;
                fix = Messages.Format(Messages.WormRetentionFixCount, Math.Max(2, keep));
                break;

            case RetentionModes.Gfs:
                if (interval is not { } each)
                {
                    return null;
                }

                if (SeveralADay(task.Schedule) && task.BackupType != BackupTypes.Incremental)
                {
                    // Only the last backup of a day is kept; the others of that day go at the next run.
                    youngest = each;
                    fix = Messages.Format(Messages.WormRetentionFixGfsDay, WholeDays(protection));
                }
                else
                {
                    // A day leaves the daily backups after so many days; without them a backup that is not the
                    // weekly or monthly one goes at the next run.
                    youngest = task.Retention.Gfs.Daily > 0 ? ((task.Retention.Gfs.Daily - 1) * Day) + each : each;
                    int days = (int)Math.Ceiling((protection - each) / Day) + 1;
                    fix = Messages.Format(Messages.WormRetentionFixGfs, Math.Max(1, days));
                }

                break;

            default:
                return null;
        }

        if (youngest >= protection)
        {
            return null;
        }

        return WormSettings.Of(destination) is { } worm
            ? Messages.Format(Messages.WormRetentionTooShort, TaskDescriber.Destination(destination), worm.Days, Duration(protection), Duration(youngest), fix)
            : Messages.Format(Messages.ObjectLockRetentionTooShort, TaskDescriber.Destination(destination), destination.ObjectLockDays,
                Duration(protection), Duration(youngest), fix);
    }

    /// <summary>
    /// How long after it is written a backup cannot be deleted there: WORM counts its lock delay and a margin
    /// (section 6.3), S3 Object Lock its days from the upload (section 6.3, 4b); zero if nothing protects it.
    /// </summary>
    public static TimeSpan Protection(TaskDestination destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        return WormSettings.Of(destination) is { } worm ? worm.Protection
            : destination.Kind == DestinationKinds.S3 && destination.ObjectLockDays > 0 ? TimeSpan.FromDays(destination.ObjectLockDays)
            : TimeSpan.Zero;
    }

    /// <summary>The average time between runs of the schedule; null for a task run by hand.</summary>
    public static TimeSpan? Interval(ScheduleOptions schedule)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        int times = Math.Max(1, schedule.Times.Distinct(StringComparer.Ordinal).Count());
        return schedule.Type switch
        {
            ScheduleTypes.Daily => Week / (times * DaysOf(schedule.DaysOfWeek, 7)),
            ScheduleTypes.Weekly => Week / (times * DaysOf(schedule.DaysOfWeek, 1)),
            ScheduleTypes.Monthly => TimeSpan.FromDays(30) / (times * DaysOf(schedule.DaysOfMonth, 1)),
            ScheduleTypes.Interval when schedule.IntervalMinutes > 0 => TimeSpan.FromMinutes(schedule.IntervalMinutes),
            _ => null,
        };

        static int DaysOf(List<int> days, int none) => days.Count == 0 ? none : days.Distinct().Count();
    }

    /// <summary>The schedule runs the task more than once on some days.</summary>
    private static bool SeveralADay(ScheduleOptions schedule) => schedule.Type == ScheduleTypes.Interval
        ? schedule.IntervalMinutes is > 0 and < 24 * 60
        : schedule.Times.Distinct(StringComparer.Ordinal).Count() > 1;

    /// <summary>How often a full backup comes; null if only the first one is full.</summary>
    private static TimeSpan? FullEvery(TaskDefinition task, TimeSpan run)
    {
        if (task.BackupType == BackupTypes.Full)
        {
            return run;
        }

        var force = task.ForceFull;
        var candidates = new List<TimeSpan>();
        if (force.EveryRuns > 0)
        {
            candidates.Add(force.EveryRuns * run);
        }

        if (force.Weekday is >= 1 and <= 7)
        {
            candidates.Add(Week);
        }

        if (force.MonthDay is not null)
        {
            candidates.Add(Month);
        }

        return candidates.Count == 0 ? null : candidates.Min();
    }

    /// <summary>Days enough to cover the time: 7 days 4 hours need 8.</summary>
    private static int WholeDays(TimeSpan time) => (int)Math.Ceiling(time.TotalDays);

    /// <summary>"7 дн. 4 ч", "8 дн.", "3 ч".</summary>
    private static string Duration(TimeSpan time)
    {
        int hours = (int)Math.Round(time.TotalHours);
        return hours < 24 ? Messages.Format(Messages.WormDurationHours, hours)
            : hours % 24 == 0 ? Messages.Format(Messages.WormDurationDays, hours / 24)
            : Messages.Format(Messages.WormDurationDaysHours, hours / 24, hours % 24);
    }
}
