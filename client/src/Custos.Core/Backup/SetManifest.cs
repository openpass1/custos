using System.Globalization;
using System.Text;
using System.Text.Json;
using Custos.Core.Json;

namespace Custos.Core.Backup;

/// <summary>
/// The first part of <c>custos-set.json</c>, everything except the file list. Enough to know which task a set belongs
/// to and how sets form chains (sections 6.6, 7).
/// </summary>
public sealed class SetManifestHeader
{
    public const string FormatName = "custos-set";
    public const int CurrentVersion = 1;

    public string Format { get; set; } = FormatName;

    public int FormatVersion { get; set; } = CurrentVersion;

    public string SetName { get; set; } = string.Empty;

    public string TaskId { get; set; } = string.Empty;

    public string TaskName { get; set; } = string.Empty;

    /// <summary>The run that made the set; also the id of the set.</summary>
    public string RunId { get; set; } = string.Empty;

    public string BackupType { get; set; } = string.Empty;

    /// <summary>For incremental and differential sets: the run id of the full set they build on.</summary>
    public string? ParentFullRunId { get; set; }

    public string? ParentFullSetName { get; set; }

    public string Computer { get; set; } = string.Empty;

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset FinishedAt { get; set; }

    public string Status { get; set; } = string.Empty;

    public List<ManifestSource> Sources { get; set; } = [];

    /// <summary>Files of the sources at the time of the run.</summary>
    public long FileCount { get; set; }

    /// <summary>Files stored in this set.</summary>
    public long SetFileCount { get; set; }

    public long SetBytes { get; set; }
}

/// <summary>A source of the task and the folder it got inside the set ("" = the set folder itself).</summary>
public sealed class ManifestSource
{
    public string Id { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    public string Target { get; set; } = string.Empty;
}

/// <summary>One file of the manifest.</summary>
public readonly record struct ManifestFile(string Path, long Size, DateTime ModifiedUtc, bool InSet, string? Sha256);

/// <summary>Writes and reads <c>custos-set.json</c>.</summary>
public static class SetManifest
{
    public const string FileName = "custos-set.json";

    /// <summary>The file list comes last, so that the header can be read without the whole list.</summary>
    public static void Write(Stream stream, SetManifestHeader header, IEnumerable<ManifestFile> files)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(files);
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteString("format", header.Format);
        writer.WriteNumber("format_version", header.FormatVersion);
        writer.WriteString("set_name", header.SetName);
        writer.WriteString("task_id", header.TaskId);
        writer.WriteString("task_name", header.TaskName);
        writer.WriteString("run_id", header.RunId);
        writer.WriteString("backup_type", header.BackupType);
        WriteNullable(writer, "parent_full_run_id", header.ParentFullRunId);
        WriteNullable(writer, "parent_full_set_name", header.ParentFullSetName);
        writer.WriteString("computer", header.Computer);
        writer.WriteString("started_at", UtcTimeConverter.ToText(header.StartedAt));
        writer.WriteString("finished_at", UtcTimeConverter.ToText(header.FinishedAt));
        writer.WriteString("status", header.Status);
        writer.WriteStartArray("sources");
        foreach (var source in header.Sources)
        {
            writer.WriteStartObject();
            writer.WriteString("id", source.Id);
            writer.WriteString("path", source.Path);
            writer.WriteString("target", source.Target);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteNumber("file_count", header.FileCount);
        writer.WriteNumber("set_file_count", header.SetFileCount);
        writer.WriteNumber("set_bytes", header.SetBytes);
        writer.WriteStartArray("files");
        foreach (var file in files)
        {
            writer.WriteStartObject();
            writer.WriteString("path", file.Path);
            writer.WriteNumber("size", file.Size);
            writer.WriteString("modified", file.ModifiedUtc.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture));
            writer.WriteBoolean("in_set", file.InSet);
            if (file.Sha256 is not null)
            {
                writer.WriteString("sha256", file.Sha256);
            }

            writer.WriteEndObject();
            if (writer.BytesPending > 64 * 1024)
            {
                writer.Flush();
            }
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
    }

    /// <summary>Reads the header of a manifest, stopping at the file list.</summary>
    /// <exception cref="InvalidDataException">The file is not a CUSTOS manifest.</exception>
    public static SetManifestHeader ReadHeader(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var buffer = new byte[64 * 1024];
        int length = 0;
        while (true)
        {
            if (length == buffer.Length)
            {
                if (buffer.Length >= 16 * 1024 * 1024)
                {
                    throw new InvalidDataException("The manifest header is too large.");
                }

                Array.Resize(ref buffer, buffer.Length * 2);
            }

            int read = stream.Read(buffer, length, buffer.Length - length);
            length += read;
            bool final = read == 0;
            if (TryParseHeader(buffer.AsSpan(0, length), final, out var header))
            {
                return header;
            }

            if (final)
            {
                throw new InvalidDataException("The manifest ends before its file list.");
            }
        }
    }

    /// <summary>Reads every file entry of a manifest, for checks and restores.</summary>
    public static List<ManifestFile> ReadFiles(Stream stream)
    {
        using var document = JsonDocument.Parse(stream);
        var result = new List<ManifestFile>();
        if (!document.RootElement.TryGetProperty("files", out var files))
        {
            return result;
        }

        foreach (var file in files.EnumerateArray())
        {
            result.Add(new ManifestFile(
                file.GetProperty("path").GetString() ?? string.Empty,
                file.GetProperty("size").GetInt64(),
                DateTime.Parse(file.GetProperty("modified").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
                file.GetProperty("in_set").GetBoolean(),
                file.TryGetProperty("sha256", out var hash) ? hash.GetString() : null));
        }

        return result;
    }

    private static bool TryParseHeader(ReadOnlySpan<byte> data, bool final, out SetManifestHeader header)
    {
        header = new SetManifestHeader();
        var reader = new Utf8JsonReader(data, isFinalBlock: final, state: default);
        try
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return final ? throw new InvalidDataException("The manifest is not a JSON object.") : false;
            }

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == 0)
                {
                    return Validate(header);
                }

                if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1)
                {
                    continue;
                }

                string name = reader.GetString()!;
                if (name == "files")
                {
                    return Validate(header);
                }

                if (!reader.Read())
                {
                    return false;
                }

                if (name == "sources")
                {
                    if (!ReadSources(ref reader, header.Sources))
                    {
                        return false;
                    }

                    continue;
                }

                if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                {
                    if (!reader.TrySkip())
                    {
                        return false;
                    }

                    continue;
                }

                Assign(header, name, ref reader);
            }

            return final ? throw new InvalidDataException("The manifest is incomplete.") : false;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The manifest is not valid JSON: " + ex.Message, ex);
        }
    }

    private static bool ReadSources(ref Utf8JsonReader reader, List<ManifestSource> sources)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            return reader.TrySkip();
        }

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                return true;
            }

            if (reader.TokenType != JsonTokenType.StartObject)
            {
                continue;
            }

            var source = new ManifestSource();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                string property = reader.GetString()!;
                if (!reader.Read())
                {
                    return false;
                }

                string value = reader.TokenType == JsonTokenType.String ? reader.GetString()! : string.Empty;
                switch (property)
                {
                    case "id": source.Id = value; break;
                    case "path": source.Path = value; break;
                    case "target": source.Target = value; break;
                }
            }

            sources.Add(source);
        }

        return false;
    }

    private static void Assign(SetManifestHeader header, string name, ref Utf8JsonReader reader)
    {
        string? text = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
        long number = reader.TokenType == JsonTokenType.Number ? reader.GetInt64() : 0;
        switch (name)
        {
            case "format": header.Format = text ?? string.Empty; break;
            case "format_version": header.FormatVersion = (int)number; break;
            case "set_name": header.SetName = text ?? string.Empty; break;
            case "task_id": header.TaskId = text ?? string.Empty; break;
            case "task_name": header.TaskName = text ?? string.Empty; break;
            case "run_id": header.RunId = text ?? string.Empty; break;
            case "backup_type": header.BackupType = text ?? string.Empty; break;
            case "parent_full_run_id": header.ParentFullRunId = text; break;
            case "parent_full_set_name": header.ParentFullSetName = text; break;
            case "computer": header.Computer = text ?? string.Empty; break;
            case "started_at": header.StartedAt = ParseTime(text); break;
            case "finished_at": header.FinishedAt = ParseTime(text); break;
            case "status": header.Status = text ?? string.Empty; break;
            case "file_count": header.FileCount = number; break;
            case "set_file_count": header.SetFileCount = number; break;
            case "set_bytes": header.SetBytes = number; break;
        }
    }

    private static DateTimeOffset ParseTime(string? text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value) ? value.ToUniversalTime() : default;

    private static bool Validate(SetManifestHeader header) =>
        header.Format == SetManifestHeader.FormatName && header.RunId.Length > 0 && header.TaskId.Length > 0
            ? true
            : throw new InvalidDataException("The file is not a CUSTOS set manifest.");

    private static void WriteNullable(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }

    /// <summary>Lower-case hex, as in the manifests.</summary>
    public static string ToHex(ReadOnlySpan<byte> hash)
    {
        var builder = new StringBuilder(hash.Length * 2);
        foreach (byte b in hash)
        {
            builder.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }
}
