using Custos.Core.Model;

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
        Assert.True(editor.Pages[2].Visible);
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
        Assert.Equal(8, editor.Pages.Count);
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
