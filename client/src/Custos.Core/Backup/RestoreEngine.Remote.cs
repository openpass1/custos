using System.Security.Cryptography;
using System.Text;
using Custos.Core.Archives;
using Custos.Core.Databases;
using Custos.Core.Model;
using Custos.Core.Remote;
using Custos.Core.Text;

namespace Custos.Core.Backup;

/// <summary>
/// Restore of a set of a Linux server (CUSTOS-remote-pull.md 4.5), only into a folder: a tar per root with the newest
/// version of every entry the chosen set lists, the dumps in «db» and README.txt with the commands for the server.
/// The tars are made by copying the blocks of the sets' files.tar as they are, newest set first; nothing is unpacked
/// on Windows (4.6).
/// </summary>
public sealed partial class RestoreEngine
{
    /// <summary>Missing files named one by one in the log; the rest only counted.</summary>
    private const int MissingListed = 20;

    /// <summary>Whether a set comes from a Linux server and is restored by <see cref="RunRemote"/>.</summary>
    public static bool IsRemote(SetManifestHeader header)
    {
        ArgumentNullException.ThrowIfNull(header);
        return header.SourceKind == SourceKinds.Ssh;
    }

    /// <summary>The name of the tar of a root: "/var/www" → "var-www.tar".</summary>
    public static string TarName(string root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var name = new StringBuilder();
        foreach (char c in root.Trim('/'))
        {
            name.Append(c == '/' ? '-' : char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-' ? c : '_');
        }

        return (name.Length > 0 ? name.ToString() : "root") + ".tar";
    }

    private RestoreResult RunRemote(RestoreRequest request, List<StoredSet> chain, RestoreResult result, CancellationToken cancellationToken)
    {
        if (request.TargetFolder is not { } folder)
        {
            result.Status = RunStatuses.Error;
            result.ErrorMessage = Messages.RestoreRemoteNeedsFolder;
            _log.Write(RunLogLevel.Error, result.ErrorMessage);
            return result;
        }

        var header = request.Set.Header;
        var listing = SetCatalog.ReadManifest(request.Set, request.Access).Files;
        var state = new RemoteRestore();
        foreach (var file in listing)
        {
            state.Wanted[RemotePath.Key(file)] = file.Posix?.Type == "d" ? -1 : file.Size;
        }

        PlanTars(request, folder, header.Remote?.Roots ?? [], state, result);
        var dumps = PlanDumps(request, result);
        _progress.FilesTotal = state.Wanted.Values.Count(s => s >= 0) + dumps.Count;
        _progress.BytesTotal = state.Wanted.Values.Where(s => s > 0).Sum() + dumps.Sum(d => d.File.Size);
        Report(force: true);

        try
        {
            for (int i = chain.Count - 1; i >= 0 && state.Wanted.Count > 0; i--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var set = chain[i];
                var files = ReferenceEquals(set, request.Set) ? listing
                    : SetCatalog.ReadManifest(set, request.Access).Files;
                var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var file in files.Where(f => f.InSet && f.Sha256 is not null))
                {
                    string key = RemotePath.Key(file);
                    if (state.Wanted.ContainsKey(key))
                    {
                        hashes[key] = file.Sha256!;
                    }
                }

                ReadSetTar(request, set, state, hashes, result, cancellationToken);
                if (state.WriteError is not null)
                {
                    break;
                }
            }

            if (state.WriteError is null)
            {
                ReportMissing(state, result);
                FinishTars(request, state, result);
            }
        }
        finally
        {
            foreach (var output in state.Outputs.Where(o => o.Stream is not null))
            {
                output.Abandon(request.WriteSession);
            }
        }

        if (state.WriteError is { } error)
        {
            Failed(result, error);
        }
        else if (dumps.Count > 0)
        {
            var todo = dumps.Where(p => Wanted(request, p, result)).ToList();
            if (request.Set.Storage == SetStorage.Archive)
            {
                RestoreFromArchive(request, request.Set, todo, result, cancellationToken);
            }
            else
            {
                foreach (var planned in todo)
                {
                    RestoreFromFolder(request, request.Set, planned, result, cancellationToken);
                }
            }
        }

        _progress.CurrentFile = null;
        Report(force: true);
        var lines = dumps.Count > 0 ? CheckDumps(request, dumps, result) : [];
        if (state.WriteError is null)
        {
            WriteRemoteReadme(request, folder, state, lines);
        }

        result.Status = result.FilesFailed > 0 ? RunStatuses.Error : result.Warnings > 0 ? RunStatuses.Warning : RunStatuses.Success;
        _log.Write(result.FilesFailed > 0 ? RunLogLevel.Error : RunLogLevel.Info, Messages.Format(Messages.RestoreFinished,
            result.FilesRestored, Sizes.Format(result.Bytes), result.FilesSkipped, result.FilesFailed));
        return result;
    }

    /// <summary>A tar per root, opened under a temporary name. A tar that is there already is left alone with
    /// <see cref="RestoreExisting.Skip"/> (its entries are skipped) and replaced otherwise.</summary>
    private void PlanTars(RestoreRequest request, string folder, IReadOnlyList<string> roots, RemoteRestore state, RestoreResult result)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string root in roots.Where(r => r.StartsWith('/') && r.Length > 1).Distinct(StringComparer.Ordinal).OrderByDescending(r => r.Length))
        {
            state.Outputs.Add(new TarOutput(root, Path.Combine(folder, Unique(TarName(root)))));
        }

        // Entries outside every root (none, unless the set was made in an odd way) go into one more tar.
        if (state.Wanted.Keys.Any(k => state.OutputFor(RemotePath.FromKey(k)) is null))
        {
            state.Outputs.Add(new TarOutput(string.Empty, Path.Combine(folder, Unique("other.tar"))));
        }

        foreach (var output in state.Outputs)
        {
            bool exists = request.WriteSession.Run(() => File.Exists(output.Path));
            if (exists && request.Existing == RestoreExisting.Skip)
            {
                output.Skipped = true;
                _log.Write(RunLogLevel.Info, Messages.Format(Messages.RestoreSkipped, output.Path));
            }
        }

        foreach (var key in state.Wanted.Keys.ToList())
        {
            if (state.OutputFor(RemotePath.FromKey(key)) is { Skipped: true })
            {
                if (state.Wanted[key] >= 0)
                {
                    result.FilesSkipped++;
                }

                state.Wanted.Remove(key);
            }
        }

        foreach (var output in state.Outputs.Where(o => !o.Skipped))
        {
            output.Open(request.WriteSession);
        }

        string Unique(string name)
        {
            string unique = name;
            for (int n = 2; !names.Add(unique); n++)
            {
                unique = string.Concat(Path.GetFileNameWithoutExtension(name), "-", n.ToString(System.Globalization.CultureInfo.InvariantCulture), ".tar");
            }

            return unique;
        }
    }

    /// <summary>Streams files.tar of a set through the router: what is still wanted is copied into the tars.</summary>
    private void ReadSetTar(RestoreRequest request, StoredSet set, RemoteRestore state, Dictionary<string, string> hashes, RestoreResult result, CancellationToken cancellationToken)
    {
        _progress.CurrentFile = set.Header.SetName;
        Report(force: true);
        using var router = new TarRouterStream(this, state, hashes, result);
        string? problem = null;
        try
        {
            if (set.Storage == SetStorage.Archive)
            {
                using var reader = request.Access.Open(set);
                var entry = reader.Entries.FirstOrDefault(e => !e.IsFolder && string.Equals(ArchivePaths.ToZip(e.Path), BackupEngine.RemoteTarName, StringComparison.Ordinal));
                if (entry is null)
                {
                    problem = Messages.VerifyFileMissing;
                }
                else
                {
                    reader.Extract([entry.Index], _ => router, (_, outcome) =>
                    {
                        if (outcome != ExtractOutcome.Ok)
                        {
                            problem = BackupEngine.OutcomeText(outcome);
                        }
                    }, cancellationToken);
                }
            }
            else
            {
                var store = request.Access.Store;
                using var input = store.OpenRead(store.Combine(set.Location, BackupEngine.RemoteTarName));
                var buffer = new byte[BufferSize];
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0 && state.WriteError is null)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    router.Write(buffer, 0, read);
                }
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            problem = Messages.VerifyFileMissing;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problem = ex.Message;
        }

        router.EndOfTar();
        problem ??= state.WriteError is null ? router.Problem : null;
        if (problem is not null)
        {
            Failed(result, Messages.Format(Messages.RestoreRemoteTarProblem, set.Header.SetName, problem));
        }
    }

    /// <summary>Files (not folders: tar makes them) that no set of the chain holds.</summary>
    private void ReportMissing(RemoteRestore state, RestoreResult result)
    {
        var missing = state.Wanted.Where(w => w.Value >= 0).Select(w => w.Key).ToList();
        foreach (string key in missing.Take(MissingListed))
        {
            Failed(result, Messages.Format(Messages.RestoreNotInChain, RemotePath.Display(key)));
        }

        if (missing.Count > MissingListed)
        {
            result.FilesFailed += missing.Count - MissingListed;
            _log.Write(RunLogLevel.Warning, Messages.Format(Messages.RestoreRemoteMoreMissing, missing.Count - MissingListed));
        }

        _progress.FilesDone += missing.Count;
    }

    /// <summary>Ends every tar with its two zero blocks and gives it its name.</summary>
    private void FinishTars(RestoreRequest request, RemoteRestore state, RestoreResult result)
    {
        foreach (var output in state.Outputs.Where(o => o.Stream is not null))
        {
            try
            {
                output.Close(request.WriteSession);
                result.Bytes += output.Length;
                _log.Write(RunLogLevel.Info, Messages.Format(Messages.RestoreRemoteTarWritten, output.Path, output.Members, Sizes.Format(output.Length)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                output.Abandon(request.WriteSession);
                Failed(result, Messages.Format(Messages.RestoreFileFailed, output.Path, ex.Message));
            }
        }
    }

    private void WriteRemoteReadme(RestoreRequest request, string folder, RemoteRestore state, IReadOnlyList<(ManifestDatabase Database, string Path, bool Complete)> dumps)
    {
        string readme = Path.Combine(folder, "README.txt");
        try
        {
            var tars = state.Outputs.Where(o => o.Written || o.Skipped).Select(o => (o.Root, Path.GetFileName(o.Path))).ToList();
            string text = RemoteReadme.Build(request.Set.Header, folder, tars, dumps);
            request.WriteSession.Run(() =>
            {
                Directory.CreateDirectory(folder);
                File.WriteAllText(readme, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            });
            _log.Write(RunLogLevel.Info, Messages.Format(Messages.RestoreRemoteReadme, readme));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Write(RunLogLevel.Warning, Messages.Format(Messages.RestoreFileFailed, readme, ex.Message));
        }
    }

    /// <summary>What a restore of a Linux server still looks for, and where it goes.</summary>
    private sealed class RemoteRestore
    {
        /// <summary>Entries of the date not taken yet, by key: the size, -1 for a folder.</summary>
        public Dictionary<string, long> Wanted { get; } = new(StringComparer.Ordinal);

        /// <summary>The tars, longest root first; the one for entries outside the roots (root "") last.</summary>
        public List<TarOutput> Outputs { get; } = [];

        /// <summary>A tar could not be written: the restore stops.</summary>
        public string? WriteError { get; set; }

        public TarOutput? OutputFor(byte[] path)
        {
            foreach (var output in Outputs)
            {
                if (output.Holds(path))
                {
                    return output;
                }
            }

            return null;
        }
    }

    /// <summary>A tar being written under a temporary name.</summary>
    private sealed class TarOutput(string root, string path)
    {
        private readonly byte[] _root = Encoding.UTF8.GetBytes(root);

        public string Root { get; } = root;

        public string Path { get; } = path;

        public string Temp => Path + BackupEngine.TempSuffix;

        public bool Skipped { get; set; }

        /// <summary>Got its name.</summary>
        public bool Written { get; private set; }

        public FileStream? Stream { get; private set; }

        public long Members { get; set; }

        public long Length { get; private set; }

        /// <summary>Whether a path belongs into this tar (the tar without a root takes everything).</summary>
        public bool Holds(ReadOnlySpan<byte> path) =>
            _root.Length == 0
            || (path.StartsWith(_root) && (path.Length == _root.Length || path[_root.Length] == (byte)'/'));

        public void Open(INetworkSession session) => Stream = session.Run(() =>
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            return new FileStream(Temp, FileMode.Create, FileAccess.ReadWrite, FileShare.None, BufferSize);
        });

        public void Close(INetworkSession session)
        {
            var stream = Stream!;
            stream.Write(new byte[2 * TarParser.BlockSize]);
            Length = stream.Length;
            stream.Dispose();
            Stream = null;
            session.Run(() =>
            {
                if (File.Exists(Path))
                {
                    File.SetAttributes(Path, FileAttributes.Normal);
                }

                File.Move(Temp, Path, overwrite: true);
            });
            Written = true;
        }

        public void Abandon(INetworkSession session)
        {
            try
            {
                Stream?.Dispose();
                Stream = null;
                session.Run(() => File.Delete(Temp));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The temporary name never looks like a restored tar.
            }
        }
    }

    /// <summary>
    /// Follows files.tar of one set and copies every member still wanted (header blocks, data, padding) into the tar
    /// of its root. A file whose SHA-256 differs from the manifest is cut out again and counts as failed.
    /// </summary>
    private sealed class TarRouterStream(RestoreEngine engine, RemoteRestore state, Dictionary<string, string> hashes, RestoreResult result) : TarFeedStream
    {
        private static readonly byte[] Zeros = new byte[TarParser.BlockSize];

        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private TarMember? _member;
        private TarOutput? _output;
        private long _start;
        private string? _expected;
        private bool _counted;

        public override void MemberStart(TarMember member, ReadOnlySpan<byte> header)
        {
            ArgumentNullException.ThrowIfNull(member);
            _output = null;
            if (state.WriteError is not null)
            {
                return;
            }

            string key = member.Key;
            if (!state.Wanted.Remove(key, out long size))
            {
                // Not of this date, or a newer set gave it already.
                return;
            }

            var output = state.OutputFor(member.AbsolutePath);
            if (output?.Stream is not { } stream)
            {
                return;
            }

            _member = member;
            _output = output;
            _counted = size >= 0;
            _expected = member.IsFile ? hashes.GetValueOrDefault(key) : null;
            if (_counted)
            {
                engine._progress.CurrentFile = RemotePath.Display(member.AbsolutePath);
                engine.Report(force: false);
            }

            try
            {
                _start = stream.Position;
                stream.Write(header);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Stop(ex);
            }
        }

        public override void MemberData(ReadOnlySpan<byte> data)
        {
            if (_output?.Stream is not { } stream)
            {
                return;
            }

            if (_expected is not null)
            {
                _hash.AppendData(data);
            }

            try
            {
                stream.Write(data);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Stop(ex);
                return;
            }

            engine._progress.BytesDone += data.Length;
            engine.Report(force: false);
        }

        public override void MemberEnd()
        {
            if (_output?.Stream is not { } stream || _member is not { } member)
            {
                return;
            }

            int padding = (int)((TarParser.BlockSize - (member.Size % TarParser.BlockSize)) % TarParser.BlockSize);
            Guard(() => stream.Write(Zeros, 0, padding));
            if (_output is null)
            {
                return;
            }

            string display = RemotePath.Display(member.AbsolutePath);
            if (_expected is not null && !string.Equals(SetManifest.ToHex(_hash.GetHashAndReset()), _expected, StringComparison.OrdinalIgnoreCase))
            {
                Cut();
                engine.Failed(result, Messages.Format(Messages.RestoreFileFailed, display, Messages.VerifyContentDiffers));
            }
            else
            {
                _output.Members++;
                if (_counted)
                {
                    result.FilesRestored++;
                    engine._log.Write(RunLogLevel.Detail, Messages.Format(Messages.RestoreFileDone, display, Sizes.Format(member.Size)));
                }
            }

            if (_counted)
            {
                engine._progress.FilesDone++;
            }

            _output = null;
            _member = null;
        }

        /// <summary>The tar of the set ended or broke off: a member in the middle is cut out again.</summary>
        public void EndOfTar()
        {
            if (_output is null || _member is not { } member)
            {
                return;
            }

            Cut();
            if (_counted)
            {
                engine._progress.FilesDone++;
                engine.Failed(result, Messages.Format(Messages.RestoreFileFailed, RemotePath.Display(member.AbsolutePath), Messages.RemoteRestoreCut));
            }

            _output = null;
            _member = null;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _hash.Dispose();
            }

            base.Dispose(disposing);
        }

        private void Cut()
        {
            _hash.GetHashAndReset();
            if (_output?.Stream is { } stream)
            {
                Guard(() =>
                {
                    stream.SetLength(_start);
                    stream.Position = _start;
                });
            }
        }

        private void Guard(Action write)
        {
            try
            {
                write();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Stop(ex);
            }
        }

        private void Stop(Exception ex)
        {
            state.WriteError ??= Messages.Format(Messages.RestoreFileFailed, _output?.Path ?? string.Empty, ex.Message);
            _output = null;
            _member = null;
        }
    }
}
