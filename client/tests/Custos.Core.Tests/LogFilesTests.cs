using System.Text;
using Custos.Core.Agent;
using Custos.Core.Backup;
using Custos.Core.Ipc;

namespace Custos.Core.Tests;

/// <summary>The log files of the service (section 6.10): the journal of each day and the log of each run.</summary>
public sealed class LogFilesTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void A_line_is_written_and_read_back_with_its_level_and_task()
    {
        var time = new DateTimeOffset(2026, 11, 15, 16, 10, 5, TimeSpan.Zero);
        string text = LogText.Format(time, LogLineLevels.Warning, "Office [main]", "A file\r\nis locked", oneLine: true);
        Assert.Equal(time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)
            + " [W] [Office [main]] A file is locked", text);

        var line = LogText.Parse(text, readTask: true)!;
        Assert.Equal(time, line.Time);
        Assert.Equal(LogLineLevels.Warning, line.Level);
        Assert.Equal("Office [main]", line.TaskName);
        Assert.Equal("A file is locked", line.Text);

        // A run log has no task: brackets at the start of its text stay text.
        var run = LogText.Parse(LogText.Format(time, LogLineLevels.Error, null, "[D:] is gone"), readTask: false)!;
        Assert.Null(run.TaskName);
        Assert.Equal("[D:] is gone", run.Text);
        Assert.Equal(LogLineLevels.Error, run.Level);

        Assert.Null(LogText.Parse("   at Custos.Core.Something()", readTask: false));
        Assert.Null(LogText.Parse(string.Empty, readTask: true));
    }

    [Theory]
    [InlineData(LogFilterLevels.All, null, 4)]
    [InlineData(LogFilterLevels.Problems, null, 2)]
    [InlineData(LogFilterLevels.Errors, null, 1)]
    [InlineData(LogFilterLevels.All, "LOCKED", 1)]
    [InlineData(LogFilterLevels.All, "photos", 2)]
    [InlineData(LogFilterLevels.Problems, "photos", 1)]
    [InlineData("unknown", "  ", 4)]
    public void The_filter_takes_the_level_and_a_text_of_the_line_or_its_task(string level, string? search, int expected)
    {
        LogLine[] lines =
        [
            new() { Level = LogLineLevels.Info, Text = "Started", TaskName = "Office" },
            new() { Level = LogLineLevels.Warning, Text = "A file is locked", TaskName = "Office" },
            new() { Level = LogLineLevels.Error, Text = "The disk is full", TaskName = "Photos" },
            new() { Level = LogLineLevels.Detail, Text = "Copied IMG_0001.JPG", TaskName = "Photos" },
        ];

        Assert.Equal(expected, lines.Count(new LogFilter(level, search).Matches));
    }

    [Fact]
    public void A_run_log_goes_to_the_folder_of_its_month_and_is_found_by_the_run_id()
    {
        string folder = _temp.Combine("runs");
        string runId = "1a2b3c4d-0000-0000-0000-000000000000";
        var started = new DateTimeOffset(2026, 11, 4, 16, 41, 3, TimeSpan.Zero);
        string path;
        using (var file = RunLogFile.Create(folder, "Docs: office", runId, started))
        {
            file.Write(RunLogLevel.Info, "Started");
            file.Write(RunLogLevel.Warning, "Locked");
            path = file.Path;
        }

        string month = started.ToLocalTime().ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(Path.Combine(folder, month), Path.GetDirectoryName(path));
        Assert.EndsWith(" 1a2b3c4d.log", path, StringComparison.Ordinal);
        Assert.Equal(path, RunLogFile.Find(folder, runId.ToUpperInvariant()));
        Assert.Null(RunLogFile.Find(folder, "ffffffff-0000-0000-0000-000000000000"));
        Assert.Null(RunLogFile.Find(_temp.Combine("none"), runId));

        // The BOM lets Notepad show any language; the lines are those of the log.
        Assert.Equal(Encoding.UTF8.Preamble.ToArray(), File.ReadAllBytes(path).Take(3));
        var excerpt = LogText.Read(path, _ => true, 100);
        Assert.Equal(["Started", "Locked"], excerpt.Lines.Select(l => l.Text));
        Assert.Equal(LogLineLevels.Warning, excerpt.Lines[1].Level);
    }

    [Fact]
    public void Run_logs_of_older_versions_move_into_month_folders_and_old_ones_are_deleted()
    {
        string folder = _temp.Combine("runs");
        var now = new DateTime(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);
        _temp.WriteFile("runs/2026-05-01 10-00-00 Docs 11111111.log", "old", now.AddDays(-198));
        _temp.WriteFile("runs/2026-11-14 10-00-00 Docs 22222222.log", "new", now.AddDays(-1));
        _temp.WriteFile("runs/notes.log", "not a run", now);
        _temp.WriteFile("runs/2026-11/2026-11-14 10-00-00 Docs 22222222.log", "already there", now);

        Assert.Equal(1, RunLogFile.MoveToMonthFolders(folder));
        Assert.True(File.Exists(Path.Combine(folder, "2026-05", "2026-05-01 10-00-00 Docs 11111111.log")));
        Assert.True(File.Exists(Path.Combine(folder, "2026-11-14 10-00-00 Docs 22222222.log")));
        Assert.True(File.Exists(Path.Combine(folder, "notes.log")));
        Assert.Equal(0, RunLogFile.MoveToMonthFolders(folder));

        // Found wherever it is.
        Assert.NotNull(RunLogFile.Find(folder, "11111111-0000-0000-0000-000000000000"));

        int deleted = RunLogFile.DeleteOld(folder, new DateTimeOffset(now.AddDays(-180)));
        Assert.Equal(1, deleted);
        Assert.False(Directory.Exists(Path.Combine(folder, "2026-05")));
        Assert.True(Directory.Exists(Path.Combine(folder, "2026-11")));
    }

    [Fact]
    public void Reading_a_big_log_keeps_the_last_lines_that_match_and_counts_the_rest()
    {
        string path = _temp.Combine("big.log");
        var time = new DateTimeOffset(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);
        var text = new StringBuilder();
        for (int i = 1; i <= 50; i++)
        {
            string level = i % 10 == 0 ? LogLineLevels.Error : LogLineLevels.Detail;
            text.AppendLine(LogText.Format(time.AddSeconds(i), level, null, "line " + i));
        }

        text.AppendLine("   continued");
        File.WriteAllText(path, text.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var all = LogText.Read(path, _ => true, 20);
        Assert.Equal(51, all.Total);
        Assert.Equal(51, all.Matched);
        Assert.Equal(20, all.Lines.Count);
        Assert.Equal("line 32", all.Lines[0].Text);

        // The line without a time belongs to the line before it.
        Assert.Equal("   continued", all.Lines[^1].Text);
        Assert.Equal(LogLineLevels.Error, all.Lines[^1].Level);
        Assert.Equal(time.AddSeconds(50), all.Lines[^1].Time);

        var errors = LogText.Read(path, new LogFilter(LogFilterLevels.Errors, null).Matches, 3);
        Assert.Equal(51, errors.Total);
        Assert.Equal(6, errors.Matched);
        Assert.Equal(["line 40", "line 50", "   continued"], errors.Lines.Select(l => l.Text));
    }

    [Fact]
    public void The_journal_has_a_file_for_each_day_and_gives_its_last_lines_back()
    {
        string folder = _temp.Combine("journal");
        var day1 = new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero).ToLocalTime();
        var day2 = day1.AddDays(1);
        using (var journal = new JournalFile(folder))
        {
            journal.Write(new LogLine { Time = day1, Text = "Service started" });
            journal.Write(new LogLine { Time = day1.AddMinutes(1), Level = LogLineLevels.Warning, TaskId = "t1", TaskName = "Docs", Text = "Locked" });
            journal.Write(new LogLine { Time = day2, Level = LogLineLevels.Error, TaskId = "t2", TaskName = "Gone", Text = "Line one\nline two" });
            journal.Write(new LogLine { Time = day2.AddMinutes(1), TaskId = "t1", TaskName = "Docs", Text = "Done" });
        }

        string file1 = JournalFile.PathFor(folder, DateOnly.FromDateTime(day1.DateTime));
        string file2 = JournalFile.PathFor(folder, DateOnly.FromDateTime(day2.DateTime));
        Assert.Equal(2, File.ReadAllLines(file1).Length);
        Assert.Equal(2, File.ReadAllLines(file2).Length);

        // Opened again (a restart): lines go on in the same file, without a second BOM.
        using (var journal = new JournalFile(folder))
        {
            journal.Write(new LogLine { Time = day2.AddMinutes(2), Text = "Service started" });
        }

        byte[] bytes = File.ReadAllBytes(file2);
        Assert.Equal(Encoding.UTF8.Preamble.ToArray(), bytes.Take(3));
        Assert.Equal(-1, bytes.AsSpan(3).IndexOf(Encoding.UTF8.Preamble));

        string? Id(string name) => name == "Docs" ? "t1" : null;
        var recent = JournalFile.ReadRecent(folder, 10, Id);
        Assert.Equal(["Service started", "Locked", "Line one line two", "Done", "Service started"], recent.Select(l => l.Text));
        Assert.Equal("t1", recent[1].TaskId);
        Assert.Equal("Docs", recent[1].TaskName);
        Assert.Null(recent[2].TaskId);
        Assert.Equal("Gone", recent[2].TaskName);
        Assert.Equal(LogLineLevels.Error, recent[2].Level);

        Assert.Equal(["Done", "Service started"], JournalFile.ReadRecent(folder, 2, Id).Select(l => l.Text));
        Assert.Empty(JournalFile.ReadRecent(_temp.Combine("none"), 10, Id));

        File.SetLastWriteTimeUtc(file1, DateTime.UtcNow.AddDays(-200));
        File.WriteAllText(Path.Combine(folder, "readme.txt"), "not a day");
        Assert.Equal(1, JournalFile.DeleteOld(folder, DateTimeOffset.UtcNow.AddDays(-180)));
        Assert.False(File.Exists(file1));
        Assert.True(File.Exists(file2));
    }
}
