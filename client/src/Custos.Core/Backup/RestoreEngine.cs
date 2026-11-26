using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Custos.Core.Archives;
using Custos.Core.Databases;
using Custos.Core.Text;

namespace Custos.Core.Backup;

/// <summary>What to do with a file that already exists where a restored file goes (section 6.11).</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<RestoreExisting>))]
public enum RestoreExisting
{
    /// <summary>Leave it alone.</summary>
    Skip,

    /// <summary>Replace it.</summary>
    Overwrite,

    /// <summary>Replace it only if it is older than the copy.</summary>
    OverwriteOlder,
}

/// <summary>One restore to perform.</summary>
public sealed class RestoreRequest
{
    /// <summary>The sets found in the destination; the chain of <see cref="Set"/> is taken from them.</summary>
    public required IReadOnlyList<StoredSet> Sets { get; init; }

    /// <summary>The set whose date to restore.</summary>
    public required StoredSet Set { get; init; }

    /// <summary>Where to put the files: null = where they came from; a folder = below it, as they lie in the set.</summary>
    public string? TargetFolder { get; init; }

    public RestoreExisting Existing { get; init; } = RestoreExisting.Skip;

    /// <summary>Require the source paths in the manifest to be relative and normalize them before reading.</summary>
    public bool ConfinedSource { get; init; }

    /// <summary>Where the folder «db» with the database dumps and their README.txt goes (section 6.2); null =
    /// <see cref="TargetFolder"/>. With both null the dumps are not restored.</summary>
    public string? DumpsFolder { get; init; }

    /// <summary>Reads the sets: the destination they lie in (a folder, a share with its logon, a server) and the
    /// password of the archives.</summary>
    public required ArchiveAccess Access { get; init; }

    /// <summary>The logon to write the restored files with.</summary>
    public INetworkSession WriteSession { get; init; } = ProcessSession.Instance;
}

/// <summary>What a restore did.</summary>
public sealed class RestoreResult
{
    /// <summary>One of <see cref="Model.RunStatuses"/>.</summary>
    public string Status { get; set; } = Model.RunStatuses.Success;

    public long FilesRestored { get; set; }

    public long FilesSkipped { get; set; }

    public long FilesFailed { get; set; }

    /// <summary>Database dumps restored incomplete or missing from the set.</summary>
    public long Warnings { get; set; }

    public long Bytes { get; set; }

    /// <summary>The first error, in words.</summary>
    public string? ErrorMessage { get; set; }
}

/// <summary>A restore in progress.</summary>
public sealed class RestoreProgress
{
    public long FilesDone { get; set; }

    public long FilesTotal { get; set; }

    public long BytesDone { get; set; }

    public long BytesTotal { get; set; }

    public string? CurrentFile { get; set; }

    public double Percent => BytesTotal > 0 ? Math.Min(100, BytesDone * 100.0 / BytesTotal)
        : FilesTotal > 0 ? Math.Min(100, FilesDone * 100.0 / FilesTotal) : 0;

    public RestoreProgress Copy() => (RestoreProgress)MemberwiseClone();
}

/// <summary>
/// Restores the state of a date (section 6.11): the files the chosen set lists, each from the newest set of its chain
/// that holds it (the full set, then the incremental or differential sets). Files deleted before that date are not
/// restored. Every file is checked against the SHA-256 of its manifest while it is written, and appears under its
/// name only when complete.
/// </summary>
public sealed partial class RestoreEngine(IRunLog log, Action<RestoreProgress>? onProgress = null)
{
    private const int BufferSize = 1024 * 1024;
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);

    private readonly IRunLog _log = log ?? throw new ArgumentNullException(nameof(log));
    private readonly RestoreProgress _progress = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly HashSet<string> _restored = new(PathNames.Comparer);
    private TimeSpan _lastProgress = TimeSpan.MinValue;

    /// <summary>Runs the restore. Problems with single files end up in the result; a set that cannot be read at all
    /// throws.</summary>
    /// <exception cref="InvalidDataException">A set of the chain is missing or has no readable manifest.</exception>
    /// <exception cref="ArchiveException">An archive cannot be opened (for example the password does not fit).</exception>
    public RestoreResult Run(RestoreRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = new RestoreResult();
        if (!ReferenceEquals(request.Access.Store, Destinations.LocalStore.Default))
        {
            // Stopping the restore also stops a download that is under way.
            request.Access.Store.Cancellation = cancellationToken;
        }

        var chain = SetCatalog.RestoreChain(request.Sets, request.Set);
        _log.Write(RunLogLevel.Info, Messages.Format(Messages.RestoreStarted, request.Set.Header.SetName,
            request.TargetFolder ?? Messages.RestoreToOriginal, string.Join(", ", chain.Select(s => s.Header.SetName))));
        if (IsRemote(request.Set.Header))
        {
            return RunRemote(request, chain, result, cancellationToken);
        }

        var manifests = new List<(StoredSet Set, Dictionary<string, ManifestFile> Files)>();
        foreach (var set in chain)
        {
            var files = SetCatalog.ReadManifest(set, request.Access).Files;
            manifests.Add((set, files.ToDictionary(f => Key(f.Path), PathNames.Comparer)));
        }

        // Each file of the date, from the newest set of the chain that holds it. A 7z archive lists some files before
        // they are packed (7-Zip packs by name), without a SHA-256; one that could not be read after all is not in
        // that archive, so it comes from an older set.
        var entries = new Dictionary<StoredSet, HashSet<string>>();
        bool Holds(StoredSet set, Dictionary<string, ManifestFile> files, string key)
        {
            if (!files.TryGetValue(key, out var stored) || !stored.InSet)
            {
                return false;
            }

            if (stored.Sha256 is not null || set.Storage != SetStorage.Archive)
            {
                return true;
            }

            if (!entries.TryGetValue(set, out var present))
            {
                using var reader = request.Access.Open(set);
                entries[set] = present = reader.Entries.Where(e => !e.IsFolder).Select(e => Key(e.Path)).ToHashSet(PathNames.Comparer);
            }

            return present.Contains(key);
        }

        var work = new Dictionary<StoredSet, List<Planned>>();
        foreach (var file in manifests[^1].Files.Values)
        {
            var holder = manifests.LastOrDefault(m => Holds(m.Set, m.Files, Key(file.Path)));
            if (holder.Set is null)
            {
                Failed(result, Messages.Format(Messages.RestoreNotInChain, file.Path));
                continue;
            }

            string? destination = request.ConfinedSource && !SafeSourcePath(file.Path) ? null
                : Destination(request.Set.Header, file.Path, request.TargetFolder);
            if (destination is null)
            {
                Failed(result, Messages.Format(Messages.RestoreNoPlace, file.Path));
                continue;
            }

            if (!work.TryGetValue(holder.Set, out var list))
            {
                work[holder.Set] = list = [];
            }

            list.Add(new Planned(holder.Files[Key(file.Path)], destination));
        }

        var dumps = PlanDumps(request, result);
        if (dumps.Count > 0)
        {
            work[request.Set] = [.. work.GetValueOrDefault(request.Set) ?? [], .. dumps];
        }

        _progress.FilesTotal = work.Values.Sum(l => l.Count);
        _progress.BytesTotal = work.Values.Sum(l => l.Sum(p => p.File.Size));
        Report(force: true);
        foreach (var (set, list) in work)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var todo = list.Where(p => Wanted(request, p, result)).ToList();
            if (set.Storage == SetStorage.Archive)
            {
                RestoreFromArchive(request, set, todo, result, cancellationToken);
            }
            else
            {
                foreach (var planned in todo)
                {
                    RestoreFromFolder(request, set, planned, result, cancellationToken);
                }
            }
        }

        _progress.CurrentFile = null;
        Report(force: true);
        if (dumps.Count > 0)
        {
            FinishDumps(request, dumps, result);
        }

        result.Status = result.FilesFailed > 0 ? Model.RunStatuses.Error : result.Warnings > 0 ? Model.RunStatuses.Warning : Model.RunStatuses.Success;
        _log.Write(result.FilesFailed > 0 ? RunLogLevel.Error : RunLogLevel.Info, Messages.Format(Messages.RestoreFinished,
            result.FilesRestored, Sizes.Format(result.Bytes), result.FilesSkipped, result.FilesFailed));
        return result;
    }

    /// <summary>
    /// Where a file of the set goes: below <paramref name="targetFolder"/> as it lies in the set, or back to its source
    /// (the source whose folder in the set the path starts with). Null if no source fits or the path is not safe.
    /// </summary>
    public static string? Destination(SetManifestHeader header, string storedPath, string? targetFolder)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(storedPath);
        string path = ArchivePaths.ToWindows(storedPath);
        if (!ArchivePaths.IsSafeRelative(path))
        {
            return null;
        }

        if (targetFolder is not null)
        {
            return Path.Combine(targetFolder, Local(path));
        }

        foreach (var source in header.Sources.OrderByDescending(s => s.Target.Length))
        {
            string target = ArchivePaths.ToWindows(source.Target);
            if (target.Length == 0)
            {
                return Path.Combine(source.Path, Local(path));
            }

            if (string.Equals(path, target, PathNames.Comparison))
            {
                // A single file stored under its name.
                return source.Path;
            }

            if (path.StartsWith(target + "\\", PathNames.Comparison))
            {
                return Path.Combine(source.Path, Local(path[(target.Length + 1)..]));
            }
        }

        return null;
    }

    private bool Wanted(RestoreRequest request, Planned planned, RestoreResult result)
    {
        bool exists;
        DateTime existingTime;
        try
        {
            (exists, existingTime) = request.WriteSession.Run(() =>
                File.Exists(planned.Destination) ? (true, File.GetLastWriteTimeUtc(planned.Destination)) : (false, default));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            exists = false;
            existingTime = default;
        }

        bool skip = exists && (request.Existing == RestoreExisting.Skip
            || (request.Existing == RestoreExisting.OverwriteOlder && existingTime >= planned.File.ModifiedUtc));
        if (!skip)
        {
            return true;
        }

        result.FilesSkipped++;
        _progress.FilesDone++;
        _progress.BytesDone += planned.File.Size;
        _log.Write(RunLogLevel.Detail, Messages.Format(Messages.RestoreSkipped, planned.Destination));
        return false;
    }

    private void RestoreFromFolder(RestoreRequest request, StoredSet set, Planned planned, RestoreResult result, CancellationToken cancellationToken)
    {
        _progress.CurrentFile = planned.Destination;
        Report(force: false);
        Output? output = null;
        try
        {
            var store = request.Access.Store;
            string path = planned.File.Path;
            if (request.ConfinedSource)
            {
                if (!SafeSourcePath(path))
                {
                    Failed(result, Messages.Format(Messages.RestoreNoPlace, path));
                    return;
                }

                path = ArchivePaths.ToZip(path);
            }

            using var input = store.OpenRead(store.Combine(set.Location, path));
            output = Output.Create(request.WriteSession, planned.Destination);
            var buffer = new byte[BufferSize];
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                output.Write(buffer.AsSpan(0, read));
                _progress.BytesDone += read;
                Report(force: false);
            }

            Complete(request, planned, output, result);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            output?.Abandon(request.WriteSession);
            Failed(result, Messages.Format(Messages.RestoreFileFailed, planned.Destination, ex.Message));
        }
        catch
        {
            output?.Abandon(request.WriteSession);
            throw;
        }

        _progress.FilesDone++;
    }

    private void RestoreFromArchive(RestoreRequest request, StoredSet set, List<Planned> todo, RestoreResult result, CancellationToken cancellationToken)
    {
        if (todo.Count == 0)
        {
            return;
        }

        using var reader = request.Access.Open(set);
        var byPath = reader.Entries.Where(e => !e.IsFolder)
            .GroupBy(e => Key(e.Path), PathNames.Comparer)
            .ToDictionary(g => g.Key, g => g.Last(), PathNames.Comparer);
        var wanted = new Dictionary<int, Planned>();
        foreach (var planned in todo)
        {
            if (byPath.TryGetValue(Key(planned.File.Path), out var entry))
            {
                wanted[entry.Index] = planned;
            }
            else if (planned.Database is { } database)
            {
                // A 7z archive lists its dumps before packing them: one that failed at its start is not there.
                _progress.FilesDone++;
                result.Warnings++;
                _log.Write(RunLogLevel.Warning, Messages.Format(Messages.RestoreDumpMissing, DumpName(database), Messages.VerifyFileMissing));
            }
            else
            {
                _progress.FilesDone++;
                Failed(result, Messages.Format(Messages.RestoreFileFailed, planned.Destination, Messages.VerifyFileMissing));
            }
        }

        var open = new Dictionary<int, Output>();
        try
        {
            reader.Extract(
                wanted.Keys,
                entry =>
                {
                    var planned = wanted[entry.Index];
                    _progress.CurrentFile = planned.Destination;
                    Report(force: false);
                    try
                    {
                        var output = Output.Create(request.WriteSession, planned.Destination);
                        output.Written = count =>
                        {
                            _progress.BytesDone += count;
                            Report(force: false);
                        };
                        open[entry.Index] = output;
                        return output;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        Failed(result, Messages.Format(Messages.RestoreFileFailed, planned.Destination, ex.Message));
                        return null;
                    }
                },
                (entry, outcome) =>
                {
                    var planned = wanted[entry.Index];
                    _progress.FilesDone++;
                    if (!open.Remove(entry.Index, out var output))
                    {
                        return;
                    }

                    if (outcome != ExtractOutcome.Ok)
                    {
                        output.Abandon(request.WriteSession);
                        Failed(result, Messages.Format(Messages.RestoreFileFailed, planned.Destination, BackupEngine.OutcomeText(outcome)));
                        return;
                    }

                    try
                    {
                        Complete(request, planned, output, result);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        output.Abandon(request.WriteSession);
                        Failed(result, Messages.Format(Messages.RestoreFileFailed, planned.Destination, ex.Message));
                    }
                },
                cancellationToken);
        }
        finally
        {
            foreach (var output in open.Values)
            {
                output.Abandon(request.WriteSession);
            }
        }
    }

    /// <summary>Checks the SHA-256, then gives the file its name and its time.</summary>
    private void Complete(RestoreRequest request, Planned planned, Output output, RestoreResult result)
    {
        using var done = output;
        string hash = output.Finish();
        if (planned.File.Sha256 is { } expected && !string.Equals(hash, expected, StringComparison.OrdinalIgnoreCase))
        {
            output.Abandon(request.WriteSession);
            Failed(result, Messages.Format(Messages.RestoreFileFailed, planned.Destination, Messages.VerifyContentDiffers));
            return;
        }

        request.WriteSession.Run(() =>
        {
            if (File.Exists(planned.Destination))
            {
                File.SetAttributes(planned.Destination, FileAttributes.Normal);
            }

            File.Move(output.Temp, planned.Destination, overwrite: true);
            File.SetLastWriteTimeUtc(planned.Destination, DateTime.SpecifyKind(planned.File.ModifiedUtc, DateTimeKind.Utc));
        });
        _restored.Add(planned.Destination);
        result.FilesRestored++;
        result.Bytes += output.Length;
        _log.Write(RunLogLevel.Detail, Messages.Format(Messages.RestoreFileDone, planned.Destination, Sizes.Format(output.Length)));
    }

    private void Failed(RestoreResult result, string message)
    {
        result.FilesFailed++;
        result.ErrorMessage ??= message;
        _log.Write(RunLogLevel.Warning, message);
    }

    private void Report(bool force)
    {
        if (onProgress is null)
        {
            return;
        }

        var now = _clock.Elapsed;
        if (!force && now - _lastProgress < ProgressInterval)
        {
            return;
        }

        _lastProgress = now;
        onProgress(_progress.Copy());
    }

    /// <summary>A path of a manifest or archive, the same whatever separators it was written with.</summary>
    private static string Key(string path) => ArchivePaths.ToWindows(path);

    /// <summary>A "\"-separated path for this system.</summary>
    private static string Local(string windowsPath) => windowsPath.Replace('\\', Path.DirectorySeparatorChar);

    private static bool SafeSourcePath(string path) => path.Length != 0 && path[0] is not ('/' or '\\')
        && ArchivePaths.IsSafeRelative(path);

    /// <summary>The dumps of the chosen set (each set has whole dumps, so only that set matters), into
    /// <c>&lt;folder&gt;\db\…</c>.</summary>
    private List<Planned> PlanDumps(RestoreRequest request, RestoreResult result)
    {
        var databases = request.Set.Header.Databases;
        if (databases.Count == 0)
        {
            return [];
        }

        string? folder = request.DumpsFolder ?? request.TargetFolder;
        if (folder is null)
        {
            _log.Write(RunLogLevel.Warning, Messages.RestoreDumpsNoFolder);
            result.Warnings++;
            return [];
        }

        var planned = new List<Planned>();
        foreach (var database in databases)
        {
            if (database.File is null || (request.ConfinedSource ? !SafeSourcePath(database.File) : !ArchivePaths.IsSafeRelative(database.File)))
            {
                continue;
            }

            var file = new ManifestFile(database.File, database.Bytes ?? 0, request.Set.Header.StartedAt.UtcDateTime, InSet: true, database.Sha256);
            planned.Add(new Planned(file, Path.Combine(folder, Local(Key(database.File)))) { Database = database });
        }

        return planned;
    }

    /// <summary>
    /// Checks the end line of every restored dump: an incomplete one is renamed «….incomplete.sql», so that nobody
    /// loads it by mistake. Then writes db\README.txt with the commands to load them.
    /// </summary>
    private void FinishDumps(RestoreRequest request, List<Planned> dumps, RestoreResult result)
    {
        var lines = CheckDumps(request, dumps, result);
        string folder = request.DumpsFolder ?? request.TargetFolder!;
        string readme = Path.Combine(folder, DbTools.SetFolder, "README.txt");
        try
        {
            string text = DumpReadme.Build(request.Set.Header, folder, lines);
            request.WriteSession.Run(() =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(readme)!);
                File.WriteAllText(readme, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            });
            _log.Write(RunLogLevel.Info, Messages.Format(Messages.RestoreDumpsReadme, readme));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Write(RunLogLevel.Warning, Messages.Format(Messages.RestoreFileFailed, readme, ex.Message));
        }
    }

    /// <summary>Renames incomplete dumps and warns about missing ones; gives every dump that is there.</summary>
    private List<(ManifestDatabase Database, string Path, bool Complete)> CheckDumps(RestoreRequest request, List<Planned> dumps, RestoreResult result)
    {
        var lines = new List<(ManifestDatabase Database, string Path, bool Complete)>();
        foreach (var planned in dumps)
        {
            var database = planned.Database!;
            string path = planned.Destination;
            if (!_restored.Contains(path))
            {
                if (database.Ok == false || !request.WriteSession.Run(() => File.Exists(path)))
                {
                    continue;
                }

                // Skipped as already there: listed as it is.
                lines.Add((database, path, true));
                continue;
            }

            bool complete = database.Ok != false && request.WriteSession.Run(() => DumpCheck.HasEndLine(database.Engine, ReadTail(path)));
            if (!complete)
            {
                string renamed = Path.ChangeExtension(path, ".incomplete.sql");
                request.WriteSession.Run(() => File.Move(path, renamed, overwrite: true));
                path = renamed;
                result.Warnings++;
                _log.Write(RunLogLevel.Warning, Messages.Format(Messages.RestoreDumpIncomplete, renamed));
            }

            lines.Add((database, path, complete));
        }

        foreach (var database in request.Set.Header.Databases.Where(d => d.Ok == false && d.File is null))
        {
            result.Warnings++;
            _log.Write(RunLogLevel.Warning, Messages.Format(Messages.RestoreDumpMissing, DumpName(database), database.Error ?? "?"));
        }

        return lines;
    }

    private static string DumpName(ManifestDatabase database) => database.Name ?? DbTools.UsersFileStem;

    private static byte[] ReadTail(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        long start = Math.Max(0, stream.Length - DumpCheck.TailBytes);
        stream.Seek(start, SeekOrigin.Begin);
        var tail = new byte[stream.Length - start];
        stream.ReadExactly(tail);
        return tail;
    }

    private sealed record Planned(ManifestFile File, string Destination)
    {
        /// <summary>Set for the dump of a database.</summary>
        public ManifestDatabase? Database { get; init; }
    }

    /// <summary>A restored file being written under a temporary name, hashed on the way.</summary>
    private sealed class Output : Stream
    {
        private readonly FileStream _stream;
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private long _length;

        private Output(FileStream stream, string temp)
        {
            _stream = stream;
            Temp = temp;
        }

        public string Temp { get; }

        public Action<int>? Written { get; set; }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => _length;

        public override long Position
        {
            get => _length;
            set => throw new NotSupportedException();
        }

        public static Output Create(INetworkSession session, string destination)
        {
            string temp = destination + BackupEngine.TempSuffix;
            var stream = session.Run(() =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                return new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize);
            });
            return new Output(stream, temp);
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _stream.Write(buffer);
            _hash.AppendData(buffer);
            _length += buffer.Length;
            Written?.Invoke(buffer.Length);
        }

        /// <summary>Closes the file and gives the SHA-256 of what was written.</summary>
        public string Finish()
        {
            _stream.Dispose();
            return SetManifest.ToHex(_hash.GetHashAndReset());
        }

        /// <summary>Closes and deletes the temporary file.</summary>
        public void Abandon(INetworkSession session)
        {
            try
            {
                Dispose();
                session.Run(() => File.Delete(Temp));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The temporary name never looks like a restored file.
            }
        }

        public override void Flush() => _stream.Flush();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _stream.Dispose();
                _hash.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
