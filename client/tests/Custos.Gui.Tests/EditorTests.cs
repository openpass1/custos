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
