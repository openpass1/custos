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
        Dispatcher.UIThread.Post(() =>
        {
            if (CustosEdition.IsLinux && window.IsVisible && screen is not null)
            {
                LimitToScreen(window, screen);
            }

            MoveOnto(window, CustosEdition.IsLinux ? screen : null);
        }, DispatcherPriority.Background);
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
        ApplyLinuxLimits(window, minimumWidth, minimumHeight, LinuxClientRoom(window, screen), limitMaximum: true);
    }

    /// <summary>Fits the normal main window, leaving the compositor free to maximize it above that size.</summary>
    public static bool FitLinuxMain(Window window, Screen screen, double minimumWidth, double minimumHeight)
    {
        var room = LinuxClientRoom(window, screen);
        bool fits = window.Width <= room.Width && window.Height <= room.Height;
        ApplyLinuxLimits(window, minimumWidth, minimumHeight, room, limitMaximum: false);
        return fits;
    }

    public static PixelPoint ClampLinuxMainPosition(Window window, PixelPoint position, Screen screen)
    {
        return ClampPosition(position, LinuxFrame(window, screen, new Size(window.Width, window.Height)), screen);
    }

    private static Size LinuxClientRoom(Window window, Screen screen)
    {
        var decoration = LinuxDecoration(window, screen);
        return new Size(Math.Max(1, Math.Floor(screen.WorkingArea.Width / screen.Scaling - decoration.Width)),
            Math.Max(1, Math.Floor(screen.WorkingArea.Height / screen.Scaling - decoration.Height)));
    }

    private static Size LinuxFrame(Window window, Screen screen, Size client)
    {
        var decoration = LinuxDecoration(window, screen);
        return new Size(client.Width + decoration.Width, client.Height + decoration.Height);
    }

    private static Size LinuxDecoration(Window window, Screen screen)
    {
        // The compositor may include sizeable shadows in the frame it places. Reserve some pixels at startup,
        // before it reports the actual frame (which may be larger on a scaled display).
        var client = window.ClientSize;
        var frame = window.FrameSize ?? client;
        double horizontal = Math.Max(80, (frame.Width - client.Width) * window.RenderScaling);
        double vertical = Math.Max(104, (frame.Height - client.Height) * window.RenderScaling);
        return new Size(horizontal / screen.Scaling, vertical / screen.Scaling);
    }

    private static void ApplyLinuxLimits(Window window, double minimumWidth, double minimumHeight, Size room, bool limitMaximum)
    {
        double width = room.Width;
        double height = room.Height;
        window.MinWidth = Math.Min(minimumWidth, width);
        window.MinHeight = Math.Min(minimumHeight, height);
        if (limitMaximum)
        {
            window.MaxWidth = width;
            window.MaxHeight = height;
        }

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

        var frame = CustosEdition.IsLinux ? LinuxFrame(window, screen, window.ClientSize) : window.FrameSize ?? window.ClientSize;
        var position = ClampPosition(window.Position, frame, screen);
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
