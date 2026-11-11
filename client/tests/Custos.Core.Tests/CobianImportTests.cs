using System.Text;
using Custos.Core.Cobian;
using Custos.Core.Model;

namespace Custos.Core.Tests;

/// <summary>The task list below is made up: no real paths, servers or accounts.</summary>
public sealed class CobianImportTests
{
    /// <summary>21:30 on 01.01.2024 as a Delphi TDateTime.</summary>
    private const double EveningTime = 45292 + (21.5 / 24);

    private static readonly string List = $$"""
        {11//
        Name=Documents
        Id={00000000-1111-2222-3333-444444444444}
        Enabled=true
        Include subdirectories=true
        Create separated backups=true
        Use VSC=true
        Backup type=1
        Priority=1
        Source="1,D:\Data\Documents","0,D:\Data\notes.txt"
        Destination="1,\\nas\backup\docs","1,E:\Backup"
        Schedule type=1
        Date/Time={{Bits(EveningTime)}}
        Full copies=4
        One full every=7
        Use fixed day=false
        Compression=0
        Encryption=0
        Exclusions="*.tmp","D:\Data\Documents\Cache"
        Inclusions=
        Impersonate=true
        Run as User name=backup-user
        Run as Domain=EXAMPLE
        Pre-backup events=
        //11}
        {11//
        Name=Photos weekly
        Enabled=false
        Backup type=2
        Source=1,D:\Photos and Videos
        Destination=1,F:\Copies
        Schedule type=2
        Day of the week=1,5
        Date/Time={{Bits(EveningTime)}}
        Compression=1
        Full copies=0
        Post-backup events="1,C:\Tools\notify.cmd"
        //11}
        {11//
        Name=Hourly
        Backup type=0
        Mirror=false
        Source=1,C:\Work
        Destination=2,ftp://example.invalid/backup
        Schedule type=5
        Timer=30
        //11}
        """;

    [Fact]
    public void Blocks_are_split_into_values()
    {
        var blocks = CobianListParser.Parse(List);

        Assert.Equal(3, blocks.Count);
        Assert.Equal("Documents", blocks[0].Get("name"));
        Assert.Equal(1, blocks[0].LineNumber);
        Assert.Equal(string.Empty, blocks[0].Get("Inclusions"));
        Assert.Null(blocks[0].Get("Missing key"));
    }

    [Fact]
    public void An_incremental_task_with_an_account_is_converted()
    {
        var result = CobianImporter.Convert(CobianListParser.Parse(List));
        var imported = result.Tasks[0];
        var task = imported.Task;

        Assert.False(task.Enabled);
        Assert.True(imported.EnabledInCobian);
        Assert.Equal("Documents", task.Name);
        Assert.Equal(BackupTypes.Incremental, task.BackupType);
        Assert.Equal(1, task.Priority);
        Assert.Equal([(SourceKinds.Folder, @"D:\Data\Documents"), (SourceKinds.File, @"D:\Data\notes.txt")], task.Sources.Select(s => (s.Kind, s.Path)));
        Assert.Equal([(DestinationKinds.Unc, @"\\nas\backup\docs"), (DestinationKinds.Local, @"E:\Backup")], task.Destinations.Select(d => (d.Kind, d.Path)));
        Assert.Equal(ScheduleTypes.Daily, task.Schedule.Type);
        Assert.Equal(["21:30"], task.Schedule.Times);
        Assert.Equal(RetentionModes.Count, task.Retention.Mode);
        Assert.Equal(4, task.Retention.KeepFull);
        Assert.Equal(7, task.ForceFull.EveryRuns);
        Assert.True(task.Vss.Enabled);
        Assert.Equal(VssFailureActions.Continue, task.Vss.OnFailure);
        Assert.Contains(@"D:\Data\Documents\Cache", task.Filters.Exclude);
        Assert.Equal([@"D:\Data\Documents\Cache"], task.Filters.ExcludeDirs);

        var account = Assert.Single(result.Accounts);
        Assert.Equal(@"EXAMPLE\backup-user", account.Credential.DisplayName);
        Assert.Equal(["Documents"], account.TaskNames);
        Assert.Equal(account.Credential.Id, imported.AccountId);
        Assert.Equal(account.Credential.Id, task.Destinations[0].CredentialId);
        Assert.Null(task.Destinations[1].CredentialId);
        Assert.Contains(imported.Warnings, w => w.Code == ImportWarningCodes.RunAsAccount);
    }

    [Fact]
    public void Weekly_differential_with_compression_is_converted_with_warnings()
    {
        var imported = CobianImporter.Convert(CobianListParser.Parse(List)).Tasks[1];
        var task = imported.Task;

        Assert.False(imported.EnabledInCobian);
        Assert.Equal(BackupTypes.Differential, task.BackupType);
        Assert.Equal([@"D:\Photos and Videos"], task.Sources.Select(s => s.Path));
        Assert.Equal(ScheduleTypes.Weekly, task.Schedule.Type);
        Assert.Equal([1, 5], task.Schedule.DaysOfWeek);
        Assert.Equal(ArchiveFormats.Zip, task.Archive.Format);
        Assert.Equal(RetentionModes.None, task.Retention.Mode);
        Assert.Contains("notify.cmd", task.Comment, StringComparison.Ordinal);
        var codes = imported.Warnings.Select(w => w.Code).ToList();
        Assert.Contains(ImportWarningCodes.BackupTypeAssumed, codes);
        Assert.Contains(ImportWarningCodes.WeekdaysAssumed, codes);
        Assert.Contains(ImportWarningCodes.CompressionLater, codes);
        Assert.Contains(ImportWarningCodes.EventsInComment, codes);
    }

    [Fact]
    public void Ftp_destinations_are_left_out_with_a_warning()
    {
        var imported = CobianImporter.Convert(CobianListParser.Parse(List)).Tasks[2];

        Assert.Equal(BackupTypes.Full, imported.Task.BackupType);
        Assert.Equal(ScheduleTypes.Interval, imported.Task.Schedule.Type);
        Assert.Equal(30, imported.Task.Schedule.IntervalMinutes);
        Assert.Empty(imported.Task.Destinations);
        var codes = imported.Warnings.Select(w => w.Code).ToList();
        Assert.Contains(ImportWarningCodes.FtpNotSupported, codes);
        Assert.Contains(ImportWarningCodes.NoDestinations, codes);
    }

    [Fact]
    public void Lists_are_read_in_any_of_the_encodings_cobian_writes()
    {
        const string text = "{11//\r\nName=Документы\r\n//11}\r\n";

        Assert.Equal(text, CobianListParser.Decode([.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(text)]));
        Assert.Equal(text, CobianListParser.Decode([.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text)]));
        Assert.Equal(text, CobianListParser.Decode(Encoding.UTF8.GetBytes(text)));
        Assert.Equal(text, CobianListParser.Decode(Encoding.Unicode.GetBytes(text)));
    }

    [Fact]
    public void Comma_text_is_split_like_delphi_writes_it()
    {
        Assert.Equal(["a b", "c", "d \"q\""], CobianListParser.SplitCommaText("\"a b\",c,\"d \"\"q\"\"\""));
        Assert.Equal(["a", "b"], CobianListParser.SplitCommaText("a b"));
        Assert.Empty(CobianListParser.SplitCommaText("  "));
    }

    [Fact]
    public void Date_time_bits_are_decoded_in_either_order()
    {
        string bits = Bits(EveningTime);

        Assert.Equal(EveningTime, CobianListParser.DecodeDateTimeBits(bits));
        Assert.Equal(EveningTime, CobianListParser.DecodeDateTimeBits(new string([.. bits.Reverse()])));
        Assert.Equal(new TimeSpan(21, 30, 0), CobianListParser.TimeOfDay(EveningTime));
        Assert.Null(CobianListParser.DecodeDateTimeBits("0101"));
    }

    private static string Bits(double value) => Convert.ToString(BitConverter.DoubleToInt64Bits(value), 2).PadLeft(64, '0');
}
