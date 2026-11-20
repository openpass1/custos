using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace Custos.Mac.Gui;

/// <summary>
/// Small builders for the forms that are made in code (the editor, the settings, the dialogs): the same M3 look as
/// the main window, with fewer lines than XAML for forms whose rows depend on what is chosen. As compact as the new
/// window of Windows: the name of a field on its left, its explanation behind a (?) mark, 28 point fields.
/// </summary>
internal static class Ui
{
    public const double Gap = 12;

    /// <summary>The title of a page of the editor or the settings.</summary>
    public static TextBlock Title(string text) => new() { Text = text, Classes = { "pagetitle" }, Margin = new Thickness(0, 0, 0, 6) };

    /// <summary>A section of a page, in the main colour.</summary>
    public static TextBlock Heading(string text) => new() { Text = text, Classes = { "section" }, Margin = new Thickness(0, 10, 0, 2) };

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

    /// <summary>A number field without the spinner buttons, as in the window of Windows (the arrow keys still step it).</summary>
    public static NumericUpDown Number(int value, int min, int max, double width = 100) => new()
    {
        ShowButtonSpinner = false,
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
            // The hint is behind a (?) mark after the title, as in the window of Windows; pressing the mark does not
            // check the button.
            var mark = Help(hint);
            mark.Margin = new Thickness(8, 0, 0, 0);
            mark.VerticalAlignment = VerticalAlignment.Center;
            mark.PointerPressed += (_, e) => e.Handled = true;
            content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children = { new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center }, mark },
            };
        }

        return new RadioButton { GroupName = group, Content = content, IsChecked = value, Margin = new Thickness(0, 1) };
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

    /// <summary>A tonal button with an icon before the text, like the action buttons of the forms of Windows.</summary>
    public static Button Tonal(string icon, string text, Action click) => WithIcon(Make("TonalButton", text, click), icon, text);

    /// <summary>A round button with only an icon; its text is the tip.</summary>
    public static Button IconButton(string icon, string tip, Action click)
    {
        var button = Make("IconButton", string.Empty, click);
        button.Content = new Icon { Kind = icon, Size = 20 };
        ToolTip.SetTip(button, tip);
        return button;
    }

    /// <summary>The main button of a form that saves: a check mark and «Сохранить».</summary>
    public static Button Save() => WithIcon(Make("FilledButton", MacStrings.Save, () => { }), "check", MacStrings.Save);

    private static Button WithIcon(Button button, string icon, string text)
    {
        button.Padding = new Thickness(16, 0, 24, 0);
        button.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                new Icon { Kind = icon, Size = 18, VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center },
            },
        };
        return button;
    }

    /// <summary>
    /// Label on the left (without the colon of the shared texts, as in the window of Windows), the control on the right,
    /// its explanation behind a (?) mark after it. Without a label the control starts at the left edge.
    /// </summary>
    public static Control Row(string label, Control control, string? note = null, double labelWidth = 190)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions($"{labelWidth},*"), Margin = new Thickness(0, 2) };
        label = label.TrimEnd(':', ' ');
        if (label.Length > 0)
        {
            var text = Label(label);
            text.TextWrapping = TextWrapping.Wrap;
            text.VerticalAlignment = VerticalAlignment.Top;
            text.Margin = new Thickness(0, 5, Gap, 0);
            grid.Children.Add(text);
        }

        Control right = control;
        if (!string.IsNullOrEmpty(note))
        {
            // A text field that fills the line keeps doing so with the mark at its end; anything else (a drop-down, a
            // number, a line of controls) gets the mark right after it.
            bool fills = control is Avalonia.Controls.TextBox or Grid && control.HorizontalAlignment == HorizontalAlignment.Stretch && double.IsNaN(control.Width);
            var line = new Grid { ColumnDefinitions = new ColumnDefinitions(fills ? "*,Auto" : "Auto,*") };
            var mark = Help(note);
            Grid.SetColumn(mark, 1);
            line.Children.Add(control);
            line.Children.Add(mark);
            right = line;
        }

        Grid.SetColumn(right, label.Length > 0 ? 1 : 0);
        Grid.SetColumnSpan(right, label.Length > 0 ? 1 : 2);
        grid.Children.Add(right);
        return grid;
    }

    /// <summary>A (?) mark whose tip is the explanation (a transparent frame around it, so the pointer finds it).</summary>
    public static Control Help(string text)
    {
        var mark = new Border
        {
            Background = Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(6, 5, 0, 0),
            Child = new Icon { Kind = "help", Size = 18, Classes = { "help" } },
        };
        ToolTip.SetTip(mark, text);
        ToolTip.SetShowDelay(mark, 200);
        return mark;
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
        var panel = new StackPanel { Spacing = 4, Margin = new Thickness(20, 12, 20, 16) };
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
        CornerRadius = new CornerRadius(10),
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
    /// <see cref="Ui"/> handle it in their own first handler, after which an ordinary one is never called (the «ОК»
    /// of the dialogs and every other button with async work did nothing).
    /// </summary>
    public static void OnClick(this Button button, Func<Task> work, Action<Exception>? failed = null) =>
        button.AddHandler(Button.ClickEvent, (_, e) =>
        {
            e.Handled = true;
            Run(work, failed);
        }, RoutingStrategies.Bubble, handledEventsToo: true);
}
