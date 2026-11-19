using Avalonia;
using Avalonia.Media;
using Custos.Gui;

namespace Custos.Mac.Gui;

/// <summary>The window and the icon in the menu bar.</summary>
internal static class GuiProgram
{
    /// <summary><c>Custos [--demo] [--tray]</c>: <c>--demo</c> shows made-up tasks without the service (for the
    /// pictures of CI); <c>--tray</c> starts with the icon in the menu bar only (after an update, at login).</summary>
    public static int Run(string[] args)
    {
        App.Demo = args.Contains("--demo", StringComparer.Ordinal);
        App.StartHidden = args.Contains("--tray", StringComparer.Ordinal);
        if (OperatingSystem.IsMacOS())
        {
            // The window size and the columns of this user, where Mac programs keep theirs (not ~/.config).
            UiState.Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "CUSTOS");
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
    }

    /// <summary>Also used by the XAML previewer of an IDE.</summary>
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .With(new FontManagerOptions { DefaultFamilyName = Fonts.Roboto })
        .With(new MacOSPlatformOptions { ShowInDock = !App.StartHidden })
        .LogToTrace();
}

/// <summary>The font of the window: Roboto, carried inside the program (Assets/Fonts, Apache 2.0).</summary>
internal static class Fonts
{
    public const string Roboto = "avares://Custos/Assets/Fonts#Roboto";
}
