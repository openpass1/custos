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

    private static StoredSet Set(string runId, int day, string type, string? parent) => new(
        "/sets/" + runId,
        new SetManifestHeader { RunId = runId, TaskId = "task", BackupType = type, ParentFullRunId = parent, StartedAt = Day0.AddDays(day) });

    private static void WriteSet(string folder, string taskId, string runId, DateTimeOffset startedAt)
    {
        Directory.CreateDirectory(folder);
        using var stream = File.Create(Path.Combine(folder, SetManifest.FileName));
        SetManifest.Write(stream, new SetManifestHeader { TaskId = taskId, RunId = runId, BackupType = BackupTypes.Full, StartedAt = startedAt }, []);
    }
}
