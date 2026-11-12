using System.Text;
using Custos.Core.Cobian;
using Custos.Core.Model;

namespace Custos.Gui.Tests;

/// <summary>The small dialogs and the import from Cobian, opened with made-up data. Nothing here is sent to a service.</summary>
public sealed class DialogTests
{
    [Theory]
    [InlineData("en-US")]
    [InlineData("ru-RU")]
    public void The_source_dialog_gives_back_a_network_folder_with_its_account(string culture) => Sta.Run(culture, () =>
    {
        using var monitor = Samples.Monitor();
        using var toolTip = new ToolTip();
        var source = Samples.Office().Sources[2];
        using var dialog = new SourceDialog(new EditorContext(monitor, toolTip), source);
        dialog.Show();
        Sta.Pump();

        Assert.True(Sta.Find<CredentialPicker>(dialog).Single().Visible);
        ((Button)dialog.AcceptButton!).PerformClick();
        Sta.Pump();

        Assert.Equal(DialogResult.OK, dialog.DialogResult);
        Assert.Equal(
            (source.Id, source.Kind, source.Path, source.Recursive, source.CredentialId),
            (dialog.Source.Id, dialog.Source.Kind, dialog.Source.Path, dialog.Source.Recursive, dialog.Source.CredentialId));
    });

    [Fact]
    public void The_source_dialog_of_a_new_source_starts_empty() => Sta.Run("en-US", () =>
    {
        using var monitor = Samples.Monitor();
        using var toolTip = new ToolTip();
        using var dialog = new SourceDialog(new EditorContext(monitor, toolTip), new TaskSource());
        dialog.Show();
        Sta.Pump();

        Assert.Equal(Strings.SourceAddTitle, dialog.Text);
        Assert.False(Sta.Find<CredentialPicker>(dialog).Single().Visible);
    });

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void The_destination_dialog_gives_back_the_destination(int index) => Sta.Run("ru-RU", () =>
    {
        using var monitor = Samples.Monitor();
        using var toolTip = new ToolTip();
        var destination = Samples.Office().Destinations[index];
        using var dialog = new DestinationDialog(new EditorContext(monitor, toolTip), destination, isNew: false);
        dialog.Show();
        Sta.Pump();

        Assert.Equal(Strings.DestinationEditTitle, dialog.Text);
        Assert.Equal(destination.Kind == DestinationKinds.Unc, Sta.Find<CredentialPicker>(dialog).Single().Visible);
        ((Button)dialog.AcceptButton!).PerformClick();
        Sta.Pump();

        Assert.Equal(DialogResult.OK, dialog.DialogResult);
        Assert.Equal(
            (destination.Id, destination.Kind, destination.Path, destination.UsbLabel, destination.CredentialId, destination.DisconnectAfter),
            (dialog.Destination.Id, dialog.Destination.Kind, dialog.Destination.Path, dialog.Destination.UsbLabel, dialog.Destination.CredentialId, dialog.Destination.DisconnectAfter));
    });

    [Theory]
    [InlineData(TaskEventKinds.Run)]
    [InlineData(TaskEventKinds.ServiceStop)]
    public void The_event_dialog_gives_back_the_event(string kind) => Sta.Run("ru-RU", () =>
    {
        using var monitor = Samples.Monitor();
        using var toolTip = new ToolTip();
        var item = kind == TaskEventKinds.Run
            ? new TaskEvent { Command = @"C:\Scripts\dump.cmd", Args = "-full", TimeoutS = 90 }
            : new TaskEvent { Kind = kind, Service = "Spooler", TimeoutS = 120 };
        using var dialog = new EventDialog(new EditorContext(monitor, toolTip), item, isNew: false);
        dialog.Show();
        Sta.Pump();

        ((Button)dialog.AcceptButton!).PerformClick();
        Sta.Pump();

        Assert.Equal(DialogResult.OK, dialog.DialogResult);
        Assert.Equal((item.Kind, item.Command, item.Args, item.Service, item.TimeoutS),
            (dialog.Event.Kind, dialog.Event.Command, dialog.Event.Args, dialog.Event.Service, dialog.Event.TimeoutS));
    });

    [Fact]
    public void A_destination_needs_its_folder_or_label() => Sta.Run("en-US", () =>
    {
        Assert.Equal(Strings.ErrorDestinationFolderRequired, DestinationDialog.Check(new TaskDestination()));
        Assert.Equal(Strings.ErrorUsbLabelRequired, DestinationDialog.Check(new TaskDestination { Kind = DestinationKinds.Usb, Path = "Copies" }));
        Assert.Equal(Strings.Format(Strings.ErrorNotUnc, @"D:\Backup"), DestinationDialog.Check(new TaskDestination { Kind = DestinationKinds.Unc, Path = @"D:\Backup" }));
        Assert.Null(DestinationDialog.Check(new TaskDestination { Kind = DestinationKinds.Local, Path = @"D:\Backup" }));
    });

    [Theory]
    [InlineData("en-US")]
    [InlineData("ru-RU")]
    public void The_account_dialog_shows_the_account_but_never_a_password(string culture) => Sta.Run(culture, () =>
    {
        using var monitor = Samples.Monitor();
        using var dialog = new CredentialDialog(monitor, Samples.Account());
        dialog.Show();
        Sta.Pump();

        var boxes = Sta.Find<TextBox>(dialog);
        Assert.Contains(boxes, b => b.Text == "backup-user");
        Assert.Contains(boxes, b => b.Text == "EXAMPLE");
        Assert.Contains(boxes, b => b.UseSystemPasswordChar && b.Text.Length == 0);
    });

    [Theory]
    [InlineData("en-US")]
    [InlineData("ru-RU")]
    public void The_settings_are_read_only_without_administrator_rights(string culture) => Sta.Run(culture, () =>
    {
        using var monitor = Samples.Monitor();
        using var settings = new SettingsForm(monitor);
        settings.Show();
        Sta.Pump();

        // Not connected, so not an administrator either: everything can be looked at, nothing changed.
        Assert.False(((Button)settings.AcceptButton!).Visible);
        Assert.Equal(Strings.ButtonClose, ((Button)settings.CancelButton!).Text);
        var accounts = Assert.Single(Sta.Find<ListView>(settings));
        var row = Assert.Single(accounts.Items.Cast<ListViewItem>());
        Assert.Equal(Samples.Account().DisplayName, row.Text);
        Assert.Equal("Office documents", row.SubItems[1].Text);
        var sevenZip = Assert.Single(Sta.Find<Button>(settings), b => b.Text == Strings.SevenZipCheckButton);
        Assert.False(sevenZip.Enabled);
    });

    [Theory]
    [InlineData("en-US")]
    [InlineData("ru-RU")]
    public void The_settings_show_the_monitoring_server_but_only_administrators_connect(string culture) => Sta.Run(culture, () =>
    {
        using var monitor = Samples.Monitor();
        using var settings = new SettingsForm(monitor);
        settings.Show();
        Sta.Pump();

        Assert.True(Sta.Shows(settings, Strings.SettingsServer));
        var connect = Assert.Single(Sta.Find<Button>(settings), b => b.Text == Strings.ServerConnectButton);
        Assert.False(connect.Enabled);
        Assert.Contains(Sta.Find<CheckBox>(settings), c => c.Text == Strings.AllowRemoteEdit);
    });

    [Theory]
    [InlineData("en-US")]
    [InlineData("ru-RU")]
    public void The_history_shows_whether_the_backup_was_verified(string culture) => Sta.Run(culture, () =>
    {
        using var view = new HistoryView();
        var start = new DateTimeOffset(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);
        RunRecord Run(string id, string status, RunVerify? verify) => new()
        {
            RunId = id, TaskId = Samples.OfficeId, TaskName = "Office documents", BackupType = BackupTypes.Full,
            Trigger = RunTriggers.Manual, StartedAt = start, FinishedAt = start.AddMinutes(1), Status = status, Verify = verify,
        };

        view.ShowRuns(
        [
            Run("a", RunStatuses.Success, new RunVerify { Ok = true, Scope = "new", SetsChecked = 1 }),
            Run("b", RunStatuses.Success, new RunVerify { Ok = true, Scope = "stored", SetsChecked = 3 }),
            Run("c", RunStatuses.Error, new RunVerify { Ok = false, Scope = "stored", SetsChecked = 3, FailedSets = ["Set 2"] }),
            Run("d", RunStatuses.Success, null),
        ]);

        var cells = view.List.Items.Cast<ListViewItem>().Select(i => i.SubItems[7]).ToList();
        Assert.Equal(Strings.ColumnVerify, view.List.Columns[7].Text);
        Assert.Equal(Strings.HistoryVerifyOkNew, cells[0].Text);
        Assert.Equal(Glyphs.Green, cells[0].ForeColor);
        Assert.Equal(Strings.Format(Strings.HistoryVerifyOkStored, 3), cells[1].Text);
        Assert.Contains("Set 2", cells[2].Text, StringComparison.Ordinal);
        Assert.Equal(Glyphs.ErrorText, cells[2].ForeColor);
        Assert.Equal("—", cells[3].Text);
    });

    [Theory]
    [InlineData("en-US")]
    [InlineData("ru-RU")]
    public void The_import_lists_the_cobian_tasks_and_asks_for_the_password_of_their_account(string culture) => Sta.Run(culture, () =>
    {
        string list = Path.Combine(Path.GetTempPath(), "custos-gui-tests", Guid.NewGuid().ToString("N") + ".lst");
        Directory.CreateDirectory(Path.GetDirectoryName(list)!);
        File.WriteAllText(list, CobianList, Encoding.UTF8);
        try
        {
            using var monitor = Samples.Monitor();
            using var form = new CobianImportForm(monitor);
            form.Show();
            Sta.Pump();
            form.ReadFile(list);
            Sta.Pump();

            Assert.Equal(2, form.TaskList.Items.Count);
            Assert.All(form.TaskList.Items.Cast<ListViewItem>(), i => Assert.True(i.Checked));
            Assert.Equal(1, form.AccountCount);

            // The remarks of the selected task are in words, not codes.
            var remarks = Sta.Find<TextBox>(form).Single(b => b.Multiline);
            Assert.Contains(CobianImportForm.Describe(new ImportWarning(ImportWarningCodes.RunAsAccount, @"EXAMPLE\backup-user")), remarks.Text, StringComparison.Ordinal);
            Assert.DoesNotContain(ImportWarningCodes.RunAsAccount, remarks.Text, StringComparison.Ordinal);

            // Without the task that uses the account, its password is not asked for.
            form.TaskList.Items[0].Checked = false;
            Sta.Pump();
            Assert.Equal(0, form.AccountCount);
            form.TaskList.Items[0].Checked = true;
            Sta.Pump();
            Assert.Equal(1, form.AccountCount);
        }
        finally
        {
            File.Delete(list);
        }
    });

    /// <summary>Two made-up Cobian Backup 11 tasks; the first runs as a domain account.</summary>
    private static string CobianList => $$"""
        {11//
        Name=Documents
        Id={00000000-1111-2222-3333-444444444444}
        Enabled=true
        Include subdirectories=true
        Create separated backups=true
        Use VSC=true
        Backup type=1
        Source="1,D:\Data\Documents"
        Destination="1,\\server\backup\docs"
        Schedule type=1
        Date/Time={{Bits(45292 + (21.5 / 24))}}
        Full copies=4
        Impersonate=true
        Run as User name=backup-user
        Run as Domain=EXAMPLE
        //11}
        {11//
        Name=Photos
        Enabled=false
        Backup type=2
        Source=1,D:\Photos
        Destination=1,F:\Copies
        Schedule type=2
        Day of the week=1,5
        Date/Time={{Bits(45292 + (21.5 / 24))}}
        //11}
        """;

    /// <summary>A Delphi TDateTime as Cobian writes it: the bits of the double.</summary>
    private static string Bits(double value) => Convert.ToString(BitConverter.DoubleToInt64Bits(value), 2).PadLeft(64, '0');

    [Theory]
    [InlineData("en-US")]
    [InlineData("ru-RU")]
    public void The_restore_window_lists_the_sets_newest_first(string culture) => Sta.Run(culture, () =>
    {
        using var monitor = Samples.Monitor();
        using var form = new RestoreForm(monitor, Samples.OfficeId);
        form.Show();
        Sta.Pump();

        var header = new Custos.Core.Backup.SetManifestHeader { SetName = "Office documents 2026-11-02 21-00 (Incremental)", BackupType = BackupTypes.Incremental, StartedAt = new DateTimeOffset(2026, 11, 2, 18, 0, 0, TimeSpan.Zero), SetFileCount = 12 };
        var full = new Custos.Core.Backup.SetManifestHeader { SetName = "Office documents 2026-11-01 21-00 (Full)", BackupType = BackupTypes.Full, StartedAt = new DateTimeOffset(2026, 11, 1, 18, 0, 0, TimeSpan.Zero), SetFileCount = 340 };
        form.ShowSets(new Custos.Core.Ipc.RestoreListResult
        {
            Sets =
            [
                new Custos.Core.Ipc.RestoreSetInfo { Location = @"D:\Backup\b.7z", Where = @"D:\Backup", Header = header, Storage = ArchiveFormats.SevenZip, Bytes = 1024, Parts = 3 },
                new Custos.Core.Ipc.RestoreSetInfo { Location = @"D:\Backup\a", Where = @"D:\Backup", Header = full },
            ],
        });
        Sta.Pump();

        Assert.Equal(2, form.SetList.Items.Count);
        Assert.StartsWith(header.SetName, form.SetList.Items[0].SubItems[2].Text, StringComparison.Ordinal);
        Assert.True(form.SetList.Items[0].Selected);
        Assert.Equal(full.SetName, form.SetList.Items[1].SubItems[2].Text);
    });

    [Fact]
    public void The_restore_window_asks_for_a_folder_for_the_dumps_of_a_set_with_databases() => Sta.Run("ru-RU", () =>
    {
        using var monitor = Samples.Monitor();
        using var form = new RestoreForm(monitor, Samples.OfficeId);
        form.Show();
        Sta.Pump();
        var plain = new Custos.Core.Backup.SetManifestHeader { SetName = "Office 1", BackupType = BackupTypes.Full, StartedAt = new DateTimeOffset(2026, 11, 1, 18, 0, 0, TimeSpan.Zero) };
        var dumps = new Custos.Core.Backup.SetManifestHeader
        {
            SetName = "Office 2",
            BackupType = BackupTypes.Full,
            StartedAt = new DateTimeOffset(2026, 11, 2, 18, 0, 0, TimeSpan.Zero),
            Databases = [new Custos.Core.Backup.ManifestDatabase { Engine = DbEngines.MySql, Name = "shop", File = "db/mysql/shop.sql", Server = "127.0.0.1:3306" }],
        };

        form.ShowSets(new Custos.Core.Ipc.RestoreListResult
        {
            Sets =
            [
                new Custos.Core.Ipc.RestoreSetInfo { Location = @"D:\Backup\b", Where = @"D:\Backup", Header = dumps },
                new Custos.Core.Ipc.RestoreSetInfo { Location = @"D:\Backup\a", Where = @"D:\Backup", Header = plain },
            ],
        });
        Sta.Pump();
        var note = Sta.Find<Label>(form).Single(l => l.Text == Strings.RestoreDumpsNote);
        Assert.True(note.Visible);
        Assert.True(Sta.Find<Label>(form).Single(l => l.Text == Strings.RestoreDumpsFolder).Visible);

        form.SetList.Items[1].Selected = true;
        Sta.Pump();
        Assert.False(note.Visible);
    });

    [Fact]
    public void The_restore_window_puts_a_copy_of_a_linux_server_into_a_folder() => Sta.Run("ru-RU", () =>
    {
        using var monitor = Samples.Monitor();
        using var form = new RestoreForm(monitor, Samples.OfficeId);
        form.Show();
        Sta.Pump();
        var server = new Custos.Core.Backup.SetManifestHeader
        {
            SetName = "Web 1",
            BackupType = BackupTypes.Full,
            StartedAt = new DateTimeOffset(2026, 11, 2, 18, 0, 0, TimeSpan.Zero),
            SourceKind = SourceKinds.Ssh,
        };

        form.ShowSets(new Custos.Core.Ipc.RestoreListResult
        {
            Sets = [new Custos.Core.Ipc.RestoreSetInfo { Location = @"D:\Backup\w", Where = @"D:\Backup", Header = server }],
        });
        Sta.Pump();

        var original = Sta.Find<RadioButton>(form).Single(r => r.Text == Strings.RestoreToOriginal);
        Assert.False(original.Enabled);
        Assert.False(original.Checked);
        Assert.True(Sta.Find<RadioButton>(form).Single(r => r.Text == Strings.RestoreToFolder).Checked);
        Assert.True(Sta.Find<Label>(form).Single(l => l.Text == Strings.RestoreRemoteNote).Visible);
        Assert.False(Sta.Find<Label>(form).Single(l => l.Text == Strings.RestoreDumpsFolder).Visible);
    });

    [Theory]
    [InlineData("en-US")]
    [InlineData("ru-RU")]
    public void The_linux_server_dialog_makes_the_block_and_gives_back_the_source(string culture) => Sta.Run(culture, () =>
    {
        using var monitor = Samples.Monitor();
        using var toolTip = new ToolTip();
        var context = new EditorContext(monitor, toolTip);
        using (var empty = new RemoteSourceDialog(context, new TaskSource()))
        {
            empty.Show();
            Sta.Pump();
            Assert.Equal(Strings.RemoteAddTitle, empty.Text);
            Assert.Equal(Strings.RemoteBlockNeedsKey, empty.BlockText);
            Assert.True(Sta.Find<Label>(empty).Single(l => l.Text == Strings.RemoteOfficeEmpty).Visible);
            Assert.True(Sta.Find<Label>(empty).Single(l => l.Text == Strings.RemoteDbListEmpty).Visible);
        }

        var original = Samples.Server().Sources[0];
        original.HostKeySha256 = null;
        using var dialog = new RemoteSourceDialog(context, original);
        dialog.Show();
        Sta.Pump();

        // The key of the task is there: the block installs custos-helper as it is built in, with the key from the office only.
        Assert.Equal(Strings.RemoteEditTitle, dialog.Text);
        string block = dialog.BlockText.ReplaceLineEndings("\n");
        Assert.StartsWith("bash -e <<'CUSTOS_INSTALL_EOF'\n", block, StringComparison.Ordinal);
        Assert.Contains(Custos.Core.Remote.HelperScript.Sha256, block, StringComparison.Ordinal);
        Assert.Contains(Custos.Core.Remote.HelperSetup.KeyLine(Samples.SshKey().PublicKey!, "203.0.113.10"), block, StringComparison.Ordinal);
        Assert.Contains("root       = /var/www\n", block, StringComparison.Ordinal);
        Assert.False(Sta.Find<Label>(dialog).Single(l => l.Text == Strings.RemoteOfficeEmpty).Visible);
        var etc = Sta.Find<Label>(dialog).Single(l => l.Text == Strings.RemoteEtcWarning);
        Assert.False(etc.Visible);

        // A preset with the whole /etc warns; a changed list is a list of one's own.
        var preset = Sta.Find<ComboBox>(dialog).Single();
        preset.SelectedIndex = 2;
        Sta.Pump();
        Assert.True(etc.Visible);
        Assert.Contains("root       = /home\n", dialog.BlockText.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        var roots = Sta.Find<TextBox>(dialog).Single(b => b.Multiline && !b.ReadOnly && b.Text.Contains("/home", StringComparison.Ordinal));
        roots.Text += Environment.NewLine + "/srv";
        Sta.Pump();
        Assert.Equal(RemoteSourceDialog.PresetTitle(RemotePresets.Custom), preset.Text);

        // «Copy» and «Save as» stand side by side, at one height; the databases come before the key, the speed after the check.
        var copy = Sta.Find<Button>(dialog).Single(b => b.Text == Strings.ButtonCopy);
        var saveAs = Sta.Find<Button>(dialog).Single(b => b.Text == Strings.ButtonSaveAs);
        Assert.Equal(copy.Top, saveAs.Top);
        var databaseChoice = Sta.Find<RadioButton>(dialog).Single(r => r.Text == Strings.DbChoiceAll);
        Assert.True(dialog.PointToClient(databaseChoice.PointToScreen(Point.Empty)).Y < dialog.PointToClient(copy.PointToScreen(Point.Empty)).Y);
        var speedBox = Sta.Find<NumericUpDown>(dialog).Single(n => n.Maximum > 65535);
        Assert.True(dialog.PointToClient(speedBox.PointToScreen(Point.Empty)).Y > dialog.PointToClient(Sta.Find<Button>(dialog).Single(b => b.Text == Strings.ButtonTestConnection).PointToScreen(Point.Empty)).Y);

        // Scrolled down and then made larger, the rows keep their height: no empty space under the last line.
        var lastNote = Sta.Find<Label>(dialog).Single(l => l.Text == Strings.RemoteNextPages);
        var rows = (TableLayoutPanel)lastNote.Parent!;
        var scroller = (ScrollableControl)rows.Parent!;
        dialog.ClientSize = new Size(760, 500);
        Sta.Pump();
        scroller.AutoScrollPosition = new Point(0, 10_000);
        Sta.Pump();
        dialog.ClientSize = new Size(1000, 900);
        Sta.Pump();
        Assert.InRange(rows.Height - lastNote.Bottom, 0, lastNote.Margin.Bottom + rows.Padding.Bottom + 2);
        Assert.Equal(rows.Width, scroller.ClientSize.Width);

        // The test: an unknown key of the server is to be confirmed first, then the answer of custos-helper is shown.
        Assert.True(dialog.ShowTest(new Custos.Core.Ipc.RemoteTestResult { HostKeySha256 = Samples.ServerKey, HostKeyUnconfirmed = true }));
        Assert.Contains(Sta.Find<Label>(dialog), l => l.Text == Strings.RemoteKeyToConfirm);
        dialog.Pin(Samples.ServerKey);
        Assert.False(dialog.ShowTest(new Custos.Core.Ipc.RemoteTestResult
        {
            Ok = true,
            HostKeySha256 = Samples.ServerKey,
            HelperVersion = "1.0.0",
            Os = "Debian GNU/Linux 12",
            Files = 1234,
            Bytes = 5_000_000,
            ServerFreeBytes = 50_000_000_000,
            Databases = [new Custos.Core.Ipc.RemoteDatabases { Engine = DbEngines.MariaDb, Names = ["site_db", "shop_db"] }],
        }));
        Assert.Contains(Sta.Find<Label>(dialog), l => l.Text.Contains("MariaDB: site_db, shop_db", StringComparison.Ordinal));
        Assert.Contains(Sta.Find<Label>(dialog), l => l.Text == Strings.Format(Strings.RemoteHostKeyPinned, Samples.ServerKey));

        // The databases the test found join the list: under «All databases» ticked, the one left out by the task not.
        var databases = Sta.Find<CheckedListBox>(dialog).Single();
        var allChoice = Sta.Find<RadioButton>(dialog).Single(r => r.Text == Strings.DbChoiceAll);
        var onlyChoice = Sta.Find<RadioButton>(dialog).Single(r => r.Text == Strings.DbChoiceOnly);
        Assert.True(allChoice.Checked);
        Assert.Equal(allChoice.Top, onlyChoice.Top);
        Assert.True(allChoice.Right < onlyChoice.Left);
        Assert.False(Sta.Find<Label>(dialog).Single(l => l.Text == Strings.RemoteDbListEmpty).Visible);
        Assert.Equal(["old_db", "site_db", "shop_db"], databases.Items.Cast<object>().Select(i => i.ToString()));
        Assert.Equal(["site_db", "shop_db"], databases.CheckedItems.Cast<object>().Select(i => i.ToString()));

        // Unticking one switches to «Selected databases»: only the ticked ones are dumped.
        databases.SetItemChecked(2, false);
        Assert.True(onlyChoice.Checked);

        ((Button)dialog.AcceptButton!).PerformClick();
        Sta.Pump();
        Assert.Equal(DialogResult.OK, dialog.DialogResult);
        var source = dialog.Source;
        Assert.Equal(
            (original.Id, SourceKinds.Ssh, "custos@vps.example.org:22", Samples.SshKeyId, Samples.ServerKey, RemotePresets.Custom, "203.0.113.10"),
            (source.Id, source.Kind, source.Path, source.CredentialId, source.HostKeySha256, source.Preset, source.OfficeIp));
        Assert.Equal(["/etc", "/home", "/root", "/var/www", "/var/spool/cron", "/srv"], source.Helper!.Roots);
        Assert.False(source.Databases!.AllDatabases);
        Assert.Equal(["mariadb/site_db"], source.Databases.Include);
        Assert.Equal(Strings.Format(Strings.DbOnly, "site_db"), RemoteSourceDialog.DescribeDatabases(source.Databases));

        // Without the engine there is nothing to choose.
        using var noEngines = new RemoteSourceDialog(context, original);
        noEngines.Show();
        Sta.Pump();
        Sta.Find<CheckBox>(noEngines).Single(c => c.Text == DbEngines.Title(DbEngines.MariaDb)).Checked = false;
        Sta.Pump();
        Assert.Empty(Sta.Find<CheckedListBox>(noEngines).Single().Items);
        Assert.False(Sta.Find<RadioButton>(noEngines).Single(r => r.Text == Strings.DbChoiceAll).Enabled);
    });

    [Fact]
    public void The_database_server_dialog_lists_what_the_test_and_the_search_found() => Sta.Run("ru-RU", () =>
    {
        using var monitor = Samples.Monitor();
        using var toolTip = new ToolTip();
        var server = new TaskDatabaseServer { Engine = DbEngines.MariaDb, BinDir = @"C:\OSPanel\modules\MariaDB-11.4\bin", AllDatabases = false, Include = ["shop"] };
        using var dialog = new DbServerDialog(new EditorContext(monitor, toolTip), server, isNew: true);
        dialog.Show();
        Sta.Pump();

        Assert.Equal("root", Sta.Find<TextBox>(dialog).Single(b => b.Text == "root").Text);
        dialog.ShowTest(new Custos.Core.Ipc.DbTestResult { Ok = true, Version = "11.4.4-MariaDB", Databases = ["blog", "shop"], DataDir = @"C:\OSPanel\data\MariaDB-11.4\" });
        Sta.Pump();
        var list = Sta.Find<CheckedListBox>(dialog).Single();
        Assert.Equal(["shop", "blog"], list.Items.Cast<string>());
        Assert.Equal(["shop"], list.CheckedItems.Cast<string>());
        Assert.Contains(Sta.Find<Label>(dialog), l => l.Text.StartsWith(Strings.Format(Strings.DbTestOk, "11.4.4-MariaDB", 2), StringComparison.Ordinal));

        // «All databases»: everything is ticked, new databases too; unticking one switches to «Selected databases».
        var all = new TaskDatabaseServer { Engine = DbEngines.MySql, BinDir = @"C:\OSPanel\modules\MySQL-8.0\bin" };
        using (var allDialog = new DbServerDialog(new EditorContext(monitor, toolTip), all, isNew: false))
        {
            allDialog.Show();
            Sta.Pump();
            var allList = Sta.Find<CheckedListBox>(allDialog).Single();
            var allChoice = Sta.Find<RadioButton>(allDialog).Single(r => r.Text == Strings.DbChoiceAll);
            var onlyChoice = Sta.Find<RadioButton>(allDialog).Single(r => r.Text == Strings.DbChoiceOnly);
            Assert.Equal(allChoice.Left, onlyChoice.Left);
            allDialog.ShowTest(new Custos.Core.Ipc.DbTestResult { Ok = true, Version = "8.4.8", Databases = ["shop", "blog"] });
            Assert.Equal(["shop", "blog"], allList.CheckedItems.Cast<string>());
            var built = allDialog.Build();
            Assert.True(built.AllDatabases);
            Assert.Empty(built.Exclude);

            allList.SetItemChecked(allList.Items.IndexOf("blog"), false);
            Assert.True(onlyChoice.Checked);
            built = allDialog.Build();
            Assert.False(built.AllDatabases);
            Assert.Equal(["shop"], built.Include);

            // Back to «All databases»: everything is ticked again.
            allChoice.Checked = true;
            Assert.Equal(["shop", "blog"], allList.CheckedItems.Cast<string>());
            Assert.True(allDialog.Build().AllDatabases);
        }

        using var find = new DbFindDialog(monitor);
        find.ShowFound(new Custos.Core.Ipc.DbFindResult
        {
            Servers =
            [
                new Custos.Core.Ipc.DbCandidate { Engine = DbEngines.MySql, BinDir = @"C:\OSPanel\modules\MySQL-8.0\bin", Version = "8.0", Host = "127.127.126.26", Port = 3306, Running = true },
                new Custos.Core.Ipc.DbCandidate { Engine = DbEngines.PostgreSql, BinDir = @"C:\Program Files\PostgreSQL\17\bin", Version = "17", Port = 5432 },
            ],
        });
        var found = Sta.Find<ListView>(find).Single();
        Assert.Equal(2, found.Items.Count);
        Assert.Equal("MySQL 8.0 — 127.127.126.26:3306", found.Items[0].Text);
        Assert.Equal(Strings.DbNotRunning, found.Items[1].SubItems[1].Text);
    });

    [Theory]
    [InlineData("en-US")]
    [InlineData("ru-RU")]
    public void The_countdown_names_the_task_the_action_and_the_seconds_left(string culture) => Sta.Run(culture, () =>
    {
        using var monitor = Samples.Monitor();
        var countdown = new Custos.Core.Ipc.PowerCountdown
        {
            Action = AfterActions.Shutdown,
            TaskName = "Office documents",
            Deadline = DateTimeOffset.UtcNow.AddSeconds(42),
        };
        using var form = new PowerCountdownForm(monitor, countdown);
        form.Show();
        Sta.Pump();

        Assert.InRange(form.SecondsLeft, 40, 42);
        Assert.Contains("Office documents", form.Message, StringComparison.Ordinal);
        Assert.Contains(Custos.Core.Messages.PowerShutdown, form.Message, StringComparison.Ordinal);
        Assert.True(form.TopMost);

        form.ShowCountdown(new Custos.Core.Ipc.PowerCountdown { Action = AfterActions.Sleep, TaskName = "Photos", Deadline = DateTimeOffset.UtcNow.AddSeconds(-5) });
        Assert.Equal(0, form.SecondsLeft);
        Assert.Contains(Custos.Core.Messages.PowerSleep, form.Message, StringComparison.Ordinal);
    });
}
