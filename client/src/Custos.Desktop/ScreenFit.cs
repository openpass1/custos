using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using Custos.Core;

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
            LimitToScreen(window, screen);
        }
    }

    /// <summary>Once the window is open: no taller than its screen, and moved onto the free part of it.</summary>
    public static void Keep(Window window) => window.Opened += (_, _) =>
    {
        var owner = window.Owner as Window;
        var screen = CustosEdition.IsLinux && owner is not null
            ? owner.Screens.ScreenFromWindow(owner) ?? owner.Screens.Primary
            : window.Screens.ScreenFromWindow(window) ?? window.Screens.Primary;
        if (screen is not null)
        {
            LimitToScreen(window, screen);
        }

        // After the layout, when the size of the frame is known.
        Dispatcher.UIThread.Post(() => MoveOnto(window, CustosEdition.IsLinux ? screen : null), DispatcherPriority.Background);
    };

    private static void LimitToScreen(Window window, Screen screen)
    {
        if (CustosEdition.IsLinux)
        {
            LimitLinux(window, screen, window.MinWidth, window.MinHeight);
        }
        else
        {
            Cap(window, screen.WorkingArea.Height / screen.Scaling);
        }
    }

    /// <summary>Leave room in the work area for decorations even before the window manager supplies frame extents.</summary>
    public static void LimitLinux(Window window, Screen screen, double minimumWidth, double minimumHeight)
    {
        double width = Math.Max(1, Math.Floor(screen.WorkingArea.Width / screen.Scaling) - 16);
        double height = Math.Max(1, Math.Floor(screen.WorkingArea.Height / screen.Scaling) - 44);
        window.MinWidth = Math.Min(minimumWidth, width);
        window.MinHeight = Math.Min(minimumHeight, height);
        window.MaxWidth = width;
        window.MaxHeight = height;
        if (window.WindowState != WindowState.Normal)
        {
            return;
        }

        if (window.Width > width)
        {
            window.Width = width;
        }

        if (window.Height > height)
        {
            window.Height = height;
        }
    }

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

    public static void MoveOnto(Window window, Screen? target = null)
    {
        if (!window.IsVisible || window.WindowState != WindowState.Normal
            || (target ?? window.Screens.ScreenFromWindow(window) ?? window.Screens.Primary) is not { } screen)
        {
            return;
        }

        var position = ClampPosition(window.Position, window.FrameSize ?? window.ClientSize, screen);
        if (position != window.Position)
        {
            window.Position = position;
        }
    }

    public static PixelPoint ClampPosition(PixelPoint position, Size frame, Screen screen)
    {
        var area = screen.WorkingArea;
        int width = (int)Math.Ceiling(frame.Width * screen.Scaling);
        int height = (int)Math.Ceiling(frame.Height * screen.Scaling);
        return new PixelPoint(Math.Max(area.X, Math.Min(position.X, area.Right - width)),
            Math.Max(area.Y, Math.Min(position.Y, area.Bottom - height)));
    }
}
