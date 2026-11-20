using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Custos.Desktop;

/// <summary>Shows the pieces of a form (Form.cs) one under the other.</summary>
internal sealed partial class FormView : UserControl
{
    public static readonly StyledProperty<IEnumerable<FormItem>?> ItemsProperty =
        AvaloniaProperty.Register<FormView, IEnumerable<FormItem>?>(nameof(Items));

    public static readonly IValueConverter Weight = new FuncValueConverter<bool, FontWeight>(strong => strong ? FontWeight.Medium : FontWeight.Normal);

    public static readonly IValueConverter Wrapping = new FuncValueConverter<bool, TextWrapping>(wrap => wrap ? TextWrapping.Wrap : TextWrapping.NoWrap);

    public FormView()
    {
        InitializeComponent();
    }

    public IEnumerable<FormItem>? Items
    {
        get => GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ItemsProperty)
        {
            List.ItemsSource = Items;
        }
    }
}
