using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Custos.Core.Model;
using Custos.Gui;

namespace Custos.Desktop;

/// <summary>
/// The task editor of the new window: the pages of the task on the left, the open page on the right, «Сохранить»
/// sends the task to the service and closes the window once the service stored it.
/// </summary>
internal sealed partial class EditorWindow : Window
{
    private readonly EditorModel _model;
    private bool _saving;

    /// <summary>Only for the XAML previewer of an IDE.</summary>
    public EditorWindow()
        : this(new EditorModel(new ServiceMonitor(), new TaskDefinition { Name = Strings.NewTaskName }, isNew: true, demo: true))
    {
    }

    public EditorWindow(EditorModel model)
    {
        _model = model;
        model.Context.Owner = this;
        DataContext = model;
        InitializeComponent();
        model.PropertyChanged += OnModelChanged;
        ActualThemeVariantChanged += (_, _) => TitleBar.Paint(this);
        ScreenFit.Keep(this);
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.S && e.KeyModifiers == KeyModifiers.Control)
            {
                e.Handled = true;
                OnSave(this, new RoutedEventArgs());
            }
        };
    }

    public EditorModel Model => _model;

    /// <summary>The task as the service stored it, after «Сохранить»; null when the window was closed without it.</summary>
    public TaskDefinition? Saved { get; private set; }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        TitleBar.Paint(this);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // The answer of the service is awaited; closing now would leave the user unsure whether the task was saved.
        if (_saving)
        {
            e.Cancel = true;
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _model.PropertyChanged -= OnModelChanged;
        base.OnClosed(e);
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EditorModel.Page))
        {
            PageScroll.Offset = default;
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    private void OnSave(object? sender, RoutedEventArgs e) => Safe.Run(SaveAsync, ex => _model.Problem = ex.Message);

    private async Task SaveAsync()
    {
        if (_saving)
        {
            return;
        }

        _saving = true;
        SaveButton.IsEnabled = CancelButton.IsEnabled = false;
        Cursor = new Cursor(StandardCursorType.Wait);
        bool saved;
        try
        {
            saved = await _model.SaveAsync();
        }
        finally
        {
            _saving = false;
            SaveButton.IsEnabled = CancelButton.IsEnabled = true;
            Cursor = Cursor.Default;
        }

        if (saved)
        {
            Saved = _model.Saved;
            Close();
        }
    }
}
