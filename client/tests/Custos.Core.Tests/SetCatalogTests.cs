using Custos.Core.Backup;
using Custos.Core.Model;

namespace Custos.Core.Tests;

public sealed class SetCatalogTests
{
    private static readonly DateTimeOffset Day0 = new(2026, 1, 1, 22, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Nothing_is_removed_while_there_are_no_more_fulls_than_kept()
    {
        var sets = new[] { Full("F1", 0), Incremental("I1", 1, "F1"), Full("F2", 2) };

        Assert.Empty(SetCatalog.PlanCountRetention(sets, keepFull: 2, protectedRunId: "F2"));
    }

    [Fact]
    public void Old_chains_go_as_a_whole()
    {
        var sets = new[]
        {
            Full("F1", 0), Incremental("I1a", 1, "F1"), Differential("D1b", 2, "F1"),
            Full("F2", 3), Incremental("I2a", 4, "F2"),
            Full("F3", 5),
        };

        var removed = SetCatalog.PlanCountRetention(sets, keepFull: 2, protectedRunId: "F3");

        Assert.Equal(["F1", "I1a", "D1b"], removed.Select(s => s.Header.RunId));
    }

    [Fact]
    public void The_full_set_the_new_set_builds_on_stays()
    {
        // F2 was made by a run that failed on another destination: never committed, so I3 still builds on F1.
        var sets = new[] { Full("F1", 0), Incremental("I1", 1, "F1"), Full("F2", 2), Incremental("I3", 3, "F1") };

        Assert.Empty(SetCatalog.PlanCountRetention(sets, keepFull: 1, protectedRunId: "I3"));
    }

    [Fact]
    public void Orphans_go_only_when_older_than_every_kept_chain()
    {
        var sets = new[]
        {
            Incremental("Old", 0, "Gone"), Full("F1", 1), Full("F2", 2), Incremental("New", 3, "Gone"), Full("F3", 4),
        };

        var removed = SetCatalog.PlanCountRetention(sets, keepFull: 2, protectedRunId: "F3");

        Assert.Equal(["Old", "F1"], removed.Select(s => s.Header.RunId));
    }

    [Fact]
    public void At_least_one_full_is_always_kept()
    {
        var sets = new[] { Full("F1", 0), Full("F2", 1) };

        var removed = SetCatalog.PlanCountRetention(sets, keepFull: 0, protectedRunId: null);

        Assert.Equal(["F1"], removed.Select(s => s.Header.RunId));
    }

    [Fact]
    public void Age_removes_sets_older_than_the_limit()
    {
        var sets = new[]
        {
            Full("F1", 0), Incremental("I1", 1, "F1"), Incremental("I2", 2, "F1"), Full("F2", 10), Incremental("I3", 11, "F2"),
        };

        var removed = SetCatalog.PlanAgeRetention(sets, maxAgeDays: 5, now: Day0.AddDays(12), protectedRunId: "I3");

        Assert.Equal(["F1", "I1", "I2"], removed.Select(s => s.Header.RunId));
    }

    [Fact]
    public void Age_keeps_old_sets_a_young_set_needs()
    {
        // I2 is young: its full set and the incremental set before it stay. D2 needs only its full set.
        var incremental = new[] { Full("F1", 0), Incremental("I1", 1, "F1"), Incremental("I2", 8, "F1") };
        var differential = new[] { Full("F1", 0), Differential("D1", 1, "F1"), Differential("D2", 8, "F1") };

        Assert.Empty(SetCatalog.PlanAgeRetention(incremental, maxAgeDays: 5, now: Day0.AddDays(10), protectedRunId: "I2"));
        Assert.Equal(["D1"], SetCatalog.PlanAgeRetention(differential, maxAgeDays: 5, now: Day0.AddDays(10), protectedRunId: "D2")
            .Select(s => s.Header.RunId));
    }

    [Fact]
    public void Age_never_removes_the_newest_full_set()
    {
        var sets = new[] { Full("F1", 0), Full("F2", 1) };

        Assert.Equal(["F1"], SetCatalog.PlanAgeRetention(sets, maxAgeDays: 5, now: Day0.AddDays(100), protectedRunId: null)
            .Select(s => s.Header.RunId));
        Assert.Empty(SetCatalog.PlanAgeRetention(sets, maxAgeDays: 0, now: Day0.AddDays(100), protectedRunId: null));
    }

    [Fact]
    public void Gfs_keeps_the_last_set_of_each_day_week_and_month()
    {
        // One full set a day from Thursday 1 January to Wednesday 21 January, and one more earlier on the 21st.
        var sets = Enumerable.Range(0, 21).Select(d => Full("D" + (d + 1), d)).ToList();
        sets.Add(At("Early21", Day0.AddDays(20).AddHours(-12), BackupTypes.Full, null));
        var gfs = new GfsOptions { Daily = 3, Weekly = 2, Monthly = 1 };

        var removed = SetCatalog.PlanGfsRetention(sets, gfs, TimeZoneInfo.Utc, protectedRunId: "D21");

        // Days: 21, 20, 19. Weeks: the one of 19-25 January (21st) and of 12-18 January (18th). Month: the 21st.
        var kept = sets.Except(removed).Select(s => s.Header.RunId).Order(StringComparer.Ordinal);
        Assert.Equal(["D18", "D19", "D20", "D21"], kept);
    }

    [Fact]
    public void Gfs_keeps_full_sets_for_weeks_and_months_and_drops_the_incremental_ones_between()
    {
        // Thursday 1 January to Wednesday 21 January: incremental sets, full on the first day and on every Monday (5, 12, 19).
        var sets = new List<StoredSet>();
        string parent = "F1";
        for (int day = 0; day < 21; day++)
        {
            var date = Day0.AddDays(day);
            if (day == 0 || date.DayOfWeek == DayOfWeek.Monday)
            {
                parent = "F" + (day + 1);
                sets.Add(Full(parent, day));
            }
            else
            {
                sets.Add(Incremental("I" + (day + 1), day, parent));
            }
        }

        var removed = SetCatalog.PlanGfsRetention(sets, new GfsOptions { Daily = 3, Weekly = 3, Monthly = 1 }, TimeZoneInfo.Utc, protectedRunId: "I21");

        // Days 19 (Monday, full), 20 and 21; weeks: the full sets of the 19th, 12th and 5th; the month: its last full, the 19th.
        // The Sunday incremental sets (18th, 11th) are no longer kept as weeks, so their chains go.
        var kept = sets.Except(removed).Select(s => s.Header.RunId).Order(StringComparer.Ordinal);
        Assert.Equal(["F12", "F19", "F5", "I20", "I21"], kept);
    }

    [Fact]
    public void A_year_of_daily_incremental_backups_with_gfs_keeps_about_twenty_sets()
    {
        // What the engine does in this mode: full on the first run of every week, retention after every run.
        var sets = new List<StoredSet>();
        string parent = string.Empty;
        var gfs = new GfsOptions { Daily = 7, Weekly = 4, Monthly = 12 };
        for (int day = 0; day < 365; day++)
        {
            var date = Day0.AddDays(day);
            bool newWeek = day == 0 || SetCatalog.IsoWeek(date.DateTime) != SetCatalog.IsoWeek(date.AddDays(-1).DateTime);
            string id = "R" + day;
            if (newWeek)
            {
                parent = id;
            }

            sets.Add(newWeek ? Set(id, day, BackupTypes.Full, null) : Set(id, day, BackupTypes.Incremental, parent));
            var removed = SetCatalog.PlanGfsRetention(sets, gfs, TimeZoneInfo.Utc, protectedRunId: id);
            sets.RemoveAll(s => removed.Contains(s));
        }

        int fulls = sets.Count(s => s.Header.BackupType == BackupTypes.Full);
        Assert.InRange(sets.Count, 19, 24);
        Assert.InRange(fulls, 13, 17);
    }

    [Fact]
    public void Gfs_counts_days_in_local_time()
    {
        var utcPlus3 = TimeZoneInfo.CreateCustomTimeZone("UTC+3", TimeSpan.FromHours(3), "UTC+3", "UTC+3");
        var sets = new[]
        {
            At("Jan1", new DateTimeOffset(2026, 1, 1, 20, 0, 0, TimeSpan.Zero), BackupTypes.Full, null),
            At("Jan2a", new DateTimeOffset(2026, 1, 1, 22, 0, 0, TimeSpan.Zero), BackupTypes.Full, null),
            At("Jan2b", new DateTimeOffset(2026, 1, 1, 23, 0, 0, TimeSpan.Zero), BackupTypes.Full, null),
        };

        var removed = SetCatalog.PlanGfsRetention(sets, new GfsOptions { Daily = 2, Weekly = 0, Monthly = 0 }, utcPlus3, protectedRunId: null);

        Assert.Equal(["Jan2a"], removed.Select(s => s.Header.RunId));
    }

    [Fact]
    public void Gfs_keeps_what_the_chosen_sets_build_on()
    {
        var incremental = new List<StoredSet> { Full("F1", 0) };
        incremental.AddRange(Enumerable.Range(1, 5).Select(d => Incremental("I" + d, d, "F1")));
        var differential = new List<StoredSet> { Full("F1", 0) };
        differential.AddRange(Enumerable.Range(1, 5).Select(d => Differential("D" + d, d, "F1")));
        var gfs = new GfsOptions { Daily = 2, Weekly = 0, Monthly = 0 };

        Assert.Empty(SetCatalog.PlanGfsRetention(incremental, gfs, TimeZoneInfo.Utc, protectedRunId: "I5"));
        Assert.Equal(["D1", "D2", "D3"], SetCatalog.PlanGfsRetention(differential, gfs, TimeZoneInfo.Utc, protectedRunId: "D5")
            .Select(s => s.Header.RunId));
    }

    [Fact]
    public void Scan_finds_the_sets_of_the_task_and_skips_partial_and_foreign_folders()
    {
        using var temp = new TempFolder();
        WriteSet(temp.Combine("B"), "task-1", "run-2", Day0.AddDays(1));
        WriteSet(temp.Combine("A"), "task-1", "run-1", Day0);
        WriteSet(temp.Combine("C" + SetCatalog.PartialSuffix), "task-1", "run-3", Day0.AddDays(2));
        WriteSet(temp.Combine("D"), "task-2", "run-4", Day0);
        Directory.CreateDirectory(temp.Combine("E"));

        var sets = SetCatalog.Scan(temp.Path, "TASK-1");

        Assert.Equal(["run-1", "run-2"], sets.Select(s => s.Header.RunId));
        Assert.Equal(temp.Combine("A"), sets[0].Location);
    }

    [Fact]
    public void Deleting_a_set_removes_read_only_files_too()
    {
        using var temp = new TempFolder();
        string file = temp.WriteFile("Set/sub/file.txt", "x", DateTime.UtcNow);
        File.SetAttributes(file, FileAttributes.ReadOnly);

        SetCatalog.DeleteSetFolder(temp.Combine("Set"));

        Assert.False(Directory.Exists(temp.Combine("Set")));
    }

    [Fact]
    public void Set_names_follow_the_template()
    {
        var time = new DateTime(2026, 3, 7, 9, 5, 0);

        Assert.Equal("Docs 2026-03-07 09-05 (Differential)", SetNaming.Expand(LayoutOptions.DefaultNameTemplate, "Docs", time, "PC", BackupTypes.Differential));
        Assert.Equal("PC_ a_b", SetNaming.Expand("{computer}: {task}", "a/b", time, "PC", BackupTypes.Full));
        Assert.Equal("CUSTOS", SetNaming.CleanName(" .. "));
    }

    [Fact]
    public void Unique_names_skip_existing_and_partial_folders()
    {
        using var temp = new TempFolder();
        Directory.CreateDirectory(temp.Combine("Set"));
        Directory.CreateDirectory(temp.Combine("Set (2)" + SetCatalog.PartialSuffix));

        Assert.Equal("Set (3)", SetNaming.MakeUnique(temp.Path, "Set"));
        Assert.Equal("Other", SetNaming.MakeUnique(temp.Path, "Other"));
    }

    private static StoredSet Full(string runId, int day) => Set(runId, day, BackupTypes.Full, null);

    private static StoredSet Incremental(string runId, int day, string parent) => Set(runId, day, BackupTypes.Incremental, parent);

    private static StoredSet Differential(string runId, int day, string parent) => Set(runId, day, BackupTypes.Differential, parent);

    private static StoredSet Set(string runId, int day, string type, string? parent) => At(runId, Day0.AddDays(day), type, parent);

    private static StoredSet At(string runId, DateTimeOffset startedAt, string type, string? parent) => new(
        "/sets/" + runId,
        new SetManifestHeader { RunId = runId, TaskId = "task", BackupType = type, ParentFullRunId = parent, StartedAt = startedAt });

    private static void WriteSet(string folder, string taskId, string runId, DateTimeOffset startedAt)
    {
        Directory.CreateDirectory(folder);
        using var stream = File.Create(Path.Combine(folder, SetManifest.FileName));
        SetManifest.Write(stream, new SetManifestHeader { TaskId = taskId, RunId = runId, BackupType = BackupTypes.Full, StartedAt = startedAt }, []);
    }
}
