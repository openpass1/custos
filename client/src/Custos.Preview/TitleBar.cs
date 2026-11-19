using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace Custos.Preview;

/// <summary>
/// Windows 11 draws the title bar in the window's own surface colour; Windows 10 only makes it dark in the dark theme.
/// Elsewhere (and on older Windows) the system title bar stays as it is.
/// </summary>
internal static partial class TitleBar
{
    private const int UseImmersiveDarkMode = 20;
    private const int CaptionColor = 35;
    private const int TextColor = 36;

    public static void Paint(Window window)
    {
        if (!OperatingSystem.IsWindows() || window.TryGetPlatformHandle()?.Handle is not { } hwnd || hwnd == IntPtr.Zero)
        {
            return;
        }

        var variant = window.ActualThemeVariant;
        int dark = variant == ThemeVariant.Dark ? 1 : 0;
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            _ = DwmSetWindowAttribute(hwnd, UseImmersiveDarkMode, ref dark, sizeof(int));
        }

        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)
            && window.TryFindResource("SurfaceContainer", variant, out object? back) && back is ISolidColorBrush caption
            && window.TryFindResource("OnSurface", variant, out object? fore) && fore is ISolidColorBrush text)
        {
            int captionColor = ColorRef(caption.Color);
            int textColor = ColorRef(text.Color);
            _ = DwmSetWindowAttribute(hwnd, CaptionColor, ref captionColor, sizeof(int));
            _ = DwmSetWindowAttribute(hwnd, TextColor, ref textColor, sizeof(int));
        }
    }

    private static int ColorRef(Color color) => color.R | (color.G << 8) | (color.B << 16);

    [LibraryImport("dwmapi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
