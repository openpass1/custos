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

    public Dictionary<string, int> ColumnsWithCard { get; set; } = [];

    public Dictionary<string, int> ColumnsWithoutCard { get; set; } = [];

    public int BottomTab { get; set; }

    public Dictionary<string, int> LogColumns { get; set; } = [];

    public Dictionary<string, int> HistoryColumns { get; set; } = [];

    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CUSTOS", "ui.json");

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
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllBytes(FilePath, JsonSerializer.SerializeToUtf8Bytes(this, UiJsonContext.Default.UiState));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Only the look of the window is lost.
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(UiState))]
internal sealed partial class UiJsonContext : JsonSerializerContext;
