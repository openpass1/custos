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

    /// <summary>The database dumps of the set (section 6.2), with the users and rights of each server.</summary>
    public List<ManifestDatabase> Databases { get; set; } = [];

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

/// <summary>
/// One dump of a set: a database, or the users and rights of its server. A 7z archive packs the manifest before the
/// dumps (7-Zip packs by name, and «custos-set.json» comes before «db/…»), so there a dump may be listed without
/// <see cref="Ok"/>, <see cref="Bytes"/> and <see cref="Sha256"/>: then its own last line tells whether it is complete
/// (a dump that failed ends with <see cref="Custos.Core.Databases.DumpCheck.IncompleteMarker"/>).
/// </summary>
public sealed class ManifestDatabase
{
    /// <summary>One of <see cref="Model.DbEngines"/>.</summary>
    public string Engine { get; set; } = string.Empty;

    /// <summary>The real name of the database; null for the users and rights of the server.</summary>
    public string? Name { get; set; }

    /// <summary>Where the dump lies in the set ("db/mysql/site_db.sql"); null if there is none (the server could
    /// not be reached).</summary>
    public string? File { get; set; }

    /// <summary>The server, "host:port".</summary>
    public string Server { get; set; } = string.Empty;

    public long? Bytes { get; set; }

    public string? Sha256 { get; set; }

    /// <summary>True: complete. False: failed. Null: not known when the manifest was written.</summary>
    public bool? Ok { get; set; }

    public string? Error { get; set; }

    public bool IsUsers => Name is null;
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
        if (header.Databases.Count > 0)
        {
            writer.WriteStartArray("databases");
            foreach (var database in header.Databases)
            {
                writer.WriteStartObject();
                writer.WriteString("engine", database.Engine);
                if (database.Name is null)
                {
                    writer.WriteBoolean("users", true);
                }
                else
                {
                    writer.WriteString("name", database.Name);
                }

                WriteNullable(writer, "file", database.File);
                writer.WriteString("server", database.Server);
                if (database.Bytes is { } bytes)
                {
                    writer.WriteNumber("bytes", bytes);
                }

                if (database.Sha256 is not null)
                {
                    writer.WriteString("sha256", database.Sha256);
                }

                if (database.Ok is { } ok)
                {
                    writer.WriteBoolean("ok", ok);
                }

                if (database.Error is not null)
                {
                    writer.WriteString("error", database.Error);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

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

                if (name == "databases")
                {
                    if (!ReadDatabases(ref reader, header.Databases))
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

    private static bool ReadDatabases(ref Utf8JsonReader reader, List<ManifestDatabase> databases)
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

            var database = new ManifestDatabase();
            bool users = false;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                string property = reader.GetString()!;
                if (!reader.Read())
                {
                    return false;
                }

                if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                {
                    if (!reader.TrySkip())
                    {
                        return false;
                    }

                    continue;
                }

                string? text = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                bool? flag = reader.TokenType switch { JsonTokenType.True => true, JsonTokenType.False => false, _ => null };
                switch (property)
                {
                    case "engine": database.Engine = text ?? string.Empty; break;
                    case "name": database.Name = text; break;
                    case "users": users = flag == true; break;
                    case "file": database.File = text; break;
                    case "server": database.Server = text ?? string.Empty; break;
                    case "bytes": database.Bytes = reader.TokenType == JsonTokenType.Number ? reader.GetInt64() : null; break;
                    case "sha256": database.Sha256 = text; break;
                    case "ok": database.Ok = flag; break;
                    case "error": database.Error = text; break;
                }
            }

            if (users)
            {
                database.Name = null;
            }

            if (database.Name is not null || users)
            {
                databases.Add(database);
            }
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
