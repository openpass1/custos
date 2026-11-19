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

namespace Custos.Preview;

/// <summary>The main window: toolbar, task list, card, the tabs under them and the status bar (docs/client-ui.md).</summary>
internal sealed partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
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
        DataContext = vm;
        InitializeComponent();
        BuildHeader();
        ShowCardColumn();
        vm.PropertyChanged += OnViewModelChanged;
        ActualThemeVariantChanged += (_, _) =>
        {
            TitleBar.Paint(this);
            BuildColorMenu();
        };
        BuildColorMenu();
    }

    public MainViewModel ViewModel => _vm;

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        TitleBar.Paint(this);
    }

    protected override void OnClosed(EventArgs e)
    {
        _vm.PropertyChanged -= OnViewModelChanged;
        _vm.Dispose();
        base.OnClosed(e);
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
            BuildColorMenu();
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
    /// The colours of the «Настройки» menu: a round sample of each scheme (its main colour on its background, in the
    /// theme now shown) and a check on the one in use.
    /// </summary>
    private void BuildColorMenu()
    {
        ColorMenu.Children.Clear();
        var theme = this.FindResource("Swatch") as ControlTheme;
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
            var item = new RadioButton
            {
                Theme = theme,
                GroupName = "color",
                IsChecked = id == _vm.ColorChoice,
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 12,
                    Children = { sample, new TextBlock { Text = Palettes.Title(id), VerticalAlignment = VerticalAlignment.Center } },
                },
            };
            item.Click += (_, _) =>
            {
                CloseMenu(item);
                _vm.ColorChoice = id;
            };
            ColorMenu.Children.Add(item);
        }
    }

    private async void OnEdit(object? sender, RoutedEventArgs e) => await OpenEditor(_vm.EditSelected());

    private async void OnCreate(object? sender, RoutedEventArgs e) => await OpenEditor(_vm.CreateNew());

    private async void OnTaskDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_vm.CanEdit)
        {
            await OpenEditor(_vm.EditSelected());
        }
    }

    /// <summary>The task editor as a dialog; «Сохранить» only says that the trial window keeps nothing.</summary>
    private async Task OpenEditor(EditorModel? model)
    {
        if (model is null)
        {
            return;
        }

        var editor = new EditorWindow(model);
        await editor.ShowDialog(this);
        if (editor.Saved)
        {
            _vm.Snack(Strings.PreviewNotSaved);
        }
    }

    private void OnReadOnly(object? sender, RoutedEventArgs e)
    {
        CloseMenu(sender);
        _vm.ReadOnlyNote();
    }

    private void OnTheme(object? sender, RoutedEventArgs e)
    {
        CloseMenu(sender);
        string? tag = (sender as Control)?.Tag as string;
        _vm.ThemeChoice = tag is Core.Model.Themes.Light or Core.Model.Themes.Dark ? tag : null;
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
