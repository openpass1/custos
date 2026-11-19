using System.ServiceProcess;
using Avalonia.Controls;
using Avalonia.Platform;
using Custos.Desktop;

namespace Custos.Windows;

/// <summary>What Windows gives the windows of CUSTOS: the tray icons of Custos.exe and the control of the service.</summary>
internal sealed class WindowsPlatform : DesktopPlatform
{
    private readonly Dictionary<TrayState, WindowIcon> _icons = [];

    public override WindowIcon? TrayIcon(TrayState state)
    {
        if (!_icons.TryGetValue(state, out var icon))
        {
            using var stream = AssetLoader.Open(new Uri($"avares://CustosNew/Assets/tray-{state.ToString().ToLowerInvariant()}.ico"));
            icon = _icons[state] = new WindowIcon(stream);
        }

        return icon;
    }

    public override bool ControlsService => true;

    public override bool? ServiceRunning() => ServiceControl.Query() switch
    {
        ServiceControllerStatus.Running => true,
        ServiceControllerStatus.Stopped => false,
        _ => null,
    };

    // The copy with administrator rights reports its own errors; the tray icon shows the new state by itself.
    public override void ControlService(bool start) => ServiceControl.RequestElevated(start);
}
