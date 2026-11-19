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

        if (!MenuWorks() | !EditorWorks() | !SelectionWorks() | !ToolbarWorks(folder))
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
            vm.Snack(DesktopStrings.NotYet);
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
