using System.Globalization;
using Custos.Core.Backup;
using Custos.Core.Model;
using Custos.Core.Text;

namespace Custos.Gui.Tests;

/// <summary>
/// The task editor: every page loads its part of the task and writes it back. A task opened and saved without changes
/// must come back exactly as it was, or opening the editor would change tasks behind the user's back.
/// </summary>
public sealed class EditorTests
{
    [Theory]
    [InlineData("en-US")]
    [InlineData("ru-RU")]
    public void A_task_comes_back_unchanged_after_every_page_was_shown(string culture) => Sta.Run(culture, () =>
    {
        var task = Samples.Office();

        Assert.Equal(Samples.Json(task), Samples.Json(OpenAndCollect(task)));
    });

    [Theory]
    [InlineData(ScheduleTypes.Daily)]
    [InlineData(ScheduleTypes.Monthly)]
    [InlineData(ScheduleTypes.Interval)]
    [InlineData(ScheduleTypes.Manual)]
    public void Every_kind_of_schedule_comes_back_unchanged(string type) => Sta.Run("ru-RU", () =>
    {
        var task = Samples.Office();
        task.Schedule = type switch
        {
            ScheduleTypes.Daily => new ScheduleOptions { Type = type, Times = ["02:00"], DaysOfWeek = [1, 2, 3, 4, 5] },
            ScheduleTypes.Monthly => new ScheduleOptions { Type = type, Times = ["23:00"], DaysOfMonth = [1, 15, -1], IntervalMinutes = 30 },
            ScheduleTypes.Interval => new ScheduleOptions { Type = type, IntervalMinutes = 90, DaysOfWeek = [6, 7], CatchUp = false },
            _ => new ScheduleOptions { Type = type },
        };

        Assert.Equal(Samples.Json(task), Samples.Json(OpenAndCollect(task)));
    });

    [Fact]
    public void Days_of_the_month_are_ticked_in_a_calendar_next_to_the_time() => Sta.Run("ru-RU", () =>
    {
        // The 31st is the last day of every month (a month without a chosen day runs on its last day), so it is shown
        // and saved as «last day of the month»; the 29th stays.
        var task = Samples.Office();
        task.Schedule = new ScheduleOptions { Type = ScheduleTypes.Monthly, Times = ["23:00"], DaysOfMonth = [29, 31] };
        task.ForceFull = new ForceFullOptions { MonthDay = 31 };
        using var monitor = Samples.Monitor();
        using var editor = new TaskEditorForm(monitor, task, isNew: false);
        editor.Show();
        Sta.Pump();
        var page = editor.Pages.OfType<SchedulePage>().Single();
        editor.SelectPage(page);
        Sta.Pump();

        var days = Assert.Single(Sta.Find<MonthDaysBox>(page));
        var time = Sta.Find<TextBox>(page).Single(t => t.Text == "23:00");
        Assert.Same(days.Parent, time.Parent);
        Assert.Equal([29, -1], days.Days);
        Assert.Equal("29, " + Strings.LastDayShort, days.Text);
        Assert.True(Sta.Shows(page, Strings.ScheduleMonthDays));

        days.OpenCalendar();
        var calendar = Assert.IsType<ToolStripDropDown>(days.Calendar);
        var boxes = Sta.Find<CheckBox>(calendar);
        Assert.Equal([.. Enumerable.Range(1, 30).Select(d => d.ToString(CultureInfo.CurrentCulture)), Strings.LastDayOfMonth], boxes.Select(b => b.Text));
        Assert.Equal(["29", Strings.LastDayOfMonth], boxes.Where(b => b.Checked).Select(b => b.Text));

        boxes.Single(b => b.Text == "1").Checked = true;
        boxes.Single(b => b.Text == Strings.LastDayOfMonth).Checked = false;
        Assert.Equal([1, 29], days.Days);
        Assert.Equal("1, 29", days.Text);
        Assert.True(Sta.ShowsPart(page, "Каждый месяц 1-го и 29-го в 23:00 (в феврале — в последний день)"));

        calendar.Close();
        Sta.Pump();
        Assert.Null(days.Calendar);

        // Nothing ticked: the page says what is missing.
        days.Days = [1];
        days.OpenCalendar();
        var again = Assert.IsType<ToolStripDropDown>(days.Calendar);
        Sta.Find<CheckBox>(again).Single(b => b.Text == "1").Checked = false;
        again.Close();
        Sta.Pump();
        Assert.Empty(days.Days);
        Assert.True(Sta.Shows(page, Strings.ErrorNoMonthDays));
        days.Days = [1, 29];

        var collected = editor.Collect(out string? problem);
        Assert.Null(problem);
        Assert.Equal([1, 29], collected!.Schedule.DaysOfMonth);
        Assert.Equal(-1, collected.ForceFull.MonthDay);
    });

    [Theory]
    [InlineData(ArchiveFormats.Zip, 0)]
    [InlineData(ArchiveFormats.SevenZip, 4095)]
    [InlineData(ArchiveFormats.SevenZip, 1234)]
    public void Archive_settings_come_back_unchanged(string format, int splitMb) => Sta.Run("ru-RU", () =>
    {
        var task = Samples.Office();
        task.Archive = new ArchiveOptions
        {
            Format = format,
            Level = 7,
            SplitMb = splitMb,
            Encrypt = true,
            EncryptNames = format == ArchiveFormats.SevenZip,
            PasswordCredentialId = Samples.ArchivePasswordId,
        };
        task.Verify = new VerifyOptions { EveryRuns = 3, StoredEveryDays = 7 };

        Assert.Equal(Samples.Json(task), Samples.Json(OpenAndCollect(task)));
    });

    [Theory]
    [InlineData(RetentionModes.Age)]
    [InlineData(RetentionModes.Gfs)]
    public void Retention_by_age_and_gfs_comes_back_unchanged(string mode) => Sta.Run("ru-RU", () =>
    {
        var task = Samples.Office();
        task.Retention = new RetentionOptions { Mode = mode, KeepFull = 5, MaxAgeDays = 45, Gfs = new GfsOptions { Daily = 10, Weekly = 0, Monthly = 6 } };

        Assert.Equal(Samples.Json(task), Samples.Json(OpenAndCollect(task)));
    });

    [Theory]
    [InlineData(MirrorModes.Exact)]
    [InlineData(MirrorModes.Safe)]
    public void A_mirror_comes_back_unchanged(string mode) => Sta.Run("ru-RU", () =>
    {
        var task = Samples.Office();
        task.BackupType = BackupTypes.Mirror;
        task.MirrorMode = mode;
        task.Retention.SafeMirrorDays = 14;

        Assert.Equal(Samples.Json(task), Samples.Json(OpenAndCollect(task)));
    });

    [Fact]
    public void Events_come_back_unchanged() => Sta.Run("ru-RU", () =>
    {
        var task = Samples.Office();
        task.Events = new TaskEvents
        {
            Pre =
            [
                new TaskEvent { Command = @"C:\Scripts\dump.cmd", Args = "-full \"D:\\Dumps\"", TimeoutS = 600 },
                new TaskEvent { Kind = TaskEventKinds.ServiceStop, Service = "MSSQLSERVER", TimeoutS = 90 },
            ],
            Post = [new TaskEvent { Kind = TaskEventKinds.ServiceStart, Service = "MSSQLSERVER" }],
            AbortOnPreFailure = false,
            After = AfterActions.Shutdown,
        };

        Assert.Equal(Samples.Json(task), Samples.Json(OpenAndCollect(task)));
    });

    [Fact]
    public void Destinations_on_servers_come_back_unchanged() => Sta.Run("ru-RU", () =>
    {
        var task = Samples.Office();
        task.Archive = new ArchiveOptions { Format = ArchiveFormats.Zip, Level = 5 };
        task.Destinations =
        [
            Samples.Remote(DestinationKinds.Sftp), Samples.Remote(DestinationKinds.Ftp), Samples.Remote(DestinationKinds.Ftps),
            Samples.Remote(DestinationKinds.WebDav), Samples.Remote(DestinationKinds.S3),
        ];

        // The S3 sample has Object Lock for 30 days: the retention must not delete sooner.
        task.Retention = new RetentionOptions { Mode = RetentionModes.Age, MaxAgeDays = 30 };

        Assert.Equal(Samples.Json(task), Samples.Json(OpenAndCollect(task)));
    });

    [Theory]
    [InlineData(DestinationKinds.Ftp)]
    [InlineData(DestinationKinds.WebDav)]
    [InlineData(DestinationKinds.S3)]
    public void Seven_zip_is_not_accepted_with_a_server_that_takes_files_from_start_to_end_only(string kind) => Sta.Run("en-US", () =>
    {
        var task = Samples.Office();
        task.Archive = new ArchiveOptions { Format = ArchiveFormats.SevenZip, Level = 5 };
        task.Destinations.Add(Samples.Remote(kind));

        // The S3 sample has Object Lock for 30 days: the retention must not delete sooner.
        task.Retention = new RetentionOptions { Mode = RetentionModes.Age, MaxAgeDays = 30 };
        using var monitor = Samples.Monitor();
        using var editor = new TaskEditorForm(monitor, task, isNew: false);
        editor.Show();
        Sta.Pump();

        string why = Strings.Format(Strings.ArchiveSevenZipNeedsSeek, TaskDescriber.Destination(Samples.Remote(kind)));
        Assert.Null(editor.Collect(out string? problem));
        Assert.Equal(why, problem);

        // The page says why, and once another format is chosen 7z is no longer offered.
        editor.SelectPage(editor.Pages[5]);
        Sta.Pump();
        Assert.True(Sta.Shows(editor.Pages[5], why));
        Sta.Find<RadioButton>(editor.Pages[5]).Single(r => r.Text == Strings.ArchiveZip).Checked = true;
        Sta.Pump();

        Assert.False(Sta.Find<RadioButton>(editor.Pages[5]).Single(r => r.Text == Strings.ArchiveSevenZip).Enabled);
        Assert.Equal(ArchiveFormats.Zip, editor.Collect(out problem)!.Archive.Format);
        Assert.Null(problem);
    });

    [Fact]
    public void Seven_zip_goes_to_sftp() => Sta.Run("en-US", () =>
    {
        var task = Samples.Office();
        task.Archive = new ArchiveOptions { Format = ArchiveFormats.SevenZip, Level = 5 };
        task.Destinations.Add(Samples.Remote(DestinationKinds.Sftp));

        Assert.Equal(Samples.Json(task), Samples.Json(OpenAndCollect(task)));
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_protection_from_deletion_is_not_accepted_where_files_are_replaced(bool worm) => Sta.Run("en-US", () =>
    {
        // A mirror writes over the files of the last run.
        var task = Samples.Office();
        var destination = worm ? Samples.Office().Destinations[1] : Samples.Remote(DestinationKinds.S3);
        if (worm)
        {
            destination.WormDays = 7;
            destination.WormLockHours = 3;
        }

        task.Destinations = [destination];
        task.BackupType = BackupTypes.Mirror;
        task.MirrorMode = MirrorModes.Exact;

        using var monitor = Samples.Monitor();
        using var editor = new TaskEditorForm(monitor, task, isNew: false);
        editor.Show();
        Sta.Pump();
        editor.SelectPage(editor.Pages[3]);
        Sta.Pump();

        Assert.Null(editor.Collect(out string? problem));
        Assert.Equal(worm
            ? Custos.Core.Messages.Format(Custos.Core.Messages.WormNeedsSets, @"\\nas\backup\office", 7)
            : Custos.Core.Messages.Format(Custos.Core.Messages.ObjectLockNeedsSets, "s3://office-backup/pc01"), problem);

        // The first page is shown, where the type is changed; the mirror stays on there, with what is wrong with it.
        var basic = editor.Pages[0];
        Assert.True(basic.Visible);
        var mirror = Sta.Find<RadioButton>(basic).Single(r => r.Text == Strings.TypeMirrorTitle);
        Assert.True(mirror.Enabled);
        Assert.True(Sta.Shows(basic, Strings.Format(Strings.MirrorProtectedNote, TaskDescriber.Destination(destination))));

        // Once another type is chosen, the mirror cannot be chosen again.
        Sta.Find<RadioButton>(basic).Single(r => r.Text == Strings.TypeFullTitle).Checked = true;
        Sta.Pump();
        Assert.False(mirror.Enabled);
    });

    [Fact]
    public void A_mirror_is_not_offered_once_a_destination_has_worm() => Sta.Run("ru-RU", () =>
    {
        var task = Samples.Office();
        using var monitor = Samples.Monitor();
        using var editor = new TaskEditorForm(monitor, task, isNew: false);
        editor.Show();
        Sta.Pump();
        var basic = editor.Pages[0];
        var mirror = Sta.Find<RadioButton>(basic).Single(r => r.Text == Strings.TypeMirrorTitle);
        Assert.True(mirror.Enabled);

        // WORM is set on a destination, as the destination dialog does.
        var withWorm = Samples.Office();
        withWorm.Destinations[1].WormDays = 7;
        withWorm.Destinations[1].WormLockHours = 3;
        editor.Pages[3].LoadFrom(withWorm);
        Sta.Pump();

        Assert.False(mirror.Enabled);
        Assert.True(Sta.Shows(basic, Strings.Format(Strings.MirrorProtectedNote, TaskDescriber.Destination(withWorm.Destinations[1]))));
    });

    [Theory]
    [InlineData(true, ArchiveFormats.None)]
    [InlineData(true, ArchiveFormats.Zip)]
    [InlineData(false, ArchiveFormats.None)]
    [InlineData(false, ArchiveFormats.Zip)]
    public void A_destination_that_protects_its_files_keeps_a_folder_for_each_backup(bool worm, string archive) => Sta.Run("ru-RU", () =>
    {
        // Plain copies in one shared folder would write over the files of the last run. With archives the box is
        // kept on too, so that it does not change with the format.
        var task = Samples.Office();
        var destination = worm ? Samples.Office().Destinations[1] : Samples.Remote(DestinationKinds.S3);
        destination.WormDays = worm ? 7 : 0;
        destination.WormLockHours = worm ? 3 : 0;
        task.Destinations = [destination];
        task.BackupType = BackupTypes.Full;
        task.Layout.SeparateFolders = false;
        task.Archive = new ArchiveOptions { Format = archive, Level = 5 };

        // Two backups on three days a week: 26 full ones cover the 30 days of the S3 Object Lock (and the WORM).
        task.Retention.KeepFull = 26;

        using var monitor = Samples.Monitor();
        using var editor = new TaskEditorForm(monitor, task, isNew: false);
        editor.Show();
        Sta.Pump();
        var page = Assert.IsType<DestinationsPage>(editor.Pages[3]);
        editor.SelectPage(page);
        Sta.Pump();

        var separate = Sta.Find<CheckBox>(page).Single(c => c.Text == Strings.SeparateFolders);
        Assert.True(separate.Checked);
        Assert.False(separate.Enabled);
        Assert.True(Sta.Shows(page, Strings.Format(Strings.SeparateForcedNote, TaskDescriber.Destination(destination))));
        var collected = editor.Collect(out string? problem);
        Assert.Null(problem);
        Assert.True(collected!.Layout.SeparateFolders);

        // Retention tells that WORM keeps a backup for its days whatever the rule.
        var retention = editor.Pages.OfType<RetentionPage>().Single();
        editor.SelectPage(retention);
        Sta.Pump();
        Assert.Equal(worm, Sta.Shows(retention, Strings.Format(Strings.RetentionWormNote, 7)));
    });

    [Fact]
    public void Retention_that_would_delete_backups_before_their_worm_is_over_is_not_saved() => Sta.Run("ru-RU", () =>
    {
        var task = Samples.Office();
        task.BackupType = BackupTypes.Full;
        task.Destinations[1].WormDays = 7;
        task.Destinations[1].WormLockHours = 3;
        task.Retention.KeepFull = 3;

        using var monitor = Samples.Monitor();
        using var editor = new TaskEditorForm(monitor, task, isNew: false);
        editor.Show();
        Sta.Pump();

        Assert.Null(editor.Collect(out string? problem));
        Assert.Equal(WormRetention.Problem(task), problem);
        Assert.StartsWith(TaskDescriber.Destination(task.Destinations[1]), problem, StringComparison.Ordinal);
        Assert.True(editor.Pages.OfType<RetentionPage>().Single().Visible);

        // Enough full backups: saved.
        var retention = editor.Pages.OfType<RetentionPage>().Single();
        Sta.Find<NumericUpDown>(retention).Single(n => n.Value == 3).Value = 8;
        Assert.NotNull(editor.Collect(out problem));
        Assert.Null(problem);
    });

    [Fact]
    public void Retention_is_off_for_a_shared_folder_where_it_has_nothing_to_delete() => Sta.Run("ru-RU", () =>
    {
        var task = Samples.Office();
        task.BackupType = BackupTypes.Incremental;
        task.Layout.SeparateFolders = false;
        task.Retention = new RetentionOptions { Mode = RetentionModes.Age, MaxAgeDays = 5 };
        using var monitor = Samples.Monitor();
        using var editor = new TaskEditorForm(monitor, task, isNew: false);
        editor.Show();
        Sta.Pump();
        var retention = editor.Pages.OfType<RetentionPage>().Single();
        editor.SelectPage(retention);
        Sta.Pump();

        string note = Strings.Format(Strings.RetentionSharedNote, Strings.SeparateFolders, Strings.PageDestinations, Strings.PageArchive);
        var byAge = Sta.Find<RadioButton>(retention).Single(r => r.Text == Strings.RetentionAge);
        Assert.True(Sta.Shows(retention, note));
        Assert.True(byAge.Checked);
        Assert.False(byAge.Enabled);
        Assert.All(Sta.Find<NumericUpDown>(retention), n => Assert.False(n.Enabled));

        // The rule is kept as it was, and comes back with a folder for each backup.
        Assert.Equal(5, editor.Collect(out _)!.Retention.MaxAgeDays);
        var destinations = editor.Pages[3];
        Sta.Find<CheckBox>(destinations).Single(c => c.Text == Strings.SeparateFolders).Checked = true;
        Sta.Pump();
        Assert.True(byAge.Enabled);
        Assert.False(Sta.Shows(retention, note));
    });

    [Fact]
    public void A_mirror_keeps_plain_files() => Sta.Run("en-US", () =>
    {
        var task = Samples.Office();
        task.Archive = new ArchiveOptions { Format = ArchiveFormats.Zip, Level = 7 };
        using var monitor = Samples.Monitor();
        using var editor = new TaskEditorForm(monitor, task, isNew: false);
        editor.Show();
        Sta.Pump();

        Sta.Find<RadioButton>(editor.Pages[0]).Single(r => r.Text == Strings.TypeMirrorTitle).Checked = true;
        Sta.Pump();
        var collected = editor.Collect(out string? problem);

        Assert.Null(problem);
        Assert.Equal(BackupTypes.Mirror, collected!.BackupType);
        Assert.Equal(MirrorModes.Exact, collected.MirrorMode);
        Assert.Equal(ArchiveFormats.None, collected.Archive.Format);
        Assert.False(Sta.Find<RadioButton>(editor.Pages[5]).Single(r => r.Text == Strings.ArchiveZip).Enabled);
    });

    [Fact]
    public void Retention_warns_when_nothing_forces_a_full_backup() => Sta.Run("en-US", () =>
    {
        // The office task is differential with forced full backups; here nothing forces one.
        var task = Samples.Office();
        task.BackupType = BackupTypes.Incremental;
        task.ForceFull = new ForceFullOptions();
        using var monitor = Samples.Monitor();
        using var editor = new TaskEditorForm(monitor, task, isNew: false);
        editor.Show();
        Sta.Pump();
        var retention = editor.Pages.OfType<RetentionPage>().Single();
        editor.SelectPage(retention);
        Sta.Pump();

        // Incremental, «by count», no forced full backup: nothing could ever be deleted.
        Assert.True(Sta.Shows(retention, Strings.RetentionNoFullWarning));

        // Grandfather-father-son makes its own full backups.
        Sta.Find<RadioButton>(retention).Single(r => r.Text == Strings.RetentionGfs).Checked = true;
        Sta.Pump();
        Assert.False(Sta.Shows(retention, Strings.RetentionNoFullWarning));

        // «By count» again, now with a full backup every 7th run.
        Sta.Find<RadioButton>(retention).Single(r => r.Text == Strings.RetentionCount).Checked = true;
        Sta.Pump();
        Assert.True(Sta.Shows(retention, Strings.RetentionNoFullWarning));
        Sta.Find<CheckBox>(editor.Pages[0]).Single(c => c.Text == Strings.ForceEvery).Checked = true;
        Sta.Pump();
        Assert.False(Sta.Shows(retention, Strings.RetentionNoFullWarning));
    });

    [Fact]
    public void The_folder_name_is_picked_from_a_list_and_shows_an_example() => Sta.Run("en-US", () =>
    {
        using var monitor = Samples.Monitor();
        using var editor = new TaskEditorForm(monitor, Samples.Office(), isNew: false);
        editor.Show();
        Sta.Pump();
        var page = Assert.IsType<DestinationsPage>(editor.Pages[3]);
        editor.SelectPage(page);
        Sta.Pump();

        var template = Sta.Find<ComboBox>(page).Single(c => c.DropDownStyle == ComboBoxStyle.DropDown);
        Assert.Equal(DestinationsPage.TemplatePresets, template.Items.Cast<string>());
        Assert.Equal(LayoutOptions.DefaultNameTemplate, template.Text);
        template.Text = "{task} {yyyy}";
        Sta.Pump();

        string year = DateTime.Now.Year.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Assert.Contains(Sta.Find<Label>(page), l => l.Text == Strings.Format(Strings.NameTemplateExample, "Office documents " + year));
        Assert.Equal("{task} {yyyy}", editor.Collect(out _)!.Layout.NameTemplate);
    });

    [Fact]
    public void Encryption_needs_a_password() => Sta.Run("en-US", () =>
    {
        var task = Samples.Office();
        task.Archive = new ArchiveOptions { Format = ArchiveFormats.Zip, Encrypt = true };
        using var monitor = Samples.Monitor();
        using var editor = new TaskEditorForm(monitor, task, isNew: false);
        editor.Show();
        Sta.Pump();

        Assert.Null(editor.Collect(out string? problem));
        Assert.Equal(Strings.ArchivePasswordRequired, problem);
    });

    [Fact]
    public void A_disabled_task_needs_neither_sources_nor_destinations() => Sta.Run("en-US", () =>
    {
        var task = Samples.Archive();
        task.Sources.Clear();
        task.Destinations.Clear();

        Assert.Equal(Samples.Json(task), Samples.Json(OpenAndCollect(task)));
    });

    [Fact]
    public void A_new_task_is_not_accepted_without_sources() => Sta.Run("en-US", () =>
    {
        using var monitor = Samples.Monitor();
        using var editor = new TaskEditorForm(monitor, TaskEditorForm.NewTask(monitor.Snapshot), isNew: true);
        editor.Show();
        Sta.Pump();

        Assert.Null(editor.Collect(out string? problem));
        Assert.Equal(Strings.ErrorNoSources, problem);
        Assert.True(editor.Pages[1].Visible);

        // A new task skips log files along with the temporary ones.
        Assert.Contains(Sta.Find<TextBox>(editor.Pages[1]), b => b.Text == "~$*; *.tmp; *.log");
    });

    [Fact]
    public void A_network_destination_needs_a_network_path() => Sta.Run("en-US", () =>
    {
        var task = Samples.Office();
        task.Destinations[1].Path = @"D:\Backup\office";
        using var monitor = Samples.Monitor();
        using var editor = new TaskEditorForm(monitor, task, isNew: false);
        editor.Show();
        Sta.Pump();

        Assert.Null(editor.Collect(out string? problem));
        Assert.Equal(Strings.Format(Strings.ErrorNotUnc, @"D:\Backup\office"), problem);
        Assert.True(editor.Pages[3].Visible);
    });

    [Fact]
    public void A_drive_letter_in_the_folder_of_a_usb_disk_is_dropped() => Sta.Run("en-US", () =>
    {
        var task = Samples.Office();
        task.Destinations[2].Path = @"E:\Copies\";

        var collected = OpenAndCollect(task);

        Assert.Equal("Copies", collected.Destinations[2].Path);
    });

    [Fact]
    public void The_filters_are_on_the_sources_page_and_a_picked_folder_is_added_once() => Sta.Run("ru-RU", () =>
    {
        using var monitor = Samples.Monitor();
        using var editor = new TaskEditorForm(monitor, Samples.Office(), isNew: false);
        editor.Show();
        Sta.Pump();

        // No page of its own any more.
        Assert.Equal(9, editor.Pages.Count);
        Assert.DoesNotContain(editor.Pages, p => p.Title == Strings.FiltersHeading);

        var sources = Assert.IsType<SourcesPage>(editor.Pages[1]);
        editor.SelectPage(sources);
        Sta.Pump();
        var folders = Sta.Find<TextBox>(sources).Single(b => b.Multiline);
        Assert.Equal(["Cache", @"C:\Data\Office\Old"], folders.Lines);

        // "Browse…" adds the picked folder as a line of its own, once.
        sources.AddExcludedFolder(@"C:\Data\Office\Archive");
        sources.AddExcludedFolder(@"c:\data\office\archive");
        Sta.Pump();

        var task = editor.Collect(out string? problem);
        Assert.Null(problem);
        Assert.Equal(["Cache", @"C:\Data\Office\Old", @"C:\Data\Office\Archive"], task!.Filters.ExcludeDirs);
        Assert.Equal(["*.docx", "*.xlsx"], task.Filters.Include);
    });

    [Fact]
    public void A_task_of_databases_only_needs_no_sources_and_keeps_its_servers() => Sta.Run("ru-RU", () =>
    {
        var task = Samples.Office();
        task.Sources.Clear();
        task.DbServers =
        [
            new TaskDatabaseServer { Engine = DbEngines.MySql, BinDir = @"C:\OSPanel\modules\MySQL-8.0\bin", Host = "127.127.126.26", Port = 3306, CredentialId = "db-account", Exclude = ["test"] },
            new TaskDatabaseServer { Engine = DbEngines.PostgreSql, BinDir = @"C:\Program Files\PostgreSQL\17\bin", AllDatabases = false, Include = ["crm"] },
        ];

        var collected = OpenAndCollect(task);

        Assert.Equal(Samples.Json(task), Samples.Json(collected));
        using var monitor = Samples.Monitor();
        using var editor = new TaskEditorForm(monitor, task.Clone(), isNew: false);
        editor.Show();
        Sta.Pump();
        var page = Assert.IsType<DatabasesPage>(editor.Pages[2]);
        editor.SelectPage(page);
        Sta.Pump();
        var list = Sta.Find<ListView>(page).Single();
        Assert.Equal("MySQL 127.127.126.26:3306", list.Items[0].Text);
        Assert.Equal(Strings.Format(Strings.DbAllExcept, "test"), list.Items[0].SubItems[1].Text);
        Assert.Equal(Strings.Format(Strings.DbOnly, "crm"), list.Items[1].SubItems[1].Text);
    });

    [Theory]
    [InlineData("en-US")]
    [InlineData("ru-RU")]
    public void A_linux_server_comes_back_unchanged_and_stays_the_only_source(string culture) => Sta.Run(culture, () =>
    {
        var task = Samples.Server();

        Assert.Equal(Samples.Json(task), Samples.Json(OpenAndCollect(task)));
        using var monitor = Samples.Monitor();
        using var editor = new TaskEditorForm(monitor, task.Clone(), isNew: false);
        editor.Show();
        Sta.Pump();
        var sources = Assert.IsType<SourcesPage>(editor.Pages[1]);
        editor.SelectPage(sources);
        Sta.Pump();
        var row = Sta.Find<ListView>(sources).Single().Items[0];
        Assert.Equal(("custos@vps.example.org:22", "custos@vps.example.org"), (row.Text, row.SubItems[2].Text));
        Assert.False(Sta.Find<Button>(sources).Single(b => b.Text == Strings.ButtonAdd).Enabled);
        Assert.False(Sta.Find<Button>(sources).Single(b => b.Text == Strings.SourceAddLinux).Enabled);
        Assert.True(Sta.Find<Label>(sources).Single(l => l.Text == Strings.SourcesRemoteNote).Visible);
        Assert.False(Sta.Find<CheckBox>(sources).Single(c => c.Text == Strings.SkipHidden).Enabled);

        var databases = Assert.IsType<DatabasesPage>(editor.Pages[2]);
        editor.SelectPage(databases);
        Sta.Pump();
        Assert.False(Sta.Find<Button>(databases).Single(b => b.Text == Strings.ButtonAdd).Enabled);
        Assert.True(Sta.Find<Label>(databases).Single(l => l.Text == Strings.DatabasesRemoteNote).Visible);
    });

    [Fact]
    public void A_linux_server_takes_no_mirror_and_none_of_the_windows_reading_options() => Sta.Run("ru-RU", () =>
    {
        var task = Samples.Server();
        task.Layout.SeparateFolders = false;
        using var monitor = Samples.Monitor();
        using var editor = new TaskEditorForm(monitor, task, isNew: false);
        editor.Show();
        Sta.Pump();

        var basic = editor.Pages[0];
        Assert.False(Sta.Find<RadioButton>(basic).Single(r => r.Text == Strings.TypeMirrorTitle).Enabled);
        Assert.True(Sta.Shows(basic, Strings.MirrorRemoteNote));

        // Its backups always go into a folder each, whatever the box said.
        var destinations = editor.Pages[3];
        editor.SelectPage(destinations);
        Sta.Pump();
        var separate = Sta.Find<CheckBox>(destinations).Single(c => c.Text == Strings.SeparateFolders);
        Assert.True(separate.Checked);
        Assert.False(separate.Enabled);
        Assert.True(Sta.Shows(destinations, Strings.SeparateRemoteNote));

        var advanced = editor.Pages.OfType<AdvancedPage>().Single();
        editor.SelectPage(advanced);
        Sta.Pump();
        Assert.False(Sta.Find<CheckBox>(advanced).Single(c => c.Text == Strings.VssEnabled).Enabled);
        Assert.False(Sta.Find<CheckBox>(advanced).Single(c => c.Text == Strings.PreserveAcl).Enabled);
        Assert.False(Assert.Single(Sta.Find<NumericUpDown>(advanced), n => n.Maximum == 5).Enabled);
        Assert.True(Sta.Shows(advanced, Strings.AdvancedVssRemote));
        Assert.True(Sta.Shows(advanced, Strings.AdvancedRetriesRemote));
        Assert.True(Sta.Shows(advanced, Strings.AdvancedAclRemote));

        // The values stay as they were, for when the source changes.
        var collected = editor.Collect(out string? problem);
        Assert.Null(problem);
        Assert.Equal((task.Vss.Enabled, task.Copy.ReadRetries, task.Copy.PreserveAcl), (collected!.Vss.Enabled, collected.Copy.ReadRetries, collected.Copy.PreserveAcl));
    });

    [Fact]
    public void Options_that_do_nothing_for_the_task_are_off_with_the_reason() => Sta.Run("en-US", () =>
    {
        // Only network folders to read, only servers and cloud drives to write to, no events before.
        var task = Samples.Office();
        task.Sources = [Samples.Office().Sources[2]];
        task.Destinations = [Samples.Remote(DestinationKinds.Sftp), Samples.Cloud(DestinationKinds.GDrive)];
        task.Events = new TaskEvents();
        task.Copy.PreserveAcl = true;
        using var monitor = Samples.Monitor();
        using var editor = new TaskEditorForm(monitor, task, isNew: false);
        editor.Show();
        Sta.Pump();

        var basic = editor.Pages[0];
        Assert.False(Sta.Find<RadioButton>(basic).Single(r => r.Text == Strings.TypeMirrorTitle).Enabled);
        Assert.True(Sta.Shows(basic, Strings.Format(Strings.MirrorCloudNote, TaskDescriber.Destination(task.Destinations[1]))));

        var advanced = editor.Pages.OfType<AdvancedPage>().Single();
        editor.SelectPage(advanced);
        Sta.Pump();
        var vss = Sta.Find<CheckBox>(advanced).Single(c => c.Text == Strings.VssEnabled);
        var acl = Sta.Find<CheckBox>(advanced).Single(c => c.Text == Strings.PreserveAcl);
        Assert.False(vss.Enabled);
        Assert.True(Sta.Shows(advanced, Strings.AdvancedVssNoLocal));
        Assert.False(acl.Enabled);
        Assert.True(Sta.Shows(advanced, Strings.AdvancedAclNoNtfs));
        Assert.True(Assert.Single(Sta.Find<NumericUpDown>(advanced), n => n.Maximum == 5).Enabled);

        var events = editor.Pages.OfType<EventsPage>().Single();
        editor.SelectPage(events);
        Sta.Pump();
        Assert.False(Sta.Find<CheckBox>(events).Single(c => c.Text == Strings.EventsAbortOnPre).Enabled);

        // A folder on a local disk and a USB disk: both work again. An archive keeps no NTFS permissions.
        var local = Samples.Office();
        local.Archive = new ArchiveOptions { Format = ArchiveFormats.Zip, Level = 5 };
        using var other = new TaskEditorForm(monitor, local, isNew: false);
        other.Show();
        Sta.Pump();
        var page = other.Pages.OfType<AdvancedPage>().Single();
        other.SelectPage(page);
        Sta.Pump();
        Assert.True(Sta.Find<CheckBox>(page).Single(c => c.Text == Strings.VssEnabled).Enabled);
        Assert.False(Sta.Find<CheckBox>(page).Single(c => c.Text == Strings.PreserveAcl).Enabled);
        Assert.True(Sta.Shows(page, Strings.AdvancedAclArchive));
        Assert.True(Sta.Find<RadioButton>(other.Pages[0]).Single(r => r.Text == Strings.TypeMirrorTitle).Enabled);
    });

    [Fact]
    public void A_task_with_folders_cannot_take_a_linux_server_as_well() => Sta.Run("en-US", () =>
    {
        using var monitor = Samples.Monitor();
        using var editor = new TaskEditorForm(monitor, Samples.Office(), isNew: false);
        editor.Show();
        Sta.Pump();
        var sources = editor.Pages[1];
        editor.SelectPage(sources);
        Sta.Pump();

        Assert.True(Sta.Find<Button>(sources).Single(b => b.Text == Strings.ButtonAdd).Enabled);
        Assert.False(Sta.Find<Button>(sources).Single(b => b.Text == Strings.SourceAddLinux).Enabled);
        Assert.False(Sta.Find<Label>(sources).Single(l => l.Text == Strings.SourcesRemoteNote).Visible);
    });

    /// <summary>Opens the editor, shows every page as a user clicking through them would, and collects the task.</summary>
    private static TaskDefinition OpenAndCollect(TaskDefinition task)
    {
        using var monitor = Samples.Monitor();
        using var editor = new TaskEditorForm(monitor, task.Clone(), isNew: false);
        editor.Show();
        Sta.Pump();
        foreach (var page in editor.Pages)
        {
            editor.SelectPage(page);
            Sta.Pump();
            Assert.True(page.Visible, page.Title);
        }

        var collected = editor.Collect(out string? problem);
        Assert.Null(problem);
        return collected!;
    }
}
