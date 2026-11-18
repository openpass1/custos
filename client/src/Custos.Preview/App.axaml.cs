using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Custos.Gui;

namespace Custos.Preview;

internal sealed partial class App : Application
{
    /// <summary>Made-up tasks instead of the service (the «--demo» switch, and the screenshots).</summary>
    public static bool Demo { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = CreateMainWindow(Demo);
            desktop.MainWindow = window;
            window.ViewModel.Start();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>The window with its connection to the service, or with the made-up tasks.</summary>
    public static MainWindow CreateMainWindow(bool demo)
    {
        // ServiceMonitor raises its events on the UI thread it was made on.
        AvaloniaSynchronizationContext.InstallIfNeeded();
        return new MainWindow(new MainViewModel(new ServiceMonitor(), demo));
    }
}
