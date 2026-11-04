namespace Custos.Gui;

/// <summary>Icons embedded from the Resources folder (drawn by tools/make-icons.py).</summary>
internal static class AppIcons
{
    /// <summary>The program icon: a blue shield, with all its sizes (Windows picks one per place).</summary>
    public static Icon App() => Load("custos.ico", Size.Empty);

    /// <summary>Tray icon: the service is running and all is well.</summary>
    public static Icon TrayOk() => Load("tray-ok.ico", SystemInformation.SmallIconSize);

    /// <summary>Tray icon: a task is running.</summary>
    public static Icon TrayRunning() => Load("tray-running.ico", SystemInformation.SmallIconSize);

    /// <summary>Tray icon: the last run of a task ended with warnings.</summary>
    public static Icon TrayWarning() => Load("tray-warning.ico", SystemInformation.SmallIconSize);

    /// <summary>Tray icon: the last run of a task failed.</summary>
    public static Icon TrayError() => Load("tray-error.ico", SystemInformation.SmallIconSize);

    /// <summary>Tray icon: no connection to the service (grey shield).</summary>
    public static Icon TrayDown() => Load("tray-down.ico", SystemInformation.SmallIconSize);

    private static Icon Load(string name, Size size)
    {
        using var stream = typeof(AppIcons).Assembly.GetManifestResourceStream("Custos.Gui.Resources." + name)
            ?? throw new InvalidOperationException($"Embedded icon {name} is missing.");
        return size.IsEmpty ? new Icon(stream) : new Icon(stream, size);
    }
}
