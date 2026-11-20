using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using Custos.Core.Ipc;
using Custos.Core.Model;
using Custos.Gui;

namespace Custos.Mac.Gui;

/// <summary>
/// The program of the user: the icon in the menu bar (the tray of Windows) and the main window. Closing the window
/// leaves the icon; «Завершить CUSTOS» in its menu quits both. The service goes on either way.
/// </summary>
internal sealed partial class App : Application, IDisposable
{
    private const int MaxStatusText = 120;

    private readonly Dictionary<MenuBarState, WindowIcon> _icons = [];
    private MainWindow? _window;
    private TrayIcon? _tray;
    private NativeMenuItem? _status;
    private NativeMenuItem? _open;
    private NativeMenuItem? _runAll;
    private NativeMenuItem? _stopAll;
    private NativeMenuItemSeparator? _commandsSeparator;
    private MenuBarState? _shownState;

    /// <summary>The state the icon shows, as the tray icons of Windows.</summary>
    internal enum MenuBarState
    {
        Ok,
        Running,
        Warning,
        Error,
        Down,
    }

    /// <summary>Made-up tasks instead of the service (the «--demo» switch, and the screenshots).</summary>
    public static bool Demo { get; set; }

    /// <summary>Only the icon in the menu bar at the start (<c>--tray</c>: at login, after an update).</summary>
    public static bool StartHidden { get; set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        // The colour scheme of this user (Settings → Look), the neutral grey one until another is picked.
        Palettes.Apply(this, UiState.Load().Color);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = CreateMainWindow(Demo);
            _window = window;
            var monitor = window.ViewModel.Monitor;
            monitor.AskPassword = () => AskPasswordAsync(monitor);
            monitor.StatusChanged += (_, _) => UpdateMenuBar();
            monitor.SnapshotChanged += (_, _) => UpdateMenuBar();
            monitor.ProgressChanged += (_, _) => UpdateMenuBar();
            CreateMenuBar();

            // ⌘Q and «Завершить» of the Dock: the window must really close then.
            desktop.ShutdownRequested += (_, _) => window.Quitting = true;
            window.Closed += (_, _) => MacDock.Show(false);
            window.PropertyChanged += (_, e) =>
            {
                if (e.Property == Visual.IsVisibleProperty && e.NewValue is false)
                {
                    MacDock.Show(false);
                }
            };

            // A click on CUSTOS in the Finder or the Launchpad while it runs only in the menu bar.
            if (TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime activatable)
            {
                activatable.Activated += (_, e) =>
                {
                    if (e.Kind == ActivationKind.Reopen)
                    {
                        ShowWindow();
                    }
                };
            }

            window.ViewModel.Start();
            UpdateMenuBar();
            if (!StartHidden)
            {
                ShowWindow();
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    public void Dispose() => _tray?.Dispose();

    /// <summary>The window with its connection to the service, or with the made-up tasks.</summary>
    public static MainWindow CreateMainWindow(bool demo)
    {
        // ServiceMonitor raises its events on the UI thread it was made on.
        AvaloniaSynchronizationContext.InstallIfNeeded();
        return new MainWindow(new MainViewModel(new ServiceMonitor(), demo));
    }

    /// <summary>The state of the icon and the text of the first line of its menu (the tooltip of the tray icon of Windows).</summary>
    internal static (MenuBarState State, string Text) Describe(ServiceMonitor monitor) =>
        Describe(monitor.Status.State, monitor.Snapshot, monitor.Progress);

    internal static (MenuBarState State, string Text) Describe(ServiceState service, AgentSnapshot? snapshot, RunProgressEvent? progress)
    {
        if (service != ServiceState.Running || snapshot is null)
        {
            return (MenuBarState.Down, ServiceMonitor.Describe(service));
        }

        if (snapshot.Current is { } current)
        {
            double percent = progress?.RunId == current.RunId ? progress.Progress.Percent : current.Progress?.Percent ?? 0;
            return (MenuBarState.Running, Strings.Format(Strings.TrayRunning, current.TaskName, (int)Math.Floor(percent)));
        }

        // The worst last result among the enabled tasks.
        if (Worst(snapshot, RunStatuses.Error) is { } failed)
        {
            return (MenuBarState.Error, Strings.Format(Strings.TrayError, failed));
        }

        return (Worst(snapshot, RunStatuses.Warning) ?? Worst(snapshot, RunStatuses.Aborted)) is { } warned
            ? (MenuBarState.Warning, Strings.Format(Strings.TrayWarning, warned))
            : (MenuBarState.Ok, Strings.TrayOk);
    }

    private static string? Worst(AgentSnapshot snapshot, string status) =>
        snapshot.Tasks.Where(t => t.Enabled)
            .FirstOrDefault(t => snapshot.States.FirstOrDefault(s => string.Equals(s.TaskId, t.Id, StringComparison.OrdinalIgnoreCase))?.LastRun?.Status == status)
            ?.Name;

    private static WindowIcon LoadIcon(string name)
    {
        using var stream = AssetLoader.Open(new Uri($"avares://Custos/Assets/tray-{name}.png"));
        return new WindowIcon(stream);
    }

    private void CreateMainMenuBarItems(NativeMenu menu)
    {
        _status = new NativeMenuItem { Header = Strings.AppTitle, IsEnabled = false };
        _open = new NativeMenuItem { Header = Strings.MenuOpen };
        _open.Click += (_, _) => ShowWindow();
        _runAll = new NativeMenuItem { Header = Strings.MenuRunAll };
        _runAll.Click += (_, _) => Safe.Run(RunAllAsync, Problem);
        _stopAll = new NativeMenuItem { Header = Strings.MenuStopAll };
        _stopAll.Click += (_, _) => Safe.Run(() => _window!.ViewModel.Monitor.CommandAsync(PipeProtocol.Methods.RunStopAll), Problem);
        _commandsSeparator = new NativeMenuItemSeparator();
        var quit = new NativeMenuItem { Header = MacStrings.MenuQuit, ToolTip = MacStrings.MenuQuitNote };
        quit.Click += (_, _) => Quit();
        menu.Items.Add(_status);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(_open);
        menu.Items.Add(_runAll);
        menu.Items.Add(_stopAll);
        menu.Items.Add(_commandsSeparator);
        menu.Items.Add(quit);
    }

    private void CreateMenuBar()
    {
        foreach (var state in Enum.GetValues<MenuBarState>())
        {
            _icons[state] = LoadIcon(state.ToString().ToLowerInvariant());
        }

        var menu = new NativeMenu();
        CreateMainMenuBarItems(menu);
        _tray = new TrayIcon { Icon = _icons[MenuBarState.Down], ToolTipText = Strings.AppTitle, Menu = menu, IsVisible = true };
        TrayIcon.SetIcons(this, [_tray]);
    }

    /// <summary>The icon, the state line and which commands this user has (the «Интерфейс для пользователей» setting).</summary>
    private void UpdateMenuBar()
    {
        if (_window is not { } window || _tray is null)
        {
            return;
        }

        var monitor = window.ViewModel.Monitor;
        var (state, text) = Describe(monitor);
        if (state != _shownState)
        {
            _tray.Icon = _icons[state];
            _shownState = state;
        }

        string line = Strings.Format(Strings.TrayTooltip, text);
        if (line.Length > MaxStatusText)
        {
            line = line[..(MaxStatusText - 1)] + "…";
        }

        _status!.Header = line;
        _tray.ToolTipText = line;

        // An administrator gets everything; other users what the settings give them (Full, State only, Hidden).
        var snapshot = monitor.Snapshot;
        string mode = Demo || monitor.Status.Hello?.IsAdmin == true || snapshot is null || !TrayModes.IsKnown(snapshot.Settings.TrayMode)
            ? TrayModes.Full : snapshot.Settings.TrayMode;
        bool full = mode == TrayModes.Full;
        _tray.IsVisible = mode != TrayModes.Hidden;
        _open!.IsVisible = full;
        _runAll!.IsVisible = full;
        _stopAll!.IsVisible = full;
        _commandsSeparator!.IsVisible = full;

        var current = snapshot?.Current;
        _runAll.IsEnabled = monitor.IsConnected && snapshot?.Tasks.Any(t => t.Enabled) == true;
        _stopAll.IsEnabled = monitor.IsConnected && ((current is not null && !current.Stopping) || snapshot?.Queue.Count > 0);
    }

    private async Task RunAllAsync()
    {
        var result = await _window!.ViewModel.Monitor.CommandAsync(PipeProtocol.Methods.RunAll);
        if (!result.Ok)
        {
            Problem(new ServiceUnavailableException(result.Message ?? Strings.NothingQueued));
        }
    }

    /// <summary>What a command of the menu could not do: shown in the window, which comes up for it.</summary>
    private void Problem(Exception ex)
    {
        if (_window is { } window)
        {
            ShowWindow();
            window.ViewModel.Snack(ex.Message);
        }
    }

    private void ShowWindow()
    {
        if (_window is not { } window)
        {
            return;
        }

        MacDock.Show(true);
        window.Show();
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
    }

    /// <summary>The program password, over the main window (brought up for it when it was hidden).</summary>
    private async Task<bool> AskPasswordAsync(ServiceMonitor monitor)
    {
        if (_window is not { } window)
        {
            return false;
        }

        if (!window.IsVisible)
        {
            ShowWindow();
        }

        return await Dialogs.ProgramPassword(window, monitor);
    }

    private void Quit()
    {
        if (_window is { } window)
        {
            window.Quitting = true;
            window.ViewModel.Monitor.Lock();
            window.Close();
            window.ViewModel.Dispose();
        }

        if (_tray is not null)
        {
            _tray.IsVisible = false;
        }

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }
}
