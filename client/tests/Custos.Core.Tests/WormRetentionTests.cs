using Custos.Core.Backup;
using Custos.Core.Model;

namespace Custos.Core.Tests;

/// <summary>Whether the retention of a task fits the WORM of its destinations (section 6.6): WORM for 7 days with
/// the lock 3 hours after writing protects a backup for 7 days 4 hours.</summary>
public sealed class WormRetentionTests
{
    private static TaskDefinition Task(string type = BackupTypes.Full, int wormDays = 7, string schedule = ScheduleTypes.Daily, int intervalMinutes = 0)
    {
        var task = new TaskDefinition { Name = "Docs", BackupType = type };
        task.Destinations.Add(new TaskDestination { Kind = DestinationKinds.Unc, Path = @"\\nas\backup", WormDays = wormDays, WormLockHours = 3 });
        task.Schedule.Type = schedule;
        task.Schedule.IntervalMinutes = intervalMinutes;
        return task;
    }

    private static string Fix(string format, int value) => Messages.Format(format, value);

    [Fact]
    public void Keeping_too_few_full_backups_made_daily_is_refused_with_the_number_that_fits()
    {
        var task = Task();
        task.Retention.Mode = RetentionModes.Count;
        task.Retention.KeepFull = 3;

        string? problem = WormRetention.Problem(task);

        Assert.NotNull(problem);
        Assert.Contains(@"\\nas\backup", problem, StringComparison.Ordinal);
        Assert.Contains(Messages.Format(Messages.WormDurationDaysHours, 7, 4), problem, StringComparison.Ordinal);
        Assert.Contains(Messages.Format(Messages.WormDurationDays, 3), problem, StringComparison.Ordinal);
        Assert.EndsWith(Fix(Messages.WormRetentionFixCount, 8), problem, StringComparison.Ordinal);

        task.Retention.KeepFull = 7;
        Assert.NotNull(WormRetention.Problem(task));
        task.Retention.KeepFull = 8;
        Assert.Null(WormRetention.Problem(task));
    }

    [Fact]
    public void Incremental_chains_count_by_their_full_backups()
    {
        // A full backup every Sunday: two chains kept means the oldest goes at least a week after its last backup.
        var task = Task(BackupTypes.Incremental);
        task.ForceFull.Weekday = 7;
        task.Retention.Mode = RetentionModes.Count;
        task.Retention.KeepFull = 2;
        Assert.Null(WormRetention.Problem(task));

        task.Retention.KeepFull = 1;
        Assert.EndsWith(Fix(Messages.WormRetentionFixCount, 2), WormRetention.Problem(task), StringComparison.Ordinal);

        // Without forced full backups only the first one is full and nothing is ever deleted.
        task.ForceFull.Weekday = null;
        Assert.Null(WormRetention.Problem(task));
    }

    [Fact]
    public void Retention_by_age_needs_whole_days_beyond_the_protection()
    {
        var task = Task(BackupTypes.Incremental, schedule: ScheduleTypes.Interval, intervalMinutes: 60);
        task.Retention.Mode = RetentionModes.Age;
        task.Retention.MaxAgeDays = 7;

        Assert.EndsWith(Fix(Messages.WormRetentionFixAge, 8), WormRetention.Problem(task), StringComparison.Ordinal);

        task.Retention.MaxAgeDays = 8;
        Assert.Null(WormRetention.Problem(task));
        task.Retention.MaxAgeDays = 0;
        Assert.Null(WormRetention.Problem(task));
    }

    [Fact]
    public void Gfs_keeps_a_day_for_as_many_days_as_its_daily_backups()
    {
        var task = Task();
        task.Retention.Mode = RetentionModes.Gfs;
        task.Retention.Gfs.Daily = 7;

        Assert.EndsWith(Fix(Messages.WormRetentionFixGfs, 8), WormRetention.Problem(task), StringComparison.Ordinal);

        task.Retention.Gfs.Daily = 8;
        Assert.Null(WormRetention.Problem(task));
    }

    [Fact]
    public void Gfs_deletes_the_earlier_backups_of_a_day_unless_they_are_incremental()
    {
        var task = Task(BackupTypes.Differential, schedule: ScheduleTypes.Interval, intervalMinutes: 60);
        task.Retention.Mode = RetentionModes.Gfs;
        task.Retention.Gfs.Daily = 30;

        string? problem = WormRetention.Problem(task);
        Assert.NotNull(problem);
        Assert.Contains(Messages.Format(Messages.WormDurationHours, 1), problem, StringComparison.Ordinal);
        Assert.EndsWith(Fix(Messages.WormRetentionFixGfsDay, 8), problem, StringComparison.Ordinal);

        // Incremental backups of a day are needed by the last one, so they stay with it.
        task.BackupType = BackupTypes.Incremental;
        Assert.Null(WormRetention.Problem(task));
        task.Retention.Gfs.Daily = 8;
        Assert.EndsWith(Fix(Messages.WormRetentionFixGfs, 9), WormRetention.Problem(task), StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_is_checked_without_worm_for_a_mirror_or_without_a_schedule_where_it_counts()
    {
        var task = Task(wormDays: 0);
        task.Retention.Mode = RetentionModes.Count;
        task.Retention.KeepFull = 1;
        Assert.Null(WormRetention.Problem(task));

        task = Task(BackupTypes.Mirror);
        task.Retention.Mode = RetentionModes.Count;
        task.Retention.KeepFull = 1;
        Assert.Null(WormRetention.Problem(task));

        task = Task(schedule: ScheduleTypes.Manual);
        task.Retention.Mode = RetentionModes.Count;
        task.Retention.KeepFull = 1;
        Assert.Null(WormRetention.Problem(task));

        // Retention by age does not depend on the schedule.
        task.Retention.Mode = RetentionModes.Age;
        task.Retention.MaxAgeDays = 3;
        Assert.NotNull(WormRetention.Problem(task));

        task.Retention.Mode = RetentionModes.None;
        Assert.Null(WormRetention.Problem(task));
    }

    [Fact]
    public void The_longest_protection_of_the_destinations_counts()
    {
        var task = Task();
        task.Destinations.Add(new TaskDestination { Kind = DestinationKinds.Usb, Path = @"E:\Backup", UsbLabel = "Archive", WormDays = 14, WormLockHours = 1 });
        task.Retention.Mode = RetentionModes.Age;
        task.Retention.MaxAgeDays = 10;

        string? problem = WormRetention.Problem(task);

        Assert.NotNull(problem);
        Assert.Contains("Archive", problem, StringComparison.Ordinal);
        Assert.EndsWith(Fix(Messages.WormRetentionFixAge, 15), problem, StringComparison.Ordinal);
    }

    [Fact]
    public void The_interval_of_a_schedule_is_its_average()
    {
        Assert.Equal(TimeSpan.FromDays(1), WormRetention.Interval(new ScheduleOptions()));
        Assert.Equal(TimeSpan.FromHours(12), WormRetention.Interval(new ScheduleOptions { Times = ["08:00", "20:00"] }));
        Assert.Equal(TimeSpan.FromDays(7.0 / 5), WormRetention.Interval(new ScheduleOptions { DaysOfWeek = [1, 2, 3, 4, 5] }));
        Assert.Equal(TimeSpan.FromDays(7), WormRetention.Interval(new ScheduleOptions { Type = ScheduleTypes.Weekly, DaysOfWeek = [5] }));
        Assert.Equal(TimeSpan.FromDays(15), WormRetention.Interval(new ScheduleOptions { Type = ScheduleTypes.Monthly, DaysOfMonth = [1, 15] }));
        Assert.Equal(TimeSpan.FromMinutes(90), WormRetention.Interval(new ScheduleOptions { Type = ScheduleTypes.Interval, IntervalMinutes = 90 }));
        Assert.Null(WormRetention.Interval(new ScheduleOptions { Type = ScheduleTypes.Manual }));
    }
}
