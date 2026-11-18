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

namespace Custos.Preview.Shots;

/// <summary>
/// Pictures of the trial window with the made-up tasks, in Russian, at twice the size (as on a 200 % screen). The time
/// and the light on the progress bar are fixed, so the pictures are the same on every run.
/// </summary>
internal static class Program
{
    private const double Scale = 2;

    public static int Main(string[] args)
    {
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

        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .With(new FontManagerOptions { DefaultFamilyName = Fonts.Roboto })
            .SetupWithoutStarting();

        Console.WriteLine($"Icons: {IconData.CheckAll()} parse.");

        Shot(folder, "1-glavnoe-okno-svetloe", "light", "Photos", Tab.Log, card: true);
        Shot(folder, "2-glavnoe-okno-tyomnoe", "dark", "Desktop", Tab.Progress, card: true);
        Shot(folder, "3-istoriya-bez-kartochki-svetloe", "light", "Accounting", Tab.History, card: false, note: true);
        Shot(folder, "4-kartochka-zashchita-tyomnoe", "dark", "Documents", Tab.History, card: true);

        if (!MenuWorks())
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

    private static void Shot(string folder, string name, string theme, string task, Tab tab, bool card, bool note = false)
    {
        var window = App.CreateMainWindow(demo: true);
        var vm = window.ViewModel;
        vm.ThemeChoice = theme;
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
            vm.ReadOnlyNote();
        }

        Pump();

        string path = Path.Combine(folder, name + ".png");
        Save(window, path);
        Console.WriteLine(path);
        window.Close();
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
        var dark = ((Panel)flyout.Content!).Children.OfType<Button>().First(b => Equals(b.Tag, "dark"));
        dark.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();
        bool ok = !flyout.IsOpen && window.ViewModel.ThemeChoice == "dark" && window.ActualThemeVariant == ThemeVariant.Dark;
        Console.WriteLine(ok ? "Menu: the item works and the menu closes." : $"Menu: open={flyout.IsOpen}, theme={window.ViewModel.ThemeChoice}, variant={window.ActualThemeVariant}.");
        window.Close();
        Pump();
        return ok;
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
