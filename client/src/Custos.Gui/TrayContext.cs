using System.ServiceProcess;
using Custos.Core.Ipc;
using Custos.Core.Model;

namespace Custos.Gui;

/// <summary>
/// The interface process: the tray icon with its menu, the connection to the service and the main window
/// (docs/client-ui.md, section 4). Closing the window keeps the tray icon; "Close interface" ends the process. The
/// service keeps running either way; the menu can also start and stop it (with administrator rights).
/// </summary>
/// <remarks>
/// A user who is not an administrator gets what the setting «Интерфейс для пользователей» says (section 4): the full
/// interface, only the state (the icon and <see cref="StatusForm"/>), or no icon at all. The process runs in every
/// mode: the countdown before a shutdown must reach every session, and the Start menu opens the state window. The
/// theme of the settings applies to the windows; open ones are made anew when it changes.
/// </remarks>
internal sealed class TrayContext : ApplicationContext
{
    /// <summary>Windows cuts the tooltip of a tray icon at 127 characters.</summary>
    private const int MaxTooltip = 120;

    private readonly SynchronizationContext _ui;
    private readonly ServiceMonitor _monitor;
    private readonly NotifyIcon _trayIcon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _runAll;
    private readonly ToolStripMenuItem _stopAll;
    private readonly ToolStripMenuItem _serviceStart;
    private readonly ToolStripMenuItem _serviceStop;
    private readonly Font _boldFont;
    private readonly Bitmap _shield;
    private readonly Dictionary<TrayState, Icon> _icons = new()
    {
        [TrayState.Ok] = AppIcons.TrayOk(),
        [TrayState.Running] = AppIcons.TrayRunning(),
        [TrayState.Warning] = AppIcons.TrayWarning(),
        [TrayState.Error] = AppIcons.TrayError(),
        [TrayState.Down] = AppIcons.TrayDown(),
    };

    private readonly RegisteredWaitHandle _activateWait;
    private readonly ToolStripMenuItem _open;
    private readonly ToolStripItem[] _fullOnly;
    private readonly LastKnown _lastKnown;
    private MainForm? _mainForm;
    private StatusForm? _statusForm;
    private PowerCountdownForm? _powerForm;

    /// <summary>What this user gets of the interface now (<see cref="TrayModes"/>).</summary>
    private string _mode;

    /// <summary>The open windows are to be made anew in the new theme as soon as no dialog is open over them.</summary>
    private bool _recreatePending;

    /// <param name="activate">Set by a second copy started in the same session: show the window.</param>
    /// <param name="showWindow">Open the main window right away (started by the user, not at logon).</param>
    public TrayContext(EventWaitHandle activate, bool showWindow)
    {
        _ui = SynchronizationContext.Current
            ?? throw new InvalidOperationException("TrayContext must be created on the UI thread.");

        _lastKnown = LastKnown.Load();
        _mode = _lastKnown.TrayMode;
        _menu = new ContextMenuStrip();
        _boldFont = new Font(_menu.Font, FontStyle.Bold);
        var open = _open = new ToolStripMenuItem(Strings.MenuOpen, null, (_, _) => ShowWindow()) { Font = _boldFont };
        _runAll = new ToolStripMenuItem(Strings.MenuRunAll, null, (_, _) => RunAll());
        _stopAll = new ToolStripMenuItem(Strings.MenuStopAll, null, (_, _) => StopAll());

        // The shield of Windows says that the command asks for administrator rights.
        using (var shield = SystemIcons.GetStockIcon(StockIconId.Shield, StockIconOptions.SmallIcon))
        {
            _shield = shield.ToBitmap();
        }

        _serviceStart = new ToolStripMenuItem(Strings.MenuServiceStart, _shield, (_, _) => ControlService(start: true));
        _serviceStop = new ToolStripMenuItem(Strings.MenuServiceStop, _shield, (_, _) => ControlService(start: false));
        var close = new ToolStripMenuItem(Strings.MenuClose, null, (_, _) => ExitThread());
        var serviceSeparator = new ToolStripSeparator();
        _fullOnly = [_runAll, _stopAll, serviceSeparator, _serviceStart, _serviceStop];
        _menu.Items.AddRange([open, _runAll, _stopAll, serviceSeparator, _serviceStart, _serviceStop, new ToolStripSeparator(), close]);
        _menu.Opening += (_, _) => UpdateServiceItems();

        _trayIcon = new NotifyIcon { ContextMenuStrip = _menu };
        _trayIcon.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                ShowWindow();
            }
        };

        _monitor = new ServiceMonitor();
        _monitor.AskPassword = () => PasswordPromptForm.AskAsync(_monitor, Form.ActiveForm);
        _monitor.StatusChanged += (_, _) =>
        {
            ApplySettings();
            UpdateTray();
            UpdatePower();
        };
        _monitor.SnapshotChanged += (_, _) =>
        {
            ApplySettings();
            UpdateTray();
            UpdatePower();
            AnnounceUpdate();
        };
        _trayIcon.BalloonTipClicked += (_, _) => ShowWindow();
        _monitor.ProgressChanged += (_, _) => UpdateTray();
        Application.Idle += OnIdle;
        ShowMode();
        UpdateTray();
        _monitor.Start();

        _activateWait = ThreadPool.RegisterWaitForSingleObject(
            activate, (_, _) => _ui.Post(_ => ShowWindow(), null), null, Timeout.Infinite, executeOnlyOnce: false);
        if (showWindow)
        {
            ShowWindow();
        }
    }

    private enum TrayState
    {
        Ok,
        Running,
        Warning,
        Error,
        Down,
    }

    protected override void ExitThreadCore()
    {
        Application.Idle -= OnIdle;
        _activateWait.Unregister(null);
        _trayIcon.Visible = false;
        _powerForm?.Dispose();
        if (_mainForm is { IsDisposed: false })
        {
            _mainForm.SaveLayout();
            _mainForm.Dispose();
        }

        _statusForm?.Dispose();

        _monitor.Dispose();
        base.ExitThreadCore();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _trayIcon.Dispose();
            _menu.Dispose();
            _boldFont.Dispose();
            _shield.Dispose();
            foreach (var icon in _icons.Values)
            {
                icon.Dispose();
            }
        }

        base.Dispose(disposing);
    }

    /// <summary>The window of this user: the main window, or the state window when they get only the state.</summary>
    private void ShowWindow()
    {
        Form form;
        if (_mode == TrayModes.Full)
        {
            CloseStatusForm();
            form = _mainForm is { IsDisposed: false } main ? main : _mainForm = new MainForm(_monitor);
        }
        else
        {
            CloseMainForm();
            form = _statusForm is { IsDisposed: false } status ? status : _statusForm = new StatusForm(_monitor);
        }

        form.Show();
        if (form.WindowState == FormWindowState.Minimized)
        {
            form.WindowState = FormWindowState.Normal;
        }

        form.Activate();
    }

    private void CloseMainForm()
    {
        if (_mainForm is { IsDisposed: false } main)
        {
            main.SaveLayout();
            main.Dispose();
        }

        _mainForm = null;
    }

    private void CloseStatusForm()
    {
        _statusForm?.Dispose();
        _statusForm = null;
    }

    /// <summary>
    /// What the service says about this user's interface and the theme. Without an answer from the service what it
    /// said last stays (<see cref="LastKnown"/>): a user without the icon does not get it back while the service starts.
    /// </summary>
    private void ApplySettings()
    {
        if (!_monitor.IsConnected || _monitor.Snapshot is not { } snapshot)
        {
            return;
        }

        string mode = _monitor.Status.Hello?.IsAdmin == true || !TrayModes.IsKnown(snapshot.Settings.TrayMode)
            ? TrayModes.Full : snapshot.Settings.TrayMode;
        string theme = Themes.IsKnown(snapshot.Settings.Theme) ? snapshot.Settings.Theme : Themes.Auto;
        if (mode == _lastKnown.TrayMode && theme == _lastKnown.Theme)
        {
            return;
        }

        bool newTheme = theme != _lastKnown.Theme;
        _lastKnown.TrayMode = mode;
        _lastKnown.Theme = theme;
        _lastKnown.Save();
        if (mode != _mode)
        {
            _mode = mode;
            ShowMode();
        }

        if (newTheme)
        {
            Theme.Apply(theme);
        }

        // The open window changes kind or theme; while a dialog is open over it, that waits (OnIdle).
        _recreatePending = true;
        RecreateWindows();
    }

    /// <summary>The icon and its menu for the current mode.</summary>
    private void ShowMode()
    {
        bool full = _mode == TrayModes.Full;
        _open.Text = full ? Strings.MenuOpen : Strings.MenuOpenStatus;
        foreach (var item in _fullOnly)
        {
            item.Visible = full;
        }

        _trayIcon.Visible = _mode != TrayModes.Hidden;
    }

    private void OnIdle(object? sender, EventArgs e)
    {
        if (_recreatePending)
        {
            RecreateWindows();
        }
    }

    /// <summary>
    /// Makes the open windows anew, in the current theme and of the kind of the current mode, once nothing else is open
    /// over them: a dialog would lose what the user typed. Hidden windows are simply dropped; they are made when needed.
    /// </summary>
    private void RecreateWindows()
    {
        var ours = new HashSet<Form?> { _mainForm, _statusForm, _powerForm };
        if (Application.OpenForms.Cast<Form>().Any(f => !ours.Contains(f)))
        {
            return;
        }

        _recreatePending = false;
        bool visible = (_mainForm is { IsDisposed: false, Visible: true }) || (_statusForm is { IsDisposed: false, Visible: true });
        CloseMainForm();
        CloseStatusForm();
        if (_powerForm is not null)
        {
            _powerForm.Dispose();
            _powerForm = null;
            UpdatePower();
        }

        if (visible)
        {
            ShowWindow();
        }
    }

    /// <summary>Shows the countdown to a shutdown or sleep after a run while the service counts, and hides it after.</summary>
    /// <summary>
    /// A new version on GitHub is told once per version in the tray, to administrators only: only they can install it.
    /// </summary>
    private void AnnounceUpdate()
    {
        if (_monitor.Snapshot?.ProgramUpdate is not { Available: true, Latest: { } version }
            || _monitor.Status.Hello?.IsAdmin != true || !_trayIcon.Visible || version == _lastKnown.AnnouncedUpdate)
        {
            return;
        }

        _lastKnown.AnnouncedUpdate = version;
        _lastKnown.Save();
        _trayIcon.ShowBalloonTip(10_000, Strings.Format(Strings.UpdateBalloonTitle, version), Strings.UpdateBalloonText, ToolTipIcon.Info);
    }

    private void UpdatePower()
    {
        var power = _monitor.IsConnected ? _monitor.Snapshot?.Power : null;
        if (power is null)
        {
            if (_powerForm is not null)
            {
                _powerForm.Dispose();
                _powerForm = null;
            }

            return;
        }

        if (_powerForm is null || _powerForm.IsDisposed)
        {
            _powerForm = new PowerCountdownForm(_monitor, power);
            _powerForm.Show();
        }
        else
        {
            _powerForm.ShowCountdown(power);
        }
    }

    private void RunAll() => Ui.Run(null, async () =>
    {
        var result = await _monitor.CommandAsync(PipeProtocol.Methods.RunAll);
        if (!result.Ok)
        {
            Ui.ShowInfo(null, result.Message ?? Strings.NothingQueued);
        }
    });

    /// <summary>Empties the queue and stops the run in progress.</summary>
    private void StopAll() => Ui.Run(null, () => _monitor.CommandAsync(PipeProtocol.Methods.RunStopAll));

    /// <summary>What Windows says about the service decides which of the two commands is available.</summary>
    private void UpdateServiceItems()
    {
        var status = ServiceControl.Query();
        _serviceStart.Enabled = status == ServiceControllerStatus.Stopped;
        _serviceStop.Enabled = status == ServiceControllerStatus.Running;
    }

    private void ControlService(bool start)
    {
        if (!start)
        {
            string text = Strings.ServiceStopConfirm;
            if (_monitor.IsConnected && _monitor.Snapshot?.Current is { } current)
            {
                text += Environment.NewLine + Environment.NewLine + Strings.Format(Strings.ServiceStopRunning, current.TaskName);
            }

            if (!Ui.Confirm(null, text))
            {
                return;
            }
        }

        // The copy with administrator rights reports its own errors; the tray icon shows the new state by itself.
        ServiceControl.RequestElevated(start);
    }

    private void UpdateTray()
    {
        var status = _monitor.Status;
        var snapshot = _monitor.Snapshot;
        var current = snapshot?.Current;
        _runAll.Enabled = _monitor.IsConnected && snapshot?.Tasks.Any(t => t.Enabled) == true;
        _stopAll.Enabled = _monitor.IsConnected && ((current is not null && !current.Stopping) || snapshot?.Queue.Count > 0);

        TrayState state;
        string text;
        if (status.State != ServiceState.Running || snapshot is null)
        {
            state = TrayState.Down;
            text = ServiceMonitor.Describe(status.State);
        }
        else if (current is not null)
        {
            state = TrayState.Running;
            var progress = _monitor.Progress;
            double percent = progress?.RunId == current.RunId ? progress.Progress.Percent : current.Progress?.Percent ?? 0;
            text = Strings.Format(Strings.TrayRunning, current.TaskName, (int)Math.Floor(percent));
        }
        else
        {
            // The worst last result among the enabled tasks.
            var failed = Worst(snapshot, RunStatuses.Error);
            var warned = failed is null ? Worst(snapshot, RunStatuses.Warning) ?? Worst(snapshot, RunStatuses.Aborted) : null;
            (state, text) = failed is not null ? (TrayState.Error, Strings.Format(Strings.TrayError, failed))
                : warned is not null ? (TrayState.Warning, Strings.Format(Strings.TrayWarning, warned))
                : (TrayState.Ok, Strings.TrayOk);
        }

        string tooltip = Strings.Format(Strings.TrayTooltip, text);
        if (tooltip.Length > MaxTooltip)
        {
            tooltip = tooltip[..(MaxTooltip - 1)] + "…";
        }

        var icon = _icons[state];
        if (_trayIcon.Icon != icon)
        {
            _trayIcon.Icon = icon;
        }

        if (_trayIcon.Text != tooltip)
        {
            _trayIcon.Text = tooltip;
        }
    }

    private static string? Worst(AgentSnapshot snapshot, string status) =>
        snapshot.Tasks.Where(t => t.Enabled)
            .FirstOrDefault(t => snapshot.States.FirstOrDefault(s => string.Equals(s.TaskId, t.Id, StringComparison.OrdinalIgnoreCase))?.LastRun?.Status == status)
            ?.Name;
}
