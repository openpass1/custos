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
}
