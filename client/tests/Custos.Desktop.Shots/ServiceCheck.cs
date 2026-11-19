using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using Avalonia.Threading;
using Custos.Core.Ipc;
using Custos.Core.Model;
using Custos.Gui;

namespace Custos.Desktop.Shots;

/// <summary>
/// The buttons of the main window against the real CUSTOS service (the Windows job of .github/workflows/desktop.yml
/// starts CustosService.exe first): a task is cloned, switched on and off alone and together with another, run,
/// paused, resumed and stopped, then both are deleted through the question; the theme of the menu is saved in the
/// settings of the service. Each step presses the button itself, so the bindings that enable it are checked as well.
/// </summary>
internal static class ServiceCheck
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    public static int Run(string folder, Action<Window, string> save)
    {
        string work = Path.Combine(Path.GetTempPath(), "custos-desktop-check");
        string source = Path.Combine(work, "source");
        string destination = Path.Combine(work, "destination");
        if (Directory.Exists(work))
        {
            Directory.Delete(work, recursive: true);
        }

        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);
        // 16 MB at 512 KB/s: the run lasts about half a minute, long enough to pause and stop it.
        var bytes = new byte[16 * 1024 * 1024];
        Random.Shared.NextBytes(bytes);
        File.WriteAllBytes(Path.Combine(source, "big.bin"), bytes);
        File.WriteAllText(Path.Combine(source, "note.txt"), "CUSTOS");

        var window = App.CreateMainWindow(demo: false);
        var vm = window.ViewModel;
        window.Show();
        vm.Start();
        var failures = new List<string>();

        void Step(string name, bool ok)
        {
            Console.WriteLine((ok ? "OK    " : "FAILED ") + name);
            if (!ok)
            {
                failures.Add(name);
            }
        }

        try
        {
            if (!WaitFor(() => vm.IsConnected, TimeSpan.FromSeconds(90)))
            {
                Console.WriteLine($"No connection to the service: {vm.Monitor.Status.State} {vm.Monitor.Status.Problem}");
                return 1;
            }

            Step("connected as an administrator", vm.CanEdit && vm.CanRestore);

            // The task to work with: saved straight through the pipe, since the editor of this version does not save.
            var task = new TaskDefinition
            {
                Name = "Desktop check",
                BackupType = BackupTypes.Full,
                Schedule = new ScheduleOptions { Type = ScheduleTypes.Manual },
                Vss = new VssOptions { Enabled = false },
                Sources = [new TaskSource { Kind = SourceKinds.Folder, Path = source }],
                Destinations = [new TaskDestination { Kind = DestinationKinds.Local, Path = destination, SpeedLimitKbps = 512 }],
            };
            var saved = Await(vm.Monitor.RequestAsync(PipeProtocol.Methods.TaskSave, new TaskSaveRequest { Task = task },
                IpcJsonContext.Default.TaskSaveRequest, IpcJsonContext.Default.TaskSaveResult)).Task;
            Step("the task is in the list", WaitFor(() => Row(vm, saved.Id) is not null));
            vm.SelectedTask = Row(vm, saved.Id);
            Pump();

            // «Клонировать»: a copy, switched off, selected.
            Press(window, "CloneButton");
            string cloneName = Strings.Format(Strings.CloneName, saved.Name);
            Step("«Клонировать» makes a copy that is off and selected", WaitFor(() =>
                vm.Tasks.Count == 2 && vm.SelectedTask?.Name == cloneName && vm.SelectedDefinition is { Enabled: false }));
            string cloneId = vm.SelectedTask!.TaskId;

            // «Включить» for the copy alone.
            Step("the switch says «Включить» for a task that is off", vm.ToggleText == Strings.ToolEnable);
            Press(window, "ToggleButton");
            Step("«Включить» switches the copy on", WaitFor(() => Definition(vm, cloneId)?.Enabled == true));

            // Both selected with Ctrl: «Отключить» switches both off with one request.
            var list = window.FindControl<ListBox>("TaskList")!;
            list.Selection.Select(vm.Tasks.IndexOf(Row(vm, saved.Id)!));
            Pump();
            Step("two tasks selected, «Изменить» is off", vm.Selection.Count == 2 && !window.FindControl<Button>("EditButton")!.IsEnabled);
            Step("the switch says «Отключить» when all selected are on", vm.ToggleText == Strings.ToolDisable);
            Press(window, "ToggleButton");
            Step("«Отключить» switches both off", WaitFor(() => Definition(vm, saved.Id)?.Enabled == false && Definition(vm, cloneId)?.Enabled == false));

            // The task alone again, on, and run.
            vm.SelectedTask = Row(vm, saved.Id);
            Pump();
            Press(window, "ToggleButton");
            Step("the task is on again", WaitFor(() => Definition(vm, saved.Id)?.Enabled == true));
            Press(window, "RunButton");
            Step("«Запустить» starts the run", WaitFor(() => vm.Monitor.Snapshot?.Current?.TaskId == saved.Id && vm.CanPause));

            Press(window, "PauseButton");
            Step("«Пауза» pauses it and the button says «Продолжить»", WaitFor(() => vm.IsPaused && vm.PauseText == DesktopStrings.Resume));
            vm.IsProgressTab = true;
            Pump();
            save(window, Path.Combine(folder, "12-sluzhba-pauza-svetloe.png"));
            Press(window, "PauseButton");
            Step("«Продолжить» goes on", WaitFor(() => vm.Monitor.Snapshot?.Current is { } run && !vm.IsPaused));

            Press(window, "StopButton");
            Step("«Остановить» stops it", WaitFor(() => vm.Monitor.Snapshot?.Current is null
                && vm.Monitor.Snapshot?.States.FirstOrDefault(s => s.TaskId == saved.Id)?.LastRun?.Status == RunStatuses.Aborted));

            // «Удалить» for both, through the question.
            list.Selection.Select(vm.Tasks.IndexOf(Row(vm, cloneId)!));
            Pump();
            Press(window, "DeleteButton");
            Step("«Удалить» asks first", WaitFor(() => window.OwnedWindows.OfType<DialogWindow>().Any()));
            if (window.OwnedWindows.OfType<DialogWindow>().FirstOrDefault() is { } question)
            {
                var yes = question.GetLogicalDescendants().OfType<Button>().First(b => b.IsDefault);
                yes.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }

            Step("both tasks are deleted", WaitFor(() => Row(vm, saved.Id) is null && Row(vm, cloneId) is null));

            // The theme of the menu goes into the settings of the service, for all users.
            string before = vm.Monitor.Snapshot!.Settings.Theme;
            Theme(window, 2);
            Step("the dark theme is saved in the service", WaitFor(() => vm.Monitor.Snapshot?.Settings.Theme == Themes.Dark && window.ActualThemeVariant == ThemeVariant.Dark));
            Theme(window, before == Themes.Light ? 1 : before == Themes.Dark ? 2 : 0);
            Step("and back", WaitFor(() => vm.Monitor.Snapshot?.Settings.Theme == before));
        }
        finally
        {
            window.Quitting = true;
            window.Close();
            vm.Dispose();
            Pump();
        }

        Console.WriteLine(failures.Count == 0 ? "Service: every button did its work." : $"Service: {failures.Count} steps failed.");
        return failures.Count == 0 ? 0 : 1;
    }

    private static TaskRow? Row(MainViewModel vm, string id) => vm.Tasks.FirstOrDefault(r => r.TaskId == id);

    private static TaskDefinition? Definition(MainViewModel vm, string id) => vm.Monitor.Snapshot?.Tasks.FirstOrDefault(t => t.Id == id);

    /// <summary>A click on a button of the toolbar, which must be enabled: the binding that enables it is part of the check.</summary>
    private static void Press(Window window, string name)
    {
        var button = window.FindControl<Button>(name)!;
        WaitFor(() => button.IsEffectivelyEnabled, TimeSpan.FromSeconds(10));
        if (!button.IsEffectivelyEnabled)
        {
            Console.WriteLine($"       {name} is not enabled");
            return;
        }

        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();
    }

    /// <summary>The n-th theme of the «Настройки» menu: auto, light, dark.</summary>
    private static void Theme(MainWindow window, int index)
    {
        var settings = window.FindControl<Button>("SettingsButton")!;
        var flyout = (Flyout)settings.Flyout!;
        flyout.ShowAt(settings);
        Pump();
        var item = window.FindControl<StackPanel>("ThemeMenu")!.Children.OfType<RadioButton>().ElementAt(index);
        item.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();
    }

    private static T Await<T>(Task<T> task)
    {
        WaitFor(() => task.IsCompleted, Patience);
        return task.GetAwaiter().GetResult();
    }

    /// <summary>Lets the window and the connection work until the condition holds or the time is up.</summary>
    private static bool WaitFor(Func<bool> condition, TimeSpan? limit = null)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < (limit ?? Patience))
        {
            Pump();
            if (condition())
            {
                return true;
            }

            Thread.Sleep(50);
        }

        return condition();
    }

    private static void Pump()
    {
        for (int i = 0; i < 4; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }
}
