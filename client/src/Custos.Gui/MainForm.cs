using System.Drawing.Drawing2D;
using Custos.Core;
using Custos.Core.Ipc;
using Custos.Core.Model;
using Custos.Core.Monitor;
using Custos.Core.Text;

namespace Custos.Gui;

/// <summary>
/// The main window, variant "list and card" (docs/client-ui.md, section 2): toolbar, task list with the card next to
/// it, the "Log", "Run history" and "Progress" tabs below, the status bar. Closing it only hides it: the interface
/// stays in the tray.
/// </summary>
internal sealed class MainForm : Form
{
    private const int ToolbarIconSize = 24;
    private const int StateIconSize = 16;
    private const int MinListWidth = 340;
    private const int MinCardWidth = 280;
    private const int DefaultCardWidth = 400;
    private const int DefaultTabsHeight = 210;

    private readonly ServiceMonitor _monitor;
    private readonly UiState _uiState = UiState.Load();
    private readonly ToolStrip _toolbar = new() { GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(4, 2, 4, 2) };
    private readonly ToolStripSplitButton _create = Button<ToolStripSplitButton>(Strings.ToolCreate);
    private readonly ToolStripMenuItem _importFile = new(Strings.ToolImportFile);
    private readonly ToolStripMenuItem _import = new(Strings.ToolImportCobian);
    private readonly ToolStripMenuItem _export = new(Strings.ToolExportFile);
    private readonly ToolStripButton _edit = Button<ToolStripButton>(Strings.ToolEdit);
    private readonly ToolStripButton _clone = Button<ToolStripButton>(Strings.ToolClone);
    private readonly ToolStripButton _delete = Button<ToolStripButton>(Strings.ToolDelete);
    private readonly ToolStripButton _toggle = Button<ToolStripButton>(Strings.ToolEnable);
    private readonly ToolStripButton _run = Button<ToolStripButton>(Strings.ToolRun);
    private readonly ToolStripButton _stop = Button<ToolStripButton>(Strings.ToolStop);
    private readonly ToolStripButton _pause = Button<ToolStripButton>(Strings.ToolPause);
    private readonly ToolStripButton _restore = Button<ToolStripButton>(Strings.ToolRestore);
    private readonly ToolStripButton _card = Button<ToolStripButton>(Strings.ToolCard);
    private readonly ToolStripButton _settings = Button<ToolStripButton>(Strings.ToolSettings);
    private readonly ToolStripLabel _lock = new(Strings.ToolAdminsOnly) { Alignment = ToolStripItemAlignment.Right, ForeColor = SystemColors.GrayText };
    private readonly SplitContainer _outer = new() { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, FixedPanel = FixedPanel.Panel2, SplitterWidth = 6 };
    private readonly SplitContainer _inner = new() { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, FixedPanel = FixedPanel.Panel2, SplitterWidth = 6 };
    private readonly TaskListView _tasks = new() { Dock = DockStyle.Fill };
    private readonly Label _noConnection = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = SystemColors.GrayText, Padding = new Padding(24) };
    private readonly TaskCard _cardView = new() { Dock = DockStyle.Fill };
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly LogView _log = new() { Dock = DockStyle.Fill };
    private readonly HistoryView _history = new() { Dock = DockStyle.Fill };
    private readonly ProgressView _progress = new() { Dock = DockStyle.Fill };
    private readonly ContextMenuStrip _taskMenu = new();
    private readonly ToolStripStatusLabel _serviceStatus = new();
    private readonly ToolStripStatusLabel _serverStatus = new(Strings.ServerNotConnected) { Spring = true, TextAlign = ContentAlignment.MiddleLeft, ImageAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripStatusLabel _userStatus = new();
    private readonly ToolTip _toolTip = new();

    // A new version on GitHub (computers without a monitoring site): a line under the toolbar.
    private readonly FlowLayoutPanel _updateBar = new()
    {
        Dock = DockStyle.Top,
        AutoSize = true,
        WrapContents = false,
        Padding = new Padding(8, 4, 8, 4),
        Visible = false,
        BackColor = Theme.IsDark ? Color.FromArgb(28, 44, 62) : Color.FromArgb(229, 241, 251),
    };

    private readonly Label _updateText = new() { AutoSize = true, Margin = new Padding(0, 7, 12, 0) };
    private readonly Button _updateInstall = new() { Text = Strings.UpdateInstall, AutoSize = true, MinimumSize = new Size(100, 0) };
    private readonly LinkLabel _updateNotes = new() { Text = Strings.UpdateWhatsNew, AutoSize = true, Margin = new Padding(12, 7, 0, 0) };
    private ImageList? _stateImages;
    private Bitmap? _greenDot;
    private Bitmap? _greyDot;
    private Bitmap? _amberDot;
    private Bitmap? _emptyDot;
    private string? _historyKey;
    private string? _selectWhenShown;
    private bool _layoutRestored;

    public MainForm(ServiceMonitor monitor)
    {
        _monitor = monitor;

        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = Strings.AppTitle;
        Icon = AppIcons.App();
        Size = new Size(1000, 700);
        MinimumSize = new Size(700, 450);
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;

        BuildToolbar();
        BuildTaskMenu();

        _inner.Panel1.Controls.Add(_tasks);
        _inner.Panel1.Controls.Add(_noConnection);
        _inner.Panel2.Controls.Add(_cardView);
        _inner.Paint += PaintGrip;

        AddTab(Strings.TabLog, _log);
        AddTab(Strings.TabHistory, _history);
        AddTab(Strings.TabProgress, _progress);
        _outer.Panel1.Controls.Add(_inner);
        _outer.Panel2.Controls.Add(_tabs);
        _outer.Paint += PaintGrip;

        var versionStatus = new ToolStripStatusLabel(CustosInfo.Version);
        var statusStrip = new StatusStrip();
        statusStrip.Items.AddRange([_serviceStatus, _serverStatus, _userStatus, versionStatus]);

        _updateBar.Controls.AddRange([_updateText, _updateInstall, _updateNotes]);
        _updateInstall.Click += (_, _) => InstallProgramUpdate();
        _updateNotes.LinkClicked += (_, _) => OpenReleasePage();

        // Docking goes from the last control added to the first: status bar, toolbar, the update line, then the rest.
        Controls.Add(_outer);
        Controls.Add(_updateBar);
        Controls.Add(_toolbar);
        Controls.Add(statusStrip);
        ResumeLayout(performLayout: true);

        CreateImages();
        _tasks.SetColumns(_uiState.TaskColumns);
        _card.Checked = _uiState.CardVisible;
        _inner.Panel2Collapsed = !_uiState.CardVisible;
        if (_uiState.BottomTab >= 0 && _uiState.BottomTab < _tabs.TabCount)
        {
            _tabs.SelectedIndex = _uiState.BottomTab;
        }

        _tasks.SelectedTaskChanged += (_, _) => OnSelectionChanged();
        _tasks.ItemActivate += (_, _) => OnTaskActivated();
        _tasks.KeyDown += OnTaskListKeyDown;
        _tasks.ContextMenuStrip = _taskMenu;
        _monitor.StatusChanged += OnStatusChanged;
        _monitor.SnapshotChanged += OnSnapshotChanged;
        _monitor.ProgressChanged += OnProgressChanged;
        _monitor.LogAdded += OnLogAdded;
        _monitor.LogReset += OnLogReset;

        _log.Reset(_monitor.Log);
        ShowStatus();
        ShowSnapshot();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        RestoreWindow();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        // The splitters can only be placed once the window has its final size.
        Ui.Split(_outer, LogicalToDeviceUnits(150), LogicalToDeviceUnits(100),
            _outer.Height - _outer.SplitterWidth - LogicalToDeviceUnits(_uiState.TabsHeight ?? DefaultTabsHeight));
        PlaceCardSplitter();
        _layoutRestored = true;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Alt | Keys.P:
                ToggleCard();
                return true;
            case Keys.Control | Keys.N when _create.Enabled:
                CreateTask();
                return true;
            case Keys.Control | Keys.E when _edit.Enabled:
                EditTask();
                return true;
            case Keys.Control | Keys.D when _clone.Enabled:
                CloneTask();
                return true;
            case Keys.F5 when _run.Enabled:
                RunTask();
                return true;
            case Keys.Shift | Keys.F5 when _stop.Enabled:
                StopTask();
                return true;
            case Keys.F6 when _pause.Enabled:
                PauseTask();
                return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        SaveLayout();
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
        }

        // Whoever opens the window next types the program password again (section 4).
        _monitor.Lock();

        base.OnFormClosing(e);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        CreateImages();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _monitor.StatusChanged -= OnStatusChanged;
            _monitor.SnapshotChanged -= OnSnapshotChanged;
            _monitor.ProgressChanged -= OnProgressChanged;
            _monitor.LogAdded -= OnLogAdded;
            _monitor.LogReset -= OnLogReset;
            _taskMenu.Dispose();
            _toolTip.Dispose();
            DisposeImages();
        }

        base.Dispose(disposing);
    }

    // ---- Building ------------------------------------------------------------------------------------------------

    private static T Button<T>(string text)
        where T : ToolStripItem, new() => new()
        {
            Text = text,
            DisplayStyle = ToolStripItemDisplayStyle.ImageAndText,
            TextImageRelation = TextImageRelation.ImageAboveText,
            ImageScaling = ToolStripItemImageScaling.None,
            AutoToolTip = false,
            Padding = new Padding(4, 0, 4, 0),
        };

    private void BuildToolbar()
    {
        _create.ToolTipText = Strings.Format(Strings.ToolTipShortcut, Strings.ToolCreate, "Ctrl+N");
        _create.ButtonClick += (_, _) => CreateTask();
        _create.DropDownItems.AddRange([_importFile, _import, new ToolStripSeparator(), _export]);
        _create.DropDownOpening += (_, _) => _export.Enabled = _monitor.Status.Hello?.IsAdmin == true && _monitor.Snapshot?.Tasks.Count > 0;
        _importFile.Click += (_, _) => ImportFile();
        _import.Click += (_, _) => ImportTasks();
        _export.Click += (_, _) => ExportTasks();
        _edit.ToolTipText = Strings.Format(Strings.ToolTipShortcut, Strings.ToolEdit, "Ctrl+E");
        _edit.Click += (_, _) => EditTask();
        _clone.ToolTipText = Strings.Format(Strings.ToolTipShortcut, Strings.ToolClone, "Ctrl+D");
        _clone.Click += (_, _) => CloneTask();
        _delete.ToolTipText = Strings.Format(Strings.ToolTipShortcut, Strings.ToolDelete, "Del");
        _delete.Click += (_, _) => DeleteTask();
        _toggle.ToolTipText = Strings.ToolEnableHint;
        _toggle.Click += (_, _) => SwitchTasks(SelectedTasks().Any(t => !t.Enabled));
        _run.ToolTipText = Strings.Format(Strings.ToolTipShortcut, Strings.ToolRun, "F5");
        _run.Click += (_, _) => RunTask();
        _stop.ToolTipText = Strings.Format(Strings.ToolTipShortcut, Strings.ToolStop, "Shift+F5");
        _stop.Click += (_, _) => StopTask();
        _pause.ToolTipText = Strings.Format(Strings.ToolTipShortcut, Strings.ToolPauseHint, "F6");
        _pause.Click += (_, _) => PauseTask();
        _restore.ToolTipText = Strings.ToolRestoreHint;
        _restore.Click += (_, _) => OpenRestore();
        _card.CheckOnClick = true;
        _card.ToolTipText = Strings.Format(Strings.ToolTipShortcut, Strings.ToolCardHint, "Alt+P");
        _card.CheckedChanged += (_, _) => ShowCard(_card.Checked);
        _settings.ToolTipText = Strings.ToolSettings;
        _settings.Click += (_, _) => OpenSettings();
        _card.Alignment = ToolStripItemAlignment.Right;
        _settings.Alignment = ToolStripItemAlignment.Right;

        _toolbar.Items.AddRange(
        [
            _create, _edit, _clone, _delete, _toggle, new ToolStripSeparator(),
            _run, _stop, _pause, new ToolStripSeparator(),
            _restore,

            // Right-aligned items go from the right edge inwards.
            _settings, _card, _lock,
        ]);
    }

    private void BuildTaskMenu()
    {
        var run = new ToolStripMenuItem(Strings.ToolRun, null, (_, _) => RunTask());
        var stop = new ToolStripMenuItem(Strings.ToolStop, null, (_, _) => StopTask());
        var edit = new ToolStripMenuItem(Strings.ToolEdit, null, (_, _) => EditTask());
        var clone = new ToolStripMenuItem(Strings.ToolClone, null, (_, _) => CloneTask());
        var delete = new ToolStripMenuItem(Strings.ToolDelete, null, (_, _) => DeleteTask());
        var enable = new ToolStripMenuItem(Strings.ToolEnable, null, (_, _) => SwitchTasks(true));
        var disable = new ToolStripMenuItem(Strings.ToolDisable, null, (_, _) => SwitchTasks(false));
        var selectAll = new ToolStripMenuItem(Strings.MenuSelectAll, null, (_, _) => _tasks.SelectAllTasks()) { ShortcutKeyDisplayString = "Ctrl+A" };
        _taskMenu.Items.AddRange([run, stop, new ToolStripSeparator(), edit, clone, delete, new ToolStripSeparator(), enable, disable,
            new ToolStripSeparator(), selectAll]);
        _taskMenu.Opening += (_, e) =>
        {
            var selected = SelectedTasks();
            run.Enabled = _run.Enabled;
            stop.Enabled = _stop.Enabled;
            edit.Enabled = _edit.Enabled;
            clone.Enabled = _clone.Enabled;
            delete.Enabled = _delete.Enabled;
            enable.Enabled = _toggle.Enabled && selected.Any(t => !t.Enabled);
            disable.Enabled = _toggle.Enabled && selected.Any(t => t.Enabled);
            e.Cancel = _tasks.SelectedTaskId is null;
        };
    }

    private void AddTab(string title, Control content)
    {
        // The visual style of a tab page is light whatever the colour mode: in the dark theme the page takes the colour of the window.
        var page = new TabPage(title) { UseVisualStyleBackColor = !Theme.IsDark };
        page.Controls.Add(content);
        _tabs.TabPages.Add(page);
    }

    private void CreateImages()
    {
        DisposeImages();
        int size = LogicalToDeviceUnits(ToolbarIconSize);
        _toolbar.ImageScalingSize = new Size(size, size);
        _create.Image = Glyphs.Draw(Glyph.Create, size);
        _importFile.Image = Glyphs.Draw(Glyph.Import, LogicalToDeviceUnits(16));
        _import.Image = Glyphs.Draw(Glyph.Import, LogicalToDeviceUnits(16));
        _export.Image = Glyphs.Draw(Glyph.Export, LogicalToDeviceUnits(16));
        _edit.Image = Glyphs.Draw(Glyph.Edit, size);
        _clone.Image = Glyphs.Draw(Glyph.Clone, size);
        _delete.Image = Glyphs.Draw(Glyph.Delete, size);
        _toggle.Image = Glyphs.Draw(Glyph.Toggle, size);
        _run.Image = Glyphs.Draw(Glyph.Run, size);
        _stop.Image = Glyphs.Draw(Glyph.Stop, size);
        _pause.Image = Glyphs.Draw(Glyph.Pause, size);
        _restore.Image = Glyphs.Draw(Glyph.Restore, size);
        _card.Image = Glyphs.Draw(Glyph.Card, size);
        _settings.Image = Glyphs.Draw(Glyph.Settings, size);
        _lock.Image = Glyphs.Draw(Glyph.Lock, LogicalToDeviceUnits(16), Glyphs.Grey);

        int stateSize = LogicalToDeviceUnits(StateIconSize);
        _stateImages = Glyphs.StateImages(stateSize);
        _tasks.SmallImageList = _stateImages;
        _log.SmallImageList = _stateImages;
        _history.List.SmallImageList = _stateImages;

        _greenDot = CreateDot(Color.FromArgb(46, 160, 67), filled: true);
        _greyDot = CreateDot(Glyphs.Grey, filled: true);
        _amberDot = CreateDot(Glyphs.Amber, filled: true);
        _emptyDot = CreateDot(Glyphs.Grey, filled: false);
        _serverStatus.Image = _emptyDot;
        ShowStatus();
    }

    private void DisposeImages()
    {
        foreach (var item in new ToolStripItem[] { _create, _importFile, _import, _export, _edit, _clone, _delete, _toggle, _run, _stop, _pause, _restore, _card, _settings, _lock })
        {
            var image = item.Image;
            item.Image = null;
            image?.Dispose();
        }

        _serviceStatus.Image = null;
        _serverStatus.Image = null;
        _greenDot?.Dispose();
        _greyDot?.Dispose();
        _amberDot?.Dispose();
        _emptyDot?.Dispose();
        _stateImages?.Dispose();
        _stateImages = null;
    }

    private Bitmap CreateDot(Color color, bool filled)
    {
        int size = LogicalToDeviceUnits(16);
        var bitmap = new Bitmap(size, size);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        float k = size / 16f;
        var circle = new RectangleF(3 * k, 3 * k, 9 * k, 9 * k);
        if (filled)
        {
            using var brush = new SolidBrush(color);
            graphics.FillEllipse(brush, circle);
        }
        else
        {
            using var pen = new Pen(color, 1.5f * k);
            graphics.DrawEllipse(pen, circle);
        }

        return bitmap;
    }

    /// <summary>Three dots in the middle of a splitter, the sign that it can be dragged.</summary>
    private static void PaintGrip(object? sender, PaintEventArgs e)
    {
        var split = (SplitContainer)sender!;
        var r = split.SplitterRectangle;
        int dot = Math.Max(2, split.LogicalToDeviceUnits(2));
        int step = dot * 2;
        using var brush = new SolidBrush(Color.FromArgb(150, 150, 150));
        for (int i = -1; i <= 1; i++)
        {
            var center = split.Orientation == Orientation.Vertical
                ? new Point(r.X + (r.Width / 2), r.Y + (r.Height / 2) + (i * step))
                : new Point(r.X + (r.Width / 2) + (i * step), r.Y + (r.Height / 2));
            e.Graphics.FillRectangle(brush, center.X - (dot / 2), center.Y - (dot / 2), dot, dot);
        }
    }

    // ---- Window layout (ui.json) -----------------------------------------------------------------------------------

    private void RestoreWindow()
    {
        if (_uiState.Width is { } width && _uiState.Height is { } height && _uiState.Left is { } left && _uiState.Top is { } top)
        {
            var bounds = new Rectangle(left, top, Math.Max(width, MinimumSize.Width), Math.Max(height, MinimumSize.Height));

            // Only where a screen still shows at least the title bar.
            if (Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(new Rectangle(bounds.Left, bounds.Top, bounds.Width, 40))))
            {
                StartPosition = FormStartPosition.Manual;
                Bounds = bounds;
            }
        }

        if (_uiState.Maximized)
        {
            WindowState = FormWindowState.Maximized;
        }
    }

    private void PlaceCardSplitter()
    {
        if (_inner.Panel2Collapsed)
        {
            return;
        }

        int card = LogicalToDeviceUnits(_uiState.CardWidth ?? DefaultCardWidth);
        Ui.Split(_inner, LogicalToDeviceUnits(MinListWidth), LogicalToDeviceUnits(MinCardWidth), _inner.Width - _inner.SplitterWidth - card);
    }

    /// <summary>Writes ui.json; also called by the tray when the interface closes with the window open.</summary>
    public void SaveLayout()
    {
        var bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        _uiState.Left = bounds.Left;
        _uiState.Top = bounds.Top;
        _uiState.Width = bounds.Width;
        _uiState.Height = bounds.Height;
        _uiState.Maximized = WindowState == FormWindowState.Maximized;
        _uiState.CardVisible = _card.Checked;
        if (_layoutRestored && WindowState != FormWindowState.Minimized)
        {
            _uiState.TabsHeight = ToLogical(_outer.Panel2.Height);
            if (!_inner.Panel2Collapsed)
            {
                _uiState.CardWidth = ToLogical(_inner.Panel2.Width);
            }
        }

        _uiState.TaskColumns = _tasks.GetColumnWidths();
        _uiState.BottomTab = _tabs.SelectedIndex;
        _uiState.Save();
    }

    private int ToLogical(int pixels) => (int)Math.Round(pixels * 96.0 / DeviceDpi);

    private void ToggleCard() => _card.Checked = !_card.Checked;

    private void ShowCard(bool visible)
    {
        if (visible == !_inner.Panel2Collapsed)
        {
            return;
        }

        // The list keeps all its columns; next to the card it scrolls sideways when they do not fit.
        if (!visible && !_inner.Panel2Collapsed && _layoutRestored)
        {
            _uiState.CardWidth = ToLogical(_inner.Panel2.Width);
        }

        _inner.Panel2Collapsed = !visible;
        if (visible && _layoutRestored)
        {
            PlaceCardSplitter();
        }

        ShowSnapshot();
    }

    // ---- Showing the state of the service -----------------------------------------------------------------------

    private void OnStatusChanged(object? sender, EventArgs e) => ShowStatus();

    private void OnSnapshotChanged(object? sender, EventArgs e) => ShowSnapshot();

    private void OnLogAdded(object? sender, LogLinesEventArgs e) => _log.Append(e.Lines);

    private void OnLogReset(object? sender, EventArgs e) => _log.Reset(_monitor.Log);

    private void OnProgressChanged(object? sender, EventArgs e)
    {
        var current = _monitor.Snapshot?.Current;
        var progress = _monitor.Progress;
        _progress.ShowRun(current, progress);
        _cardView.ShowProgress(current, progress);
        if (current is not null && progress is not null && progress.RunId == current.RunId)
        {
            _tasks.ShowRunning(current.TaskId, TaskStates.RunningText(current.Stopping, progress.Paused, progress.Progress.Percent));
        }

        UpdateCommands();
    }

    private void ShowStatus()
    {
        var status = _monitor.Status;
        var hello = status.State == ServiceState.Running ? status.Hello : null;
        string state = ServiceMonitor.Describe(status.State);
        _serviceStatus.Text = Capitalize(state);
        _serviceStatus.Image = status.State == ServiceState.Running ? _greenDot : _greyDot;
        _userStatus.Text = hello?.User is { } user
            ? Strings.Format(Strings.UserWithRights, user, _monitor.CanEdit ? Strings.RightsAdmin : Strings.RightsUser)
            : string.Empty;
        _noConnection.Text = status.State switch
        {
            ServiceState.Running => string.Empty,
            ServiceState.Connecting => Capitalize(state),
            _ => Strings.Format(Strings.NoConnection, status.Problem ?? Capitalize(state)),
        };
        UpdateCommands();
    }

    private void ShowSnapshot()
    {
        var snapshot = _monitor.Snapshot;
        bool connected = snapshot is not null;
        _noConnection.Visible = !connected;
        _tasks.Visible = connected;
        _tasks.ShowTasks(snapshot);
        if (_selectWhenShown is { } wanted && snapshot?.Tasks.Any(t => SameId(t.Id, wanted)) == true)
        {
            _selectWhenShown = null;
            _tasks.SelectTask(wanted);
        }

        ShowSelectedTask();
        ShowServer(snapshot?.Server);
        ShowProgramUpdate(snapshot?.ProgramUpdate);
        _progress.ShowRun(snapshot?.Current, _monitor.Progress);
        _userStatus.Text = _monitor.Status.Hello?.User is { } user && connected
            ? Strings.Format(Strings.UserWithRights, user, _monitor.CanEdit ? Strings.RightsAdmin : Strings.RightsUser)
            : _userStatus.Text;
        UpdateCommands();
    }

    /// <summary>The line «Вышла CUSTOS …» while GitHub has a newer version than this one.</summary>
    private void ShowProgramUpdate(ProgramUpdateInfo? update)
    {
        bool show = update is { Available: true, Latest: not null };
        _updateBar.Visible = show;
        if (!show)
        {
            return;
        }

        bool admin = _monitor.Status.Hello?.IsAdmin == true;
        _updateText.Text = Strings.Format(Strings.UpdateBanner, update!.Latest, CustosInfo.Version);
        _updateInstall.Text = update.Installing ? Strings.UpdateInstalling : Strings.UpdateInstall;
        _updateInstall.Enabled = admin && !update.Installing;
        _toolTip.SetToolTip(_updateInstall, admin ? null : Strings.UpdateAdminOnly);
        _updateNotes.Visible = update.PageUrl is not null;
    }

    /// <summary>«Обновить»: the service downloads the version from GitHub, checks it and installs it.</summary>
    private void InstallProgramUpdate() => Ui.Run(this, async () =>
    {
        if (_monitor.Snapshot?.ProgramUpdate is not { Available: true, Latest: { } version }
            || !Ui.Confirm(this, Strings.Format(Strings.UpdateAsk, version)) || !await _monitor.EnsureUnlockedAsync())
        {
            return;
        }

        _updateInstall.Enabled = false;
        _updateInstall.Text = Strings.UpdateInstalling;
        UseWaitCursor = true;
        try
        {
            var result = await _monitor.RequestAsync(PipeProtocol.Methods.ProgramUpdate, EmptyRequest.Instance,
                IpcJsonContext.Default.EmptyRequest, IpcJsonContext.Default.OkResult, TimeSpan.FromMinutes(15));
            UseWaitCursor = false;
            if (result.Ok)
            {
                Ui.ShowInfo(this, result.Message ?? Strings.UpdateInstalling);
            }
            else
            {
                Ui.ShowWarning(this, result.Message ?? Strings.ProblemNoAnswer);
            }
        }
        finally
        {
            UseWaitCursor = false;
            if (!IsDisposed)
            {
                ShowProgramUpdate(_monitor.Snapshot?.ProgramUpdate);
            }
        }
    });

    /// <summary>«Что нового»: the page of the release on GitHub.</summary>
    private void OpenReleasePage()
    {
        if (_monitor.Snapshot?.ProgramUpdate.PageUrl is not { } url
            || !Uri.TryCreate(url, UriKind.Absolute, out var page) || page.Scheme != Uri.UriSchemeHttps)
        {
            return;
        }

        try
        {
            using var _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(page.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Ui.ShowWarning(this, ex.Message);
        }
    }

    /// <summary>The connection to the monitoring server in the status bar (docs/client-ui.md).</summary>
    private void ShowServer(ServerLinkInfo? server)
    {
        string host = server?.ServerHost ?? string.Empty;
        (string text, Image? image) = server?.State switch
        {
            null or ServerLinkStates.Off => (Strings.ServerNotConnected, _emptyDot),
            ServerLinkStates.Approved => (Strings.Format(Strings.ServerOnline, host), _greenDot),
            ServerLinkStates.Pending => (Strings.Format(Strings.ServerPending, host), _greyDot),
            ServerLinkStates.Disabled => (Strings.Format(Strings.ServerDisabled, host), _greyDot),
            ServerLinkStates.Connecting => (Strings.Format(Strings.ServerConnecting, host), _greyDot),
            ServerLinkStates.Offline => (Offline(server), _amberDot),
            _ => (Strings.Format(Strings.ServerProblem, host, ServerLink.StateText(server.State).TrimEnd('.')), _amberDot),
        };
        _serverStatus.Text = text;
        _serverStatus.Image = image;
        _serverStatus.ToolTipText = server?.Detail;
    }

    private static string Offline(ServerLinkInfo server)
    {
        int minutes = server.OfflineSince is { } since ? Math.Max(0, (int)(DateTimeOffset.UtcNow - since).TotalMinutes) : 0;
        string text = Strings.Format(Strings.ServerOffline, minutes);
        if (server.Waiting > 0)
        {
            text += " — " + Strings.Format(Sizes.Plural(server.Waiting, Strings.ServerReportsOne, Strings.ServerReportsFew, Strings.ServerReportsMany), server.Waiting);
        }

        return text;
    }

    private void OnSelectionChanged()
    {
        ShowSelectedTask();
        UpdateCommands();
    }

    private void ShowSelectedTask()
    {
        var snapshot = _monitor.Snapshot;
        var task = SelectedTask();
        if (!_inner.Panel2Collapsed)
        {
            _cardView.ShowTask(task, snapshot, _monitor.Progress);
        }

        LoadHistory();
    }

    private void UpdateCommands()
    {
        var snapshot = _monitor.Snapshot;
        bool connected = snapshot is not null && _monitor.IsConnected;
        bool canEdit = connected && _monitor.CanEdit;
        // Several tasks may be selected: editing and cloning take one, the rest act on all of them.
        var selected = SelectedTasks();
        var current = snapshot?.Current;
        bool selectedRunning = current is not null && selected.Any(t => SameId(t.Id, current.TaskId));
        bool anyWaiting = selected.Any(t => IsWaiting(t, snapshot));
        bool anyIdle = selected.Any(t => !IsWaiting(t, snapshot) && !(current is not null && SameId(t.Id, current.TaskId)));

        _create.Enabled = canEdit;
        _edit.Enabled = canEdit && selected.Count == 1;
        _clone.Enabled = canEdit && selected.Count == 1;
        _delete.Enabled = canEdit && selected.Count > 0 && !selectedRunning;
        _toggle.Enabled = canEdit && selected.Count > 0;
        _toggle.Text = selected.Count == 0 || selected.Any(t => !t.Enabled) ? Strings.ToolEnable : Strings.ToolDisable;
        _run.Enabled = connected && anyIdle;
        _stop.Enabled = connected && (selectedRunning || anyWaiting || (selected.Count == 0 && current is not null));
        _pause.Enabled = connected && current is not null && !current.Stopping && (selected.Count == 0 || selectedRunning);
        _pause.Checked = current is not null && (_monitor.Progress?.RunId == current.RunId ? _monitor.Progress.Paused : current.Paused);
        _settings.Enabled = connected;
        bool isAdmin = connected && _monitor.Status.Hello?.IsAdmin == true;
        _restore.Enabled = isAdmin;
        _restore.ToolTipText = connected && !isAdmin ? Strings.RestoreAdminsOnly : Strings.ToolRestoreHint;
        _lock.Visible = connected && !canEdit;
    }

    private void LoadHistory()
    {
        var snapshot = _monitor.Snapshot;
        var task = SelectedTask();
        if (snapshot is null || task is null)
        {
            _historyKey = null;
            _history.ShowMessage(snapshot is null ? string.Empty : Strings.HistorySelectTask);
            return;
        }

        // Loaded again only when the last run of the task changed.
        var last = snapshot.States.FirstOrDefault(s => SameId(s.TaskId, task.Id))?.LastRun;
        string key = $"{task.Id}|{last?.RunId}|{last?.Status}";
        if (key == _historyKey)
        {
            return;
        }

        _historyKey = key;
        Ui.Run(this, async () =>
        {
            try
            {
                var result = await _monitor.RequestAsync(PipeProtocol.Methods.HistoryGet, new HistoryRequest { TaskId = task.Id },
                    IpcJsonContext.Default.HistoryRequest, IpcJsonContext.Default.HistoryResult);
                if (_historyKey == key)
                {
                    _history.ShowRuns(result.Runs);
                }
            }
            catch (ServiceUnavailableException ex)
            {
                if (_historyKey == key)
                {
                    _historyKey = null;
                    _history.ShowMessage(ex.Message);
                }
            }
        });
    }

    // ---- Commands -----------------------------------------------------------------------------------------------

    private TaskDefinition? SelectedTask() => _tasks.SelectedTaskId is { } id
        ? _monitor.Snapshot?.Tasks.FirstOrDefault(t => SameId(t.Id, id))
        : null;

    /// <summary>All selected tasks, in the order of the list.</summary>
    private List<TaskDefinition> SelectedTasks()
    {
        var snapshot = _monitor.Snapshot;
        if (snapshot is null)
        {
            return [];
        }

        return [.. _tasks.SelectedTaskIds.Select(id => snapshot.Tasks.FirstOrDefault(t => SameId(t.Id, id))).OfType<TaskDefinition>()];
    }

    /// <summary>Whether the task waits in the queue.</summary>
    private static bool IsWaiting(TaskDefinition task, AgentSnapshot? snapshot) =>
        snapshot?.States.FirstOrDefault(s => SameId(s.TaskId, task.Id))?.Queued == true;

    private void OnTaskActivated()
    {
        if (_edit.Enabled)
        {
            EditTask();
        }
        else if (_inner.Panel2Collapsed)
        {
            _card.Checked = true;
        }
    }

    private void OnTaskListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Delete && e.Modifiers == Keys.None && _delete.Enabled)
        {
            DeleteTask();
            e.Handled = true;
        }
    }

    // The windows for changes ask for the program password before they open, not after a whole task was filled in.
    private void CreateTask() => Ui.Run(this, async () =>
    {
        if (!_monitor.CanEdit || !await _monitor.EnsureUnlockedAsync())
        {
            return;
        }

        using var editor = new TaskEditorForm(_monitor, TaskEditorForm.NewTask(_monitor.Snapshot), isNew: true);
        if (editor.ShowDialog(this) == DialogResult.OK && editor.Saved is { } saved)
        {
            SelectWhenShown(saved.Id);
        }
    });

    private void EditTask() => Ui.Run(this, async () =>
    {
        if (SelectedTask() is not { } task || !_monitor.CanEdit || !await _monitor.EnsureUnlockedAsync())
        {
            return;
        }

        // The task as it is now: the snapshot may have changed while the password was typed.
        using var editor = new TaskEditorForm(_monitor, (SelectedTask() ?? task).Clone(), isNew: false);
        editor.ShowDialog(this);
    });

    private void CloneTask()
    {
        if (SelectedTask() is not { } task || _monitor.Snapshot is not { } snapshot)
        {
            return;
        }

        var copy = task.Clone();
        copy.Id = TaskDefinition.NewId();
        copy.Name = UniqueName(Strings.Format(Strings.CloneName, task.Name), snapshot);
        copy.Enabled = false;
        copy.Revision = 0;
        copy.UpdatedAt = null;
        copy.UpdatedBy = null;
        foreach (var source in copy.Sources)
        {
            source.Id = TaskDefinition.NewId();
        }

        foreach (var destination in copy.Destinations)
        {
            destination.Id = TaskDefinition.NewId();
        }

        Ui.Run(this, async () =>
        {
            var result = await _monitor.RequestAsync(PipeProtocol.Methods.TaskSave, new TaskSaveRequest { Task = copy },
                IpcJsonContext.Default.TaskSaveRequest, IpcJsonContext.Default.TaskSaveResult);
            SelectWhenShown(result.Task.Id);
        });
    }

    private void DeleteTask()
    {
        var tasks = SelectedTasks();
        string question = tasks.Count == 1 ? Strings.Format(Strings.ConfirmDelete, tasks[0].Name) : Strings.Format(Strings.ConfirmDeleteMany, tasks.Count);
        if (tasks.Count == 0 || !Ui.Confirm(this, question))
        {
            return;
        }

        Ui.Run(this, async () =>
        {
            foreach (var task in tasks)
            {
                await _monitor.RequestAsync(PipeProtocol.Methods.TaskDelete, new TaskRequest { TaskId = task.Id },
                    IpcJsonContext.Default.TaskRequest, IpcJsonContext.Default.OkResult);
            }
        });
    }

    /// <summary>Switches the selected tasks on or off with one request (the group action).</summary>
    private void SwitchTasks(bool enabled)
    {
        var ids = SelectedTasks().Where(t => t.Enabled != enabled).Select(t => t.Id).ToList();
        if (ids.Count == 0 || !_monitor.CanEdit)
        {
            return;
        }

        Ui.Run(this, async () =>
        {
            var result = await _monitor.RequestAsync(PipeProtocol.Methods.TasksEnable, new TasksEnableRequest { TaskIds = ids, Enabled = enabled },
                IpcJsonContext.Default.TasksEnableRequest, IpcJsonContext.Default.TasksEnableResult);
            if (result.Problems.Count > 0)
            {
                Ui.ShowInfo(this, Strings.TasksNotChanged + "\n\n" + string.Join("\n", result.Problems));
            }
        });
    }

    private void RunTask()
    {
        var snapshot = _monitor.Snapshot;
        var current = snapshot?.Current;
        var tasks = SelectedTasks().Where(t => !IsWaiting(t, snapshot) && !(current is not null && SameId(t.Id, current.TaskId))).ToList();
        if (tasks.Count == 0)
        {
            return;
        }

        Ui.Run(this, async () =>
        {
            // One after another, in the order of the list: the service queues them.
            var messages = new List<string>();
            foreach (var task in tasks)
            {
                var result = await _monitor.RunControlAsync(PipeProtocol.Methods.TaskRun, task.Id);
                if (!result.Ok && result.Message is { } message)
                {
                    messages.Add(tasks.Count == 1 ? message : task.Name + ": " + message);
                }
            }

            if (messages.Count > 0)
            {
                Ui.ShowInfo(this, string.Join("\n", messages));
            }
        });
    }

    private void StopTask()
    {
        var snapshot = _monitor.Snapshot;
        var current = snapshot?.Current;
        var tasks = SelectedTasks();
        List<string?> ids = tasks.Count == 0
            ? [null]
            : [.. tasks.Where(t => IsWaiting(t, snapshot) || (current is not null && SameId(t.Id, current.TaskId))).Select(t => (string?)t.Id)];
        Ui.Run(this, async () =>
        {
            foreach (string? taskId in ids)
            {
                await _monitor.RunControlAsync(PipeProtocol.Methods.RunStop, taskId);
            }
        });
    }

    private void PauseTask()
    {
        var current = _monitor.Snapshot?.Current;
        if (current is null)
        {
            return;
        }

        bool paused = _monitor.Progress?.RunId == current.RunId ? _monitor.Progress.Paused : current.Paused;
        Ui.Run(this, () => _monitor.RunControlAsync(paused ? PipeProtocol.Methods.RunResume : PipeProtocol.Methods.RunPause, current.TaskId));
    }

    private void OpenSettings()
    {
        using var form = new SettingsForm(_monitor);
        form.ShowDialog(this);
    }

    private void OpenRestore() => Ui.Run(this, async () =>
    {
        if (_monitor.Status.Hello?.IsAdmin != true || !await _monitor.EnsureUnlockedAsync())
        {
            return;
        }

        using var form = new RestoreForm(_monitor, SelectedTask()?.Id);
        form.ShowDialog(this);
    });

    private void ImportTasks() => Ui.Run(this, async () =>
    {
        if (!_monitor.CanEdit || !await _monitor.EnsureUnlockedAsync())
        {
            return;
        }

        using var form = new CobianImportForm(_monitor);
        form.ShowDialog(this);
    });

    private void ImportFile() => Ui.Run(this, async () =>
    {
        if (!_monitor.CanEdit || !await _monitor.EnsureUnlockedAsync())
        {
            return;
        }

        using var form = new TaskImportForm(_monitor);
        form.ShowDialog(this);
    });

    private void ExportTasks() => Ui.Run(this, async () =>
    {
        // The passwords stored here leave the service only this way: administrators only (section 9a), and with
        // the program password when one is set (section 4).
        if (_monitor.Status.Hello?.IsAdmin != true || !await _monitor.EnsureUnlockedAsync())
        {
            return;
        }

        using var form = new TaskExportForm(_monitor);
        form.ShowDialog(this);
    });

    private void SelectWhenShown(string taskId)
    {
        _selectWhenShown = taskId;
        ShowSnapshot();
    }

    private static string UniqueName(string name, AgentSnapshot snapshot)
    {
        string candidate = name;
        for (int i = 2; snapshot.Tasks.Any(t => string.Equals(t.Name, candidate, StringComparison.OrdinalIgnoreCase)); i++)
        {
            candidate = $"{name} {i}";
        }

        return candidate;
    }

    private static string Capitalize(string text) =>
        text.Length == 0 ? text : char.ToUpper(text[0], Thread.CurrentThread.CurrentCulture) + text[1..];

    private static bool SameId(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
