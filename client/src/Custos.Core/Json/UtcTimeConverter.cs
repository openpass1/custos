using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Custos.Core.Json;

/// <summary>Writes times as UTC ISO 8601 to the second, <c>2026-10-12T21:00:00Z</c> (A.1); reads any ISO 8601 time.</summary>
public sealed class UtcTimeConverter : JsonConverter<DateTimeOffset>
{
    public const string Format = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        string? text = reader.GetString();
        if (text is null || !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value))
        {
            throw new JsonException($"'{text}' is not a valid time.");
        }

        return value.ToUniversalTime();
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(ToText(value));
    }

    public static string ToText(DateTimeOffset value) => value.UtcDateTime.ToString(Format, CultureInfo.InvariantCulture);
}
