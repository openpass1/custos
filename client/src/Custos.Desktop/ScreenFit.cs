using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Custos.Desktop;

/// <summary>
/// Keeps a window on its screen: not taller than the part of it the taskbar leaves, and moved so that its title bar,
/// by which it is dragged, is never above the top of the screen. A long dialog (a Linux server, a destination) opened
/// over a window low on the screen went above it and could not be moved.
/// </summary>
internal static class ScreenFit
{
    /// <summary>The share of the free height of the screen a window may take.</summary>
    private const double Share = 0.94;

    /// <summary>Before the window is shown over <paramref name="owner"/>: the height it may have on that screen.</summary>
    public static void Limit(Window window, Window owner)
    {
        if ((owner.Screens.ScreenFromWindow(owner) ?? owner.Screens.Primary) is { } screen)
        {
            Cap(window, screen.WorkingArea.Height / screen.Scaling);
        }
    }

    /// <summary>Once the window is open: no taller than its screen, and moved onto the free part of it.</summary>
    public static void Keep(Window window) => window.Opened += (_, _) =>
    {
        if ((window.Screens.ScreenFromWindow(window) ?? window.Screens.Primary) is { } screen)
        {
            Cap(window, screen.WorkingArea.Height / screen.Scaling);
        }

        // After the layout, when the size of the frame is known.
        Dispatcher.UIThread.Post(() => MoveOnto(window), DispatcherPriority.Background);
    };

    private static void Cap(Window window, double free)
    {
        double max = Math.Floor(free * Share);
        if (window.MaxHeight > max)
        {
            window.MaxHeight = max;
        }

        if (window.MinHeight > max)
        {
            window.MinHeight = max;
        }

        if (!double.IsNaN(window.Height) && window.Height > max)
        {
            window.Height = max;
        }
    }

    private static void MoveOnto(Window window)
    {
        if (!window.IsVisible || (window.Screens.ScreenFromWindow(window) ?? window.Screens.Primary) is not { } screen)
        {
            return;
        }

        var area = screen.WorkingArea;
        var frame = window.FrameSize ?? window.ClientSize;
        int width = (int)Math.Ceiling(frame.Width * screen.Scaling);
        int height = (int)Math.Ceiling(frame.Height * screen.Scaling);
        var at = window.Position;
        int x = Math.Max(area.X, Math.Min(at.X, area.Right - width));
        int y = Math.Max(area.Y, Math.Min(at.Y, area.Bottom - height));
        if (x != at.X || y != at.Y)
        {
            window.Position = new PixelPoint(x, y);
        }
    }
}
