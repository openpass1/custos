using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Custos.Core.Model;
using Custos.Gui;

namespace Custos.Preview;

/// <summary>The task editor of the trial window: shows the task's settings page by page and keeps nothing.</summary>
internal sealed partial class EditorWindow : Window
{
    private readonly EditorModel _model;
    private readonly DispatcherTimer _snackTimer = new() { Interval = TimeSpan.FromSeconds(5) };

    /// <summary>Only for the XAML previewer of an IDE.</summary>
    public EditorWindow()
        : this(new EditorModel(new TaskDefinition { Name = Strings.NewTaskName }, null, isNew: true))
    {
    }

    public EditorWindow(EditorModel model)
    {
        _model = model;
        DataContext = model;
        InitializeComponent();
        AddHandler(Button.ClickEvent, OnPageButton);
        model.PropertyChanged += OnModelChanged;
        _snackTimer.Tick += (_, _) => HideSnack();
        ActualThemeVariantChanged += (_, _) => TitleBar.Paint(this);
    }

    public EditorModel Model => _model;

    /// <summary>The window was closed with «Сохранить».</summary>
    public bool Saved { get; private set; }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        TitleBar.Paint(this);
    }

    protected override void OnClosed(EventArgs e)
    {
        _model.PropertyChanged -= OnModelChanged;
        _snackTimer.Stop();
        base.OnClosed(e);
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EditorModel.Page))
        {
            PageScroll.Offset = default;
        }
    }

    /// <summary>Buttons on a page (add, change, remove) would open dialogs of their own; here they only say so.</summary>
    private void OnPageButton(object? sender, RoutedEventArgs e)
    {
        if (e.Source is Visual source && PagePane.IsVisualAncestorOf(source))
        {
            SnackText.Text = Strings.PreviewEditorReadOnly;
            Snack.IsVisible = true;
            _snackTimer.Stop();
            _snackTimer.Start();
        }
    }

    private void OnSnackClose(object? sender, RoutedEventArgs e) => HideSnack();

    private void HideSnack()
    {
        _snackTimer.Stop();
        Snack.IsVisible = false;
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        Saved = true;
        Close();
    }
}
