using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Custos.Gui;

namespace Custos.Desktop.Shots;

/// <summary>
/// Pictures of the new window with the made-up tasks, in Russian, at twice the size (as on a 200 % screen), and checks
/// of what its menus and buttons do. The time and the light on the progress bar are fixed, so the pictures are the same
/// on every run.
/// </summary>
internal static class Program
{
    private const double Scale = 2;

    /// <summary><c>CustosDesktopShots [folder]</c>: the pictures and the checks with made-up tasks.
    /// <c>CustosDesktopShots --service [folder]</c>: the buttons against the CUSTOS service running on this computer.</summary>
    public static int Main(string[] args)
    {
        bool service = args.Length > 0 && args[0] == "--service";
        args = service ? args[1..] : args;
        string folder = Path.GetFullPath(args.Length > 0 ? args[0] : "shots");
        Directory.CreateDirectory(folder);

        var russian = CultureInfo.GetCultureInfo("ru-RU");
        CultureInfo.DefaultThreadCurrentCulture = russian;
        CultureInfo.DefaultThreadCurrentUICulture = russian;
        CultureInfo.CurrentCulture = russian;
        CultureInfo.CurrentUICulture = russian;
        ProgressLine.FixedPhase = 0.55;
        ProgressModel.FixedNow = DemoData.Now.AddMinutes(4).AddSeconds(31);
        Describe.FixedToday = DemoData.Now.LocalDateTime.Date;
        DesktopClock.Fixed = DemoData.Now;
        // ui.json of a user of its own: the pictures neither read nor change the one of the computer they are made on.
        UiState.Folder = Path.Combine(Path.GetTempPath(), "custos-shots-" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture));

        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .With(new FontManagerOptions { DefaultFamilyName = Fonts.Roboto })
            .SetupWithoutStarting();

        if (service)
        {
            return ServiceCheck.Run(folder, Save);
        }

        Console.WriteLine($"Icons: {IconData.CheckAll()} parse.");

        Shot(folder, "1-glavnoe-okno-svetloe", "light", "Photos", Tab.Log, card: true);
        Shot(folder, "2-glavnoe-okno-tyomnoe", "dark", "Desktop", Tab.Progress, card: true);
        Shot(folder, "3-istoriya-bez-kartochki-svetloe", "light", "Accounting", Tab.History, card: false, note: true);
        Shot(folder, "4-kartochka-zashchita-tyomnoe", "dark", "Documents", Tab.History, card: true);

        // The task editor, page by page.
        EditorShot(folder, "5-zadanie-osnovnoe-svetloe", "light", "Documents", Strings.PageBasic, height: 900);
        EditorShot(folder, "6-zadanie-naznacheniya-tyomnoe", "dark", "Documents", Strings.PageDestinations);
        EditorShot(folder, "7-zadanie-raspisanie-svetloe", "light", "Accounting", Strings.PageSchedule);
        EditorShot(folder, "8-zadanie-istochniki-tyomnoe", "dark", "Documents", Strings.PageSources, height: 900);
        EditorShot(folder, "9-zadanie-hranenie-svetloe", "light", "Accounting", Strings.PageRetention, height: 900);
        EditorShot(folder, "10-zadanie-sobytiya-tyomnoe", "dark", "Accounting", Strings.PageEvents);
        EditorShot(folder, "11-zadanie-szhatie-svetloe", "light", "Accounting", Strings.PageArchive);

        // The windows of the editor pages.
        DialogShot(folder, "14-okno-istochnik-svetloe", "light", c => new SourceDialog(c, Task("Documents").Sources[1]));
        DialogShot(folder, "15-okno-naznachenie-sftp-tyomnoe", "dark", c => new DestinationDialog(c, Task("Documents").Destinations[2], isNew: false));
        DialogShot(folder, "16-okno-naznachenie-worm-svetloe", "light", c => new DestinationDialog(c, Task("Documents").Destinations[1], isNew: false));
        DialogShot(folder, "17-okno-sobytie-svetloe", "light", c => new EventDialog(Task("Accounting").Events.Post[1], isNew: false));
        DialogShot(folder, "18-okno-uchetnaya-zapis-tyomnoe", "dark", c => new CredentialDialog(c.Monitor, null, Custos.Core.Model.CredentialKinds.Windows));
        DialogShot(folder, "19-okno-baza-dannyh-svetloe", "light", c => DbServer(c));
        DialogShot(folder, "20-okno-linux-server-svetloe", "light", c => new RemoteSourceDialog(c, new Custos.Core.Model.TaskSource()), height: 1100);

        // The windows opened from the main window.
        WindowShot(folder, "21-okno-nastroek-svetloe", "light", vm => new SettingsDialog(vm.Monitor), height: 1000);
        WindowShot(folder, "22-vosstanovlenie-tyomnoe", "dark", vm => Restore(vm), height: 1000);
        WindowShot(folder, "23-zhurnal-zapuska-svetloe", "light", vm => RunLog(vm));
        WindowShot(folder, "24-otschyot-vyklyucheniya-tyomnoe", "dark", vm => new PowerWindow(vm.Monitor, new Custos.Core.Ipc.PowerCountdown
        {
            Action = Custos.Core.Model.AfterActions.Shutdown,
            TaskName = "Documents",
            Deadline = DateTimeOffset.UtcNow.AddSeconds(42.5),
        }) { Done = true });
        string cobian = CobianListFile();
        WindowShot(folder, "25-import-cobian-svetloe", "light", vm => CobianImport(vm, cobian), height: 900);

        // Every colour scheme of the «Настройки» menu, light and dark.
        string colors = Path.Combine(folder, "colors");
        Directory.CreateDirectory(colors);
        foreach (string id in Palettes.Ids)
        {
            foreach (string theme in (string[])["light", "dark"])
            {
                Shot(colors, $"{id}-{theme}", theme, "Documents", Tab.Log, card: true, color: id);
                EditorShot(colors, $"{id}-{theme}-zadanie", theme, "Documents", Strings.PageDestinations, color: id);
            }
        }

        bool cobianWorks = CobianImportWorks(cobian);
        File.Delete(cobian);
        if (!MenuWorks() | !EditorWorks() | !SelectionWorks() | !ToolbarWorks(folder) | !cobianWorks)
        {
            return 1;
        }

        if (IconData.Missing.Count > 0)
        {
            Console.Error.WriteLine("Icons missing from IconData.g.cs (add them to tools/make-icons.py): " + string.Join(", ", IconData.Missing));
            return 1;
        }

        return 0;
    }

    private enum Tab
    {
        Log,
        History,
        Progress,
    }

    private static void Shot(string folder, string name, string theme, string task, Tab tab, bool card, bool note = false, string color = Palettes.Default)
    {
        var window = App.CreateMainWindow(demo: true);
        var vm = window.ViewModel;
        vm.SetDemoTheme(theme);
        vm.ColorChoice = color;
        window.Show();
        vm.Start();
        Pump();

        vm.SelectedTask = vm.Tasks.First(t => t.Name == task);
        vm.IsLogTab = tab == Tab.Log;
        vm.IsHistoryTab = tab == Tab.History;
        vm.IsProgressTab = tab == Tab.Progress;
        vm.ShowCard = card;
        if (note)
        {
            vm.Snack(Strings.ToolAdminsOnly);
        }

        Pump();

        string path = Path.Combine(folder, name + ".png");
        Save(window, path);
        Console.WriteLine(path);
        Close(window);
    }

    private static void EditorShot(string folder, string name, string theme, string task, string page, double height = 0, string color = Palettes.Default)
    {
        var window = App.CreateMainWindow(demo: true);
        var vm = window.ViewModel;
        vm.SetDemoTheme(theme);
        vm.ColorChoice = color;
        window.Show();
        vm.Start();
        Pump();
        vm.SelectedTask = vm.Tasks.First(t => t.Name == task);
        var model = new EditorModel(vm.Monitor, vm.SelectedDefinition!, isNew: false, demo: true);
        model.Open(page);
        var editor = new EditorWindow(model);
        if (height > 0)
        {
            editor.Height = height;
        }

        editor.Show(window);
        Pump();

        string path = Path.Combine(folder, name + ".png");
        Save(editor, path);
        Console.WriteLine(path);
        editor.Close();
        Close(window);
    }

    /// <summary>A task of the made-up ones, as the window shows it.</summary>
    private static Custos.Core.Model.TaskDefinition Task(string name) => DemoData.Snapshot().Tasks.First(t => t.Name == name);

    /// <summary>A database server as found by «Проверить подключение».</summary>
    private static DbServerDialog DbServer(EditorContext context)
    {
        var dialog = new DbServerDialog(context, new Custos.Core.Model.TaskDatabaseServer
        {
            Engine = Custos.Core.Model.DbEngines.MariaDb,
            BinDir = @"C:\Program Files\MariaDB 11.4\bin",
            Host = "127.0.0.1",
        }, isNew: true);
        dialog.ShowTest(new Custos.Core.Ipc.DbTestResult
        {
            Ok = true,
            Version = "11.4.5-MariaDB",
            Databases = ["crm", "shop", "wiki"],
            DataDir = @"C:\Program Files\MariaDB 11.4\data",
        });
        return dialog;
    }

    /// <summary>A window opened from the main window with the made-up tasks.</summary>
    private static void WindowShot(string folder, string name, string theme, Func<MainViewModel, Window> make, double height = 0)
    {
        var window = App.CreateMainWindow(demo: true);
        var vm = window.ViewModel;
        vm.SetDemoTheme(theme);
        window.Show();
        vm.Start();
        Pump();
        vm.SelectedTask = vm.Tasks.First(t => t.Name == "Documents");
        Pump();
        var dialog = make(vm);
        if (height > 0)
        {
            dialog.SizeToContent = SizeToContent.Manual;
            dialog.Height = height;
        }

        dialog.Show(window);
        Pump();
        string path = Path.Combine(folder, name + ".png");
        Save(dialog, path);
        Console.WriteLine(path);
        dialog.Close();
        Close(window);
    }

    /// <summary>«Восстановить» with three sets found in the folder of «Documents».</summary>
    private static RestoreDialog Restore(MainViewModel vm)
    {
        var dialog = new RestoreDialog(vm.Monitor, vm.SelectedTask?.TaskId);
        var result = new Custos.Core.Ipc.RestoreListResult();
        var when = DemoData.Now.AddHours(-2);
        foreach (var (type, files, bytes) in new[] { ("incremental", 214L, 48_300_000L), ("differential", 1_380L, 512_000_000L), ("full", 18_412L, 9_640_000_000L) })
        {
            result.Sets.Add(new Custos.Core.Ipc.RestoreSetInfo
            {
                Location = @"E:\Backup\Documents\" + when.ToString("yyyy-MM-dd_HHmm", CultureInfo.InvariantCulture),
                Where = @"E:\Backup\Documents",
                Header = new Custos.Core.Backup.SetManifestHeader
                {
                    SetName = when.ToString("yyyy-MM-dd_HHmm", CultureInfo.InvariantCulture),
                    TaskName = "Documents",
                    BackupType = type,
                    StartedAt = when,
                    SetFileCount = files,
                    SetBytes = bytes,
                },
            });
            when = when.AddDays(-1);
        }

        dialog.ShowSets(result);
        return dialog;
    }

    /// <summary>The log of the last run of «Desktop».</summary>
    private static RunLogWindow RunLog(MainViewModel vm)
    {
        string id = vm.Tasks.First(t => t.Name == "Desktop").TaskId;
        var run = DemoData.Snapshot().States.First(s => s.TaskId == id).LastRun!;
        var log = new RunLogWindow(null, run);
        var lines = DemoData.Log().Where(l => l.TaskId == id).ToList();
        log.ShowResult(new Custos.Core.Ipc.RunLogResult
        {
            Found = true,
            Path = @"C:\ProgramData\Custos\logs\runs\2026-11\" + run.RunId + ".log",
            Lines = lines,
            Total = lines.Count,
            Matched = lines.Count,
            RetentionDays = 365,
        });
        return log;
    }

    /// <summary>A Cobian Backup 11 list with two tasks, one of them run as an account of the network.</summary>
    private static string CobianListFile()
    {
        static string Bits(double value) => Convert.ToString(BitConverter.DoubleToInt64Bits(value), 2).PadLeft(64, '0');
        string evening = Bits(45292 + (21.5 / 24));
        string path = Path.Combine(Path.GetTempPath(), "custos-shots-" + Guid.NewGuid().ToString("N") + ".lst");
        File.WriteAllText(path, $$"""
            {11//
            Name=Documents
            Id={00000000-1111-2222-3333-444444444444}
            Enabled=true
            Include subdirectories=true
            Create separated backups=true
            Use VSC=true
            Backup type=1
            Destination="1,\\server\backup\docs"
            Destination="1,\serverackup\docs"
            Schedule type=1
            Date/Time={{evening}}
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
            Date/Time={{evening}}
            //11}
            """, System.Text.Encoding.UTF8);
        return path;
    }

    private static CobianImportDialog CobianImport(MainViewModel vm, string list)
    {
        var dialog = new CobianImportDialog(vm.Monitor);
        dialog.ReadFile(list);
        return dialog;
    }

    /// <summary>
    /// The Cobian import lists both tasks ticked and asks for the password of the account once; without the task
    /// that uses the account it is not asked for.
    /// </summary>
    private static bool CobianImportWorks(string list)
    {
        var window = App.CreateMainWindow(demo: true);
        var vm = window.ViewModel;
        window.Show();
        vm.Start();
        Pump();
        var dialog = CobianImport(vm, list);
        dialog.Show(window);
        Pump();
        var problems = new List<string>();
        if (dialog.TaskItems.Count != 2 || dialog.TaskItems.Any(t => !t.On))
        {
            problems.Add($"{dialog.TaskItems.Count} tasks read, not 2 ticked");
        }

        if (dialog.AccountCount != 1)
        {
            problems.Add($"{dialog.AccountCount} accounts asked for, not 1");
        }

        string remark = CobianImportDialog.Remark(new Custos.Core.Cobian.ImportWarning(Custos.Core.Cobian.ImportWarningCodes.RunAsAccount, @"EXAMPLE\backup-user"));
        if (dialog.TaskItems.Count > 0 && dialog.TaskItems[0].Description?.Contains(remark, StringComparison.Ordinal) != true)
        {
            problems.Add("the remark of the account is not under the task: " + dialog.TaskItems[0].Description);
        }

        if (dialog.TaskItems.Count > 0)
        {
            dialog.TaskItems[0].On = false;
            Pump();
            if (dialog.AccountCount != 0)
            {
                problems.Add("the account is still asked for without its task");
            }

            dialog.TaskItems[0].On = true;
            Pump();
            if (dialog.AccountCount != 1)
            {
                problems.Add("the account is not asked for again");
            }
        }

        dialog.Close();
        Close(window);
        Console.WriteLine(problems.Count == 0 ? "Cobian import: OK" : "Cobian import: " + string.Join("; ", problems));
        return problems.Count == 0;
    }

    /// <summary>A window of the editor over the editor of «Documents».</summary>
    private static void DialogShot(string folder, string name, string theme, Func<EditorContext, FormDialog> make, double height = 0)
    {
        var window = App.CreateMainWindow(demo: true);
        var vm = window.ViewModel;
        vm.SetDemoTheme(theme);
        window.Show();
        vm.Start();
        Pump();
        vm.SelectedTask = vm.Tasks.First(t => t.Name == "Documents");
        var model = new EditorModel(vm.Monitor, vm.SelectedDefinition!, isNew: false, demo: true);
        var editor = new EditorWindow(model);
        editor.Show(window);
        Pump();
        var dialog = make(model.Context);
        if (height > 0)
        {
            dialog.SizeToContent = SizeToContent.Manual;
            dialog.Height = height;
        }

        dialog.Show(editor);
        Pump();
        string path = Path.Combine(folder, name + ".png");
        Save(dialog, path);
        Console.WriteLine(path);
        dialog.Close();
        editor.Close();
        Close(window);
    }

    /// <summary>
    /// «Изменить» opens the editor with the 9 pages; «Добавить назначение» opens its window; an empty name keeps the
    /// editor open with the reason under the page; «Сохранить» with a new name closes it and the task list shows the
    /// new name (made-up tasks: the window keeps the change itself).
    /// </summary>
    private static bool EditorWorks()
    {
        var window = App.CreateMainWindow(demo: true);
        var vm = window.ViewModel;
        window.Show();
        vm.Start();
        Pump();
        vm.SelectedTask = vm.Tasks.First(t => t.Name == "Documents");
        Pump();
        var edit = window.GetVisualDescendants().OfType<ToolButton>().First(b => b.IconKind == "edit");
        edit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();
        if (window.OwnedWindows.OfType<EditorWindow>().FirstOrDefault() is not { } editor)
        {
            Console.WriteLine("Editor: «Изменить» did not open it.");
            Close(window);
            return false;
        }

        bool rightTask = editor.Model.Title.Contains("Documents", StringComparison.Ordinal) && editor.Model.Pages.Count == 9;
        editor.Model.Open(Strings.PageDestinations);
        Pump();
        Click(editor, Strings.DestinationAddTitle);
        var opened = editor.OwnedWindows.OfType<DestinationDialog>().FirstOrDefault();
        bool dialog = opened is not null;
        opened?.Close();
        Pump();

        editor.Model.Open(Strings.PageBasic);
        Pump();
        var name = editor.GetVisualDescendants().OfType<TextBox>().First(t => t.Text == "Documents");
        name.Text = string.Empty;
        Click(editor, DesktopStrings.Save);
        bool refused = editor.IsVisible && editor.Model.HasProblem;
        name.Text = "Documents 2";
        Click(editor, DesktopStrings.Save);
        Pump();
        bool closed = !editor.IsVisible && window.OwnedWindows.Count == 0;
        bool renamed = vm.Tasks.Any(t => t.Name == "Documents 2");
        if (!OperatingSystem.IsWindows() && editor.Model.Problem?.Contains(@"\\nas", StringComparison.Ordinal) == true)
        {
            // The made-up tasks have Windows paths, which are not full paths elsewhere: the check stops at them.
            closed = renamed = true;
            editor.Close();
        }

        bool ok = rightTask && dialog && refused && closed && renamed;
        Console.WriteLine(ok ? "Editor: opens with the task and its 9 pages, opens the window of a destination, refuses an empty name, saves a new one."
            : $"Editor: task={rightTask}, destination window={dialog}, empty name refused={refused}, closed={closed}, renamed={renamed}, problem={editor.Model.Problem}.");
        Close(window);
        return ok;
    }

    /// <summary>Presses the button with this text in a window.</summary>
    private static void Click(Window window, string text)
    {
        var button = window.GetVisualDescendants().OfType<Button>()
            .First(b => b.IsEffectivelyVisible && b.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == text));
        if (button.Command is { } command)
        {
            command.Execute(button.CommandParameter);
        }
        else
        {
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }

        Pump();
    }

    /// <summary>A click on an item of the «Настройки» menu does what it says and closes the menu.</summary>
    private static bool MenuWorks()
    {
        var window = App.CreateMainWindow(demo: true);
        window.Show();
        window.ViewModel.Start();
        Pump();
        var settings = window.GetVisualDescendants().OfType<ToolButton>().First(b => b.IconKind == "settings");
        var flyout = (Flyout)settings.Flyout!;
        flyout.ShowAt(settings);
        Pump();
        var dark = window.FindControl<StackPanel>("ThemeMenu")!.Children.OfType<RadioButton>().ElementAt(2);
        dark.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();
        bool ok = !flyout.IsOpen && window.ViewModel.ThemeNow == "dark" && window.ActualThemeVariant == ThemeVariant.Dark;
        Console.WriteLine(ok ? "Menu: the item works and the menu closes." : $"Menu: open={flyout.IsOpen}, theme={window.ViewModel.ThemeNow}, variant={window.ActualThemeVariant}.");

        // A colour of the menu puts its scheme in place at once.
        flyout.ShowAt(settings);
        Pump();
        var sepia = window.FindControl<StackPanel>("ColorMenu")!.Children.OfType<RadioButton>().ElementAt(Palettes.Ids.ToList().IndexOf("sepia"));
        sepia.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();
        var primary = window.FindResource(ThemeVariant.Dark, "Primary") as ISolidColorBrush;
        bool painted = !flyout.IsOpen && window.ViewModel.ColorChoice == "sepia" && primary?.Color == Palettes.Sample("sepia", dark: true).Primary;
        Console.WriteLine(painted ? "Menu: a colour puts its scheme in place." : $"Menu: colour={window.ViewModel.ColorChoice}, primary={primary?.Color}, open={flyout.IsOpen}.");
        ok &= painted;
        Close(window);
        return ok;
    }

    /// <summary>
    /// Several tasks selected with Ctrl: one task to edit is no longer there, the switch says «Включить» while one of
    /// them is off, and the selection stays when the list is sorted anew.
    /// </summary>
    private static bool SelectionWorks()
    {
        var window = App.CreateMainWindow(demo: true);
        var vm = window.ViewModel;
        window.Show();
        vm.Start();
        Pump();
        var list = window.FindControl<ListBox>("TaskList")!;
        vm.SelectedTask = vm.Tasks.First(t => t.Name == "Accounting");
        Pump();
        bool one = vm.Selection.Count == 1 && list.SelectedItems?.Count == 1 && vm.CanEditOne && vm.ToggleText == Strings.ToolDisable;
        list.Selection.Select(vm.Tasks.IndexOf(vm.Tasks.First(t => t.Name == "Old archive")));
        Pump();
        bool two = vm.Selection.Count == 2 && !vm.CanEditOne && vm.CanToggle && vm.CanDelete && vm.ToggleText == Strings.ToolEnable;
        vm.SortBy(3);
        Pump();
        bool kept = vm.Selection.Count == 2 && list.SelectedItems?.Count == 2;
        // A click without Ctrl selects that task alone again.
        list.SelectedIndex = vm.Tasks.IndexOf(vm.Tasks.First(t => t.Name == "Accounting"));
        Pump();
        bool alone = vm.Selection.Count == 1 && list.SelectedItems?.Count == 1 && vm.SelectedTask?.Name == "Accounting" && vm.CanEditOne;
        bool ok = one && two && kept && alone;
        Console.WriteLine(ok ? "Selection: several tasks with Ctrl, «Изменить» for one only, «Включить» while one is off, kept when sorted, a click selects one again."
            : $"Selection: one={one}, two={two}, kept after sorting={kept}, one again after a click={alone}.");
        Close(window);
        return ok;
    }

    /// <summary>
    /// Every button of the toolbar fits: the window is not made narrower than that. On a screen of 1024 points the
    /// window still fits: «Клонировать» and «Восстановить» move into «⋮ Ещё».
    /// </summary>
    private static bool ToolbarWorks(string folder)
    {
        var window = App.CreateMainWindow(demo: true);
        var vm = window.ViewModel;
        vm.ColorChoice = Palettes.Default;
        window.Show();
        vm.Start();
        Pump();
        window.Refit();
        Pump();
        var settings = window.FindControl<Button>("SettingsButton")!;
        bool Inside() => settings.TranslatePoint(new Point(settings.Bounds.Width, 0), window) is { } p && p.X <= window.Bounds.Width + 0.5;
        bool Shown(string name) => window.FindControl<Control>(name)!.IsVisible;
        double minimum = window.MinWidth;
        bool full = minimum is > 1000 and < 1200 && Shown("CloneButton") && Shown("RestoreButton") && !Shown("MoreButton") && Inside();
        window.Width = 900;
        Pump();
        bool kept = window.Bounds.Width >= minimum - 0.5 && Inside();

        window.ScreenWidth = 1024;
        window.Refit();
        window.Width = 1024 - 16;
        Pump();
        window.Refit();
        Pump();
        bool narrow = window.MinWidth <= 1008 && !Shown("CloneButton") && !Shown("RestoreButton") && Shown("MoreButton") && Inside();
        Save(window, Path.Combine(folder, "13-uzkiy-ekran-svetloe.png"));
        bool ok = full && kept && narrow;
        Console.WriteLine(ok ? $"Toolbar: every button fits ({minimum:0} points at least); on a screen of 1024 two go into «Ещё»."
            : $"Toolbar: full={full} (minimum {minimum:0}), kept={kept}, narrow screen={narrow} (minimum {window.MinWidth:0}).");
        Close(window);
        return ok;
    }

    /// <summary>The window closes for real (the program quits) and its connection goes with it.</summary>
    private static void Close(MainWindow window)
    {
        window.Quitting = true;
        window.Close();
        window.ViewModel.Dispose();
        Pump();
    }

    private static void Pump()
    {
        for (int i = 0; i < 6; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static void Save(Window window, string path)
    {
        var size = new PixelSize((int)Math.Round(window.Bounds.Width * Scale), (int)Math.Round(window.Bounds.Height * Scale));
        using var bitmap = new RenderTargetBitmap(size, new Vector(96 * Scale, 96 * Scale));
        bitmap.Render(window);
        bitmap.Save(path, PngBitmapEncoderOptions.Default);
    }
}
