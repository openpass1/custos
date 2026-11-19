using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Custos.Gui;

namespace Custos.Preview;

/// <summary>
/// The colour schemes of the window (Palettes.g.cs, made by tools/make-palettes.mjs) and putting one in place while the
/// window is open: every colour is a DynamicResource, so a change shows at once.
/// </summary>
internal static partial class Palettes
{
    public const string Site = "site";

    public static IEnumerable<string> Ids => Schemes.Select(s => s.Id);

    public static string Title(string id) => id switch
    {
        "neutral" => Strings.PreviewColorNeutral,
        "blue" => Strings.PreviewColorBlue,
        "teal" => Strings.PreviewColorTeal,
        "graphite" => Strings.PreviewColorGraphite,
        _ => Strings.PreviewColorSite,
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

/// <summary>The colour and the theme picked in the trial window, kept in %APPDATA%\CUSTOS\preview.json.</summary>
internal sealed class PreviewSettings
{
    private static readonly string FilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CUSTOS", "preview.json");

    /// <summary>Off for the screenshot run, which sets everything itself.</summary>
    public static bool Enabled { get; set; } = true;

    public string? Color { get; set; }

    public string? Theme { get; set; }

    public static PreviewSettings Load()
    {
        if (!Enabled)
        {
            return new PreviewSettings();
        }

        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<PreviewSettings>(File.ReadAllText(FilePath)) ?? new PreviewSettings() : new PreviewSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new PreviewSettings();
        }
    }

    public void Save()
    {
        if (!Enabled)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not remembered: the trial window works on without it.
        }
    }
}
