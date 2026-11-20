using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Rendering;

namespace Custos.Desktop;

/// <summary>A Material Symbols icon (IconData.g.cs), drawn as a path so it is sharp at every screen scale.</summary>
internal sealed class Icon : Control, ICustomHitTest
{
    public static readonly StyledProperty<string?> KindProperty = AvaloniaProperty.Register<Icon, string?>(nameof(Kind));

    public static readonly StyledProperty<bool> FilledProperty = AvaloniaProperty.Register<Icon, bool>(nameof(Filled));

    public static readonly StyledProperty<double> SizeProperty = AvaloniaProperty.Register<Icon, double>(nameof(Size), 24);

    /// <summary>The colour comes from the text around the icon (a button, a menu item), like a glyph of a font.</summary>
    public static readonly StyledProperty<IBrush?> ForegroundProperty = TextElement.ForegroundProperty.AddOwner<Icon>();

    static Icon()
    {
        AffectsRender<Icon>(KindProperty, FilledProperty, ForegroundProperty);
        AffectsMeasure<Icon>(SizeProperty);
    }

    /// <summary>The whole square of the icon takes the pointer, not only its strokes: a «?» shows its tip anywhere on it.</summary>
    public bool HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

    public string? Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public bool Filled
    {
        get => GetValue(FilledProperty);
        set => SetValue(FilledProperty, value);
    }

    public double Size
    {
        get => GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        if (IconData.Get(Kind, Filled) is not { } geometry || Foreground is not { } brush)
        {
            return;
        }

        double scale = Size / 960;
        var offset = new Point(Math.Round((Bounds.Width - Size) / 2), Math.Round((Bounds.Height - Size) / 2));
        using (context.PushTransform(Matrix.CreateTranslation(0, 960) * Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offset.X, offset.Y)))
        {
            context.DrawGeometry(brush, null, geometry);
        }
    }

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);
}

internal static partial class IconData
{
    private static readonly Dictionary<string, Geometry> Parsed = new(StringComparer.Ordinal);

    /// <summary>Names asked for that tools/make-icons.py did not put in; the screenshot run fails on any.</summary>
    public static HashSet<string> Missing { get; } = new(StringComparer.Ordinal);

    /// <summary>The icon's path, or null for an unknown name; the filled form falls back to the plain one.</summary>
    public static Geometry? Get(string? kind, bool filled)
    {
        if (string.IsNullOrEmpty(kind))
        {
            return null;
        }

        string key = filled && Paths.ContainsKey(kind + "+fill") ? kind + "+fill" : kind;
        if (Parsed.TryGetValue(key, out var geometry))
        {
            return geometry;
        }

        if (!Paths.TryGetValue(key, out var data))
        {
            Missing.Add(kind);
            return null;
        }

        geometry = StreamGeometry.Parse(data);
        Parsed[key] = geometry;
        return geometry;
    }

    /// <summary>Every icon parses (the screenshot run checks this, so a bad path fails CI, not the user).</summary>
    public static int CheckAll()
    {
        foreach (var key in Paths.Keys)
        {
            _ = StreamGeometry.Parse(Paths[key]);
        }

        return Paths.Count;
    }
}
