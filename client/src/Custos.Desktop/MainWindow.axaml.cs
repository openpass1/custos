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
using Avalonia.Styling;
using Custos.Core;
using Custos.Gui;

namespace Custos.Desktop;

/// <summary>The main window: toolbar, task list, card, the tabs under them and the status bar (docs/client-ui.md).</summary>
internal sealed partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly MainActions _actions;
    private readonly List<Icon> _sortArrows = [];
    private GridLength _cardWidth = new(350);
    private bool _selecting;

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
        RestoreLayout();
        BuildHeader();
        ShowCardColumn();
        vm.PropertyChanged += OnViewModelChanged;
        vm.SelectionRestored += (_, _) => ShowSelection();
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

    /// <summary>Set when the program quits: then the window really closes.</summary>
    public bool Quitting { get; set; }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        TitleBar.Paint(this);
    }

    /// <summary>Closing the window leaves CUSTOS in the tray, as Custos.exe does. The program password typed for this
    /// window is forgotten (section 4), and the size and the panes are kept for the next time.</summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        SaveLayout();
        if (!Quitting)
        {
            e.Cancel = true;
            Hide();
            _vm.Monitor.Lock();
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _vm.PropertyChanged -= OnViewModelChanged;
        base.OnClosed(e);
    }

    /// <summary>The size and place of the window and of its panes, as this user left them (ui.json, at 100 % scale).</summary>
    private void RestoreLayout()
    {
        var ui = _vm.Ui;
        if (ui.Width is >= 820 and <= 10000 && ui.Height is >= 520 and <= 10000)
        {
            Width = ui.Width.Value;
            Height = ui.Height.Value;
        }

        if (ui.Left is { } left && ui.Top is { } top && Screens.All.Any(s => s.WorkingArea.Contains(new PixelPoint(left + 40, top + 20))))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(left, top);
        }

        if (ui.Maximized)
        {
            WindowState = WindowState.Maximized;
        }

        _vm.ShowCard = ui.CardVisible;
        if (ui.CardWidth is >= 280 and <= 2000)
        {
            _cardWidth = new GridLength(ui.CardWidth.Value);
        }

        if (ui.TabsHeight is >= 120 and <= 2000)
        {
            Body.RowDefinitions[2].Height = new GridLength(ui.TabsHeight.Value);
        }

        _vm.LogLevel = Core.Agent.LogFilterLevels.IsKnown(ui.JournalLevel) ? ui.JournalLevel : Core.Agent.LogFilterLevels.All;
        _vm.LogSelectedOnly = ui.JournalSelectedOnly;
        _vm.IsHistoryTab = ui.BottomTab == 1;
        _vm.IsProgressTab = ui.BottomTab == 2;
        _vm.IsLogTab = ui.BottomTab is not (1 or 2);
    }

    private void SaveLayout()
    {
        var ui = _vm.Ui;
        ui.Maximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal)
        {
            ui.Left = Position.X;
            ui.Top = Position.Y;
            ui.Width = (int)Math.Round(Width);
            ui.Height = (int)Math.Round(Height);
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
        if (e.PropertyName == nameof(MainViewModel.ShowCard))
        {
            ShowCardColumn();
        }
        else if (e.PropertyName == nameof(MainViewModel.ColorChoice))
        {
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
    private void ShowSelection()
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
            columns[2].MinWidth = 280;
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

    private void OnEdit(object? sender, RoutedEventArgs e) => Safe.Run(_actions.EditAsync, ShowProblem);

    private void OnClone(object? sender, RoutedEventArgs e) => Safe.Run(_actions.CloneAsync, ShowProblem);

    private void OnDelete(object? sender, RoutedEventArgs e) => Safe.Run(_actions.DeleteAsync, ShowProblem);

    private void OnToggle(object? sender, RoutedEventArgs e) => Safe.Run(_actions.ToggleAsync, ShowProblem);

    private void OnRun(object? sender, RoutedEventArgs e) => Safe.Run(_actions.RunAsync, ShowProblem);

    private void OnStop(object? sender, RoutedEventArgs e) => Safe.Run(_actions.StopAsync, ShowProblem);

    private void OnPause(object? sender, RoutedEventArgs e) => Safe.Run(_actions.PauseAsync, ShowProblem);

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

    /// <summary>What the new window does not have yet: it says so and where to find it meanwhile.</summary>
    private void OnNotYet(object? sender, RoutedEventArgs e)
    {
        CloseMenu(sender);
        _vm.Snack(DesktopStrings.NotYet);
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
