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

    /// <summary>The services (not the drivers) as the registry lists them: readable by every user.</summary>
    public override IReadOnlyList<string> ServiceNames()
    {
        try
        {
            using var services = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
            var names = new List<string>();
            foreach (string name in services?.GetSubKeyNames() ?? [])
            {
                using var key = services!.OpenSubKey(name);
                if (key?.GetValue("Type") is int type && (type & 0x30) != 0 && key.GetValue("ImagePath") is not null)
                {
                    names.Add(name);
                }
            }

            return [.. names.Order(StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            // The list is only a help: the name can be typed.
            return [];
        }
    }
}
