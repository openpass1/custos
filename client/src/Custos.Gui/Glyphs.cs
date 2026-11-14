using System.Drawing.Drawing2D;

namespace Custos.Gui;

/// <summary>Pictures of the toolbar, the editor pages and the dialogs.</summary>
internal enum Glyph
{
    Create,
    Edit,
    Clone,
    Delete,
    Run,
    Stop,
    Pause,
    Restore,
    Card,
    Settings,
    Import,
    Export,
    Lock,
    Info,
    Folder,
    Drive,
    Clock,
    Box,
    Layers,
    Funnel,
    Bolt,
    Sliders,
    Help,
    Database,
    Paste,
}

/// <summary>State of a task or a run, always shown with a picture and a color (docs/client-ui.md, section 1).</summary>
internal enum StateIcon
{
    /// <summary>Never ran: an empty ring.</summary>
    None,
    Success,
    Warning,
    Error,
    Running,
    Queued,

    /// <summary>Disabled task, or no connection to the service.</summary>
    Disabled,
}

/// <summary>
/// Draws the icons of the interface at any size, so they stay sharp at every screen scale. Plain shapes in one color;
/// only "Run" (green) and "Stop" (red) and the state icons are colored.
/// </summary>
internal static class Glyphs
{
    // Each colour has a light and a dark variant (Theme): the same hue, lighter on the dark background so that it
    // keeps its contrast. A window takes the colours when it is created.
    public static Color Ink => Theme.IsDark ? Color.FromArgb(222, 225, 228) : Color.FromArgb(60, 64, 68);

    public static Color Green => Theme.IsDark ? Color.FromArgb(108, 203, 95) : Color.FromArgb(16, 124, 16);

    public static Color Red => Theme.IsDark ? Color.FromArgb(255, 110, 96) : Color.FromArgb(196, 43, 28);

    public static Color Amber => Theme.IsDark ? Color.FromArgb(252, 186, 50) : Color.FromArgb(214, 145, 0);

    public static Color Blue => Theme.IsDark ? Color.FromArgb(96, 170, 255) : Color.FromArgb(0, 103, 192);

    public static Color Grey => Theme.IsDark ? Color.FromArgb(150, 156, 162) : Color.FromArgb(128, 134, 140);

    /// <summary>Text color for warnings and errors in lists (darker than the icons on white, lighter on black, for contrast).</summary>
    public static Color WarningText => Theme.IsDark ? Color.FromArgb(252, 196, 90) : Color.FromArgb(138, 90, 0);

    public static Color ErrorText => Theme.IsDark ? Color.FromArgb(255, 138, 128) : Color.FromArgb(176, 32, 20);

    public static Bitmap Draw(Glyph glyph, int size) => Draw(glyph, size, glyph switch
    {
        Glyph.Run => Green,
        Glyph.Stop => Red,
        _ => Ink,
    });

    public static Bitmap Draw(Glyph glyph, int size, Color color)
    {
        var bitmap = new Bitmap(size, size);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        // Everything is drawn on a 24 x 24 grid and scaled.
        float k = size / 24f;
        g.ScaleTransform(k, k);
        using var pen = new Pen(color, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        using var brush = new SolidBrush(color);
        switch (glyph)
        {
            case Glyph.Create:
                g.DrawLine(pen, 12, 4, 12, 20);
                g.DrawLine(pen, 4, 12, 20, 12);
                break;

            case Glyph.Edit:
                using (var path = new GraphicsPath())
                {
                    path.AddPolygon([new PointF(16, 4), new PointF(20, 8), new PointF(9, 19), new PointF(4, 20), new PointF(5, 15)]);
                    g.DrawPath(pen, path);
                }

                g.DrawLine(pen, 14, 6, 18, 10);
                break;

            case Glyph.Clone:
                g.DrawRectangle(pen, 8, 8, 12, 12);
                g.DrawLines(pen, [new PointF(16, 5), new PointF(16, 4), new PointF(4, 4), new PointF(4, 16), new PointF(5, 16)]);
                break;

            case Glyph.Delete:
                g.DrawLine(pen, 4, 6, 20, 6);
                g.DrawLines(pen, [new PointF(9, 6), new PointF(9, 3.5f), new PointF(15, 3.5f), new PointF(15, 6)]);
                g.DrawLines(pen, [new PointF(6, 6), new PointF(7, 20), new PointF(17, 20), new PointF(18, 6)]);
                g.DrawLine(pen, 10, 10, 10, 16);
                g.DrawLine(pen, 14, 10, 14, 16);
                break;

            case Glyph.Run:
                g.FillPolygon(brush, [new PointF(7, 4), new PointF(20, 12), new PointF(7, 20)]);
                break;

            case Glyph.Stop:
                using (var path = RoundedRectangle(new RectangleF(5, 5, 14, 14), 2))
                {
                    g.FillPath(brush, path);
                }

                break;

            case Glyph.Pause:
                g.FillRectangle(brush, 6, 5, 4, 14);
                g.FillRectangle(brush, 14, 5, 4, 14);
                break;

            case Glyph.Restore:
                g.DrawArc(pen, 4, 4, 16, 16, -60, 280);
                g.FillPolygon(brush, [new PointF(16.5f, 1.5f), new PointF(19.5f, 7.5f), new PointF(13, 8)]);
                g.DrawLines(pen, [new PointF(12, 8), new PointF(12, 12), new PointF(15, 14)]);
                break;

            case Glyph.Card:
                g.DrawRectangle(pen, 3, 5, 18, 14);
                g.DrawLine(pen, 13, 5, 13, 19);
                g.FillRectangle(brush, 13, 5, 8, 14);
                break;

            case Glyph.Settings:
                DrawGear(g, brush);
                break;

            case Glyph.Import:
                g.DrawLines(pen, [new PointF(4, 14), new PointF(4, 20), new PointF(20, 20), new PointF(20, 14)]);
                g.DrawLine(pen, 12, 3, 12, 15);
                g.DrawLines(pen, [new PointF(7, 10), new PointF(12, 15), new PointF(17, 10)]);
                break;

            case Glyph.Export:
                g.DrawLines(pen, [new PointF(4, 14), new PointF(4, 20), new PointF(20, 20), new PointF(20, 14)]);
                g.DrawLine(pen, 12, 15, 12, 3);
                g.DrawLines(pen, [new PointF(7, 8), new PointF(12, 3), new PointF(17, 8)]);
                break;

            case Glyph.Lock:
                g.FillRectangle(brush, 5, 11, 14, 10);
                g.DrawArc(pen, 8, 4, 8, 12, 180, 180);
                g.DrawLine(pen, 8, 10, 8, 11);
                g.DrawLine(pen, 16, 10, 16, 11);
                break;

            case Glyph.Info:
                g.DrawEllipse(pen, 3, 3, 18, 18);
                g.DrawLine(pen, 12, 11, 12, 17);
                g.FillEllipse(brush, 10.8f, 6.3f, 2.4f, 2.4f);
                break;

            case Glyph.Folder:
                g.DrawLines(pen, [new PointF(3, 18), new PointF(3, 6), new PointF(9, 6), new PointF(11, 8), new PointF(21, 8), new PointF(21, 18), new PointF(3, 18)]);
                g.DrawLine(pen, 3, 11, 21, 11);
                break;

            case Glyph.Drive:
                g.DrawRectangle(pen, 3, 8, 18, 9);
                g.FillEllipse(brush, 16, 11.5f, 2.5f, 2.5f);
                g.DrawLine(pen, 6, 12.75f, 12, 12.75f);
                break;

            case Glyph.Clock:
                g.DrawEllipse(pen, 3, 3, 18, 18);
                g.DrawLines(pen, [new PointF(12, 7), new PointF(12, 12), new PointF(16, 14)]);
                break;

            case Glyph.Box:
                g.DrawRectangle(pen, 4, 9, 16, 11);
                g.DrawRectangle(pen, 3, 5, 18, 4);
                g.DrawLine(pen, 10, 13, 14, 13);
                break;

            case Glyph.Layers:
                g.DrawPolygon(pen, [new PointF(12, 4), new PointF(21, 9), new PointF(12, 14), new PointF(3, 9)]);
                g.DrawLines(pen, [new PointF(3, 13), new PointF(12, 18), new PointF(21, 13)]);
                g.DrawLines(pen, [new PointF(3, 17), new PointF(12, 22), new PointF(21, 17)]);
                break;

            case Glyph.Funnel:
                g.DrawPolygon(pen, [new PointF(3, 5), new PointF(21, 5), new PointF(14, 13), new PointF(14, 20), new PointF(10, 18), new PointF(10, 13)]);
                break;

            case Glyph.Bolt:
                g.DrawPolygon(pen, [new PointF(13, 3), new PointF(5, 14), new PointF(11, 14), new PointF(10, 21), new PointF(19, 10), new PointF(13, 10)]);
                break;

            case Glyph.Sliders:
                g.DrawLine(pen, 4, 7, 20, 7);
                g.DrawLine(pen, 4, 17, 20, 17);
                g.FillEllipse(brush, 13, 4.5f, 5, 5);
                g.FillEllipse(brush, 6, 14.5f, 5, 5);
                break;

            case Glyph.Database:
                // A cylinder with a seam: the usual sign of a database.
                g.DrawEllipse(pen, 5, 3, 14, 5);
                g.DrawLine(pen, 5, 5.5f, 5, 18.5f);
                g.DrawLine(pen, 19, 5.5f, 19, 18.5f);
                g.DrawArc(pen, 5, 9.5f, 14, 5, 0, 180);
                g.DrawArc(pen, 5, 16, 14, 5, 0, 180);
                break;

            case Glyph.Paste:
                // A clipboard: the board, its clip and two lines of text.
                g.DrawLines(pen, [new PointF(9, 5), new PointF(5, 5), new PointF(5, 21), new PointF(19, 21), new PointF(19, 5), new PointF(15, 5)]);
                g.FillRectangle(brush, 9, 2.5f, 6, 4.5f);
                g.DrawLine(pen, 9, 12, 15, 12);
                g.DrawLine(pen, 9, 16, 15, 16);
                break;

            case Glyph.Help:
                g.DrawEllipse(pen, 3, 3, 18, 18);
                g.DrawArc(pen, 9, 6.5f, 6, 6, 180, 230);
                g.DrawLine(pen, 12, 12.5f, 12, 14);
                g.FillEllipse(brush, 10.8f, 16, 2.4f, 2.4f);
                break;
        }

        return bitmap;
    }

    public static Bitmap Draw(StateIcon state, int size)
    {
        var bitmap = new Bitmap(size, size);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        float k = size / 16f;
        g.ScaleTransform(k, k);
        var circle = new RectangleF(1.5f, 1.5f, 13, 13);
        using var white = new Pen(Color.White, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        switch (state)
        {
            case StateIcon.None:
                using (var ring = new Pen(Grey, 1.4f))
                {
                    g.DrawEllipse(ring, new RectangleF(2.5f, 2.5f, 11, 11));
                }

                break;

            case StateIcon.Queued:
                using (var ring = new Pen(Blue, 1.6f))
                {
                    g.DrawEllipse(ring, new RectangleF(2.5f, 2.5f, 11, 11));
                    g.DrawLines(ring, [new PointF(8, 5), new PointF(8, 8.3f), new PointF(10.3f, 9.6f)]);
                }

                break;

            default:
                using (var fill = new SolidBrush(StateColor(state)))
                {
                    g.FillEllipse(fill, circle);
                }

                switch (state)
                {
                    case StateIcon.Success:
                        g.DrawLines(white, [new PointF(4.8f, 8.2f), new PointF(7, 10.4f), new PointF(11.3f, 5.8f)]);
                        break;
                    case StateIcon.Warning:
                        g.DrawLine(white, 8, 4.5f, 8, 8.8f);
                        g.FillEllipse(Brushes.White, 7, 10.3f, 2, 2);
                        break;
                    case StateIcon.Error:
                        g.DrawLine(white, 5.5f, 5.5f, 10.5f, 10.5f);
                        g.DrawLine(white, 5.5f, 10.5f, 10.5f, 5.5f);
                        break;
                    case StateIcon.Running:
                        g.FillPolygon(Brushes.White, [new PointF(6.3f, 4.8f), new PointF(11.3f, 8), new PointF(6.3f, 11.2f)]);
                        break;
                    case StateIcon.Disabled:
                        g.DrawLine(white, 4.8f, 8, 11.2f, 8);
                        break;
                }

                break;
        }

        return bitmap;
    }

    /// <summary>The fill of a state icon: the same in both themes, since the white mark on it needs the darker hue.</summary>
    public static Color StateColor(StateIcon state) => state switch
    {
        StateIcon.Success => Color.FromArgb(16, 124, 16),
        StateIcon.Warning => Color.FromArgb(214, 145, 0),
        StateIcon.Error => Color.FromArgb(196, 43, 28),
        StateIcon.Running or StateIcon.Queued => Color.FromArgb(0, 103, 192),
        _ => Color.FromArgb(128, 134, 140),
    };

    /// <summary>An image list with every <see cref="StateIcon"/>, keyed by its name.</summary>
    public static ImageList StateImages(int size)
    {
        var list = new ImageList { ColorDepth = ColorDepth.Depth32Bit, ImageSize = new Size(size, size) };
        foreach (var state in Enum.GetValues<StateIcon>())
        {
            list.Images.Add(state.ToString(), Draw(state, size));
        }

        return list;
    }

    private static void DrawGear(Graphics g, Brush brush)
    {
        using var path = new GraphicsPath();
        const int teeth = 8;
        var points = new List<PointF>();
        for (int i = 0; i < teeth * 2; i++)
        {
            // Alternating outer and inner radius, each with a short plateau.
            float radius = i % 2 == 0 ? 10f : 7.4f;
            for (int j = -1; j <= 1; j += 2)
            {
                double angle = (i + (j * 0.22)) * Math.PI / teeth;
                points.Add(new PointF(12 + (radius * (float)Math.Cos(angle)), 12 + (radius * (float)Math.Sin(angle))));
            }
        }

        path.AddPolygon([.. points]);

        // The hole in the middle: the alternate fill mode leaves the inner circle empty.
        path.AddEllipse(8.5f, 8.5f, 7, 7);
        path.FillMode = FillMode.Alternate;
        g.FillPath(brush, path);
    }

    private static GraphicsPath RoundedRectangle(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
