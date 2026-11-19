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
        PreviewSettings.Enabled = false;

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

        // The task editor, page by page.
        EditorShot(folder, "5-zadanie-osnovnoe-svetloe", "light", "Documents", Strings.PageBasic, height: 900);
        EditorShot(folder, "6-zadanie-naznacheniya-tyomnoe", "dark", "Documents", Strings.PageDestinations);
        EditorShot(folder, "7-zadanie-raspisanie-svetloe", "light", "Accounting", Strings.PageSchedule);
        EditorShot(folder, "8-zadanie-istochniki-tyomnoe", "dark", "Documents", Strings.PageSources, height: 900);
        EditorShot(folder, "9-zadanie-hranenie-svetloe", "light", "Accounting", Strings.PageRetention, height: 900);
        EditorShot(folder, "10-zadanie-sobytiya-tyomnoe", "dark", "Accounting", Strings.PageEvents);
        EditorShot(folder, "11-zadanie-szhatie-svetloe", "light", "Accounting", Strings.PageArchive);

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

        if (!MenuWorks() || !EditorWorks())
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
        vm.ThemeChoice = theme;
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
            vm.ReadOnlyNote();
        }

        Pump();

        string path = Path.Combine(folder, name + ".png");
        Save(window, path);
        Console.WriteLine(path);
        window.Close();
        Pump();
    }

    private static void EditorShot(string folder, string name, string theme, string task, string page, double height = 0, string color = Palettes.Default)
    {
        var window = App.CreateMainWindow(demo: true);
        var vm = window.ViewModel;
        vm.ThemeChoice = theme;
        vm.ColorChoice = color;
        window.Show();
        vm.Start();
        Pump();
        vm.SelectedTask = vm.Tasks.First(t => t.Name == task);
        var model = vm.EditSelected()!;
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
        window.Close();
        Pump();
    }

    /// <summary>
    /// «Изменить» opens the editor; a button on a page only says that the trial window does not do it; «Сохранить» closes
    /// the editor and the main window says that nothing was kept.
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
            window.Close();
            Pump();
            return false;
        }

        bool rightTask = editor.Model.Title.Contains("Documents", StringComparison.Ordinal) && editor.Model.Pages.Count == 8;
        editor.Model.Open(Strings.PageDestinations);
        Pump();
        var add = editor.GetVisualDescendants().OfType<Button>().First(b => b.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == Strings.DestinationAddTitle));
        add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();
        bool snack = editor.FindControl<Panel>("Snack")!.IsVisible;
        var save = editor.GetVisualDescendants().OfType<Button>().First(b => b.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == Strings.PreviewSave));
        save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();
        bool closed = !editor.IsVisible && window.OwnedWindows.Count == 0;
        bool told = vm.SnackVisible && vm.SnackText == Strings.PreviewNotSaved;
        bool ok = rightTask && snack && closed && told;
        Console.WriteLine(ok ? "Editor: opens with the task, page buttons only say so, «Сохранить» closes it and keeps nothing."
            : $"Editor: task={rightTask}, page button note={snack}, closed={closed}, main window note={told}.");
        window.Close();
        Pump();
        return ok;
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

        // A colour of the menu puts its scheme in place at once.
        flyout.ShowAt(settings);
        Pump();
        var teal = window.FindControl<StackPanel>("ColorMenu")!.Children.OfType<RadioButton>().ElementAt(Palettes.Ids.ToList().IndexOf("teal"));
        teal.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();
        var primary = window.FindResource(ThemeVariant.Dark, "Primary") as ISolidColorBrush;
        bool painted = !flyout.IsOpen && window.ViewModel.ColorChoice == "teal" && primary?.Color == Palettes.Sample("teal", dark: true).Primary;
        Console.WriteLine(painted ? "Menu: a colour puts its scheme in place." : $"Menu: colour={window.ViewModel.ColorChoice}, primary={primary?.Color}, open={flyout.IsOpen}.");
        ok &= painted;
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
