using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
        ActualThemeVariantChanged += (_, _) => PaintTitleBar();
    }

    public MainViewModel ViewModel => _vm;

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        PaintTitleBar();
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

    /// <summary>
    /// Windows 11 draws the title bar in the window's own surface colour; Windows 10 only makes it dark in the dark theme.
    /// Elsewhere (and on older Windows) the system title bar stays as it is.
    /// </summary>
    private void PaintTitleBar()
    {
        if (!OperatingSystem.IsWindows() || TryGetPlatformHandle()?.Handle is not { } hwnd || hwnd == IntPtr.Zero)
        {
            return;
        }

        var variant = ActualThemeVariant;
        int dark = variant == ThemeVariant.Dark ? 1 : 0;
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            _ = NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.UseImmersiveDarkMode, ref dark, sizeof(int));
        }

        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)
            && this.TryFindResource("SurfaceContainer", variant, out object? back) && back is ISolidColorBrush caption
            && this.TryFindResource("OnSurface", variant, out object? fore) && fore is ISolidColorBrush text)
        {
            int captionColor = ColorRef(caption.Color);
            int textColor = ColorRef(text.Color);
            _ = NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.CaptionColor, ref captionColor, sizeof(int));
            _ = NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.TextColor, ref textColor, sizeof(int));
        }
    }

    private static int ColorRef(Color color) => color.R | (color.G << 8) | (color.B << 16);
}

internal static partial class NativeMethods
{
    public const int UseImmersiveDarkMode = 20;
    public const int CaptionColor = 35;
    public const int TextColor = 36;

    [LibraryImport("dwmapi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
