using Avalonia.Controls;
using Custos.Gui;

namespace Custos.Desktop;

/// <summary>The states the tray icon (the menu bar icon on macOS) shows, as the tray icons of Custos.exe.</summary>
internal enum TrayState
{
    Ok,
    Running,
    Warning,
    Error,
    Down,
}

/// <summary>
/// What the windows need from the operating system: set by the program (CustosNew.exe on Windows) before the window
/// opens. This base class does nothing, which is what the pictures of CI and the tests need.
/// </summary>
internal class DesktopPlatform
{
    public static DesktopPlatform Current { get; set; } = new();

    /// <summary>The icon of the tray for a state; null = no icon at all.</summary>
    public virtual WindowIcon? TrayIcon(TrayState state) => null;

    /// <summary>The name of the theme that follows the system: «Как в Windows».</summary>
    public virtual string ThemeAuto => Strings.ThemeAuto;

    /// <summary>The tray menu has «Запустить службу» and «Остановить службу» (Windows: with administrator rights).</summary>
    public virtual bool ControlsService => false;

    /// <summary>What the system says about the service: true = running, false = stopped, null = in between or unknown.</summary>
    public virtual bool? ServiceRunning() => null;

    /// <summary>Starts or stops the service the way the system allows it (the UAC prompt of Windows).</summary>
    public virtual void ControlService(bool start)
    {
    }

    /// <summary>The names of the services of this computer, for an event that stops or starts one.</summary>
    public virtual IReadOnlyList<string> ServiceNames() => [];
}

/// <summary>The font of the windows: Roboto, carried inside the program (Assets/Fonts, Apache 2.0).</summary>
internal static class Fonts
{
    public const string Roboto = "avares://Custos.Desktop/Assets/Fonts#Roboto";
}
