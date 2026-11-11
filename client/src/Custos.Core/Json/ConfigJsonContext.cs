using System.Text.Json;
using System.Text.Json.Serialization;
using Custos.Core.Model;

namespace Custos.Core.Json;

/// <summary>
/// JSON of the files in C:\ProgramData\CUSTOS\config and of the set manifests: snake_case like appendix A,
/// indented so that an administrator can read them, nulls written out.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    WriteIndented = true,
    Converters = [typeof(UtcTimeConverter)])]
[JsonSerializable(typeof(TaskDefinition))]
[JsonSerializable(typeof(TasksFile))]
[JsonSerializable(typeof(CredentialsFile))]
[JsonSerializable(typeof(AgentSettings))]
[JsonSerializable(typeof(JsonElement))]
public sealed partial class ConfigJsonContext : JsonSerializerContext;

/// <summary><c>config\tasks.json</c>.</summary>
public sealed class TasksFile
{
    /// <summary>Grows with every change of the configuration (A.4, A.6).</summary>
    public long ConfigRevision { get; set; }

    public List<TaskDefinition> Tasks { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary><c>config\credentials.json</c>.</summary>
public sealed class CredentialsFile
{
    public List<StoredCredential> Credentials { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}
