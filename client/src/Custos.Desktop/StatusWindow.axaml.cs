using Avalonia.Controls;

namespace Custos.Desktop;

/// <summary>The state of the backups for a user without the full interface; closing it only hides it.</summary>
internal sealed partial class StatusWindow : Window
{
    /// <summary>Only for the XAML previewer of an IDE.</summary>
    public StatusWindow()
        : this(new MainViewModel(new Custos.Gui.ServiceMonitor(), demo: true, null))
    {
    }

    public StatusWindow(MainViewModel model)
    {
        DataContext = model;
        InitializeComponent();
        Opened += (_, _) => TitleBar.Paint(this);
        ActualThemeVariantChanged += (_, _) => TitleBar.Paint(this);
        ScreenFit.Keep(this);
        Closing += (_, e) =>
        {
            if (!Quitting)
            {
                e.Cancel = true;
                Hide();
            }
        };
    }

    /// <summary>The interface ends: the window really closes.</summary>
    public bool Quitting { get; set; }
}
