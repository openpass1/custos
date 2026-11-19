using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Custos.Core;
using Custos.Gui;

namespace Custos.Mac.Gui;

/// <summary>The main window: toolbar, task list, card, the tabs under them and the status bar (docs/client-ui.md).</summary>
internal sealed partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly MainActions _actions;
    private readonly List<Icon> _sortArrows = [];
    private GridLength _cardWidth = new(350);

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
        BuildHeader();
        ShowCardColumn();
        vm.PropertyChanged += OnViewModelChanged;
    }

    public MainViewModel ViewModel => _vm;

    /// <summary>Set when the program quits: then the window really closes.</summary>
    public bool Quitting { get; set; }

    /// <summary>Closing the window leaves CUSTOS in the menu bar, as the window of Windows goes to the tray. The
    /// program password typed for this window is forgotten (section 4).</summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!Quitting)
        {
            e.Cancel = true;
            Hide();
            _vm.Monitor.Lock();
        }

        base.OnClosing(e);
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

    private void OnCreate(object? sender, RoutedEventArgs e) => Safe.Run(_actions.CreateAsync, ShowProblem);

    private void OnEdit(object? sender, RoutedEventArgs e) => Safe.Run(_actions.EditAsync, ShowProblem);

    private void OnClone(object? sender, RoutedEventArgs e) => Safe.Run(_actions.CloneAsync, ShowProblem);

    private void OnDelete(object? sender, RoutedEventArgs e) => Safe.Run(_actions.DeleteAsync, ShowProblem);

    private void OnToggle(object? sender, RoutedEventArgs e) => Safe.Run(_actions.ToggleAsync, ShowProblem);

    private void OnRun(object? sender, RoutedEventArgs e) => Safe.Run(_actions.RunAsync, ShowProblem);

    private void OnStop(object? sender, RoutedEventArgs e) => Safe.Run(_actions.StopAsync, ShowProblem);

    private void OnPause(object? sender, RoutedEventArgs e) => Safe.Run(_actions.PauseAsync, ShowProblem);

    private void OnRestore(object? sender, RoutedEventArgs e) => Safe.Run(_actions.RestoreAsync, ShowProblem);

    private void OnSettings(object? sender, RoutedEventArgs e) => Safe.Run(_actions.SettingsAsync, ShowProblem);

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

    private void OnTaskDoubleTapped(object? sender, TappedEventArgs e) => Safe.Run(_actions.EditAsync, ShowProblem);

    private void OnRunDoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as ListBox)?.SelectedItem is HistoryRow { RunId: { } runId } row)
        {
            Safe.Run(() => RunLogWindow.ShowAsync(this, _vm.Monitor, runId, row.TaskName ?? string.Empty, row.StartedAt), ShowProblem);
        }
    }

    private void ShowProblem(Exception ex) => _vm.Snack(ex.Message);

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
            // No browser to open the page with: nothing to do in a trial window.
        }
    }

    private void OnSnackClose(object? sender, RoutedEventArgs e) => _vm.SnackVisible = false;

    /// <summary>A menu item was clicked: the menu goes away, as menus do.</summary>
    private static void CloseMenu(object? sender)
    {
        if (sender is ILogical item && item.FindLogicalAncestorOfType<Popup>() is { } popup)
        {
            popup.Close();
        }
    }
}
