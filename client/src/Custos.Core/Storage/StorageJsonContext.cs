using System.Text.Json.Serialization;
using Custos.Core.Model;

namespace Custos.Core.Storage;

/// <summary>JSON of the values kept in database columns.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(List<DestinationResult>))]
internal sealed partial class StorageJsonContext : JsonSerializerContext;
