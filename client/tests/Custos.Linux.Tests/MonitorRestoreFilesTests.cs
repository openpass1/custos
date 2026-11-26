using Custos.Service.Platform;

namespace Custos.Linux.Tests;

public sealed class MonitorRestoreFilesTests
{
    [Fact]
    public void Source_reads_stay_in_the_destination_without_following_links_or_opening_special_files()
    {
        string source = TestPlace.NewFolder("restore-source");
        string outside = TestPlace.NewFolder("restore-outside");
        string localFile = Path.Combine(source, "local.txt");
        string externalFile = Path.Combine(outside, "external.txt");
        File.WriteAllText(localFile, "local contents");
        File.WriteAllText(externalFile, "external contents");

        using (var stream = MonitorRestoreFiles.OpenSource(source, localFile))
        using (var reader = new StreamReader(stream))
        {
            Assert.Equal("local contents", reader.ReadToEnd());
        }

        string link = Path.Combine(source, "link.txt");
        File.CreateSymbolicLink(link, externalFile);
        string folderLink = Path.Combine(source, "outside");
        Directory.CreateSymbolicLink(folderLink, outside);
        string fifo = Path.Combine(source, "fifo");
        var made = Tool.Run("mkfifo", [fifo]);
        Assert.True(made.ExitCode == 0, made.Output);

        Assert.Throws<IOException>(() => MonitorRestoreFiles.OpenSource(source, externalFile));
        Assert.Throws<IOException>(() => MonitorRestoreFiles.OpenSource(source, link));
        Assert.Throws<IOException>(() => MonitorRestoreFiles.OpenSource(source, Path.Combine(folderLink, "external.txt")));
        Assert.Throws<IOException>(() => MonitorRestoreFiles.OpenSource(source, fifo));
    }

    [Fact]
    public void The_job_and_receipt_are_private_and_an_existing_job_is_never_reserved_twice()
    {
        Assert.SkipWhen(TestPlace.Installed, "Use the test service data directory for restore reservations.");
        Directory.CreateDirectory(DataFolders.Database, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Guid job = Guid.NewGuid();
        MonitorRestoreFiles.Reserve(job);
        string output = MonitorRestoreFiles.Output(job);
        string record = MonitorRestoreFiles.RecordFolder(job);
        const UnixFileMode privateDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        Assert.Equal(privateDirectory, File.GetUnixFileMode(output));
        Assert.Equal(privateDirectory, File.GetUnixFileMode(record));
        Assert.NotEqual(Path.GetDirectoryName(output), Path.GetDirectoryName(record));

        Directory.Delete(output);
        Assert.Throws<IOException>(() => MonitorRestoreFiles.Reserve(job));
        Assert.False(Directory.Exists(output));
    }
}
