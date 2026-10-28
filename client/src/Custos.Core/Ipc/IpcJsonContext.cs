using System.Text.Json.Serialization;

namespace Custos.Core.Ipc;

/// <summary>JSON contract of the service pipe: snake_case names, null values omitted (the same style as protocol.md).</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PipeMessage))]
[JsonSerializable(typeof(HelloRequest))]
[JsonSerializable(typeof(HelloResult))]
[JsonSerializable(typeof(ServiceStateEvent))]
public sealed partial class IpcJsonContext : JsonSerializerContext;
