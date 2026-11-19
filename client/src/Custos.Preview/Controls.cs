using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace Custos.Preview;

/// <summary>A toolbar button: an icon in a pill-shaped indicator with a label under it (like a navigation rail item).</summary>
internal sealed class ToolButton : Button
{
    public static readonly StyledProperty<string?> LabelProperty = AvaloniaProperty.Register<ToolButton, string?>(nameof(Label));

    public static readonly StyledProperty<string?> IconKindProperty = AvaloniaProperty.Register<ToolButton, string?>(nameof(IconKind));

    public string? Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public string? IconKind
    {
        get => GetValue(IconKindProperty);
        set => SetValue(IconKindProperty, value);
    }
}

/// <summary>A toolbar button that stays pressed («Карточка»).</summary>
internal sealed class ToolToggle : ToggleButton
{
    public static readonly StyledProperty<string?> LabelProperty = ToolButton.LabelProperty.AddOwner<ToolToggle>();

    public static readonly StyledProperty<string?> IconKindProperty = ToolButton.IconKindProperty.AddOwner<ToolToggle>();

    public string? Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public string? IconKind
    {
        get => GetValue(IconKindProperty);
        set => SetValue(IconKindProperty, value);
    }
}

/// <summary>A small ring with the share done, for the running task in the list (as on the site's machine page).</summary>
internal sealed class Ring : Control
{
    public static readonly StyledProperty<double> ValueProperty = AvaloniaProperty.Register<Ring, double>(nameof(Value));

    public static readonly StyledProperty<IBrush?> TrackProperty = AvaloniaProperty.Register<Ring, IBrush?>(nameof(Track));

    public static readonly StyledProperty<IBrush?> FillProperty = AvaloniaProperty.Register<Ring, IBrush?>(nameof(Fill));

    static Ring() => AffectsRender<Ring>(ValueProperty, TrackProperty, FillProperty);

    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public IBrush? Track
    {
        get => GetValue(TrackProperty);
        set => SetValue(TrackProperty, value);
    }

    public IBrush? Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        const double stroke = 2.5;
        double size = Math.Min(Bounds.Width, Bounds.Height);
        double r = (size - stroke) / 2;
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        if (Track is { } track)
        {
            context.DrawEllipse(null, new Pen(track, stroke), center, r, r);
        }

        double share = Math.Clamp(Value / 100, 0, 1);
        if (Fill is not { } fill || share <= 0)
        {
            return;
        }

        var pen = new Pen(fill, stroke, lineCap: PenLineCap.Round);
        if (share >= 0.999)
        {
            context.DrawEllipse(null, pen, center, r, r);
            return;
        }

        double angle = (share * 2 * Math.PI) - (Math.PI / 2);
        var start = new Point(center.X, center.Y - r);
        var end = new Point(center.X + (r * Math.Cos(angle)), center.Y + (r * Math.Sin(angle)));
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(start, false);
            g.ArcTo(end, new Size(r, r), 0, share > 0.5, SweepDirection.Clockwise);
            g.EndFigure(false);
        }

        context.DrawGeometry(null, pen, geometry);
    }
}

/// <summary>
/// The 8 px M3 progress bar of the site: the done part, a gap, the rest with a stop dot at its end, and a light running
/// along the done part while the run goes on.
/// </summary>
internal sealed class ProgressLine : Control
{
    public static readonly StyledProperty<double> ValueProperty = AvaloniaProperty.Register<ProgressLine, double>(nameof(Value));

    public static readonly StyledProperty<bool> LiveProperty = AvaloniaProperty.Register<ProgressLine, bool>(nameof(Live));

    public static readonly StyledProperty<IBrush?> FillProperty = AvaloniaProperty.Register<ProgressLine, IBrush?>(nameof(Fill));

    public static readonly StyledProperty<IBrush?> TrackProperty = AvaloniaProperty.Register<ProgressLine, IBrush?>(nameof(Track));

    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private bool _frameRequested;

    static ProgressLine()
    {
        AffectsRender<ProgressLine>(ValueProperty, LiveProperty, FillProperty, TrackProperty);
        HeightProperty.OverrideDefaultValue<ProgressLine>(8);
    }

    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public bool Live
    {
        get => GetValue(LiveProperty);
        set => SetValue(LiveProperty, value);
    }

    public IBrush? Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public IBrush? Track
    {
        get => GetValue(TrackProperty);
        set => SetValue(TrackProperty, value);
    }

    /// <summary>Set by the screenshot run: the light stands still at one place, so the pictures do not change.</summary>
    public static double? FixedPhase { get; set; }

    public override void Render(DrawingContext context)
    {
        const double gap = 4;
        double h = Bounds.Height, w = Bounds.Width, radius = h / 2;
        double done = Math.Round(w * Math.Clamp(Value / 100, 0, 1));
        if (Track is { } track && w - done - gap > h)
        {
            var rest = new Rect(done + (done > 0 ? gap : 0), 0, w - done - (done > 0 ? gap : 0), h);
            context.DrawRectangle(track, null, rest, radius, radius);
            if (Fill is { } dot)
            {
                context.DrawEllipse(dot, null, new Point(w - radius, radius), 2, 2);
            }
        }

        if (Fill is not { } fill || done < h)
        {
            return;
        }

        var bar = new Rect(0, 0, done, h);
        context.DrawRectangle(fill, null, bar, radius, radius);
        if (!Live)
        {
            return;
        }

        // A soft light, 1.8 s from left to right, like the site's «sheen».
        double phase = FixedPhase ?? (Clock.Elapsed.TotalSeconds % 1.8 / 1.8);
        double center = -0.3 + (1.6 * phase);
        var light = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0, 255, 255, 255), center - 0.15),
                new GradientStop(Color.FromArgb(110, 255, 255, 255), center),
                new GradientStop(Color.FromArgb(0, 255, 255, 255), center + 0.15),
            },
        };
        context.DrawRectangle(light, null, bar, radius, radius);
        RequestFrame();
    }

    private void RequestFrame()
    {
        if (_frameRequested || FixedPhase is not null || TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }

        _frameRequested = true;
        top.RequestAnimationFrame(_ =>
        {
            _frameRequested = false;
            if (Live && IsEffectivelyVisible)
            {
                InvalidateVisual();
            }
        });
    }
}

/// <summary>
/// An M3 outlined text field: a frame with the label on its top edge and a hint under it. The TextBox or ComboBox put
/// inside goes without a frame of its own (Styles/M3.axaml), so the frame turns primary when it has the keyboard.
/// </summary>
internal sealed class Field : ContentControl
{
    public static readonly StyledProperty<string?> LabelProperty = AvaloniaProperty.Register<Field, string?>(nameof(Label));

    public static readonly StyledProperty<string?> HintProperty = AvaloniaProperty.Register<Field, string?>(nameof(Hint));

    public static readonly StyledProperty<string?> TrailingIconProperty = AvaloniaProperty.Register<Field, string?>(nameof(TrailingIcon));

    public string? Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public string? Hint
    {
        get => GetValue(HintProperty);
        set => SetValue(HintProperty, value);
    }

    public string? TrailingIcon
    {
        get => GetValue(TrailingIconProperty);
        set => SetValue(TrailingIconProperty, value);
    }
}
