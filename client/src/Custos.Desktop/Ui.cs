using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace Custos.Desktop;

/// <summary>
/// Small builders for the forms that are made in code (the editor, the settings, the dialogs): the same M3 look as
/// the main window, with fewer lines than XAML for forms whose rows depend on what is chosen.
/// </summary>
internal static class Ui
{
    public const double Gap = 12;

    public static TextBlock Title(string text) => new() { Text = text, Classes = { "title" }, Margin = new Thickness(0, 0, 0, 8) };

    public static TextBlock Heading(string text) => new()
    {
        Text = text,
        FontSize = 15,
        FontWeight = FontWeight.Medium,
        Margin = new Thickness(0, 14, 0, 4),
    };

    /// <summary>A line of explanation in the muted colour.</summary>
    public static TextBlock Note(string text) => new()
    {
        Text = text,
        Classes = { "small", "var" },
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 2, 0, 4),
    };

    public static TextBlock Label(string text) => new()
    {
        Text = text,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, Gap, 0),
    };

    public static TextBox TextBox(string? text = null, string? watermark = null, double width = double.NaN) => new()
    {
        Text = text ?? string.Empty,
        PlaceholderText = watermark,
        Width = width,
        MinWidth = double.IsNaN(width) ? 200 : 0,
        VerticalContentAlignment = VerticalAlignment.Center,
    };

    public static TextBox Lines(IEnumerable<string> lines, double height = 90) => new()
    {
        Text = string.Join('\n', lines),
        AcceptsReturn = true,
        TextWrapping = TextWrapping.NoWrap,
        Height = height,
    };

    public static List<string> ReadLines(TextBox box) =>
        (box.Text ?? string.Empty).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();

    public static NumericUpDown Number(int value, int min, int max, double width = 120) => new()
    {
        Value = Math.Clamp(value, min, max),
        Minimum = min,
        Maximum = max,
        Increment = 1,
        FormatString = "0",
        Width = width,
        HorizontalAlignment = HorizontalAlignment.Left,
    };

    public static int Read(NumericUpDown box) => (int)(box.Value ?? box.Minimum);

    public static CheckBox Check(string text, bool value) => new() { Content = Wrap(text), IsChecked = value };

    public static bool Read(CheckBox box) => box.IsChecked == true;

    public static RadioButton Radio(string group, string text, bool value, string? hint = null)
    {
        object content = Wrap(text);
        if (hint is not null)
        {
            content = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = text, FontWeight = FontWeight.Medium, TextWrapping = TextWrapping.Wrap },
                    Note(hint),
                },
            };
        }

        return new RadioButton { GroupName = group, Content = content, IsChecked = value, Margin = new Thickness(0, 2) };
    }

    /// <summary>A drop-down of (value, text) pairs.</summary>
    public static ComboBox Choice(IEnumerable<(string Value, string Text)> items, string? selected, double width = 280)
    {
        var list = items.ToList();
        var box = new ComboBox
        {
            ItemsSource = list.Select(i => i.Text).ToList(),
            Width = width,
            HorizontalAlignment = HorizontalAlignment.Left,
            Tag = list.Select(i => i.Value).ToList(),
        };
        int index = list.FindIndex(i => i.Value == selected);
        box.SelectedIndex = index >= 0 ? index : 0;
        return box;
    }

    public static string Read(ComboBox box) =>
        box.Tag is List<string> values && box.SelectedIndex >= 0 && box.SelectedIndex < values.Count ? values[box.SelectedIndex] : string.Empty;

    public static void Select(ComboBox box, string value)
    {
        if (box.Tag is List<string> values && values.IndexOf(value) is int index and >= 0)
        {
            box.SelectedIndex = index;
        }
    }

    public static Button Filled(string text, Action click) => Make("FilledButton", text, click);

    public static Button Outlined(string text, Action click) => Make("OutlinedButton", text, click);

    public static Button Text(string text, Action click) => Make("TextButton", text, click);

    /// <summary>Label on the left, the control on the right, an optional note under it.</summary>
    public static Control Row(string label, Control control, string? note = null, double labelWidth = 190)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions($"{labelWidth},*"), Margin = new Thickness(0, 4) };
        var text = Label(label);
        text.TextWrapping = TextWrapping.Wrap;
        text.VerticalAlignment = VerticalAlignment.Top;
        text.Margin = new Thickness(0, 8, Gap, 0);
        grid.Children.Add(text);
        Control right = control;
        if (note is not null)
        {
            right = new StackPanel { Spacing = 2, Children = { control, Note(note) } };
        }

        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        return grid;
    }

    public static StackPanel Horizontal(params Control[] children)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var child in children)
        {
            child.VerticalAlignment = VerticalAlignment.Center;
            panel.Children.Add(child);
        }

        return panel;
    }

    public static StackPanel Stack(params Control[] children)
    {
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.AddRange(children);
        return panel;
    }

    /// <summary>A page of a form: scrolls when it is taller than the window.</summary>
    public static ScrollViewer Page(params Control[] children)
    {
        var panel = new StackPanel { Spacing = 6, Margin = new Thickness(24, 18, 28, 24) };
        panel.Children.AddRange(children);
        return new ScrollViewer { Content = panel, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }

    public static Border Divider() => new() { Classes = { "divider" }, Margin = new Thickness(0, 8) };

    /// <summary>A red line for what is wrong in a form.</summary>
    public static TextBlock Error() => new()
    {
        Foreground = Brush("Error"),
        TextWrapping = TextWrapping.Wrap,
        IsVisible = false,
        Margin = new Thickness(0, 6, 0, 0),
    };

    public static void Show(TextBlock error, string? text)
    {
        error.Text = text ?? string.Empty;
        error.IsVisible = !string.IsNullOrEmpty(text);
    }

    /// <summary>A list with a fixed height whose rows are texts (sources, destinations, events).</summary>
    public static ListBox List(double height = 160) => new()
    {
        Height = height,
        Theme = Theme("M3List"),
        Background = Brush("SurfaceContainerLow"),
        CornerRadius = new CornerRadius(12),
        Padding = new Thickness(4),
    };

    public static IBrush? Brush(string key) =>
        Application.Current?.TryFindResource(key, Application.Current.ActualThemeVariant, out object? value) == true ? value as IBrush : null;

    public static ControlTheme? Theme(string key) =>
        Application.Current?.TryFindResource(key, out object? value) == true ? value as ControlTheme : null;

    private static TextBlock Wrap(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };

    private static Button Make(string theme, string text, Action click)
    {
        var button = new Button { Theme = Theme(theme), Content = text, VerticalAlignment = VerticalAlignment.Center };
        button.Click += (_, e) =>
        {
            e.Handled = true;
            click();
        };
        return button;
    }
}

/// <summary>Click handlers that run async work and never let an exception out of an event.</summary>
internal static class Safe
{
    public static async void Run(Func<Task> work, Action<Exception>? failed = null)
    {
        try
        {
            await work();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            failed?.Invoke(ex);
        }
    }

    /// <summary>
    /// Runs <paramref name="work"/> on a click. It is called also when the click is already handled: the buttons of
    /// <see cref="Ui"/> handle it in their own first handler, after which an ordinary one was never called (the
    /// «ОК» of every dialog did nothing).
    /// </summary>
    public static void OnClick(this Button button, Func<Task> work, Action<Exception>? failed = null) =>
        button.AddHandler(Button.ClickEvent, (_, e) =>
        {
            e.Handled = true;
            Run(work, failed);
        }, RoutingStrategies.Bubble, handledEventsToo: true);
}
