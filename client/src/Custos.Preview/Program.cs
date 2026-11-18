using Avalonia;
using Avalonia.Media;

namespace Custos.Preview;

internal static class Program
{
    /// <summary>
    /// CustosPreview.exe [--demo]: the trial window. Without the switch it shows the tasks of the CUSTOS service on this
    /// computer; with «--demo» it shows made-up tasks and needs no service.
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        App.Demo = args.Any(a => a.Equals("--demo", StringComparison.OrdinalIgnoreCase) || a.Equals("/demo", StringComparison.OrdinalIgnoreCase));
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>Also used by the XAML previewer of an IDE.</summary>
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .With(new FontManagerOptions { DefaultFamilyName = Fonts.Roboto })
        .LogToTrace();
}

/// <summary>The font of the window: Roboto, carried inside the program (Assets/Fonts, Apache 2.0).</summary>
internal static class Fonts
{
    public const string Roboto = "avares://CustosPreview/Assets/Fonts#Roboto";
}
