using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Custos.Gui;

namespace Custos.Desktop;

/// <summary>
/// A dialog of the new window made of form pieces (Form.cs): the title, the pieces in a pane that scrolls when the
/// screen is too low for them, what is wrong in red, «Отмена» and the main button at the bottom right. The main button
/// runs <see cref="OkAsync"/>, which says whether the dialog may close; the buttons are off while it runs.
/// </summary>
internal abstract class FormDialog : Window
{
    private readonly TextBlock _error = new()
    {
        Foreground = Ui.Brush("Error"),
        TextWrapping = TextWrapping.Wrap,
        IsVisible = false,
        Margin = new Thickness(0, 0, 16, 0),
        VerticalAlignment = VerticalAlignment.Center,
    };

    private readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _cancel;
    private bool _busy;
    private bool _result;

    protected FormDialog(string title, double width = 640, bool sizable = false)
    {
        Title = title;
        Width = width;
        MinWidth = Math.Min(width, 480);
        SizeToContent = SizeToContent.Height;
        CanResize = sizable;
        CanMinimize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Classes.Add("surface");

        var view = new FormView { Items = Items, Margin = new Thickness(20, 0, 20, 12) };
        var head = new TextBlock { Text = title, Classes = { "headline" }, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(20, 12, 20, 6) };
        Scroll = new ScrollViewer { Content = view, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        _cancel = Ui.Text(Strings.ButtonCancel, () => Close());
        OkButton = Ui.Filled(Strings.ButtonOk, () => { });
        OkButton.IsDefault = true;
        OkButton.OnClick(OkPressedAsync, ex => ShowError(ex.Message));
        _buttons.Children.Add(_cancel);
        _buttons.Children.Add(OkButton);
        var bottom = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(20, 8, 16, 10),
            Children = { _error, _buttons },
        };
        Grid.SetColumn(_buttons, 1);
        var dock = new DockPanel();
        DockPanel.SetDock(head, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        dock.Children.Add(head);
        dock.Children.Add(bottom);
        dock.Children.Add(new Border { Classes = { "divider" }, Height = 1, Margin = new Thickness(0), [DockPanel.DockProperty] = Dock.Bottom });
        dock.Children.Add(Scroll);
        Content = dock;

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && !_busy)
            {
                Close();
            }
        };
        Opened += (_, _) => TitleBar.Paint(this);
        ScreenFit.Keep(this);
        ActualThemeVariantChanged += (_, _) => TitleBar.Paint(this);
        Closing += (_, e) => e.Cancel = _busy;
    }

    /// <summary>The pieces of the dialog, top to bottom.</summary>
    public ObservableCollection<FormItem> Items { get; } = [];

    protected Button OkButton { get; }

    /// <summary>«Отмена», or «Закрыть» where nothing can be changed.</summary>
    protected Button CancelButton => _cancel;

    protected ScrollViewer Scroll { get; }

    /// <summary>Opens the dialog over <paramref name="owner"/>; true when it was closed with the main button.</summary>
    public async Task<bool> ShowAsync(Window owner, ServiceMonitor? monitor = null)
    {
        // Sized for the screen before it is placed over the owner, so that it opens whole on it.
        ScreenFit.Limit(this, owner);
        var previous = monitor?.AskPassword;
        if (monitor is not null)
        {
            // The program password belongs to the active modal window, not its disabled owner.
            monitor.AskPassword = () => Dialogs.ProgramPassword(this, monitor);
        }

        try
        {
            await ShowDialog(owner);
        }
        finally
        {
            if (monitor is not null)
            {
                monitor.AskPassword = previous;
            }
        }

        return _result;
    }

    /// <summary>The main button: checks and saves, and says whether the dialog may close.</summary>
    protected abstract Task<bool> OkAsync();

    /// <summary>What is wrong, in red beside the buttons; null hides it.</summary>
    protected void ShowError(string? text)
    {
        _error.Text = text ?? string.Empty;
        _error.IsVisible = !string.IsNullOrEmpty(text);
    }

    /// <summary>The buttons are off while a request to the service runs.</summary>
    protected bool Busy
    {
        get => _busy;
        set
        {
            _busy = value;
            OkButton.IsEnabled = !value;
            _cancel.IsEnabled = !value;
            Cursor = value ? new Cursor(StandardCursorType.Wait) : Cursor.Default;
        }
    }

    /// <summary>A button beside «Отмена», on its left.</summary>
    protected Button AddButton(string text, Func<Task> work)
    {
        var button = Ui.Text(text, () => { });
        button.OnClick(work, ex => ShowError(ex.Message));
        _buttons.Children.Insert(0, button);
        return button;
    }

    /// <summary>Closes the dialog as done with the main button.</summary>
    protected void Done()
    {
        _result = true;
        _busy = false;
        Close();
    }

    /// <summary>A folder picked in the dialog of the system, starting at <paramref name="start"/> when it exists.</summary>
    public static async Task<string?> PickFolderAsync(Window owner, string title, string? start = null)
    {
        var options = new FolderPickerOpenOptions { Title = title, AllowMultiple = false };
        if (!string.IsNullOrWhiteSpace(start) && Directory.Exists(start))
        {
            options.SuggestedStartLocation = await owner.StorageProvider.TryGetFolderFromPathAsync(start);
        }

        var folders = await owner.StorageProvider.OpenFolderPickerAsync(options);
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    /// <summary>A file picked in the dialog of the system.</summary>
    public static async Task<string?> PickFileAsync(Window owner, string title, string? start = null, IReadOnlyList<FilePickerFileType>? types = null)
    {
        var options = new FilePickerOpenOptions { Title = title, AllowMultiple = false, FileTypeFilter = types };
        string? folder = string.IsNullOrWhiteSpace(start) ? null : File.Exists(start) ? Path.GetDirectoryName(start) : Directory.Exists(start) ? start : null;
        if (folder is not null)
        {
            options.SuggestedStartLocation = await owner.StorageProvider.TryGetFolderFromPathAsync(folder);
        }

        var files = await owner.StorageProvider.OpenFilePickerAsync(options);
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    /// <summary>Where to save a file, picked in the dialog of the system; null if the user cancelled.</summary>
    public static async Task<string?> PickSaveFileAsync(Window owner, string title, string suggestedName, string extension,
        IReadOnlyList<FilePickerFileType>? types = null)
    {
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = extension,
            FileTypeChoices = types,
        });
        return file?.TryGetLocalPath();
    }

    /// <summary>Puts a text on the clipboard of the system.</summary>
    public static async Task CopyAsync(Window owner, string text)
    {
        if (owner.Clipboard is { } clipboard)
        {
            await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, text);
        }
    }

    private async Task OkPressedAsync()
    {
        if (_busy)
        {
            return;
        }

        ShowError(null);
        Busy = true;
        bool done;
        try
        {
            done = await OkAsync();
        }
        catch (ServiceUnavailableException ex)
        {
            ShowError(ex.Message);
            done = false;
        }
        finally
        {
            Busy = false;
        }

        if (done)
        {
            Done();
        }
    }
}
