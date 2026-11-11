namespace Custos.Gui;

/// <summary>
/// The interface process: the tray icon with its menu, the connection to the service and the main window.
/// Closing the window keeps the tray icon; "Close interface" ends the process. The service keeps running either way.
/// </summary>
internal sealed class TrayContext : ApplicationContext
{
    private readonly SynchronizationContext _ui;
    private readonly ServiceMonitor _monitor;
    private readonly NotifyIcon _trayIcon;
    private readonly ContextMenuStrip _menu;
    private readonly Font _boldFont;
    private readonly Icon _okIcon = AppIcons.TrayOk();
    private readonly Icon _downIcon = AppIcons.TrayDown();
    private readonly RegisteredWaitHandle _activateWait;
    private MainForm? _mainForm;

    /// <param name="activate">Set by a second copy started in the same session: show the window.</param>
    public TrayContext(EventWaitHandle activate)
    {
        _ui = SynchronizationContext.Current
            ?? throw new InvalidOperationException("TrayContext must be created on the UI thread.");

        _menu = new ContextMenuStrip();
        _boldFont = new Font(_menu.Font, FontStyle.Bold);
        var open = new ToolStripMenuItem(Strings.MenuOpen, null, (_, _) => ShowMainForm()) { Font = _boldFont };
        var close = new ToolStripMenuItem(Strings.MenuClose, null, (_, _) => ExitThread())
        {
            ShortcutKeyDisplayString = Strings.MenuCloseHint,
        };
        _menu.Items.AddRange([open, new ToolStripSeparator(), close]);

        _trayIcon = new NotifyIcon { ContextMenuStrip = _menu };
        _trayIcon.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                ShowMainForm();
            }
        };

        _monitor = new ServiceMonitor();
        _monitor.StatusChanged += (_, _) => UpdateTrayIcon();
        UpdateTrayIcon();
        _trayIcon.Visible = true;
        _monitor.Start();

        _activateWait = ThreadPool.RegisterWaitForSingleObject(
            activate, (_, _) => _ui.Post(_ => ShowMainForm(), null), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    protected override void ExitThreadCore()
    {
        _activateWait.Unregister(null);
        _trayIcon.Visible = false;
        _monitor.Dispose();
        _mainForm?.Dispose();
        base.ExitThreadCore();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _trayIcon.Dispose();
            _menu.Dispose();
            _boldFont.Dispose();
            _okIcon.Dispose();
            _downIcon.Dispose();
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

    private void UpdateTrayIcon()
    {
        var status = _monitor.Status;
        _trayIcon.Icon = status.State == ServiceState.Running ? _okIcon : _downIcon;
        _trayIcon.Text = Strings.Format(Strings.TrayTooltip, ServiceMonitor.Describe(status.State));
    }
}
