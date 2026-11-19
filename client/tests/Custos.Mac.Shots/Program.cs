using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Custos.Core.Model;
using Custos.Gui;
using Custos.Mac.Gui;

namespace Custos.Mac.Shots;

/// <summary>
/// Pictures of every window of CUSTOS for macOS with the made-up tasks, in Russian, at twice the size (as on a Retina
/// screen). The time and the light on the progress bar are fixed, so the pictures are the same on every run. Any
/// exception while a window opens fails the run: that is the test of the windows on a machine without a screen.
/// </summary>
internal static class Program
{
    private const double Scale = 2;
    private static readonly List<string> Failures = [];
    private static string _folder = string.Empty;

    public static int Main(string[] args)
    {
        _folder = Path.GetFullPath(args.Length > 0 ? args[0] : "shots");
        Directory.CreateDirectory(_folder);

        var russian = CultureInfo.GetCultureInfo("ru-RU");
        CultureInfo.DefaultThreadCurrentCulture = russian;
        CultureInfo.DefaultThreadCurrentUICulture = russian;
        CultureInfo.CurrentCulture = russian;
        CultureInfo.CurrentUICulture = russian;
        ProgressLine.FixedPhase = 0.55;
        ProgressModel.FixedNow = DemoData.Now.AddMinutes(4).AddSeconds(31);
        Describe.FixedToday = DemoData.Now.LocalDateTime.Date;

        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .With(new FontManagerOptions { DefaultFamilyName = Fonts.Roboto })
            .SetupWithoutStarting();

        Console.WriteLine($"Icons: {IconData.CheckAll()} parse.");

        Step("main window, light", () => MainShot("01-glavnoe-okno-svetloe", "light", "Photos", Tab.Log, card: true));
        Step("main window, dark", () => MainShot("02-glavnoe-okno-tyomnoe", "dark", "Desktop", Tab.Progress, card: true));
        Step("history without the card", () => MainShot("03-istoriya-bez-kartochki", "light", "Accounting", Tab.History, card: false));
        Step("task editor", Editor);
        Step("destinations", Destinations);
        Step("settings", Settings);
        Step("restore", Restore);
        Step("run log", RunLog);
        Step("menu bar", MenuBar);

        if (IconData.Missing.Count > 0)
        {
            Failures.Add("icons missing from IconData.g.cs (add them to tools/make-symbols.py): " + string.Join(", ", IconData.Missing));
        }

        foreach (string failure in Failures)
        {
            Console.Error.WriteLine("FAILED: " + failure);
        }

        Console.WriteLine(Failures.Count == 0 ? "All windows opened." : $"{Failures.Count} failed.");
        return Failures.Count == 0 ? 0 : 1;
    }

    private enum Tab
    {
        Log,
        History,
        Progress,
    }

    private static void Step(string name, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Failures.Add($"{name}: {ex}");
        }
    }

    private static MainWindow Open(string theme = "light")
    {
        var window = App.CreateMainWindow(demo: true);
        window.ViewModel.ThemeChoice = theme;
        window.Show();
        window.ViewModel.Start();
        Pump();
        return window;
    }

    private static void Close(MainWindow window)
    {
        window.Quitting = true;
        window.Close();
        Pump();
    }

    private static void MainShot(string name, string theme, string task, Tab tab, bool card)
    {
        var window = Open(theme);
        var vm = window.ViewModel;
        vm.SelectedTask = vm.Tasks.First(t => t.Name == task);
        vm.IsLogTab = tab == Tab.Log;
        vm.IsHistoryTab = tab == Tab.History;
        vm.IsProgressTab = tab == Tab.Progress;
        vm.ShowCard = card;
        Pump();
        Save(window, name);
        Close(window);
    }

    /// <summary>Every page of the editor of a task with three destinations.</summary>
    private static void Editor()
    {
        var main = Open();
        var task = main.ViewModel.Monitor.Snapshot!.Tasks.First(t => t.Name == "Documents").Clone();
        var editor = new TaskEditorWindow(main.ViewModel.Monitor, task, isNew: false);
        editor.Show(main);
        Pump();
        var nav = Nav(editor);
        int count = nav.ItemCount;
        if (count != 8)
        {
            Failures.Add($"task editor: {count} pages instead of 8");
        }

        for (int i = 0; i < count; i++)
        {
            nav.SelectedIndex = i;
            Pump();
            Save(editor, $"10-redaktor-{i + 1}");
        }

        editor.Close();
        Close(main);
    }

    /// <summary>The destination dialog for a network folder, an SFTP server and S3.</summary>
    private static void Destinations()
    {
        var main = Open();
        var task = main.ViewModel.Monitor.Snapshot!.Tasks.First(t => t.Name == "Documents").Clone();
        var context = new EditorContext(main, main.ViewModel.Monitor, task);
        var cases = new (string Name, TaskDestination Destination)[]
        {
            ("20-naznachenie-smb", task.Destinations.First(d => d.Kind == DestinationKinds.Unc)),
            ("21-naznachenie-sftp", task.Destinations.First(d => d.Kind == DestinationKinds.Sftp)),
            ("22-naznachenie-disk", task.Destinations.First(d => d.Kind == DestinationKinds.Usb)),
            ("23-naznachenie-s3", new TaskDestination { Kind = DestinationKinds.S3, Endpoint = "https://s3.example.org", Bucket = "office-backup", Region = "eu-central-1", ObjectLockDays = 30 }),
            ("24-naznachenie-webdav", new TaskDestination { Kind = DestinationKinds.WebDav, Host = "https://cloud.example.org/remote.php/dav/files/backup", Path = "office" }),
        };
        foreach (var (name, destination) in cases)
        {
            var shown = DestinationDialog.Show(context, destination);
            Pump();
            if (shown.IsFaulted)
            {
                throw shown.Exception!;
            }

            var dialog = main.OwnedWindows[^1];
            Save(dialog, name);
            dialog.Close();
            Pump();
        }

        Close(main);
    }

    private static void Settings()
    {
        var main = Open();
        var settings = new SettingsWindow(main.ViewModel.Monitor);
        settings.Show(main);
        Pump();
        var nav = Nav(settings);
        for (int i = 0; i < nav.ItemCount; i++)
        {
            nav.SelectedIndex = i;
            Pump();
            Save(settings, $"30-nastrojki-{i + 1}");
        }

        settings.Close();
        Close(main);
    }

    private static void Restore()
    {
        var main = Open();
        var restore = new RestoreWindow(main.ViewModel.Monitor, main.ViewModel.Monitor.Snapshot!.Tasks[0].Id);
        restore.Show(main);
        Pump();
        Save(restore, "40-vosstanovlenie");
        restore.Close();
        Close(main);
    }

    /// <summary>The run log window: without a service it says so in its note instead of failing.</summary>
    private static void RunLog()
    {
        var main = Open();
        var shown = RunLogWindow.ShowAsync(main, main.ViewModel.Monitor, "run-1", "Documents", DemoData.Now);
        Pump();
        if (shown.IsFaulted)
        {
            throw shown.Exception!;
        }

        var log = main.OwnedWindows[^1];
        Save(log, "41-zhurnal-zapuska");
        log.Close();
        Close(main);
    }

    /// <summary>The state of the icon in the menu bar for the made-up tasks: a run is in progress.</summary>
    private static void MenuBar()
    {
        var main = Open();
        var (state, text) = App.Describe(main.ViewModel.Monitor);
        Console.WriteLine($"Menu bar: {state}, «{text}».");
        if (state != App.MenuBarState.Down)
        {
            // The demo has no service connection: the icon must say so, as the tray of Windows does.
            Failures.Add($"menu bar: {state} without a service connection");
        }

        Close(main);
    }

    private static ListBox Nav(Window window) =>
        window.GetVisualDescendants().OfType<ListBox>().First(l => l.Theme == Ui.Theme("NavList"));

    private static void Pump()
    {
        for (int i = 0; i < 6; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static void Save(Window window, string name)
    {
        string path = Path.Combine(_folder, name + ".png");
        var size = new PixelSize((int)Math.Round(window.Bounds.Width * Scale), (int)Math.Round(window.Bounds.Height * Scale));
        using var bitmap = new RenderTargetBitmap(size, new Vector(96 * Scale, 96 * Scale));
        bitmap.Render(window);
        bitmap.Save(path, PngBitmapEncoderOptions.Default);
        Console.WriteLine(path);
    }
}
