using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Custos.Core.Ipc;

/// <summary>Values of <see cref="PipeMessage.Kind"/>.</summary>
public static class PipeMessageKind
{
    public const string Request = "request";
    public const string Response = "response";
    public const string Event = "event";
}

/// <summary>
/// One message on the service pipe.
/// A request has <see cref="Id"/>, <see cref="Method"/> and parameters in <see cref="Data"/>.
/// A response has the request's <see cref="Id"/> and either the result in <see cref="Data"/> or an <see cref="Error"/>.
/// An event has <see cref="Event"/> and its payload in <see cref="Data"/>.
/// </summary>
public sealed class PipeMessage
{
    public required string Kind { get; init; }

    public long? Id { get; init; }

    public string? Method { get; init; }

    public string? Event { get; init; }

    public JsonElement? Data { get; init; }

    public PipeError? Error { get; init; }

    public static PipeMessage Request<T>(long id, string method, T parameters, JsonTypeInfo<T> typeInfo) => new()
    {
        Kind = PipeMessageKind.Request,
        Id = id,
        Method = method,
        Data = JsonSerializer.SerializeToElement(parameters, typeInfo),
    };

    public static PipeMessage Success<T>(long id, T result, JsonTypeInfo<T> typeInfo) => new()
    {
        Kind = PipeMessageKind.Response,
        Id = id,
        Data = JsonSerializer.SerializeToElement(result, typeInfo),
    };

    public static PipeMessage Failure(long id, string code, string message) => new()
    {
        Kind = PipeMessageKind.Response,
        Id = id,
        Error = new PipeError { Code = code, Message = message },
    };

    public static PipeMessage Notification<T>(string name, T payload, JsonTypeInfo<T> typeInfo) => new()
    {
        Kind = PipeMessageKind.Event,
        Event = name,
        Data = JsonSerializer.SerializeToElement(payload, typeInfo),
    };

    /// <summary>Reads <see cref="Data"/> as <typeparamref name="T"/>.</summary>
    /// <exception cref="InvalidDataException">Data is missing or has the wrong shape.</exception>
    public T GetData<T>(JsonTypeInfo<T> typeInfo)
    {
        if (Data is not { } data || data.ValueKind == JsonValueKind.Null)
        {
            throw new InvalidDataException($"Message '{Method ?? Event ?? Kind}' has no data.");
        }

        try
        {
            return data.Deserialize(typeInfo) ?? throw new InvalidDataException($"Message '{Method ?? Event ?? Kind}' has no data.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Message '{Method ?? Event ?? Kind}' has malformed data: {ex.Message}", ex);
        }
    }
}

/// <summary>Why a request failed. <see cref="Code"/> is one of <see cref="PipeProtocol.Errors"/>.</summary>
public sealed class PipeError
{
    public required string Code { get; init; }

    public required string Message { get; init; }
}
