using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using Custos.Core.Archives;
using Custos.Core.Databases;
using Custos.Core.Destinations;
using Custos.Core.Model;
using Custos.Core.Remote;
using Custos.Core.Text;

namespace Custos.Core.Backup;

/// <summary>
/// A Linux server as the source of a task (CUSTOS-remote-pull.md 4.3): custos-helper over SSH gives the dumps, the list
/// of the files and a tar of what changed, and everything goes straight into the set, nothing through temporary files.
/// </summary>
/// <remarks>
/// The set holds <c>db/&lt;engine&gt;/…sql</c> (as the dumps on Windows), <c>files.tar</c> (the folders and the files
/// changed after the time of the server at the last committed run, minus ten minutes; all for a full copy) and the
/// manifest with every entry of the server. ZIP and plain sets write the manifest after the tar, so it says exactly
/// which files the tar holds and their SHA-256. 7-Zip packs by name and writes the manifest first: there it lists what
/// the tar is going to hold by the ctime of each entry; restore reads the tar itself anyway.
/// </remarks>
public sealed partial class BackupEngine
{
    /// <summary>The tar with the files of a set from a Linux server.</summary>
    public const string RemoteTarName = "files.tar";

    /// <summary>The next copy starts this long before the time of the server at the last one (clocks, slow writes).</summary>
    public const long RemoteOverlapSeconds = 600;

    private const int RemoteAttempts = 3;
    private const int MaxWarningLines = 20;

    public static bool IsRemote(TaskDefinition task)
    {
        ArgumentNullException.ThrowIfNull(task);
        return task.Sources.Any(s => s.Kind == SourceKinds.Ssh);
    }

    /// <summary>"custos@vps.example.org:22".</summary>
    public static string RemoteLabel(TaskSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return string.Concat(source.User ?? "custos", "@", source.Host, ":", (source.Port ?? 22).ToString(CultureInfo.InvariantCulture));
    }

    private static void CheckRemoteSupported(TaskDefinition task)
    {
        if (task.Sources.Count != 1)
        {
            throw new BackupException(Messages.RemoteOnlySource);
        }

        if (task.BackupType == BackupTypes.Mirror)
        {
            throw new BackupException(Messages.RemoteNoMirror);
        }

        if (task.DbServers.Count > 0)
        {
            throw new BackupException(Messages.RemoteNoDbServers);
        }
    }

    /// <summary>
    /// For an incremental or differential copy: the set it builds on must be in every destination, with the time of
    /// the server in it. Null when it is; otherwise why a full copy is made instead.
    /// </summary>
    private static string? RemoteBaseMissing(RunContext run)
    {
        string? baseId = run.BackupType == BackupTypes.Differential ? run.State.FullRunId : run.State.LastRunId;
        foreach (var target in run.ActiveTargets)
        {
            var set = target.Sets.FirstOrDefault(s => string.Equals(s.Header.RunId, baseId, StringComparison.OrdinalIgnoreCase));
            if (set?.Header.Remote is null)
            {
                return Messages.Format(Messages.RemoteFullBecauseNoBase, target.Label);
            }
        }

        return null;
    }

    /// <summary>Connects, asks custos-helper what it has, plans the dumps and reads the list of the files.</summary>
    private void PrepareRemote(RunContext run)
    {
        var source = run.Task.Sources[0];
        string where = RemoteLabel(source);
        if (!SshKeys.IsFingerprint(source.HostKeySha256))
        {
            throw new BackupException(Messages.Format(Messages.RemoteHostKeyNotPinned, where));
        }

        if (string.IsNullOrEmpty(source.CredentialId))
        {
            throw new BackupException(Messages.RemoteKeyMissing);
        }

        var remote = new RemoteRun(source);
        run.Remote = remote;
        var target = new SshTarget(source.Host ?? string.Empty, source.Port ?? 22, source.User ?? "custos", _platform.GetSshKey(source.CredentialId), source.HostKeySha256);
        remote.Session = ConnectRemote(run, target, where);
        remote.Helper = new HelperClient(remote.Session);
        HelperInfo info;
        try
        {
            info = remote.Helper.Info(run.Cancellation);
        }
        catch (Exception ex) when (ex is HelperException or SshFailedException or IOException)
        {
            throw new BackupException(Messages.Format(Messages.RemoteInfoFailed, where, ex.Message), ex);
        }

        if (info.Protocol != HelperScript.Protocol)
        {
            throw new BackupException(Messages.Format(Messages.RemoteHelperIncompatible, where, info.HelperVersion, info.Protocol, HelperScript.Version));
        }

        remote.Info = info;
        long? free = info.Roots.Where(r => r.FreeBytes is not null).Select(r => r.FreeBytes).Min();
        _log.Write(RunLogLevel.Info, Messages.Format(Messages.RemoteConnected, where, info.HelperVersion, info.Os,
            string.Join(", ", info.Roots.Select(r => r.Path)), free is { } bytes ? Sizes.Format(bytes) : "?"));
        if (HelperScript.IsOlder(info.HelperVersion, HelperScript.Version))
        {
            Warn(run, Messages.Format(Messages.RemoteHelperOutdated, info.HelperVersion, HelperScript.Version));
        }

        var last = RemoteSetHeader(run, run.State.LastRunId);
        if (last?.Remote is { } previous && previous.ConfigSha256.Length > 0 && !string.Equals(previous.ConfigSha256, info.ConfigSha256, StringComparison.OrdinalIgnoreCase))
        {
            Warn(run, Messages.RemoteConfigChanged);
        }

        var baseHeader = run.BackupType switch
        {
            BackupTypes.Incremental => last,
            BackupTypes.Differential => RemoteSetHeader(run, run.State.FullRunId),
            _ => null,
        };
        remote.Since = baseHeader?.Remote is { } start ? Math.Max(1, start.ServerNow - RemoteOverlapSeconds) : 0;

        PlanRemoteDatabases(run, info, source);

        Phase(BackupPhases.Scan);
        try
        {
            var (entries, warnings) = remote.Helper.Manifest(run.Cancellation);
            remote.Listing = entries;
            WarnLines(run, warnings);
        }
        catch (Exception ex) when (ex is HelperException or SshFailedException or IOException)
        {
            throw new BackupException(Messages.Format(Messages.RemoteListingFailed, where, ex.Message), ex);
        }

        long sinceTicks = remote.Since * TimeSpan.TicksPerSecond;
        foreach (var entry in remote.Listing)
        {
            if (entry.IsFile)
            {
                run.Result.SourceFiles++;
                run.Result.SourceBytes += entry.Size;
            }

            if (!entry.IsFolder && (remote.Since == 0 || entry.CTimeTicks > sinceTicks))
            {
                remote.Planned.Add(entry.Key);
                run.BytesToCopy += entry.IsFile ? entry.Size : 0;
            }
        }

        remote.FilesChanged = last?.Remote is { } changedSince
            ? remote.Listing.Count(e => !e.IsFolder && e.CTimeTicks > (changedSince.ServerNow - RemoteOverlapSeconds) * TimeSpan.TicksPerSecond)
            : remote.Listing.Count(e => !e.IsFolder);
        remote.FilesDeleted = CountDeleted(run, remote);
        run.Result.Remote = new RemoteResult
        {
            HelperVersion = info.HelperVersion,
            ServerFreeBytes = free,
            FilesChanged = remote.FilesChanged,
            FilesDeleted = remote.FilesDeleted,
        };

        _progress.FilesTotal = remote.Planned.Count + DumpsToTake(run).Count();
        _progress.BytesTotal = run.BytesToCopy + (DumpsToTake(run).Any() ? Math.Max(0, run.Request.DumpBytesHint) : 0);
        string since = remote.Since == 0
            ? Messages.RemoteSinceAll
            : Messages.Format(Messages.RemoteSinceTime, TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(remote.Since), run.Request.TimeZone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
        _log.Write(RunLogLevel.Info, Messages.Format(Messages.RemotePlanned, remote.Listing.Count, run.Result.SourceFiles, Sizes.Format(run.Result.SourceBytes),
            remote.Planned.Count, Sizes.Format(run.BytesToCopy), since,
            remote.FilesDeleted is { } deleted ? Messages.Format(Messages.RemoteDeletedPart, deleted) : string.Empty));
        Report(force: true);
    }

    private ISshSession ConnectRemote(RunContext run, SshTarget target, string where)
    {
        for (int attempt = 1; ; attempt++)
        {
            run.Cancellation.ThrowIfCancellationRequested();
            try
            {
                return _platform.Ssh.Connect(target, run.Cancellation);
            }
            catch (SshHostKeyException ex)
            {
                // A changed key may be an attack: nothing is read, no second try.
                throw new BackupException(ex.Message, ex);
            }
            catch (SshFailedException ex) when (attempt < RemoteAttempts)
            {
                _log.Write(RunLogLevel.Warning, Messages.Format(Messages.RemoteRetry, ex.Message, (int)run.Request.RemoteRetryDelay.TotalSeconds));
                if (run.Cancellation.WaitHandle.WaitOne(run.Request.RemoteRetryDelay))
                {
                    run.Cancellation.ThrowIfCancellationRequested();
                }
            }
            catch (SshFailedException ex)
            {
                throw new BackupException(Messages.Format(Messages.RemoteGaveUp, where, RemoteAttempts, ex.Message), ex);
            }
        }
    }

    /// <summary>The header of a set of this task in the first destination that has it, if it came from a Linux server.</summary>
    private static SetManifestHeader? RemoteSetHeader(RunContext run, string? runId)
    {
        if (runId is null)
        {
            return null;
        }

        return run.ActiveTargets.SelectMany(t => t.Sets).Select(s => s.Header)
            .FirstOrDefault(h => h.Remote is not null && string.Equals(h.RunId, runId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Files listed by the last committed set and gone now; null when that list cannot be read.</summary>
    private long? CountDeleted(RunContext run, RemoteRun remote)
    {
        string? runId = run.State.LastRunId;
        foreach (var target in run.ActiveTargets)
        {
            var set = target.Sets.FirstOrDefault(s => s.Header.Remote is not null && string.Equals(s.Header.RunId, runId, StringComparison.OrdinalIgnoreCase));
            if (set is null)
            {
                continue;
            }

            try
            {
                var files = SetCatalog.ReadManifest(set, run.Access?.WithStore(target.Store), target.Store).Files;
                var now = remote.Listing.Select(e => e.Key).ToHashSet(StringComparer.Ordinal);
                return files.Count(f => f.Posix?.Type is "f" or "l" && !now.Contains(RemotePath.Key(f)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArchiveException or System.Text.Json.JsonException or FormatException)
            {
                _log.Write(RunLogLevel.Detail, Messages.Format(Messages.RemoteLastListUnreadable, set.Header.SetName, ex.Message));
            }
        }

        return null;
    }

    /// <summary>
    /// The dumps of every engine of helper.conf: the databases custos-helper lists (it leaves out the system ones and
    /// <c>db_exclude</c>), as chosen in the task: all but the unticked ones, or only the ticked ones.
    /// </summary>
    private void PlanRemoteDatabases(RunContext run, HelperInfo info, TaskSource source)
    {
        var choice = source.Databases;
        string address = source.Host ?? string.Empty;
        foreach (string engine in info.DbEngines)
        {
            run.Cancellation.ThrowIfCancellationRequested();
            var server = new TaskDatabaseServer { Engine = engine, Host = address, BinDir = string.Empty };
            string where = string.Concat(DbEngines.Title(engine), " ", source.Host);
            var chosen = choice is { AllDatabases: false } ? RemoteDatabaseChoice.Names(choice.Include, engine) : null;
            var client = run.Remote!.Helper!.DbClient(engine);
            try
            {
                var listed = client.Inspect(run.Cancellation).Databases;
                var names = chosen is null
                    ? listed.Except(RemoteDatabaseChoice.Names(choice?.Exclude ?? [], engine), StringComparer.Ordinal).ToList()
                    : listed.Where(n => chosen.Contains(n, StringComparer.Ordinal)).ToList();
                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { DbTools.UsersFileStem };
                foreach (string name in names)
                {
                    run.Dumps.Add(new PlannedDump(server, client, string.Empty, name, $"{DbTools.SetFolder}/{engine}/{DbTools.FileName(name, used)}.sql", where) { Address = address });
                }

                run.Dumps.Add(new PlannedDump(server, client, string.Empty, null, $"{DbTools.SetFolder}/{engine}/{DbTools.UsersFileStem}.sql", where) { Address = address });
                _log.Write(RunLogLevel.Info, Messages.Format(Messages.RemoteDbReady, where, names.Count,
                    names.Count == 0 ? Messages.DbNoDatabases : string.Join(", ", names)));
                if (listed.Except(names, StringComparer.Ordinal).ToList() is { Count: > 0 } left)
                {
                    // Under «Selected databases» this also names the databases made on the server since.
                    _log.Write(RunLogLevel.Info, Messages.Format(Messages.RemoteDbNotChosen, where, string.Join(", ", left)));
                }

                foreach (string name in chosen?.Except(listed, StringComparer.Ordinal) ?? [])
                {
                    string message = Messages.Format(Messages.DbNotFound, name, where);
                    DbFailed(run, message);
                    run.Dumps.Add(new PlannedDump(server, null, string.Empty, name, null, where) { Address = address, Ok = false, Error = message });
                }
            }
            catch (DbException ex)
            {
                DbFailed(run, Messages.Format(Messages.DbServerUnavailable, where, ex.Message));
                foreach (string name in chosen ?? [])
                {
                    run.Dumps.Add(new PlannedDump(server, null, string.Empty, name, null, where) { Address = address, Ok = false, Error = ex.Message });
                }
            }
        }
    }

    /// <summary>The tar as an item of an archive; it is read when the writer gets to it.</summary>
    private ArchiveItem RemoteTarItem(RunContext run) => new()
    {
        Path = RemoteTarName,
        ModifiedUtc = run.Request.StartedAt.UtcDateTime,
        Open = () => OpenRemoteTar(run),
    };

    /// <summary>Starts <c>tar-since … gzip</c>. A failure here stops the whole set: a set without its files is no copy.</summary>
    private RemoteTarStream OpenRemoteTar(RunContext run)
    {
        var remote = run.Remote!;
        run.Pause.WaitWhilePaused(run.Cancellation);
        run.Cancellation.ThrowIfCancellationRequested();
        lock (_sync)
        {
            _progress.CurrentFile = RemoteTarName;
        }

        Report(force: true);
        try
        {
            var stream = remote.Helper!.StartTar(remote.Since, Math.Max(0, remote.Source.SpeedLimitKbps ?? 0), run.Cancellation);
            return new RemoteTarStream(this, run, stream);
        }
        catch (SshFailedException ex)
        {
            throw new BackupException(Messages.Format(Messages.RemoteTarFailed, ex.Message), ex);
        }
    }

    /// <summary>Plain sets: <c>files.tar</c> into every destination, after the dumps.</summary>
    private void WriteRemoteTar(RunContext run)
    {
        if (run.ActiveTargets.Count == 0)
        {
            return;
        }

        var outputs = new List<Output>();
        try
        {
            foreach (var target in run.ActiveTargets)
            {
                string final = target.Store.Combine(target.WriteFolder, RemoteTarName);
                try
                {
                    outputs.Add(new Output(target, target.Store.BeginWrite(final, TempSuffix, StoreWriteMode.Stream), final));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    FailTarget(run, target, Messages.Format(Messages.DestinationWriteFailed, target.Label, ex.Message));
                }
            }

            if (outputs.Count == 0)
            {
                return;
            }

            var buffer = new byte[BufferSize];
            using (var tar = OpenRemoteTar(run))
            {
                int read;
                while (outputs.Count > 0 && (read = tar.Read(buffer, 0, buffer.Length)) > 0)
                {
                    WriteOutputs(run, outputs, buffer, read);
                }
            }

            for (int i = outputs.Count - 1; i >= 0; i--)
            {
                var output = outputs[i];
                try
                {
                    output.Writer.Commit();
                    output.Target.Files += (int)run.Remote!.StoredFiles;
                    output.Target.Bytes += run.Remote.TarBytes;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    outputs.RemoveAt(i);
                    Abandon(output);
                    FailTarget(run, output.Target, Messages.Format(Messages.DestinationWriteFailed, output.Target.Label, ex.Message));
                }
            }

            outputs.Clear();
        }
        finally
        {
            AbandonOutputs(outputs);
            lock (_sync)
            {
                _progress.CurrentFile = null;
            }
        }
    }

    /// <summary>
    /// The entries of the server for the manifest. With the tar read (ZIP, plain sets) exactly what it holds, with the
    /// SHA-256 of each file; before it (7-Zip writes the manifest first) what it is going to hold.
    /// </summary>
    private static List<ManifestFile> RemoteManifestFiles(RunContext run, out int setFiles, out long setBytes)
    {
        var remote = run.Remote!;
        bool exact = remote.TarDone;
        setFiles = 0;
        setBytes = 0;
        var files = new List<ManifestFile>(remote.Listing.Count + remote.Stored.Count);
        var listed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in remote.Listing)
        {
            string key = entry.Key;
            listed.Add(key);
            bool inSet;
            string? hash = null;
            if (exact)
            {
                inSet = remote.Stored.TryGetValue(key, out var stored);
                hash = stored?.Sha256;
            }
            else
            {
                inSet = entry.IsFolder || remote.Planned.Contains(key);
            }

            if (inSet && entry.IsFile)
            {
                setFiles++;
                setBytes += entry.Size;
            }

            files.Add(Entry(entry.Path, entry.IsFile ? entry.Size : 0, entry.MTimeTicks, inSet, hash, new ManifestPosix(
                entry.Type.ToString(), entry.Mode, entry.Uid, entry.Gid, entry.User, entry.Group,
                entry.Link.Length > 0 ? RemotePath.Display(entry.Link) : null, RemotePath.FromUnixTicks(entry.CTimeTicks))));
        }

        if (exact)
        {
            // Made after the list was read, but in the tar: listed too, so that the set says what it holds.
            foreach (var (key, stored) in remote.Stored.Where(s => !listed.Contains(s.Key)))
            {
                var member = stored.Member;
                if (member.IsFile)
                {
                    setFiles++;
                    setBytes += member.Size;
                }

                files.Add(Entry(member.AbsolutePath, member.IsFile ? member.Size : 0, member.MTimeTicks, true, stored.Sha256, new ManifestPosix(
                    TypeLetter(member.Type), member.Mode, member.Uid, member.Gid, member.User, member.Group,
                    member.Link.Length > 0 ? RemotePath.Display(member.Link) : null, RemotePath.FromUnixTicks(member.MTimeTicks))));
            }
        }

        return files;

        static ManifestFile Entry(byte[] path, long size, long mtime, bool inSet, string? hash, ManifestPosix posix)
        {
            bool utf8 = RemotePath.TryUtf8(path, out string text);
            return new ManifestFile(utf8 ? text : RemotePath.Display(path), size, RemotePath.FromUnixTicks(mtime), inSet, hash)
            {
                PathBase64 = utf8 ? null : Convert.ToBase64String(path),
                Posix = posix,
            };
        }
    }

    /// <summary>The letter of <c>find %y</c> for a tar type flag.</summary>
    private static string TypeLetter(char type) => type switch
    {
        '5' => "d",
        '2' => "l",
        '6' => "p",
        '3' => "c",
        '4' => "b",
        _ => "f",
    };

    private static ManifestRemote ManifestRemoteOf(RunContext run)
    {
        var remote = run.Remote!;
        var info = remote.Info!;
        return new ManifestRemote
        {
            Host = remote.Source.Host ?? string.Empty,
            Port = remote.Source.Port ?? 22,
            User = remote.Source.User ?? "custos",
            HostKeySha256 = remote.Session?.HostKeySha256 ?? string.Empty,
            HelperVersion = info.HelperVersion,
            Hostname = info.Hostname,
            Os = info.Os,
            ServerNow = info.Now,
            Since = remote.Since,
            ConfigSha256 = info.ConfigSha256,
            Roots = [.. info.Roots.Select(r => r.Path)],
            ServerFreeBytes = run.Result.Remote?.ServerFreeBytes,
            FilesChanged = remote.FilesChanged,
            FilesDeleted = remote.FilesDeleted,
        };
    }

    private void Warn(RunContext run, string message)
    {
        lock (_sync)
        {
            run.Result.Warnings++;
            _log.Write(RunLogLevel.Warning, message);
        }
    }

    /// <summary>The messages of custos-helper (a file changed while read, a root skipped): one warning, the lines in the log.</summary>
    private void WarnLines(RunContext run, string text)
    {
        var lines = text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0)
        {
            return;
        }

        lock (_sync)
        {
            run.Result.Warnings++;
            foreach (string line in lines.Take(MaxWarningLines))
            {
                _log.Write(RunLogLevel.Warning, Messages.Format(Messages.RemoteHelperSays, line));
            }

            if (lines.Length > MaxWarningLines)
            {
                _log.Write(RunLogLevel.Warning, Messages.Format(Messages.RemoteMoreLines, lines.Length - MaxWarningLines));
            }
        }
    }

    /// <summary>The tar is in: its end and the exit code of custos-helper decide how the run ends.</summary>
    private void RemoteTarDone(RunContext run, RemoteTarStream tar)
    {
        var remote = run.Remote!;
        SshExit exit;
        try
        {
            exit = tar.Remote.WaitForExit();
        }
        catch (SshFailedException ex)
        {
            throw new IOException(ex.Message, ex);
        }

        lock (_sync)
        {
            remote.TarDone = true;
            remote.TarBytes = tar.Bytes;
            run.StoredBytes += remote.StoredBytes;
            run.Result.FilesProcessed += remote.StoredFiles;
            if (run.Access is null)
            {
                run.Result.BytesWritten += tar.Bytes;
            }

            _progress.CurrentFile = null;
        }

        if (exit.Code == 1)
        {
            WarnLines(run, exit.Errors.Length > 0 ? exit.Errors : Messages.RemoteTarWarning);
        }
        else if (exit.Code != 0)
        {
            lock (_sync)
            {
                run.RemoteErrors++;
                run.Result.Errors++;
                string message = Messages.Format(Messages.RemoteHelperFailed, "tar-since", exit.Code, HelperClient.Brief(exit.Errors));
                run.Result.ErrorMessage ??= message;
                _log.Write(RunLogLevel.Error, message);
            }
        }

        _log.Write(RunLogLevel.Info, Messages.Format(Messages.RemoteTarWritten, remote.StoredFiles, remote.Stored.Count - remote.StoredFiles, Sizes.Format(tar.Bytes)));
    }

    /// <summary>A file of the tar was read whole.</summary>
    private void RemoteMemberDone(RunContext run, TarMember member, string? sha256)
    {
        lock (_sync)
        {
            var remote = run.Remote!;
            string key = member.Key;
            remote.Stored[key] = new StoredMember(member, sha256);
            if (member.IsFile)
            {
                remote.StoredFiles++;
                remote.StoredBytes += member.Size;
                _progress.FilesDone++;
                if (run.Request.LogEveryFile)
                {
                    _log.Write(RunLogLevel.Detail, Messages.Format(Messages.FileCopied, RemotePath.Display(member.AbsolutePath), Sizes.Format(member.Size)));
                }
            }
        }
    }

    /// <summary>What a run of a Linux server knows.</summary>
    private sealed class RemoteRun(TaskSource source)
    {
        public TaskSource Source { get; } = source;

        public ISshSession? Session { get; set; }

        public HelperClient? Helper { get; set; }

        public HelperInfo? Info { get; set; }

        /// <summary>Every entry of the roots, from <c>manifest</c>.</summary>
        public List<RemoteEntry> Listing { get; set; } = [];

        /// <summary>The files and links the tar is going to hold (by their ctime), by key.</summary>
        public HashSet<string> Planned { get; } = new(StringComparer.Ordinal);

        /// <summary>Unix seconds: the tar takes what changed after it; 0 = everything.</summary>
        public long Since { get; set; }

        /// <summary>What the tar holds, by key (case matters on Linux).</summary>
        public Dictionary<string, StoredMember> Stored { get; } = new(StringComparer.Ordinal);

        public long StoredFiles { get; set; }

        public long StoredBytes { get; set; }

        /// <summary>Size of the tar itself.</summary>
        public long TarBytes { get; set; }

        /// <summary>The tar was read to its end.</summary>
        public bool TarDone { get; set; }

        public long? FilesChanged { get; set; }

        public long? FilesDeleted { get; set; }
    }

    private sealed record StoredMember(TarMember Member, string? Sha256);

    /// <summary>
    /// The tar on its way into the set: unpacked from gzip, followed header by header (progress, SHA-256 of every file,
    /// what it holds) and passed on unchanged. A stream that breaks off or is not a tar fails with <see cref="IOException"/>,
    /// which stops the set.
    /// </summary>
    private sealed class RemoteTarStream : Stream, ITarSink
    {
        private readonly BackupEngine _engine;
        private readonly RunContext _run;
        private readonly GZipStream _gzip;
        private readonly TarParser _parser;
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private TarMember? _member;
        private bool _ended;

        public RemoteTarStream(BackupEngine engine, RunContext run, RemoteStream remote)
        {
            _engine = engine;
            _run = run;
            Remote = remote;
            _gzip = new GZipStream(remote, CompressionMode.Decompress, leaveOpen: true);
            _parser = new TarParser(this);
        }

        public RemoteStream Remote { get; }

        /// <summary>Bytes of the tar so far.</summary>
        public long Bytes { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => Bytes;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_ended || buffer.Length == 0)
            {
                return 0;
            }

            _run.Pause.WaitWhilePaused(_run.Cancellation);
            _run.Cancellation.ThrowIfCancellationRequested();
            int read;
            try
            {
                read = _gzip.Read(buffer);
                if (read > 0)
                {
                    _parser.Write(buffer[..read]);
                }
            }
            catch (InvalidDataException ex)
            {
                throw new IOException(Messages.Format(Messages.RemoteTarFailed, ex.Message), ex);
            }
            catch (IOException ex)
            {
                throw new IOException(Messages.Format(Messages.RemoteTarFailed, ex.Message), ex);
            }

            if (read > 0)
            {
                Bytes += read;
                _engine.SourceRead(read);
                return read;
            }

            _ended = true;
            if (!_parser.Ended)
            {
                // Cut off: custos-helper failed, or the connection broke. Its own message says more, if it has one.
                string reason = Messages.Format(Messages.RemoteTarCut, _parser.Position);
                try
                {
                    var exit = Remote.WaitForExit();
                    if (exit.Code != 0)
                    {
                        reason = Messages.Format(Messages.RemoteHelperFailed, "tar-since", exit.Code, HelperClient.Brief(exit.Errors));
                    }
                }
                catch (SshFailedException ex)
                {
                    reason = ex.Message;
                }

                throw new IOException(Messages.Format(Messages.RemoteTarFailed, reason));
            }

            Remote.CopyTo(Null);

            _engine.RemoteTarDone(_run, this);
            return 0;
        }

        public void MemberStart(TarMember member, ReadOnlySpan<byte> header)
        {
            _member = member;
            if (member.IsFile)
            {
                lock (_engine._sync)
                {
                    _engine._progress.CurrentFile = RemotePath.Display(member.AbsolutePath);
                }
            }
        }

        public void MemberData(ReadOnlySpan<byte> data) => _hash.AppendData(data);

        public void MemberEnd()
        {
            byte[] hash = _hash.GetHashAndReset();
            if (_member is { } member)
            {
                _engine.RemoteMemberDone(_run, member, member.IsFile ? SetManifest.ToHex(hash) : null);
            }

            _member = null;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _gzip.Dispose();
                Remote.Dispose();
                _hash.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
