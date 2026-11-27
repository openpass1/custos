using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Custos.Core;
using Custos.Gui;

namespace Custos.Desktop;

/// <summary>The main window: toolbar, task list, card, the tabs under them and the status bar (docs/client-ui.md).</summary>
internal sealed partial class MainWindow : Window
{
    private const double LinuxMinWidth = 360;
    private const double LinuxMinHeight = 320;
    private const double LinuxMinCardWidth = 160;
    private const double LinuxMinTabsHeight = 104;

    private readonly MainViewModel _vm;
    private readonly MainActions _actions;
    private readonly List<Icon> _sortArrows = [];
    private GridLength _cardWidth = new(350);
    private bool _selecting;
    private bool _replacingForLanguage;
    private bool _fitting;
    private bool _compactToolbar;
    private (PixelRect WorkArea, double Scale)? _linuxScreen;

    /// <summary>Only for the XAML previewer of an IDE; the program uses <see cref="MainWindow(MainViewModel)"/>.</summary>
    public MainWindow()
        : this(new MainViewModel(new ServiceMonitor(), demo: true))
    {
    }

    public MainWindow(MainViewModel vm)
    {
        _vm = vm;
        _actions = new MainActions(this, vm);
        DataContext = vm;
        InitializeComponent();
        if (CustosEdition.IsLinux)
        {
            MinWidth = LinuxMinWidth;
            MinHeight = LinuxMinHeight;
            Top.ColumnDefinitions[0].MinWidth = 160;
            Body.RowDefinitions[0].MinHeight = 114;
            Body.RowDefinitions[2].MinHeight = LinuxMinTabsHeight;
        }
        else if (!vm.ShowsManagement)
        {
            MinWidth = 640;
        }

        RestoreLayout();
        BuildHeader();
        ShowCardColumn();
        vm.PropertyChanged += OnViewModelChanged;
        vm.SelectionRestored += OnSelectionRestored;
        ActualThemeVariantChanged += (_, _) =>
        {
            TitleBar.Paint(this);
            BuildSettingsMenu();
        };
        BuildSettingsMenu();
    }

    public MainViewModel ViewModel => _vm;

    /// <summary>What the buttons do; the tests press them through here as well.</summary>
    public MainActions Actions => _actions;

    /// <summary>The width of the screen at 100 % scale for the tests, which have no screen; null = the real one.</summary>
    internal double? ScreenWidth { get; set; }

    /// <summary>Lays the toolbar out again for the width of the window and of the screen.</summary>
    internal void Refit() => FitToolbar();

    /// <summary>Set when the program quits: then the window really closes.</summary>
    public bool Quitting { get; set; }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        TitleBar.Paint(this);
        Toolbar.SizeChanged += (_, _) => FitToolbar();
        ToolbarEnd.SizeChanged += (_, _) => FitToolbar();
        PositionChanged += (_, _) => FitToolbar();
        if (CustosEdition.IsLinux)
        {
            PositionChanged += (_, _) => FitLinuxScreen(bringIntoView: false);
            ScalingChanged += OnLinuxScreenChanged;
            Screens.Changed += OnLinuxScreenChanged;
            Body.SizeChanged += (_, _) => FitLinuxContent();
            FitLinuxScreen();
            FitLinuxContent();
        }

        FitToolbar();
    }

    /// <summary>
    /// Every button of the toolbar fits: the window cannot be made narrower than that. On a screen narrower than the
    /// whole toolbar (1024×768, a notebook at 125 %) «Клонировать» and «Восстановить» move into «⋮ Ещё» and the margins
    /// get smaller, so the window still fits on the screen.
    /// </summary>
    private void FitToolbar()
    {
        if ((!_vm.ShowsTaskManagement && !_vm.ShowsRestore) || _fitting || !IsVisible)
        {
            return;
        }

        _fitting = true;
        try
        {
            if (CustosEdition.IsLinux)
            {
                FitLinuxToolbar();
                return;
            }

            static double WidthOf(Control c) => double.IsNaN(c.Width) ? c.Bounds.Width : c.Width;
            double moved = (_vm.ShowsTaskManagement ? WidthOf(CloneButton) : 0) + (_vm.ShowsRestore ? WidthOf(RestoreButton) : 0);
            double more = WidthOf(MoreButton);
            const double FullMargins = 14 + 12 + 14 + 6;
            const double CompactMargins = 8 + 8 + 8 + 4;

            // What the toolbar takes with every button out, whichever way it is shown now.
            double start = ToolbarStart.DesiredSize.Width - FabPanel.Margin.Right;
            double startFull = _compactToolbar ? start - more + moved : start;
            double end = ToolbarEnd.DesiredSize.Width;
            double full = startFull + end + FullMargins;
            double compact = startFull - moved + more + end + CompactMargins;

            var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
            double room = ScreenWidth ?? (screen is null ? double.PositiveInfinity : screen.WorkingArea.Width / screen.Scaling);
            room -= 16;
            double minimum = full <= room ? full : Math.Min(compact, Math.Max(room, 640));
            MinWidth = Math.Ceiling(minimum);

            bool narrow = Bounds.Width < full - 0.5;
            if (narrow != _compactToolbar)
            {
                _compactToolbar = narrow;
                CloneButton.IsVisible = !narrow && _vm.ShowsTaskManagement;
                RestoreButton.IsVisible = !narrow && _vm.ShowsRestore;
                MoreButton.IsVisible = narrow;
                Toolbar.Margin = narrow ? new Thickness(8, 4, 8, 4) : new Thickness(14, 4, 12, 4);
                FabPanel.Margin = new Thickness(0, 0, narrow ? 8 : 14, 0);
            }
        }
        finally
        {
            _fitting = false;
        }
    }

    private void FitLinuxToolbar()
    {
        const double separatorWidth = 13;
        double action = SettingsButton.Width;
        double available = ClientSize.Width - Toolbar.Margin.Left - Toolbar.Margin.Right;
        double create = _vm.ShowsTaskManagement ? FabPanel.DesiredSize.Width : 0;
        double full = create + 5 * action;
        full += _vm.ShowsTaskManagement ? 4 * action + separatorWidth : 0;
        full += _vm.ShowsRestore ? action + separatorWidth : 0;
        bool hasSpace = available >= full;
        bool showPrimaryActions = hasSpace || available >= create + 6 * action;
        bool showRun = showPrimaryActions || available >= create + 3 * action;
        bool showTaskChanges = hasSpace && _vm.ShowsTaskManagement;
        bool showRestore = hasSpace && _vm.ShowsRestore;

        EditButton.IsVisible = CloneButton.IsVisible = DeleteButton.IsVisible = ToggleButton.IsVisible = showTaskChanges;
        TaskChangesSeparator.IsVisible = showTaskChanges;
        RestoreButton.IsVisible = RestoreSeparator.IsVisible = showRestore;
        RunButton.IsVisible = showRun;
        StopButton.IsVisible = PauseButton.IsVisible = CardButton.IsVisible = showPrimaryActions;
        AdminBadge.IsVisible = _vm.ShowAdminsOnly && available >= full + 300;
        MoreButton.IsVisible = !hasSpace;
        CompactTaskItems.IsVisible = !hasSpace && _vm.ShowsTaskManagement;
        CompactRunItem.IsVisible = !showRun;
        CompactRunItems.IsVisible = !showPrimaryActions;
    }

    private void OnLinuxScreenChanged(object? sender, EventArgs e) => FitLinuxScreen();

    private void FitLinuxScreen(bool bringIntoView = true)
    {
        if (!IsVisible || (Screens.ScreenFromWindow(this) ?? Screens.Primary) is not { } screen)
        {
            return;
        }

        var geometry = (screen.WorkingArea, screen.Scaling);
        if (geometry != _linuxScreen)
        {
            _linuxScreen = geometry;
            ScreenFit.LimitLinux(this, screen, LinuxMinWidth, LinuxMinHeight);
            double menuHeight = Math.Max(100, Math.Floor(screen.WorkingArea.Height / screen.Scaling) - 80);
            MoreMenuScroll.MaxHeight = SettingsMenuScroll.MaxHeight = menuHeight;
        }

        if (bringIntoView)
        {
            Dispatcher.UIThread.Post(() => ScreenFit.MoveOnto(this), DispatcherPriority.Background);
        }
    }

    private void FitLinuxContent()
    {
        var rows = Body.RowDefinitions;
        rows[2].MaxHeight = Math.Max(LinuxMinTabsHeight, Body.Bounds.Height - rows[0].MinHeight - rows[1].ActualHeight);
        var columns = Top.ColumnDefinitions;
        columns[2].MaxWidth = Math.Max(LinuxMinCardWidth, Body.Bounds.Width * 0.46);

        double width = ClientSize.Width;
        bool compact = width < 780;
        StatusBar.ColumnDefinitions[1].Width = compact ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        StatusBar.ColumnDefinitions[3].Width = compact ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        StatusUser.MaxWidth = compact ? Math.Max(75, width * 0.24) : double.PositiveInfinity;
        SupportLinkText.IsVisible = !compact;
        LogCountText.IsVisible = width >= 480;
        foreach (var controls in new Control[] { LogFilters, HistoryNoteText })
        {
            Grid.SetRow(controls, compact ? 1 : 0);
            Grid.SetColumn(controls, compact ? 0 : 2);
            Grid.SetColumnSpan(controls, compact ? 3 : 1);
        }
    }

    /// <summary>Closing the window leaves CUSTOS in the tray, as Custos.exe does. The program password typed for this
    /// window is forgotten (section 4), and the size and the panes are kept for the next time.</summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_replacingForLanguage)
        {
            SaveLayout();
        }
        if (!Quitting && DesktopPlatform.Current.KeepsWindowInTray)
        {
            e.Cancel = true;
            Hide();
            _vm.Monitor.Lock();
        }

        base.OnClosing(e);
    }

    internal void CloseForLanguageChange()
    {
        _replacingForLanguage = true;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        if (CustosEdition.IsLinux)
        {
            Screens.Changed -= OnLinuxScreenChanged;
        }

        _vm.PropertyChanged -= OnViewModelChanged;
        _vm.SelectionRestored -= OnSelectionRestored;
        base.OnClosed(e);
    }

    /// <summary>The size and place of the window and of its panes, as this user left them (ui.json, at 100 % scale).</summary>
    private void RestoreLayout()
    {
        var ui = _vm.Ui;
        if (CustosEdition.IsLinux)
        {
            RestoreLinuxBounds(ui);
        }
        else if (ui.Width is >= 640 and <= 10000 && ui.Height is >= 520 and <= 10000)
        {
            Width = ui.Width.Value;
            Height = ui.Height.Value;
        }

        if (!CustosEdition.IsLinux && ui.Left is { } left && ui.Top is { } top
            && Screens.All.Any(s => s.WorkingArea.Contains(new PixelPoint(left + 40, top + 20))))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(left, top);
        }

        if (ui.Maximized)
        {
            WindowState = WindowState.Maximized;
        }

        _vm.ShowCard = ui.CardVisible;
        if (ui.CardWidth is { } cardWidth && cardWidth >= (CustosEdition.IsLinux ? LinuxMinCardWidth : 280) && cardWidth <= 2000)
        {
            _cardWidth = new GridLength(ui.CardWidth.Value);
        }

        if (ui.TabsHeight is { } tabsHeight && tabsHeight >= (CustosEdition.IsLinux ? LinuxMinTabsHeight : 120) && tabsHeight <= 2000)
        {
            Body.RowDefinitions[2].Height = new GridLength(ui.TabsHeight.Value);
        }

        _vm.LogLevel = Core.Agent.LogFilterLevels.IsKnown(ui.JournalLevel) ? ui.JournalLevel : Core.Agent.LogFilterLevels.All;
        _vm.LogSelectedOnly = ui.JournalSelectedOnly;
        _vm.IsHistoryTab = ui.BottomTab == 1;
        _vm.IsProgressTab = ui.BottomTab == 2;
        _vm.IsLogTab = ui.BottomTab is not (1 or 2);
    }

    private void RestoreLinuxBounds(UiState ui)
    {
        if (ui.Width is > 0 and <= 10000)
        {
            Width = Math.Max(LinuxMinWidth, ui.Width.Value);
        }

        if (ui.Height is > 0 and <= 10000)
        {
            Height = Math.Max(LinuxMinHeight, ui.Height.Value);
        }

        Screen? screen = null;
        PixelPoint? saved = null;
        if (ui.Left is { } left && ui.Top is { } top)
        {
            var title = new PixelPoint(left + 40, top + 20);
            screen = Screens.All.FirstOrDefault(candidate => candidate.WorkingArea.Contains(title));
            if (screen is not null)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                saved = new PixelPoint(left, top);
            }
        }

        if ((screen ?? Screens.ScreenFromWindow(this) ?? Screens.Primary) is { } startScreen)
        {
            ScreenFit.LimitLinux(this, startScreen, LinuxMinWidth, LinuxMinHeight);
            if (saved is { } position)
            {
                Position = ScreenFit.ClampPosition(position, new Size(Width, Height), startScreen);
            }
        }
    }

    internal void SaveLayout()
    {
        var ui = _vm.Ui;
        ui.Maximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal)
        {
            ui.Left = Position.X;
            ui.Top = Position.Y;
            ui.Width = (int)Math.Round(CustosEdition.IsLinux ? ClientSize.Width : Width);
            ui.Height = (int)Math.Round(CustosEdition.IsLinux ? ClientSize.Height : Height);
        }

        ui.CardVisible = _vm.ShowCard;
        if (CardPane.Bounds.Width > 0)
        {
            ui.CardWidth = (int)Math.Round(CardPane.Bounds.Width);
        }

        ui.TabsHeight = (int)Math.Round(Body.RowDefinitions[2].ActualHeight);
        ui.BottomTab = _vm.IsHistoryTab ? 1 : _vm.IsProgressTab ? 2 : 0;
        ui.JournalLevel = _vm.LogLevel;
        ui.JournalSelectedOnly = _vm.LogSelectedOnly;
        ui.Save();
    }

    /// <summary>The column headers of the task list: a click sorts, a second click turns the order round.</summary>
    private void BuildHeader()
    {
        string[] titles = [Strings.ColumnTask, Strings.ColumnType, Strings.ColumnSchedule, Strings.ColumnLastRun, Strings.ColumnResult, Strings.ColumnNextRun];
        var theme = this.FindResource("HeaderButton") as ControlTheme;
        for (int i = 0; i < titles.Length; i++)
        {
            int column = i;
            var arrow = new Icon { Size = 18, Kind = "arrow_upward", IsVisible = false, VerticalAlignment = VerticalAlignment.Center };
            var text = new TextBlock { Text = titles[i], VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            var button = new Button
            {
                Theme = theme,
                Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { text, arrow } },
            };
            button.Click += (_, _) =>
            {
                _vm.SortBy(column);
                ShowSort();
            };
            Grid.SetColumn(button, i);
            Header.Children.Add(button);
            _sortArrows.Add(arrow);
        }

        ShowSort();
    }

    private void ShowSort()
    {
        for (int i = 0; i < _sortArrows.Count; i++)
        {
            _sortArrows[i].IsVisible = i == _vm.SortColumn;
            _sortArrows[i].Kind = _vm.SortAscending ? "arrow_upward" : "arrow_downward";
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (CustosEdition.IsLinux && e.PropertyName == nameof(MainViewModel.ShowAdminsOnly))
        {
            FitToolbar();
        }

        if (e.PropertyName == nameof(MainViewModel.ShowCard))
        {
            ShowCardColumn();
        }
        else if (e.PropertyName == nameof(MainViewModel.ColorChoice))
        {
            // Every open window: the window of the status-only mode, the editor, a dialog.
            if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            {
                foreach (var window in desktop.Windows)
                {
                    TitleBar.Paint(window);
                }
            }

            TitleBar.Paint(this);
            BuildSettingsMenu();
        }
        else if (e.PropertyName == nameof(MainViewModel.ThemeNow))
        {
            BuildSettingsMenu();
        }
        else if (e.PropertyName == nameof(MainViewModel.SelectedTask) && !_selecting)
        {
            ShowSelection();
        }
    }

    /// <summary>Puts the selection of the model into the list (after it was made anew, or a new task was selected).</summary>
    internal void ShowSelection()
    {
        _selecting = true;
        try
        {
            var selection = TaskList.Selection;
            selection.BeginBatchUpdate();
            selection.Clear();
            foreach (var row in _vm.Selection)
            {
                int index = _vm.Tasks.IndexOf(row);
                if (index >= 0)
                {
                    selection.Select(index);
                }
            }

            selection.EndBatchUpdate();
            if (_vm.SelectedTask is { } current)
            {
                TaskList.ScrollIntoView(current);
            }
        }
        finally
        {
            _selecting = false;
        }
    }

    private void OnSelectionRestored(object? sender, EventArgs e) => ShowSelection();

    private void OnTaskSelection(object? sender, SelectionChangedEventArgs e)
    {
        if (_selecting)
        {
            return;
        }

        _selecting = true;
        try
        {
            _vm.Select([.. _vm.Tasks.Where(r => TaskList.SelectedItems?.Contains(r) == true)]);
        }
        finally
        {
            _selecting = false;
        }
    }

    /// <summary>«Карточка» off gives the list the whole width; on brings the card back at the width it had.</summary>
    private void ShowCardColumn()
    {
        var columns = Top.ColumnDefinitions;
        if (_vm.ShowCard)
        {
            columns[1].Width = new GridLength(14);
            columns[2].MinWidth = CustosEdition.IsLinux ? LinuxMinCardWidth : 280;
            columns[2].Width = _cardWidth;
        }
        else
        {
            if (CardPane.Bounds.Width > 0)
            {
                _cardWidth = new GridLength(CardPane.Bounds.Width);
            }

            columns[1].Width = new GridLength(0);
            columns[2].MinWidth = 0;
            columns[2].Width = new GridLength(0);
        }

        CardSplitter.IsVisible = _vm.ShowCard;
        CardPane.IsVisible = _vm.ShowCard;
    }

    /// <summary>
    /// The «Настройки» menu: the theme of the CUSTOS settings (for all users) and the colour of this user, each with
    /// a check on the one in use; a colour shows a round sample (its main colour on its background, in the theme now
    /// shown).
    /// </summary>
    private void BuildSettingsMenu()
    {
        var theme = this.FindResource("Swatch") as ControlTheme;
        ThemeMenu.Children.Clear();
        if (_vm.ShowsSettings)
        {
            foreach (var (id, icon, text) in (ReadOnlySpan<(string, string, string)>)[
                (Core.Model.Themes.Auto, "brightness_auto", DesktopPlatform.Current.ThemeAuto),
                (Core.Model.Themes.Light, "light_mode", Strings.ThemeLight),
                (Core.Model.Themes.Dark, "dark_mode", Strings.ThemeDark)])
            {
                var item = MenuChoice(theme, "theme", id == _vm.ThemeNow, new Icon { Kind = icon }, text);
                string chosen = id;
                item.Click += (_, _) =>
                {
                    CloseMenu(item);
                    Safe.Run(() => _actions.SetThemeAsync(chosen), ShowProblem);
                    BuildSettingsMenu();
                };
                ThemeMenu.Children.Add(item);
            }
        }

        ColorMenu.Children.Clear();
        bool dark = ActualThemeVariant == ThemeVariant.Dark;
        foreach (string id in Palettes.Ids)
        {
            var (primary, surface) = Palettes.Sample(id, dark);
            var sample = new Panel
            {
                Width = 24,
                Height = 24,
                Children =
                {
                    new Ellipse { Fill = new SolidColorBrush(surface), Stroke = this.FindResource("OutlineVariant") as IBrush, StrokeThickness = 1 },
                    new Ellipse { Width = 14, Height = 14, Fill = new SolidColorBrush(primary) },
                },
            };
            var item = MenuChoice(theme, "color", id == _vm.ColorChoice, sample, Palettes.Title(id));
            string chosen = id;
            item.Click += (_, _) =>
            {
                CloseMenu(item);
                _vm.ColorChoice = chosen;
            };
            ColorMenu.Children.Add(item);
        }
    }

    private static RadioButton MenuChoice(ControlTheme? theme, string group, bool on, Control lead, string text) => new()
    {
        Theme = theme,
        GroupName = group,
        IsChecked = on,
        Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            Children = { lead, new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center } },
        },
    };

    private void OnCreate(object? sender, RoutedEventArgs e) => Safe.Run(_actions.CreateAsync, ShowProblem);

    private void OnEdit(object? sender, RoutedEventArgs e) => ToolbarAction(sender, _actions.EditAsync);

    private void OnClone(object? sender, RoutedEventArgs e) => ToolbarAction(sender, _actions.CloneAsync);

    private void OnDelete(object? sender, RoutedEventArgs e) => ToolbarAction(sender, _actions.DeleteAsync);

    private void OnToggle(object? sender, RoutedEventArgs e) => ToolbarAction(sender, _actions.ToggleAsync);

    private void OnRun(object? sender, RoutedEventArgs e) => ToolbarAction(sender, _actions.RunAsync);

    private void OnStop(object? sender, RoutedEventArgs e) => ToolbarAction(sender, _actions.StopAsync);

    private void OnPause(object? sender, RoutedEventArgs e) => ToolbarAction(sender, _actions.PauseAsync);

    private void ToolbarAction(object? sender, Func<Task> action)
    {
        CloseMenu(sender);
        Safe.Run(action, ShowProblem);
    }

    private void OnCompactCard(object? sender, RoutedEventArgs e)
    {
        CloseMenu(sender);
        _vm.ShowCard = !_vm.ShowCard;
    }

    /// <summary>An item of the menu of the task list (a right click); its Tag says which.</summary>
    private void OnTaskMenu(object? sender, RoutedEventArgs e)
    {
        CloseMenu(sender);
        Func<Task>? work = (sender as Control)?.Tag switch
        {
            "run" => _actions.RunAsync,
            "stop" => _actions.StopAsync,
            "edit" => _actions.EditAsync,
            "clone" => _actions.CloneAsync,
            "delete" => _actions.DeleteAsync,
            "toggle" => _actions.ToggleAsync,
            _ => null,
        };
        if (work is not null)
        {
            Safe.Run(work, ShowProblem);
        }
        else
        {
            TaskList.SelectAll();
        }
    }

    private void OnImport(object? sender, RoutedEventArgs e)
    {
        CloseMenu(sender);
        Safe.Run(_actions.ImportAsync, ShowProblem);
    }

    private void OnExport(object? sender, RoutedEventArgs e)
    {
        CloseMenu(sender);
        Safe.Run(_actions.ExportAsync, ShowProblem);
    }

    private void OnCobianImport(object? sender, RoutedEventArgs e)
    {
        CloseMenu(sender);
        Safe.Run(_actions.CobianImportAsync, ShowProblem);
    }

    private void OnRestore(object? sender, RoutedEventArgs e)
    {
        CloseMenu(sender);
        Safe.Run(_actions.RestoreAsync, ShowProblem);
    }

    /// <summary>A double click on a run of the history: its log (section 6.10).</summary>
    private void OnHistoryDoubleTapped(object? sender, TappedEventArgs e) => OpenRunLog(sender);

    private void OnHistoryKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            OpenRunLog(sender);
        }
    }

    private void OpenRunLog(object? sender)
    {
        if (sender is ListBox { SelectedItem: HistoryRow row })
        {
            var log = new RunLogWindow(_vm.IsDemo ? null : _vm.Monitor, row.Run);
            Safe.Run(() => log.ShowDialog(this), ShowProblem);
        }
    }

    private void OnInstallUpdate(object? sender, RoutedEventArgs e) => Safe.Run(_actions.InstallUpdateAsync, ShowProblem);

    /// <summary>«Что нового»: the page of the release on GitHub.</summary>
    private void OnReleasePage(object? sender, RoutedEventArgs e)
    {
        if (_vm.Monitor.Snapshot?.ProgramUpdate.PageUrl is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            Safe.Run(async () => await Launcher.LaunchUriAsync(uri), ShowProblem);
        }
    }

    private void OnSettings(object? sender, RoutedEventArgs e)
    {
        CloseMenu(sender);
        Safe.Run(_actions.SettingsAsync, ShowProblem);
    }

    private void OnStartSystemService(object? sender, RoutedEventArgs e) => RunSystemServiceAction(sender, SystemServiceAction.Start);

    private void OnStopSystemService(object? sender, RoutedEventArgs e) => RunSystemServiceAction(sender, SystemServiceAction.Stop);

    private void OnRestartSystemService(object? sender, RoutedEventArgs e) => RunSystemServiceAction(sender, SystemServiceAction.Restart);

    private void RunSystemServiceAction(object? sender, SystemServiceAction action)
    {
        CloseMenu(sender);
        Safe.Run(() => _actions.ControlSystemServiceAsync(action), ShowProblem);
    }

    /// <summary>A double click: the task editor for those who may change tasks, else the card.</summary>
    private void OnTaskDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_vm.CanEditOne)
        {
            Safe.Run(_actions.EditAsync, ShowProblem);
        }
        else if (!_vm.ShowCard)
        {
            _vm.ShowCard = true;
        }
    }

    private void OnTaskKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && e.KeyModifiers == KeyModifiers.None && _vm.CanDelete)
        {
            e.Handled = true;
            Safe.Run(_actions.DeleteAsync, ShowProblem);
        }
        else if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None && _vm.CanEditOne)
        {
            e.Handled = true;
            Safe.Run(_actions.EditAsync, ShowProblem);
        }
    }

    private void OnLogLevel(object? sender, RoutedEventArgs e)
    {
        CloseMenu(sender);
        if ((sender as Control)?.Tag is string level)
        {
            _vm.LogLevel = level;
        }
    }

    private async void OnDonate(object? sender, RoutedEventArgs e)
    {
        try
        {
            await Launcher.LaunchUriAsync(new Uri(CustosInfo.DonatePage));
        }
        catch (InvalidOperationException)
        {
            // No browser to open the page with.
        }
    }

    private void OnSnackClose(object? sender, RoutedEventArgs e) => _vm.SnackVisible = false;

    /// <summary>A request the service refused or could not answer: its words in the snackbar.</summary>
    private void ShowProblem(Exception ex) => _vm.Snack(ex.Message);

    /// <summary>A menu item was clicked: the menu goes away, as menus do.</summary>
    private static void CloseMenu(object? sender)
    {
        if (sender is ILogical item && item.FindLogicalAncestorOfType<Popup>() is { } popup)
        {
            popup.Close();
        }
    }
}
