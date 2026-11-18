using Custos.Core.Model;

namespace Custos.Gui;

/// <summary>
/// Light or dark windows (the theme of the settings, A.9). WinForms draws its controls in the colour mode of the
/// application, and <see cref="SystemColors"/> follow it; the few colours of our own (<see cref="Glyphs"/>) ask
/// <see cref="IsDark"/>. A window takes the mode when it is created, so after a change the open windows are made anew.
/// </summary>
/// <remarks>
/// The legacy edition (.NET 6, Windows 7 and 8.1) is always light: neither that .NET nor those Windows have a dark mode.
/// </remarks>
internal static class Theme
{
#if CUSTOS_LEGACY
    public static bool IsDark => false;

    public static void Apply(string theme)
    {
        _ = theme;
    }
#else
    /// <summary>The windows are dark now.</summary>
    public static bool IsDark => Application.IsDarkModeEnabled;

    /// <summary>Sets the colour mode for the windows created from now on.</summary>
    public static void Apply(string theme) => Application.SetColorMode(theme switch
    {
        Themes.Dark => SystemColorMode.Dark,
        Themes.Light => SystemColorMode.Classic,
        _ => SystemColorMode.System,
    });
#endif
}
