using System.Drawing.Imaging;
using Custos.Core.Ipc;
using Custos.Core.Model;

namespace Custos.Gui.Tests;

/// <summary>
/// Stage 5: the rights and the program password in the settings, the state window of users who get only the state,
/// and the dark theme. With CUSTOS_SCREENSHOTS set to a folder, the windows are also saved there as pictures, in both
/// themes (the Windows build uploads them, so the look can be checked without installing).
/// </summary>
public sealed class Stage5Tests
{
    [Theory]
    [InlineData("en-US")]
    [InlineData("ru-RU")]
    public void The_settings_show_the_rights_the_password_and_the_theme_but_only_administrators_change_the_rights(string culture) => Sta.Run(culture, () =>
    {
        using var monitor = new ServiceMonitor();
        var snapshot = Samples.Snapshot(1);
        snapshot.Settings.TrayMode = TrayModes.Hidden;
        snapshot.Settings.Theme = Themes.Dark;
        snapshot.Settings.AllowAllUsersEdit = true;
        snapshot.ProgramPasswordSet = true;
        monitor.SimulateSnapshot(snapshot);
        using var settings = new SettingsForm(monitor);
        settings.Show();
        Sta.Pump();

        Assert.True(Sta.Shows(settings, Strings.SettingsRights));
        Assert.True(Sta.Shows(settings, Strings.SettingsLook));
        var allowAll = Assert.Single(Sta.Find<CheckBox>(settings), c => c.Text == Strings.AllowAllUsersEdit);
        Assert.True(allowAll.Checked);
        Assert.False(allowAll.Enabled);
        var combos = Sta.Find<ComboBox>(settings);
        var trayMode = Assert.Single(combos, c => c.Items.Contains(Strings.TrayModeHidden));
        Assert.Equal(Strings.TrayModeHidden, trayMode.Text);
        Assert.False(trayMode.Enabled);
        var theme = Assert.Single(combos, c => c.Items.Contains(Strings.ThemeDark));
        Assert.Equal(Strings.ThemeDark, theme.Text);

        // The password itself is never shown: only that one is set, and the buttons stay with the administrators.
        Assert.True(Sta.Shows(settings, Strings.ProgramPasswordIsSet));
        var change = Assert.Single(Sta.Find<Button>(settings), b => b.Text == Strings.ProgramPasswordChangeButton);
        Assert.False(change.Enabled);
        var remove = Assert.Single(Sta.Find<Button>(settings), b => b.Text == Strings.ProgramPasswordRemoveButton);
        Assert.True(remove.Visible);
        Assert.False(remove.Enabled);
        Assert.True(Sta.Shows(settings, Strings.SettingsRightsAdminOnly));

        // The site removed the password: the settings say so at once.
        var without = Samples.Snapshot(2);
        monitor.SimulateSnapshot(without);
        Sta.Pump();
        Assert.True(Sta.Shows(settings, Strings.ProgramPasswordNotSet));
        Assert.Contains(Sta.Find<Button>(settings), b => b.Text == Strings.ProgramPasswordSetButton);
        Assert.False(remove.Visible);
    });

    [Theory]
    [InlineData("en-US")]
    [InlineData("ru-RU")]
    public void The_state_window_shows_the_tasks_and_what_runs_without_buttons_for_changes(string culture) => Sta.Run(culture, () =>
    {
        using var monitor = Samples.Monitor();
        using var form = new StatusForm(monitor);
        form.Show();
        Sta.Pump();

        var list = Assert.Single(Sta.Find<TaskListView>(form));
        Assert.Equal(3, list.Items.Count);
        Assert.Empty(Sta.Find<Button>(form));
        Assert.DoesNotContain(Sta.Find<ToolStrip>(form), t => t is not StatusStrip);
        Assert.True(Sta.Shows(form, Strings.StatusNote));
        Assert.True(Sta.ShowsPart(form, "Photos"));

        monitor.SimulateSnapshot(Samples.Snapshot(2, running: true));
        Sta.Pump();
        Assert.True(Sta.ShowsPart(form, Strings.Format(Strings.TrayRunning, "Office documents", 47)));
        Assert.Equal(nameof(StateIcon.Running), list.Items.Cast<ListViewItem>().Single(i => (string)i.Tag! == Samples.OfficeId).ImageKey);

        // Closed by the user, it only hides: the interface stays.
        form.Close();
        Sta.Pump();
        Assert.False(form.IsDisposed);
        Assert.False(form.Visible);
    });

    [Fact]
    public void Several_tasks_are_selected_for_the_group_actions() => Sta.Run("en-US", () =>
    {
        using var monitor = Samples.Monitor();
        using var main = new MainForm(monitor) { Size = new Size(1100, 700) };
        main.Show();
        Sta.Pump();
        var list = Assert.Single(Sta.Find<TaskListView>(main));
        var toggle = Assert.Single(Sta.Find<ToolStrip>(main).SelectMany(s => s.Items.OfType<ToolStripButton>()), b => b.ToolTipText == Strings.ToolEnableHint);
        Assert.True(list.MultiSelect);

        // All of them, as after an import: one is off, so the button switches them on.
        list.SelectAllTasks();
        Sta.Pump();
        Assert.Equal(3, list.SelectedTaskIds.Count);
        Assert.Equal(Strings.ToolEnable, toggle.Text);

        // A new snapshot keeps the selection, without the task that is gone.
        monitor.SimulateSnapshot(Samples.Snapshot(2, withoutPhotos: true));
        Sta.Pump();
        Assert.Equal(2, list.SelectedTaskIds.Count);
        Assert.DoesNotContain(Samples.PhotosId, list.SelectedTaskIds);

        // One task picked again: the others are no longer selected, and an enabled task is switched off.
        list.SelectTask(Samples.OfficeId);
        Sta.Pump();
        Assert.Equal([Samples.OfficeId], list.SelectedTaskIds);
        Assert.Equal(Samples.OfficeId, list.SelectedTaskId);
        Assert.Equal(Strings.ToolDisable, toggle.Text);
    });

    [Fact]
    public void The_password_windows_check_what_was_typed_before_asking_the_service() => Sta.Run("en-US", () =>
    {
        using var monitor = Samples.Monitor();
        using var form = new ProgramPasswordForm(monitor);
        form.Show();
        Sta.Pump();
        var fields = Sta.Find<TextBox>(form);
        Assert.Equal(2, fields.Count);
        Assert.All(fields, f => Assert.True(f.UseSystemPasswordChar));

        fields[0].Text = "abc";
        fields[1].Text = "abc";
        ((Button)form.AcceptButton!).PerformClick();
        Sta.Pump();
        Assert.True(Sta.Shows(form, Strings.Format(Strings.PasswordTooShortUi, Core.Agent.ProgramPassword.MinLength)));

        fields[0].Text = "abcd";
        fields[1].Text = "abce";
        ((Button)form.AcceptButton!).PerformClick();
        Sta.Pump();
        Assert.True(Sta.Shows(form, Strings.PasswordMismatch));
        Assert.Equal(string.Empty, fields[1].Text);

        using var prompt = new PasswordPromptForm(monitor);
        prompt.Show();
        Sta.Pump();
        Assert.True(Assert.Single(Sta.Find<TextBox>(prompt)).UseSystemPasswordChar);
        Assert.True(Sta.ShowsPart(prompt, Strings.PasswordPromptText));
    });

    [Fact]
    public void Without_a_program_password_the_windows_for_changes_open_without_asking() => Sta.Run("en-US", () =>
    {
        using var monitor = Samples.Monitor();
        bool asked = false;
        monitor.AskPassword = () =>
        {
            asked = true;
            return Task.FromResult(false);
        };

        var unlocked = monitor.EnsureUnlockedAsync();
        Sta.Pump();
        Assert.True(unlocked.IsCompletedSuccessfully);
        Assert.True(unlocked.Result);
        Assert.False(asked);
    });

    [Theory]
    [InlineData(Themes.Light)]
    [InlineData(Themes.Dark)]
    public void Every_window_opens_in_both_themes(string theme) => Sta.Run("ru-RU", () =>
    {
        Theme.Apply(theme);
        try
        {
            // The colours of our own drawings follow the theme of the windows.
            Assert.Equal(Theme.IsDark ? Color.FromArgb(222, 225, 228) : Color.FromArgb(60, 64, 68), Glyphs.Ink);
            if (theme == Themes.Light)
            {
                Assert.False(Theme.IsDark);
            }

            using var monitor = Samples.Monitor();
            monitor.SimulateSnapshot(Samples.Snapshot(2, running: true));
            monitor.SimulateProgress(new RunProgressEvent { RunId = Samples.RunId, TaskId = Samples.OfficeId, Progress = Samples.Progress(470) });

            using var main = new MainForm(monitor) { Size = new Size(1100, 700) };
            main.Show();
            Sta.Pump();
            Assert.Single(Sta.Find<TaskListView>(main)).SelectTask(Samples.PhotosId);
            Sta.Pump();
            Save(main, $"main-window-{theme}");

            using var settings = new SettingsForm(monitor);
            settings.Show();
            Sta.Pump();
            Save(settings, $"settings-{theme}");

            using var editor = new TaskEditorForm(monitor, Samples.Office(), isNew: false);
            editor.Show();
            Sta.Pump();
            Save(editor, $"task-editor-{theme}");
            editor.SelectPage(editor.Pages[2]);
            Sta.Pump();
            Save(editor, $"task-editor-destinations-{theme}");

            // A monthly schedule with the calendar of days open under its box.
            var monthly = Samples.Office();
            monthly.Schedule = new ScheduleOptions { Type = ScheduleTypes.Monthly, Times = ["23:00"], DaysOfMonth = [1, 15, -1] };
            using var monthlyEditor = new TaskEditorForm(monitor, monthly, isNew: false);
            monthlyEditor.Show();
            Sta.Pump();
            var schedule = monthlyEditor.Pages.OfType<SchedulePage>().Single();
            monthlyEditor.SelectPage(schedule);
            Sta.Pump();
            var days = Assert.Single(Sta.Find<MonthDaysBox>(schedule));
            days.OpenCalendar();
            Save(monthlyEditor, $"task-editor-monthly-{theme}", days.Calendar);
            days.Calendar?.Close();
            Sta.Pump();

            using var status = new StatusForm(monitor);
            status.Show();
            Sta.Pump();
            Save(status, $"state-window-{theme}");

            using var prompt = new PasswordPromptForm(monitor);
            prompt.Show();
            Sta.Pump();
            Save(prompt, $"password-{theme}");
        }
        finally
        {
            Theme.Apply(Themes.Light);
        }
    });

    /// <summary>
    /// A picture of the window into CUSTOS_SCREENSHOTS, when it is set; with a drop-down of it (its own window) drawn
    /// where it is on the screen.
    /// </summary>
    private static void Save(Form form, string name, ToolStripDropDown? dropDown = null)
    {
        string? folder = Environment.GetEnvironmentVariable("CUSTOS_SCREENSHOTS");
        if (string.IsNullOrEmpty(folder))
        {
            return;
        }

        Directory.CreateDirectory(folder);
        var whole = new Rectangle(Point.Empty, form.Size);
        Rectangle? popup = null;
        if (dropDown is { Visible: true })
        {
            popup = new Rectangle(dropDown.Bounds.X - form.Bounds.X, dropDown.Bounds.Y - form.Bounds.Y, dropDown.Width, dropDown.Height);
            whole = Rectangle.Union(whole, popup.Value);
        }

        using var bitmap = new Bitmap(whole.Right, whole.Bottom);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
        if (popup is { } at)
        {
            dropDown!.DrawToBitmap(bitmap, at);
        }

        bitmap.Save(Path.Combine(folder, name + ".png"), ImageFormat.Png);
    }
}
