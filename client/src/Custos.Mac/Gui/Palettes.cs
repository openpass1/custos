using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace Custos.Mac.Gui;

/// <summary>
/// The colour schemes of the window (Palettes.g.cs, made by tools/make-palettes.mjs) and putting one in place while the
/// window is open: every colour is a DynamicResource, so a change shows at once. The same as Palettes.cs of
/// src/Custos.Desktop, the new window of Windows, with the names in MacStrings.
/// </summary>
internal static partial class Palettes
{
    /// <summary>The scheme the window starts with: the site's accent on grey surfaces, the look of the site as well.</summary>
    public const string Default = "neutral";

    public static IEnumerable<string> Ids => Schemes.Select(s => s.Id);

    /// <summary>The id itself when there is such a scheme, else <see cref="Default"/> (a ui.json from a later version).</summary>
    public static string Known(string? id) => Find(id)?.Id ?? Default;

    public static string Title(string id) => id switch
    {
        "graphite" => MacStrings.ColorGraphite,
        "blue" => MacStrings.ColorBlue,
        "warm" => MacStrings.ColorWarm,
        "sepia" => MacStrings.ColorSepia,
        _ => MacStrings.ColorNeutral,
    };

    /// <summary>The main colour and the background of a scheme, for the round sample in the menu.</summary>
    public static (Color Primary, Color Surface) Sample(string id, bool dark)
    {
        var colors = Find(id) is { } scheme ? (dark ? scheme.Dark : scheme.Light) : Schemes[0].Light;
        return (Color.FromUInt32(colors["Primary"]), Color.FromUInt32(colors["SurfaceContainer"]));
    }

    public static void Apply(Application app, string? id)
    {
        var scheme = Find(id) ?? Schemes[0];
        Put(app, ThemeVariant.Light, scheme.Light);
        Put(app, ThemeVariant.Dark, scheme.Dark);
    }

    private static Scheme? Find(string? id) => Schemes.FirstOrDefault(s => s.Id == id);

    private static void Put(Application app, ThemeVariant variant, IReadOnlyDictionary<string, uint> colors)
    {
        if (!app.Resources.ThemeDictionaries.TryGetValue(variant, out var provider) || provider is not ResourceDictionary dictionary)
        {
            return;
        }

        foreach (var (key, value) in colors)
        {
            dictionary[key] = new SolidColorBrush(Color.FromUInt32(value));
        }

        // State layers: the colour of the content at 8 % (hover) and 10 % (pressed).
        var content = Color.FromUInt32(colors["OnSurface"]);
        dictionary["HoverLayer"] = new SolidColorBrush(Color.FromArgb(0x14, content.R, content.G, content.B));
        dictionary["PressedLayer"] = new SolidColorBrush(Color.FromArgb(0x1A, content.R, content.G, content.B));

        // The few Fluent pieces left (text selection, focus) take the accent colour.
        var primary = Color.FromUInt32(colors["Primary"]);
        foreach (string key in (string[])["SystemAccentColor", "SystemAccentColorDark1", "SystemAccentColorDark2", "SystemAccentColorDark3",
            "SystemAccentColorLight1", "SystemAccentColorLight2", "SystemAccentColorLight3"])
        {
            dictionary[key] = primary;
        }
    }

    private sealed record Scheme(string Id, IReadOnlyDictionary<string, uint> Light, IReadOnlyDictionary<string, uint> Dark);
}
