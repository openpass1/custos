using System.Text.Json;
using System.Text.Json.Serialization;

namespace Custos.Gui;

/// <summary>
/// How this Windows user left the main window: size, card, splitters, column widths, the tab at the bottom
/// (docs/client-ui.md, 2.3). Kept in <c>%APPDATA%\CUSTOS\ui.json</c>; personal, so no administrator rights needed.
/// Sizes are stored at 100 % scale.
/// </summary>
internal sealed class UiState
{
    public int? Left { get; set; }

    public int? Top { get; set; }

    public int? Width { get; set; }

    public int? Height { get; set; }

    public bool Maximized { get; set; }

    public bool CardVisible { get; set; } = true;

    /// <summary>Width of the card; it keeps its width when the window is resized, like the preview pane of Explorer.</summary>
    public int? CardWidth { get; set; }

    /// <summary>Height of the tabs at the bottom.</summary>
    public int? TabsHeight { get; set; }

    /// <summary>Widths of the columns of the task list, the same with and without the card.</summary>
    public Dictionary<string, int> TaskColumns { get; set; } = [];

    public int BottomTab { get; set; }

    public Dictionary<string, int> LogColumns { get; set; } = [];

    /// <summary>Which messages the "Log" tab shows: one of <see cref="Core.Agent.LogFilterLevels"/>.</summary>
    public string JournalLevel { get; set; } = Core.Agent.LogFilterLevels.All;

    /// <summary>The "Log" tab shows only the tasks selected in the list.</summary>
    public bool JournalSelectedOnly { get; set; }

    /// <summary>The run history shows only runs with warnings and errors.</summary>
    public bool HistoryProblemsOnly { get; set; }

    public Dictionary<string, int> HistoryColumns { get; set; } = [];

    /// <summary>Where ui.json is kept; the interface tests use a folder of their own.</summary>
    internal static string Folder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CUSTOS");

    private static string FilePath => Path.Combine(Folder, "ui.json");

    public static UiState Load()
    {
        try
        {
            using var stream = File.OpenRead(FilePath);
            return JsonSerializer.Deserialize(stream, UiJsonContext.Default.UiState) ?? new UiState();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new UiState();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllBytes(FilePath, JsonSerializer.SerializeToUtf8Bytes(this, UiJsonContext.Default.UiState));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Only the look of the window is lost.
        }
    }
}

/// <summary>
/// What the service last said about the look of the interface for this user: the theme and what of the interface they
/// get (section 4). Kept in <c>%APPDATA%\CUSTOS\last-known.json</c>, apart from ui.json (which the main window rewrites
/// as a whole), so that at the next logon the interface starts right away in the right theme and, for a user who
/// should not see it, without a tray icon that flashes up until the service has answered.
/// </summary>
internal sealed class LastKnown
{
    /// <summary>The theme of the settings (A.9).</summary>
    public string Theme { get; set; } = Core.Model.Themes.Auto;

    /// <summary>What this user gets: <see cref="Core.Model.TrayModes.Full"/> for an administrator, else the setting.</summary>
    public string TrayMode { get; set; } = Core.Model.TrayModes.Full;

    private static string FilePath => Path.Combine(UiState.Folder, "last-known.json");

    public static LastKnown Load()
    {
        try
        {
            using var stream = File.OpenRead(FilePath);
            var known = JsonSerializer.Deserialize(stream, UiJsonContext.Default.LastKnown) ?? new LastKnown();
            if (!Core.Model.Themes.IsKnown(known.Theme))
            {
                known.Theme = Core.Model.Themes.Auto;
            }

            if (!Core.Model.TrayModes.IsKnown(known.TrayMode))
            {
                known.TrayMode = Core.Model.TrayModes.Full;
            }

            return known;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new LastKnown();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(UiState.Folder);
            File.WriteAllBytes(FilePath, JsonSerializer.SerializeToUtf8Bytes(this, UiJsonContext.Default.LastKnown));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The service tells it again at the next start.
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(UiState))]
[JsonSerializable(typeof(LastKnown))]
internal sealed partial class UiJsonContext : JsonSerializerContext;
