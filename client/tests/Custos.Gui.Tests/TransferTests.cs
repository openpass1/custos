using System.Globalization;
using Custos.Core;
using Custos.Core.Model;
using Custos.Core.Transfer;

namespace Custos.Gui.Tests;

/// <summary>The windows of the export and import of tasks (section 9a), with made-up data; nothing is sent to a service.</summary>
public sealed class TransferTests
{
    private const string ExportPassword = "correct horse battery";

    [Theory]
    [InlineData("en-US")]
    [InlineData("ru-RU")]
    public void The_export_window_counts_the_secrets_and_checks_the_password(string culture) => Sta.Run(culture, () =>
    {
        using var monitor = Samples.Monitor();
        using var form = new TaskExportForm(monitor);
        form.Show();
        Sta.Pump();

        // Every task is ticked; the office task uses one account with a password.
        Assert.Equal(3, form.TaskList.Items.Count);
        Assert.All(form.TaskList.Items.Cast<ListViewItem>(), i => Assert.True(i.Checked));
        Assert.Equal(Strings.Format(Strings.ExportSecretsCount, 1, 0, 0), form.CountsText);
        Assert.False(form.PasswordBox.Enabled);
        Assert.Null(form.Problem());

        form.IncludeSecrets.Checked = true;
        Sta.Pump();
        Assert.True(form.PasswordBox.Enabled);
        Assert.Equal(Messages.Format(Messages.ExportPasswordTooShort, TaskExport.MinPasswordLength), form.Problem());
        form.PasswordBox.Text = ExportPassword;
        form.RepeatBox.Text = ExportPassword + "!";
        Assert.Equal(Strings.ArchivePasswordMismatch, form.Problem());
        form.RepeatBox.Text = ExportPassword;
        Assert.Null(form.Problem());

        foreach (ListViewItem item in form.TaskList.Items)
        {
            item.Checked = false;
        }

        Sta.Pump();
        Assert.Equal(Strings.ExportNothingChosen, form.Problem());
        Assert.Equal(Strings.Format(Strings.ExportSecretsCount, 0, 0, 0), form.CountsText);
    });

    [Fact]
    public void The_offered_file_name_has_the_computer_and_the_date() =>
        Assert.Equal("custos-tasks-PC-01-2026-11-13.json", TaskExportForm.DefaultFileName("PC 01", new DateTime(2026, 11, 13, 10, 0, 0, DateTimeKind.Local)));

    [Theory]
    [InlineData("en-US")]
    [InlineData("ru-RU")]
    public void The_import_window_opens_the_secrets_and_asks_only_for_what_is_missing(string culture) => Sta.Run(culture, () =>
    {
        string path = WriteFile(withPassword: true);
        try
        {
            using var monitor = Samples.Monitor();
            using var form = new TaskImportForm(monitor);
            form.Show();
            Sta.Pump();
            form.ReadFile(path);
            Sta.Pump();

            // The office task is here already: replaced only when ticked. The two new ones are ticked.
            Assert.True(form.AsksExportPassword);
            Assert.Equal(3, form.TaskList.Items.Count);
            Assert.Equal([false, true, true], form.TaskList.Items.Cast<ListViewItem>().Select(i => i.Checked));
            Assert.Equal(Strings.ImportHereReplaced, form.TaskList.Items[0].SubItems[4].Text);
            Assert.Contains(TaskImportForm.Describe(new ImportRemark(ImportRemarkCodes.Replaces, "Office documents")), form.RemarksText, StringComparison.Ordinal);

            // Not opened: the password of the new share account is asked for, the SSH key cannot be typed.
            Assert.Equal(1, form.AccountCount);
            form.TaskList.Items[2].Selected = true;
            Sta.Pump();
            Assert.Contains(TaskImportForm.Describe(new ImportRemark(ImportRemarkCodes.SshKeyMissing, "custos@vps.example.org")), form.RemarksText, StringComparison.Ordinal);

            form.OpenSecrets("wrong password");
            Sta.Pump();
            Assert.False(form.SecretsOpen);
            Assert.Equal(1, form.AccountCount);

            form.OpenSecrets(ExportPassword);
            Sta.Pump();
            Assert.True(form.SecretsOpen);
            Assert.Equal(0, form.AccountCount);
            Assert.Equal([false, true, true], form.TaskList.Items.Cast<ListViewItem>().Select(i => i.Checked));
            Assert.Equal("—", form.TaskList.Items[2].SubItems[5].Text);
        }
        finally
        {
            File.Delete(path);
        }
    });

    [Fact]
    public void A_file_without_secrets_asks_no_export_password() => Sta.Run("en-US", () =>
    {
        string path = WriteFile(withPassword: false);
        try
        {
            using var monitor = Samples.Monitor();
            using var form = new TaskImportForm(monitor);
            form.Show();
            Sta.Pump();
            form.ReadFile(path);
            Sta.Pump();

            Assert.False(form.AsksExportPassword);
            Assert.Equal(1, form.AccountCount);
        }
        finally
        {
            File.Delete(path);
        }
    });

    [Fact]
    public void The_linux_server_dialog_offers_a_new_key_when_the_key_stayed_on_the_old_computer() => Sta.Run("en-US", () =>
    {
        using var monitor = Samples.Monitor();
        using var toolTip = new ToolTip();
        var context = new EditorContext(monitor, toolTip);
        using (var dialog = new RemoteSourceDialog(context, Samples.Server().Sources[0]))
        {
            dialog.Show();
            Sta.Pump();
            Assert.False(dialog.OffersNewKey);
        }

        var snapshot = Samples.Snapshot(2);
        snapshot.Credentials.Single(c => c.Id == Samples.SshKeyId).HasSecret = false;
        monitor.SimulateSnapshot(snapshot);
        using var moved = new RemoteSourceDialog(context, Samples.Server().Sources[0]);
        moved.Show();
        Sta.Pump();

        Assert.True(moved.OffersNewKey);
        Assert.Contains(Sta.Find<Label>(moved), l => l.Text == Strings.Format(Strings.RemoteKeyNotHere, "custos@vps.example.org"));
    });

    [Theory]
    [InlineData("en-US")]
    [InlineData("ru-RU")]
    public void Every_remark_of_the_file_import_is_explained_in_words(string culture)
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            var codes = typeof(ImportRemarkCodes).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                .Where(f => f.IsLiteral && f.FieldType == typeof(string))
                .Select(f => (string)f.GetRawConstantValue()!)
                .ToList();
            Assert.NotEmpty(codes);
            foreach (string code in codes)
            {
                Assert.NotEqual(code + ": x", TaskImportForm.Describe(new ImportRemark(code, "x")));
            }
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    /// <summary>
    /// A file from "another computer": the office task of the samples (so it is here already), a new task on a share
    /// with an account this computer does not have, and a new Linux server task with its own SSH key.
    /// </summary>
    private static string WriteFile(bool withPassword)
    {
        var share = new Credential { Id = "bbbbbbbb-0000-4000-8000-000000000001", User = "copy-user", Domain = "OTHER", HasSecret = true };
        var key = new Credential
        {
            Id = "bbbbbbbb-0000-4000-8000-000000000002",
            Kind = CredentialKinds.SshKey,
            User = "custos@vps.example.org",
            HasSecret = true,
            PublicKey = Samples.SshKey().PublicKey,
        };
        var shareTask = Samples.Photos();
        shareTask.Id = "cccccccc-0000-4000-8000-000000000001";
        shareTask.Name = "Share copy";
        shareTask.Destinations = [new TaskDestination { Kind = DestinationKinds.Unc, Path = @"\\nas\copies", CredentialId = share.Id }];
        var serverTask = Samples.Server();
        serverTask.Id = "cccccccc-0000-4000-8000-000000000002";
        serverTask.Destinations = [new TaskDestination { Kind = DestinationKinds.Local, Path = @"D:\Server" }];
        serverTask.Sources[0].CredentialId = key.Id;

        var file = TaskExport.Create(
            [Samples.Office(), shareTask, serverTask],
            [Samples.Account(), share, key],
            id => "secret-of-" + id,
            null,
            withPassword ? ExportPassword : null,
            "OLD-PC",
            DateTimeOffset.UtcNow);
        string path = Path.Combine(Path.GetTempPath(), "custos-gui-tests", Guid.NewGuid().ToString("N") + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, TaskExport.Serialize(file));
        return path;
    }
}
