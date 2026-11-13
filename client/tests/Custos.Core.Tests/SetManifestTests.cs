using System.Text;
using Custos.Core.Backup;
using Custos.Core.Model;

namespace Custos.Core.Tests;

public sealed class SetManifestTests
{
    private static readonly SetManifestHeader Header = new()
    {
        SetName = "Docs 2026-11-01 10-00 (Incremental)",
        TaskId = "6f0c1d2e-0000-4000-8000-000000000001",
        TaskName = "Docs",
        RunId = "run-2",
        BackupType = BackupTypes.Incremental,
        ParentFullRunId = "run-1",
        ParentFullSetName = "Docs 2026-10-31 10-00 (Full)",
        Computer = "TESTPC",
        StartedAt = new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero),
        FinishedAt = new DateTimeOffset(2026, 11, 1, 10, 5, 0, TimeSpan.Zero),
        Status = RunStatuses.Warning,
        Sources = [new ManifestSource { Id = "s1", Path = @"D:\Data", Target = "Data" }],
        FileCount = 3000,
        SetFileCount = 1500,
        SetBytes = 123456789,
    };

    [Fact]
    public void Header_and_files_survive_a_round_trip()
    {
        var files = Files(3000);
        byte[] bytes = Write(files);

        var header = SetManifest.ReadHeader(new MemoryStream(bytes));
        var read = SetManifest.ReadFiles(new MemoryStream(bytes));

        Assert.Equal(Header.SetName, header.SetName);
        Assert.Equal(Header.TaskId, header.TaskId);
        Assert.Equal(Header.RunId, header.RunId);
        Assert.Equal(Header.ParentFullRunId, header.ParentFullRunId);
        Assert.Equal(Header.ParentFullSetName, header.ParentFullSetName);
        Assert.Equal(Header.StartedAt, header.StartedAt);
        Assert.Equal(Header.FinishedAt, header.FinishedAt);
        Assert.Equal(Header.Status, header.Status);
        Assert.Equal(Header.SetBytes, header.SetBytes);
        var source = Assert.Single(header.Sources);
        Assert.Equal(@"D:\Data", source.Path);
        Assert.Equal("Data", source.Target);
        Assert.Equal(files, read);
    }

    [Fact]
    public void The_header_is_read_from_a_stream_that_delivers_a_few_bytes_at_a_time()
    {
        byte[] bytes = Write(Files(10));

        var header = SetManifest.ReadHeader(new TrickleStream(bytes, 7));

        Assert.Equal(Header.RunId, header.RunId);
        Assert.Equal("Data", Assert.Single(header.Sources).Target);
    }

    [Fact]
    public void A_full_set_has_no_parent()
    {
        var full = new SetManifestHeader { TaskId = "t", RunId = "r", BackupType = BackupTypes.Full };
        using var stream = new MemoryStream();
        SetManifest.Write(stream, full, []);

        var header = SetManifest.ReadHeader(new MemoryStream(stream.ToArray()));

        Assert.Null(header.ParentFullRunId);
        Assert.Contains("\"parent_full_run_id\": null", Encoding.UTF8.GetString(stream.ToArray()), StringComparison.Ordinal);
    }

    [Fact]
    public void Each_file_is_one_line_with_letters_as_they_are()
    {
        var files = Files(5);
        string text = Encoding.UTF8.GetString(Write(files));
        string[] lines = text.Split('\n');

        // Cyrillic is UTF-8, not \uXXXX: 2 bytes a letter instead of 6, and the file can be read in Notepad.
        Assert.Contains("файл 0.txt", text, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', text);
        Assert.DoesNotContain("\\u0", text, StringComparison.Ordinal);
        Assert.Equal(files.Count, lines.Count(l => l.StartsWith("    {\"path\":", StringComparison.Ordinal)));
        Assert.Contains("  \"files\": [", lines);
        Assert.Contains("  \"task_name\": \"Docs\",", lines);
    }

    [Fact]
    public void Paths_with_quotes_slashes_tabs_and_emoji_survive_a_round_trip()
    {
        var modified = new DateTime(2026, 11, 13, 10, 0, 0, DateTimeKind.Utc).AddTicks(1234567);
        List<ManifestFile> files =
        [
            new("Data\\\"кавычки\" и\tтаб\\😀 <b>&'+.txt", 1, modified, true, new string('b', 64)),
            new("/var/www/site/data/www/example.com/index.php", 2, modified, false, null)
            {
                Posix = new ManifestPosix("f", "644", 1001, 1001, "site", "site", null, modified),
            },
            new("/srv/\uFFFD.bin", 3, modified, true, null) { PathBase64 = "L3Nydi//LmJpbg==" },
        ];

        var read = SetManifest.ReadFiles(new MemoryStream(Write(files)));

        Assert.Equal(files, read);
    }

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("{\"format\": \"something-else\", \"run_id\": \"r\", \"task_id\": \"t\", \"files\": []}")]
    [InlineData("{\"format\": \"custos-set\", \"run_id\": \"r\"")]
    public void Other_files_are_not_manifests(string text)
    {
        Assert.Throws<InvalidDataException>(() => SetManifest.ReadHeader(new MemoryStream(Encoding.UTF8.GetBytes(text))));
    }

    private static List<ManifestFile> Files(int count) =>
        [.. Enumerable.Range(0, count).Select(i => new ManifestFile(
            $"Data\\folder {i / 100}\\файл {i}.txt",
            i * 10L,
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(i * 1.5),
            i % 2 == 0,
            i % 2 == 0 ? new string('a', 64) : null))];

    private static byte[] Write(List<ManifestFile> files)
    {
        using var stream = new MemoryStream();
        SetManifest.Write(stream, Header, files);
        return stream.ToArray();
    }

    /// <summary>A stream that returns at most a few bytes per read, like a slow network share.</summary>
    private sealed class TrickleStream(byte[] data, int chunk) : MemoryStream(data)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, chunk));
    }
}
