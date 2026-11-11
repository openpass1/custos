using Custos.Core.Ipc;
using Custos.Core.Model;

namespace Custos.Gui;

/// <summary>
/// The interface process: the tray icon with its menu, the connection to the service and the main window
/// (docs/client-ui.md, section 4). Closing the window keeps the tray icon; "Close interface" ends the process. The
/// service keeps running either way.
/// </summary>
internal sealed class TrayContext : ApplicationContext
{
    /// <summary>Windows cuts the tooltip of a tray icon at 127 characters.</summary>
    private const int MaxTooltip = 120;

    private readonly SynchronizationContext _ui;
    private readonly ServiceMonitor _monitor;
    private readonly NotifyIcon _trayIcon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _runAll;
    private readonly ToolStripMenuItem _stopCurrent;
    private readonly Font _boldFont;
    private readonly Dictionary<TrayState, Icon> _icons = new()
    {
        [TrayState.Ok] = AppIcons.TrayOk(),
        [TrayState.Running] = AppIcons.TrayRunning(),
        [TrayState.Warning] = AppIcons.TrayWarning(),
        [TrayState.Error] = AppIcons.TrayError(),
        [TrayState.Down] = AppIcons.TrayDown(),
    };

    private readonly RegisteredWaitHandle _activateWait;
    private MainForm? _mainForm;
    private PowerCountdownForm? _powerForm;

    /// <param name="activate">Set by a second copy started in the same session: show the window.</param>
    /// <param name="showWindow">Open the main window right away (started by the user, not at logon).</param>
    public TrayContext(EventWaitHandle activate, bool showWindow)
    {
        _ui = SynchronizationContext.Current
            ?? throw new InvalidOperationException("TrayContext must be created on the UI thread.");

        _menu = new ContextMenuStrip();
        _boldFont = new Font(_menu.Font, FontStyle.Bold);
        var open = new ToolStripMenuItem(Strings.MenuOpen, null, (_, _) => ShowMainForm()) { Font = _boldFont };
        _runAll = new ToolStripMenuItem(Strings.MenuRunAll, null, (_, _) => RunAll());
        _stopCurrent = new ToolStripMenuItem(Strings.MenuStopCurrent, null, (_, _) => StopCurrent());
        var close = new ToolStripMenuItem(Strings.MenuClose, null, (_, _) => ExitThread())
        {
            ShortcutKeyDisplayString = Strings.MenuCloseHint,
        };
        _menu.Items.AddRange([open, _runAll, _stopCurrent, new ToolStripSeparator(), close]);

        _trayIcon = new NotifyIcon { ContextMenuStrip = _menu };
        _trayIcon.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                ShowMainForm();
            }
        };

        _monitor = new ServiceMonitor();
        _monitor.StatusChanged += (_, _) =>
        {
            UpdateTray();
            UpdatePower();
        };
        _monitor.SnapshotChanged += (_, _) =>
        {
            UpdateTray();
            UpdatePower();
        };
        _monitor.ProgressChanged += (_, _) => UpdateTray();
        UpdateTray();
        _trayIcon.Visible = true;
        _monitor.Start();

        _activateWait = ThreadPool.RegisterWaitForSingleObject(
            activate, (_, _) => _ui.Post(_ => ShowMainForm(), null), null, Timeout.Infinite, executeOnlyOnce: false);
        if (showWindow)
        {
            ShowMainForm();
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
        _activateWait.Unregister(null);
        _trayIcon.Visible = false;
        _powerForm?.Dispose();
        if (_mainForm is { IsDisposed: false })
        {
            _mainForm.SaveLayout();
            _mainForm.Dispose();
        }

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
            foreach (var icon in _icons.Values)
            {
                icon.Dispose();
            }
        }

        base.Dispose(disposing);
    }

    private void ShowMainForm()
    {
        if (_mainForm is null || _mainForm.IsDisposed)
        {
            _mainForm = new MainForm(_monitor);
        }

        _mainForm.Show();
        if (_mainForm.WindowState == FormWindowState.Minimized)
        {
            _mainForm.WindowState = FormWindowState.Normal;
        }

        _mainForm.Activate();
    }

    /// <summary>Shows the countdown to a shutdown or sleep after a run while the service counts, and hides it after.</summary>
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

    private void StopCurrent()
    {
        string? taskId = _monitor.Snapshot?.Current?.TaskId;
        if (taskId is not null)
        {
            Ui.Run(null, () => _monitor.RunControlAsync(PipeProtocol.Methods.RunStop, taskId));
        }
    }

    private void UpdateTray()
    {
        var status = _monitor.Status;
        var snapshot = _monitor.Snapshot;
        var current = snapshot?.Current;
        _runAll.Enabled = _monitor.IsConnected && snapshot?.Tasks.Any(t => t.Enabled) == true;
        _stopCurrent.Enabled = current is not null && !current.Stopping;
        _stopCurrent.Text = current is null ? Strings.MenuStopCurrent : Strings.Format(Strings.MenuStopTask, current.TaskName);

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
