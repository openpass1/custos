using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Custos.Core.Ipc;
using Custos.Core.Model;
using Custos.Gui;

namespace Custos.Desktop;

/// <summary>
/// The interface process: the tray icon with its menu, the connection to the service and the main window
/// (docs/client-ui.md, section 4), as TrayContext of Custos.exe. Closing the window keeps the tray icon; «Закрыть
/// интерфейс» ends the process. The service keeps running either way.
/// </summary>
/// <remarks>
/// A user who is not an administrator gets what the setting «Интерфейс для пользователей» says: the full interface,
/// only the state (the icon and its tooltip), or no icon at all. Without an answer from the service what it said last
/// stays (<see cref="LastKnown"/>), so a user without the icon does not get it back while the service starts.
/// </remarks>
internal sealed partial class App : Application, IDisposable
{
    /// <summary>Windows cuts the tooltip of a tray icon at 127 characters.</summary>
    private const int MaxTooltip = 120;

    private readonly LastKnown _lastKnown = LastKnown.Load();
    private MainWindow? _window;
    private StatusWindow? _statusWindow;
    private PowerWindow? _powerWindow;
    private TrayIcon? _tray;
    private NativeMenuItem? _status;
    private NativeMenuItem? _open;
    private NativeMenuItem? _runAll;
    private NativeMenuItem? _stopAll;
    private NativeMenuItem? _close;
    private NativeMenuItemSeparator? _commandsSeparator;
    private NativeMenuItem? _serviceStart;
    private NativeMenuItem? _serviceStop;
    private NativeMenuItem? _serviceRestart;
    private NativeMenuItemSeparator? _serviceSeparator;
    private TrayState? _shownState;
    private string _mode;

    public App()
    {
        _mode = DesktopPlatform.Current.KeepsWindowInTray ? _lastKnown.TrayMode : TrayModes.Full;
    }

    /// <summary>Made-up tasks instead of the service (the «--demo» switch, and the pictures).</summary>
    public static bool Demo { get; set; }

    /// <summary>Only the tray icon at the start (<c>--tray</c>: at logon).</summary>
    public static bool StartHidden { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var platform = DesktopPlatform.Current;
            bool keepInTray = platform.KeepsWindowInTray;
            desktop.ShutdownMode = keepInTray ? ShutdownMode.OnExplicitShutdown : ShutdownMode.OnMainWindowClose;
            var window = CreateMainWindow(Demo, _lastKnown.Theme);
            _window = window;
            var monitor = window.ViewModel.Monitor;
            monitor.AskPassword = () => AskPasswordAsync(monitor);
            if (platform.UsesTray)
            {
                monitor.StatusChanged += (_, _) => OnAgentChanged();
                monitor.SnapshotChanged += (_, _) => OnAgentChanged();
                monitor.ProgressChanged += (_, _) => UpdateTray();
                desktop.ShutdownRequested += (_, _) =>
                {
                    if (_window is { } current)
                    {
                        current.Quitting = true;
                    }
                };
                CreateTray();
            }
            if (!keepInTray)
            {
                UseMainWindow(desktop, window);
            }

            window.ViewModel.Start();
            UpdateTray();
            if (keepInTray && !StartHidden)
            {
                ShowWindow();
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Builds new controls in the selected language while keeping the view model and its agent session.</summary>
    public void RefreshInterfaceLanguage()
    {
        if (_window is not { } old || ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            return;
        }

        old.SaveLayout();
        var model = old.ViewModel;
        InterfaceLanguage.Apply(model.Ui.InterfaceLanguage);
        model.RefreshLanguage();
        var window = new MainWindow(model);
        UseMainWindow(desktop, window);
        window.Show();
        window.ShowSelection();
        old.CloseForLanguageChange();
        window.Activate();
        RelabelTray();
        UpdateTray();
    }

    private void UseMainWindow(IClassicDesktopStyleApplicationLifetime desktop, MainWindow window)
    {
        _window = window;
        desktop.MainWindow = window;
        window.Closed += (_, _) =>
        {
            if (_window == window)
            {
                window.ViewModel.Dispose();
            }
        };
    }

    /// <summary>A second copy was started in this session: it asks this one to show its window.</summary>
    public static void Activate() => Dispatcher.UIThread.Post(() => (Current as App)?.ShowWindow());

    public void Dispose() => _tray?.Dispose();

    private void OnAgentChanged()
    {
        OnServiceChanged();
        UpdatePower();
    }

    /// <summary>The window with its connection to the service, or with the made-up tasks.</summary>
    public static MainWindow CreateMainWindow(bool demo, string? knownTheme = null)
    {
        // ServiceMonitor raises its events on the UI thread it was made on.
        AvaloniaSynchronizationContext.InstallIfNeeded();
        return new MainWindow(new MainViewModel(new ServiceMonitor(), demo, knownTheme));
    }

    /// <summary>The state of the icon and the text of its tooltip, as in Custos.exe.</summary>
    internal static (TrayState State, string Text) Describe(ServiceState service, AgentSnapshot? snapshot, RunProgressEvent? progress)
    {
        if (service != ServiceState.Running || snapshot is null)
        {
            return (TrayState.Down, ServiceMonitor.Describe(service));
        }

        if (snapshot.Current is { } current)
        {
            double percent = progress?.RunId == current.RunId ? progress.Progress.Percent : current.Progress?.Percent ?? 0;
            return (TrayState.Running, Strings.Format(Strings.TrayRunning, current.TaskName, (int)Math.Floor(percent)));
        }

        // The worst last result among the enabled tasks.
        if (Worst(snapshot, RunStatuses.Error) is { } failed)
        {
            return (TrayState.Error, Strings.Format(Strings.TrayError, failed));
        }

        return (Worst(snapshot, RunStatuses.Warning) ?? Worst(snapshot, RunStatuses.Aborted)) is { } warned
            ? (TrayState.Warning, Strings.Format(Strings.TrayWarning, warned))
            : (TrayState.Ok, Strings.TrayOk);
    }

    private static string? Worst(AgentSnapshot snapshot, string status) =>
        snapshot.Tasks.Where(t => t.Enabled)
            .FirstOrDefault(t => snapshot.States.FirstOrDefault(s => string.Equals(s.TaskId, t.Id, StringComparison.OrdinalIgnoreCase))?.LastRun?.Status == status)
            ?.Name;

    private void CreateTray()
    {
        var platform = DesktopPlatform.Current;
        _status = new NativeMenuItem { Header = Strings.AppTitle, IsEnabled = false };
        _open = new NativeMenuItem { Header = Strings.MenuOpen };
        _open.Click += (_, _) => ShowWindow();
        _runAll = new NativeMenuItem { Header = Strings.MenuRunAll };
        _runAll.Click += (_, _) => Safe.Run(RunAllAsync, Problem);
        _stopAll = new NativeMenuItem { Header = Strings.MenuStopAll };
        _stopAll.Click += (_, _) => Safe.Run(() => _window!.ViewModel.Monitor.CommandAsync(PipeProtocol.Methods.RunStopAll), Problem);
        _commandsSeparator = new NativeMenuItemSeparator();
        _serviceStart = new NativeMenuItem { Header = Strings.MenuServiceStart };
        _serviceStart.Click += (_, _) => ControlServiceFromTray(SystemServiceAction.Start);
        _serviceStop = new NativeMenuItem { Header = Strings.MenuServiceStop };
        _serviceStop.Click += (_, _) => ControlServiceFromTray(SystemServiceAction.Stop);
        _serviceRestart = new NativeMenuItem { Header = DesktopStrings.SystemServiceRestart };
        _serviceRestart.Click += (_, _) => ControlServiceFromTray(SystemServiceAction.Restart);
        _serviceSeparator = new NativeMenuItemSeparator();
        _close = new NativeMenuItem { Header = Strings.MenuClose };
        _close.Click += (_, _) => Quit();

        var menu = new NativeMenu();
        menu.Items.Add(_status);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(_open);
        menu.Items.Add(_runAll);
        menu.Items.Add(_stopAll);
        menu.Items.Add(_commandsSeparator);
        menu.Items.Add(_serviceStart);
        menu.Items.Add(_serviceStop);
        menu.Items.Add(_serviceRestart);
        menu.Items.Add(_serviceSeparator);
        menu.Items.Add(_close);
        menu.Opening += (_, _) => UpdateServiceItems();

        _tray = new TrayIcon
        {
            Icon = platform.TrayIcon(TrayState.Down),
            ToolTipText = Strings.AppTitle,
            Menu = menu,
            IsVisible = _mode != TrayModes.Hidden && platform.TrayIcon(TrayState.Down) is not null,
        };
        _tray.Clicked += (_, _) => ShowWindow();
        TrayIcon.SetIcons(this, [_tray]);
    }

    /// <summary>
    /// What the service says about this user's interface and the theme: kept for the next start, the icon and its
    /// menu follow the mode (an administrator always gets everything).
    /// </summary>
    private void OnServiceChanged()
    {
        var monitor = _window!.ViewModel.Monitor;
        if (monitor.IsConnected && monitor.Snapshot is { } snapshot)
        {
            string mode = !DesktopPlatform.Current.KeepsWindowInTray || monitor.Status.Hello?.IsAdmin == true || !TrayModes.IsKnown(snapshot.Settings.TrayMode)
                ? TrayModes.Full : snapshot.Settings.TrayMode;
            string theme = Themes.IsKnown(snapshot.Settings.Theme) ? snapshot.Settings.Theme : Themes.Auto;
            if (mode != _lastKnown.TrayMode || theme != _lastKnown.Theme)
            {
                _lastKnown.TrayMode = mode;
                _lastKnown.Theme = theme;
                _lastKnown.Save();
            }

            if (mode != _mode)
            {
                _mode = mode;
                if (mode != TrayModes.Full && _window.IsVisible)
                {
                    _window.Hide();
                }

                if (mode == TrayModes.Full && _statusWindow is { IsVisible: true } status)
                {
                    status.Hide();
                }
            }
        }

        UpdateTray();
    }

    private void UpdateTray()
    {
        if (_window is not { } window || _tray is null)
        {
            return;
        }

        var monitor = window.ViewModel.Monitor;
        var (state, text) = window.ViewModel.IsDemo ? (TrayState.Ok, Strings.TrayOk) : Describe(monitor.Status.State, monitor.Snapshot, monitor.Progress);
        if (state != _shownState)
        {
            _tray.Icon = DesktopPlatform.Current.TrayIcon(state);
            _shownState = state;
        }

        string tooltip = Strings.Format(Strings.TrayTooltip, text);
        if (tooltip.Length > MaxTooltip)
        {
            tooltip = tooltip[..(MaxTooltip - 1)] + "…";
        }

        _status!.Header = tooltip;
        _tray.ToolTipText = tooltip;

        bool full = _mode == TrayModes.Full;
        _tray.IsVisible = _mode != TrayModes.Hidden && _tray.Icon is not null;
        // «Состояние копий» in the status-only mode opens the window of the task list without buttons.
        _open!.Header = full ? Strings.MenuOpen : Strings.MenuOpenStatus;
        _runAll!.IsVisible = full;
        _stopAll!.IsVisible = full;
        _commandsSeparator!.IsVisible = full;
        bool systemService = full && window.ViewModel.ShowsSystemServiceActions;
        bool service = systemService || (full && DesktopPlatform.Current.ControlsService);
        _serviceStart!.IsVisible = service;
        _serviceStop!.IsVisible = service;
        _serviceRestart!.IsVisible = systemService;
        _serviceSeparator!.IsVisible = service;

        var snapshot = monitor.Snapshot;
        var current = snapshot?.Current;
        _runAll.IsEnabled = monitor.IsConnected && snapshot?.Tasks.Any(t => t.Enabled) == true;
        _stopAll.IsEnabled = monitor.IsConnected && ((current is not null && !current.Stopping) || snapshot?.Queue.Count > 0);
    }

    private void RelabelTray()
    {
        if (_tray is null)
        {
            return;
        }

        _runAll!.Header = Strings.MenuRunAll;
        _stopAll!.Header = Strings.MenuStopAll;
        _serviceStart!.Header = Strings.MenuServiceStart;
        _serviceStop!.Header = Strings.MenuServiceStop;
        _serviceRestart!.Header = DesktopStrings.SystemServiceRestart;
        _close!.Header = Strings.MenuClose;
    }

    /// <summary>Shows the countdown to a shutdown or sleep after a run while the service counts, and closes it after.</summary>
    private void UpdatePower()
    {
        var monitor = _window!.ViewModel.Monitor;
        var power = monitor.IsConnected ? monitor.Snapshot?.Power : null;
        if (power is null)
        {
            if (_powerWindow is { } shown)
            {
                shown.Done = true;
                shown.Close();
                _powerWindow = null;
            }

            return;
        }

        if (_powerWindow is null)
        {
            _powerWindow = new PowerWindow(monitor, power);
            _powerWindow.Show();
        }
        else
        {
            _powerWindow.ShowCountdown(power);
        }
    }

    private void UpdateServiceItems()
    {
        if (DesktopPlatform.Current.ControlsSystemService)
        {
            bool available = _window?.ViewModel.CanControlSystemService == true;
            _serviceStart!.IsEnabled = _serviceStop!.IsEnabled = _serviceRestart!.IsEnabled = available;
            return;
        }

        bool? running = DesktopPlatform.Current.ServiceRunning();
        _serviceStart!.IsEnabled = running == false;
        _serviceStop!.IsEnabled = running == true;
    }

    private void ControlServiceFromTray(SystemServiceAction action)
    {
        var platform = DesktopPlatform.Current;
        if (platform.ControlsSystemService && _window is { } window && window.ViewModel.CanControlSystemService)
        {
            ShowWindow();
            Safe.Run(() => window.Actions.ControlSystemServiceAsync(action), Problem);
        }
        else if (platform.ControlsService && action == SystemServiceAction.Start)
        {
            platform.ControlService(start: true);
        }
        else if (platform.ControlsService && action == SystemServiceAction.Stop)
        {
            Safe.Run(StopServiceAsync, Problem);
        }
    }

    private async Task RunAllAsync()
    {
        var result = await _window!.ViewModel.Monitor.CommandAsync(PipeProtocol.Methods.RunAll);
        if (!result.Ok)
        {
            Problem(new ServiceUnavailableException(result.Message ?? Strings.NothingQueued));
        }
    }

    /// <summary>Asks first: while the service is stopped, nothing runs, not even by the schedule.</summary>
    private async Task StopServiceAsync()
    {
        var monitor = _window!.ViewModel.Monitor;
        string text = Strings.ServiceStopConfirm;
        if (monitor.IsConnected && monitor.Snapshot?.Current is { } current)
        {
            text += Environment.NewLine + Environment.NewLine + Strings.Format(Strings.ServiceStopRunning, current.TaskName);
        }

        ShowWindow();
        if (await Dialogs.Ask(_window, text, Strings.MenuServiceStop))
        {
            DesktopPlatform.Current.ControlService(start: false);
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

    /// <summary>
    /// The main window of an administrator or of a user with the full interface; a user with «Только состояние» or
    /// «Скрыт» gets the window with the state of the backups (without an icon: when the program is started again).
    /// </summary>
    private void ShowWindow()
    {
        if (_window is not { } window)
        {
            return;
        }

        if (_mode != TrayModes.Full)
        {
            _statusWindow ??= new StatusWindow(window.ViewModel);
            _statusWindow.Show();
            _statusWindow.Activate();
            return;
        }

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
            if (_statusWindow is { } status)
            {
                status.Quitting = true;
                status.Close();
            }

            if (_powerWindow is { } power)
            {
                power.Done = true;
                power.Close();
            }

            window.Close();
            if (DesktopPlatform.Current.KeepsWindowInTray)
            {
                window.ViewModel.Dispose();
            }
        }

        if (_tray is not null)
        {
            _tray.IsVisible = false;
        }

        if (DesktopPlatform.Current.KeepsWindowInTray && ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }
}
