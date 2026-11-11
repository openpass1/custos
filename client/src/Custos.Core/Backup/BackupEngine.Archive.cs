using System.Globalization;
using System.Security.Cryptography;
using Custos.Core.Archives;
using Custos.Core.Model;
using Custos.Core.Text;

namespace Custos.Core.Backup;

/// <summary>Sets written as 7z or ZIP archives (section 6.5).</summary>
/// <remarks>
/// The archive is packed once and its bytes go to every destination at the same time (<see cref="MultiTargetStream"/>).
/// Each destination gets <c>name.7z.partial</c> (or <c>name.7z.001.partial</c>, <c>.002.partial</c> … for volumes),
/// renamed when the archive is complete and, if due, verified. The set manifest is the last item of the archive.
/// </remarks>
public sealed partial class BackupEngine
{
    private const int ArchiveBufferSize = 1024 * 1024;

    private static bool IsArchive(TaskDefinition task) => task.Archive.Format != ArchiveFormats.None;

    /// <summary>The password, the writer and the access to stored archives. A missing or broken 7z.dll stops only
    /// the tasks that need it, with its reason in the log.</summary>
    private void PrepareArchive(RunContext run)
    {
        var options = run.Task.Archive;
        string? password = null;
        if (options.Encrypt)
        {
            if (string.IsNullOrEmpty(options.PasswordCredentialId))
            {
                throw new BackupException(Messages.ArchivePasswordNotSet);
            }

            password = _platform.GetArchivePassword(options.PasswordCredentialId);
            if (password.Length == 0)
            {
                throw new BackupException(Messages.ArchivePasswordNotSet);
            }
        }

        try
        {
            run.Writer = _platform.Archives.CreateWriter(options.Format);
        }
        catch (ArchiveException ex)
        {
            throw new BackupException(ex.Message, ex);
        }

        run.Access = new ArchiveAccess(_platform.Archives, password);
        bool sevenZip = options.Format == ArchiveFormats.SevenZip;
        string details = string.Empty;
        if (password is not null)
        {
            details += Messages.ArchiveEncryptedPart + (sevenZip && options.EncryptNames ? Messages.ArchiveNamesEncryptedPart : string.Empty);
        }

        if (sevenZip && options.SplitMb > 0)
        {
            details += Messages.Format(Messages.ArchiveVolumesPart, Sizes.Format(options.SplitMb * 1024L * 1024));
        }

        if (sevenZip && _platform.Archives.SevenZipVersion is { } version)
        {
            details += Messages.Format(Messages.ArchiveLibraryPart, version);
        }

        _log.Write(RunLogLevel.Info, Messages.Format(Messages.ArchiveSettings, sevenZip ? "7z" : "ZIP", Math.Clamp(options.Level, 0, 9), details));
    }

    private void WriteArchive(RunContext run)
    {
        if (run.SourceErrors > 0)
        {
            // A set missing a whole source is not a backup of the task: nothing is packed.
            return;
        }

        var task = run.Task;
        var options = task.Archive;
        bool sevenZip = options.Format == ArchiveFormats.SevenZip;
        string extension = sevenZip ? ".7z" : ".zip";
        long volumeSize = sevenZip && options.SplitMb > 0 ? options.SplitMb * 1024L * 1024 : 0;
        var startedLocal = TimeZoneInfo.ConvertTime(run.Request.StartedAt, run.Request.TimeZone).DateTime;
        run.SetName = UniqueAcrossTargets(run, SetNaming.Expand(task.Layout.NameTemplate, task.Name, startedLocal, _platform.ComputerName, run.BackupType));

        var outputs = new List<StreamTarget>();
        foreach (var target in run.ActiveTargets)
        {
            target.SetName = run.SetName;
            target.SetFolder = target.Root;
            string first = Path.Combine(target.Root, run.SetName + extension);
            try
            {
                Stream stream = volumeSize > 0
                    ? new VolumeStream(volumeSize, n => CreatePart(target, string.Concat(first, ".", n.ToString("000", CultureInfo.InvariantCulture))), n => ReopenPart(target, n))
                    : CreatePart(target, first);
                target.Output = stream;
                target.Progress.State = DestinationStates.Writing;
                outputs.Add(new StreamTarget(target, stream));
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

        var writeOptions = new ArchiveWriteOptions
        {
            Level = Math.Clamp(options.Level, 0, 9),
            Password = run.Access!.Password,
            EncryptNames = sevenZip && options.EncryptNames,
        };
        using (var output = new MultiTargetStream(outputs, (failed, ex) =>
        {
            var target = (Target)failed.Owner;
            CloseOutput(target);
            FailTarget(run, target, Messages.Format(Messages.DestinationWriteFailed, target.Label, ex.Message));
        }))
        {
            try
            {
                run.Writer!.Write(ArchiveItems(run), output, writeOptions, run.Cancellation);
                output.Flush();
                run.ArchiveBytes = output.Length;
            }
            catch (AllTargetsFailedException)
            {
                // Every destination failed and says why.
                return;
            }
            catch (ArchiveException ex)
            {
                throw new BackupException(ex.Message, ex);
            }
        }

        foreach (var target in run.ActiveTargets)
        {
            try
            {
                target.Output?.Dispose();
                target.Output = null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                FailTarget(run, target, Messages.Format(Messages.DestinationWriteFailed, target.Label, ex.Message));
            }
        }

        if (run.Header is null)
        {
            // The writer never got to the manifest: not a set.
            throw new BackupException(Messages.CopyIncomplete);
        }

        FinishArchives(run);
    }

    /// <summary>Verifies (if due) and renames the archive in every destination that is still fine.</summary>
    private void FinishArchives(RunContext run)
    {
        Phase(run.VerifyNew ? BackupPhases.Verify : BackupPhases.Copy);
        foreach (var target in run.ActiveTargets)
        {
            try
            {
                if (run.VerifyNew)
                {
                    VerifyNewArchive(run, target);
                }

                target.Session.Run(() =>
                {
                    // The last volume first: until the first one has its name, the others are no archive to anybody.
                    for (int i = target.Temps.Count - 1; i >= 0; i--)
                    {
                        File.Move(target.Temps[i], target.Parts[i]);
                        target.Renamed.Add(target.Parts[i]);
                    }
                });
                target.Completed = true;
                target.Files = run.Copied.Count + run.Dumps.Count(d => d.InSet) + (int)(run.Remote?.StoredFiles ?? 0);
                target.Bytes = run.ArchiveBytes;
                target.NewSet = new StoredSet(target.Parts[0], run.Header!)
                {
                    Storage = SetStorage.Archive,
                    Format = run.Task.Archive.Format,
                    Parts = [.. target.Parts],
                    Bytes = run.ArchiveBytes,
                };
                target.Progress.State = DestinationStates.Done;
                string volumes = target.Parts.Count > 1 ? Messages.Format(Messages.ArchiveVolumeCount, target.Parts.Count) : string.Empty;
                _log.Write(RunLogLevel.Info, Messages.Format(Messages.ArchiveWritten, Path.GetFileName(target.Parts[0]), target.Label, target.Files, Sizes.Format(run.ArchiveBytes), volumes));
            }
            catch (BackupException ex)
            {
                FailTarget(run, target, ex.Message);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                FailTarget(run, target, Messages.Format(Messages.DestinationWriteFailed, target.Label, ex.Message));
            }
        }

        run.Result.BytesWritten = run.ArchiveBytes;
        run.SetsFinished = run.Targets.All(t => t.Completed);
        run.Verified = run.VerifyNew && run.SetsFinished;
    }

    /// <summary>A set name free in every destination: "name", or "name (2)" … if any of them has it already.</summary>
    private string UniqueAcrossTargets(RunContext run, string name)
    {
        string candidate = name;
        for (int i = 2; IsTakenAnywhere(run, candidate); i++)
        {
            candidate = SetNaming.Numbered(name, i);
        }

        return candidate;
    }

    private bool IsTakenAnywhere(RunContext run, string name)
    {
        bool taken = false;
        foreach (var target in run.ActiveTargets)
        {
            try
            {
                taken |= target.Session.Run(() => SetNaming.IsTaken(target.Root, name));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                FailTarget(run, target, Messages.Format(Messages.DestinationWriteFailed, target.Label, ex.Message));
            }
        }

        return taken;
    }

    private FileStream CreatePart(Target target, string final)
    {
        string temp = final + SetCatalog.PartialSuffix;
        var stream = Lowered(target.Session.Run(() => new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, ArchiveBufferSize)));
        target.Parts.Add(final);
        target.Temps.Add(temp);
        return stream;
    }

    private FileStream ReopenPart(Target target, int number) =>
        Lowered(target.Session.Run(() => new FileStream(target.Temps[number - 1], FileMode.Open, FileAccess.Write, FileShare.None, ArchiveBufferSize)));

    private static void CloseOutput(Target target)
    {
        try
        {
            target.Output?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The destination failed already; its partial files are removed at the end.
        }

        target.Output = null;
    }

    /// <summary>Removes what an unfinished archive left in a destination.</summary>
    private void RemoveArchiveLeftovers(Target target)
    {
        CloseOutput(target);
        if (target.Temps.Count == 0)
        {
            return;
        }

        try
        {
            target.Session.Run(() =>
            {
                foreach (string file in target.Temps.Concat(target.Renamed))
                {
                    if (File.Exists(file))
                    {
                        File.SetAttributes(file, FileAttributes.Normal);
                        File.Delete(file);
                    }
                }
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Write(RunLogLevel.Warning, Messages.Format(Messages.PartialFileNotRemoved, target.Temps[0], ex.Message));
        }
    }

    /// <summary>
    /// Database dumps, folders (full copies), files, and the manifest last. ZIP packs them in this order. 7-Zip packs
    /// the files sorted by path, so the manifest («custos-set.json») comes before every path that sorts after it, the
    /// dumps («db/…») included; see <see cref="ManifestStream"/>.
    /// </summary>
    private List<ArchiveItem> ArchiveItems(RunContext run)
    {
        var items = DumpItems(run);
        if (run.BackupType == BackupTypes.Full && !run.Task.Layout.DeleteEmptyDirs)
        {
            foreach (var context in run.Sources.Where(s => s.Scan is { Found: true, IsFile: false }))
            {
                foreach (string folder in context.Scan!.Folders)
                {
                    items.Add(new ArchiveItem { Path = Path.Combine(context.Target, folder), IsFolder = true });
                }
            }
        }

        if (run.Remote is not null)
        {
            items.Add(RemoteTarItem(run));
        }

        foreach (var (context, file) in run.ToCopy)
        {
            items.Add(new ArchiveItem
            {
                Path = StoredPath(context, file),
                Size = file.Size,
                ModifiedUtc = file.LastWriteUtc,
                CreatedUtc = file.CreationUtc,
                Attributes = file.Attributes & ZipWriter.KeptAttributes,
                Open = () => OpenSource(run, context, file),
            });
        }

        items.Add(new ArchiveItem
        {
            Path = SetManifest.FileName,
            Separate = true,
            ModifiedUtc = DateTime.UtcNow,
            Open = () => ManifestStream(run),
        });
        return items;
    }

    /// <summary>
    /// The manifest, made when the writer gets to it. In ZIP every file has been read by then. In 7z the files whose
    /// path sorts after «custos-set.json» are still to come: the manifest lists them as in the set without a SHA-256
    /// (the CRC of 7z guards them), one that turns out to be unreadable stays out of the archive (restore takes it
    /// from an older set), and one that breaks off in the middle stops the archive (see <see cref="SourceReader"/>).
    /// </summary>
    private MemoryStream ManifestStream(RunContext run)
    {
        lock (_sync)
        {
            var pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long pendingBytes = 0;
            foreach (var (_, file) in run.ToCopy)
            {
                if (!run.Copied.ContainsKey(file.OriginalPath) && !run.Failed.Contains(file.OriginalPath) && !run.Vanished.Contains(file.OriginalPath))
                {
                    pending.Add(file.OriginalPath);
                    pendingBytes += file.Size;
                }
            }

            if (run.Remote is not null)
            {
                var files = RemoteManifestFiles(run, out int setFiles, out long setBytes);
                run.Header = BuildHeader(run, run.SetName!, DateTimeOffset.UtcNow, SetStatus(run));
                run.Header.SetFileCount = setFiles;
                run.Header.SetBytes = setBytes;
                var remoteStream = new MemoryStream();
                SetManifest.Write(remoteStream, run.Header, files);
                remoteStream.Position = 0;
                return remoteStream;
            }

            run.Header = BuildHeader(run, run.SetName!, DateTimeOffset.UtcNow, SetStatus(run), pending.Count, pendingBytes);
            var stream = new MemoryStream();
            SetManifest.Write(stream, run.Header, ManifestFiles(run, pending));
            stream.Position = 0;
            return stream;
        }
    }

    /// <summary>Opens a source file for the archive writer. Null leaves it out: gone, or unreadable after the retries.</summary>
    private SourceReader? OpenSource(RunContext run, SourceContext context, ScannedFile file)
    {
        run.Pause.WaitWhilePaused(run.Cancellation);
        run.Cancellation.ThrowIfCancellationRequested();
        lock (_sync)
        {
            _progress.CurrentFile = file.OriginalPath;
        }

        Report(force: false);
        int attempts = 1 + Math.Clamp(run.Task.Copy.ReadRetries, 0, 5);
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return new SourceReader(this, run, context, file, OpenSourceFile(context, file));
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                lock (_sync)
                {
                    run.Vanished.Add(file.OriginalPath);
                    _progress.FilesDone++;
                }

                _log.Write(RunLogLevel.Detail, Messages.Format(Messages.FileVanished, file.OriginalPath));
                return null;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < attempts)
            {
                _log.Write(RunLogLevel.Detail, Messages.Format(Messages.FileRetry, file.OriginalPath, ex.Message));
                Delay(run);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                SourceFailed(run, file, ex.Message);
                return null;
            }
        }
    }

    private FileStream OpenSourceFile(SourceContext context, ScannedFile file) => Lowered(context.Session.Run(() => new FileStream(
        file.ReadPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferSize, FileOptions.SequentialScan)));

    /// <summary>
    /// Reads on from <paramref name="position"/>. On a read error the file is opened again and read on from the same
    /// place, as many times as the task allows. Returns -1 when it gives up: the item ends there, the file counts as
    /// not copied (the manifest says so, and the next run tries it again).
    /// </summary>
    private int ReadSourceWithRetries(RunContext run, SourceContext context, ScannedFile file, ref FileStream? stream, long position, Span<byte> buffer)
    {
        int attempts = 1 + Math.Clamp(run.Task.Copy.ReadRetries, 0, 5);
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                if (stream is null)
                {
                    stream = OpenSourceFile(context, file);
                    stream.Seek(position, SeekOrigin.Begin);
                }

                return stream.Read(buffer);
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < attempts)
            {
                stream?.Dispose();
                stream = null;
                _log.Write(RunLogLevel.Detail, Messages.Format(Messages.FileRetry, file.OriginalPath, ex.Message));
                Delay(run);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                stream?.Dispose();
                stream = null;
                SourceFailed(run, file, Messages.Format(Messages.FileCutInArchive, ex.Message));
                return -1;
            }
        }
    }

    private void SourceFailed(RunContext run, ScannedFile file, string reason)
    {
        lock (_sync)
        {
            run.Result.Warnings++;
            run.FailedFiles++;
            run.Failed.Add(file.OriginalPath);
            _progress.FilesDone++;
            _log.Write(RunLogLevel.Warning, Messages.Format(Messages.FileFailed, file.OriginalPath, reason));
        }
    }

    private void SourceRead(int count)
    {
        lock (_sync)
        {
            _progress.BytesDone += count;
            _speed.Add(count);
        }

        Report(force: false);
    }

    private void SourceDone(RunContext run, ScannedFile file, string hash, long bytes)
    {
        lock (_sync)
        {
            run.Copied[file.OriginalPath] = hash;
            run.Result.FilesProcessed++;
            run.StoredBytes += bytes;
            _progress.FilesDone++;
            if (run.Request.LogEveryFile)
            {
                _log.Write(RunLogLevel.Detail, Messages.Format(Messages.FileCopied, file.OriginalPath, Sizes.Format(bytes)));
            }
        }
    }

    /// <summary>
    /// A source file as the archive writer reads it: SHA-256 on the way, read retries, progress, pause. The writer
    /// may read it from any thread (7-Zip reads on its own threads), but never from two at once.
    /// </summary>
    private sealed class SourceReader(BackupEngine engine, RunContext run, SourceContext context, ScannedFile file, FileStream stream) : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private FileStream? _stream = stream;
        private long _position;
        private bool _ended;
        private bool _failed;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_ended || buffer.Length == 0)
            {
                return 0;
            }

            run.Pause.WaitWhilePaused(run.Cancellation);
            run.Cancellation.ThrowIfCancellationRequested();
            int read = engine.ReadSourceWithRetries(run, context, file, ref _stream, _position, buffer);
            if (read < 0)
            {
                _ended = true;
                _failed = true;
                if (ListedAlready())
                {
                    // The manifest is packed and says this file is whole: a cut file would be restored cut.
                    throw new ArchiveException(Messages.Format(Messages.ArchiveListedFileCut, file.OriginalPath));
                }

                return 0;
            }

            if (read == 0)
            {
                End();
                return 0;
            }

            _hash.AppendData(buffer[..read]);
            _position += read;
            engine.SourceRead(read);
            return read;
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
                // A writer that stops at the declared size without asking for more has still taken everything.
                if (!_ended && !_failed && !run.Cancellation.IsCancellationRequested && _position >= file.Size)
                {
                    End();
                }

                _stream?.Dispose();
                _stream = null;
                _hash.Dispose();
            }

            base.Dispose(disposing);
        }

        private void End()
        {
            _ended = true;
            engine.SourceDone(run, file, SetManifest.ToHex(_hash.GetHashAndReset()), _position);
        }

        private bool ListedAlready()
        {
            lock (engine._sync)
            {
                return run.Header is not null;
            }
        }
    }
}
