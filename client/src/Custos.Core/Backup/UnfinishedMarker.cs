using System.Globalization;
using System.Text.Json;
using Custos.Core.Destinations;
using Custos.Core.Json;

namespace Custos.Core.Backup;

/// <summary>
/// <c>custos-unfinished.json</c> in a set folder (section 6.6): written before anything else when a run starts a set,
/// and before retention starts to delete one; removed when the set is complete. A folder that has it and no manifest
/// is a set whose run or deletion never finished, and a later run of its task removes it.
/// </summary>
public static class UnfinishedMarker
{
    public const string FileName = "custos-unfinished.json";

    /// <summary>A run writes the set.</summary>
    public const string Writing = "writing";

    /// <summary>Retention deletes the set.</summary>
    public const string Removing = "removing";

    /// <summary>What a marker says.</summary>
    /// <param name="State"><see cref="Writing"/> or <see cref="Removing"/>.</param>
    public sealed record Info(string TaskId, string RunId, string State, DateTimeOffset Since);

    /// <summary>Writes the marker into <paramref name="folder"/>, replacing one that is there.</summary>
    /// <exception cref="IOException">The destination failed.</exception>
    public static void Write(DestinationStore store, string folder, Info info)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(info);

        // The marker is removed again: S3 Object Lock must not hold it.
        bool locks = store.LockWrites;
        store.LockWrites = false;
        try
        {
            using var writer = store.BeginWrite(store.Combine(folder, FileName), BackupEngine.TempSuffix, StoreWriteMode.Stream);
            using (var json = new Utf8JsonWriter(writer.Stream, new JsonWriterOptions { Indented = true }))
            {
                json.WriteStartObject();
                json.WriteString("task_id", info.TaskId);
                json.WriteString("run_id", info.RunId);
                json.WriteString("state", info.State);
                json.WriteString("since", UtcTimeConverter.ToText(info.Since));
                json.WriteEndObject();
            }

            writer.Commit();
        }
        finally
        {
            store.LockWrites = locks;
        }
    }

    /// <summary>The marker in <paramref name="folder"/>; null if there is none or it cannot be read as one.</summary>
    /// <exception cref="IOException">A server failed (a missing file is not a failure).</exception>
    public static Info? TryRead(DestinationStore store, string folder)
    {
        ArgumentNullException.ThrowIfNull(store);
        try
        {
            using var stream = store.OpenRead(store.Combine(folder, FileName));
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            string? taskId = root.TryGetProperty("task_id", out var task) ? task.GetString() : null;
            string? runId = root.TryGetProperty("run_id", out var run) ? run.GetString() : null;
            string? state = root.TryGetProperty("state", out var st) ? st.GetString() : null;
            string? since = root.TryGetProperty("since", out var time) ? time.GetString() : null;
            if (string.IsNullOrEmpty(taskId) || string.IsNullOrEmpty(runId) || state is not (Writing or Removing)
                || !DateTimeOffset.TryParse(since, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when))
            {
                return null;
            }

            return new Info(taskId, runId, state, when.ToUniversalTime());
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or JsonException or InvalidOperationException)
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && Model.DestinationKinds.IsFileSystem(store.Kind))
        {
            // As with manifests: a file that cannot be read on a disk is not a marker.
            return null;
        }
    }

    /// <summary>Removes the marker of a set that is complete. Where it cannot be removed (WORM locked it during a long
    /// run) it stays: the manifest beside it says the set is complete.</summary>
    public static void TryRemove(DestinationStore store, string folder)
    {
        ArgumentNullException.ThrowIfNull(store);
        try
        {
            store.DeleteFile(store.Combine(folder, FileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // See above.
        }
    }
}
