using System.Security.Cryptography;
using Custos.Core.Archives;
using Custos.Core.Model;

namespace Custos.Core.Backup;

/// <summary>
/// Verification (section 6.5): the new copy every N-th run, before it gets its final name; the stored copies every N
/// days, after a successful run. Archives are unpacked into nowhere (7-Zip and SharpZipLib check the CRC or the AES
/// authentication code) and every file is compared with the SHA-256 of the manifest; plain copies are read back and
/// compared with the manifest. Nothing is written to disk.
/// </summary>
public sealed partial class BackupEngine
{
    private static bool VerifyDue(RunContext run) =>
        run.Task.Verify.EveryRuns > 0 && run.State.RunsSinceVerify + 1 >= run.Task.Verify.EveryRuns;

    /// <summary>Reads back every file of this run in a plain set and compares it with what was copied.</summary>
    /// <exception cref="BackupException">A file differs or cannot be read.</exception>
    private void VerifyNewFolder(RunContext run, Target target)
    {
        foreach (var (context, file) in run.ToCopy)
        {
            run.Cancellation.ThrowIfCancellationRequested();
            if (!run.Copied.TryGetValue(file.OriginalPath, out string? expected))
            {
                continue;
            }

            string stored = StoredPath(context, file);
            if (run.BackupType == BackupTypes.Mirror && !target.Needed.Contains(stored))
            {
                // This mirror had the file already; the stored check looks at it.
                continue;
            }

            string? problem = CheckFile(run, target.Session, Path.Combine(target.WriteFolder, stored), expected);
            if (problem is not null)
            {
                throw new BackupException(Messages.Format(Messages.VerifyNewFailed, target.Label, Messages.Format(Messages.VerifyFileProblem, stored, problem)));
            }
        }

        _log.Write(RunLogLevel.Info, Messages.Format(Messages.VerifyNewOk, target.Label));
    }

    /// <summary>Unpacks the new archive (still under its partial name) and compares it with what was packed.</summary>
    /// <exception cref="BackupException">The archive is damaged or differs.</exception>
    private void VerifyNewArchive(RunContext run, Target target)
    {
        var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (context, file) in run.ToCopy)
        {
            if (run.Copied.TryGetValue(file.OriginalPath, out string? hash))
            {
                expected[ArchivePaths.ToWindows(StoredPath(context, file))] = hash;
            }
        }

        string? problem = CheckArchive(run, run.Access!.WithSession(target.Session), run.Task.Archive.Format, target.Temps, expected);
        if (problem is not null)
        {
            throw new BackupException(Messages.Format(Messages.VerifyNewFailed, target.Label, problem));
        }

        _log.Write(RunLogLevel.Info, Messages.Format(Messages.VerifyNewOk, target.Label));
    }

    /// <summary>Verifies every set of the task in the destinations, if the time has come.</summary>
    private void VerifyStored(RunContext run)
    {
        int days = run.Task.Verify.StoredEveryDays;
        if (days <= 0 || !run.SetsFinished)
        {
            return;
        }

        var now = run.Request.StartedAt;
        // An hour of slack: a daily run at the same time must not miss its turn by a few seconds.
        if (run.State.StoredCheckedAt is { } last && now - last < TimeSpan.FromDays(days) - TimeSpan.FromHours(1))
        {
            return;
        }

        Phase(BackupPhases.Verify);
        _log.Write(RunLogLevel.Info, Messages.StoredVerifyStarted);
        int checkedSets = 0;
        int damaged = 0;
        foreach (var target in run.Targets.Where(t => t.Completed))
        {
            foreach (var set in target.Kept)
            {
                run.Cancellation.ThrowIfCancellationRequested();
                if (run.Verified && ReferenceEquals(set, target.NewSet))
                {
                    continue;
                }

                lock (_sync)
                {
                    _progress.CurrentFile = set.Location;
                }

                Report(force: true);
                string? problem = CheckSet(run, target, set);
                checkedSets++;
                if (problem is null)
                {
                    _log.Write(RunLogLevel.Detail, Messages.Format(Messages.StoredSetOk, set.Header.SetName, target.Label));
                    continue;
                }

                damaged++;
                string message = Messages.Format(Messages.StoredSetDamaged, set.Header.SetName, target.Label, problem);
                run.Result.Errors++;
                run.Result.ErrorMessage ??= message;
                _log.Write(RunLogLevel.Error, message);
            }
        }

        lock (_sync)
        {
            _progress.CurrentFile = null;
        }

        _store.SetStoredChecked(run.Task.Id, now);
        _log.Write(damaged == 0 ? RunLogLevel.Info : RunLogLevel.Error, Messages.Format(Messages.StoredVerifyDone, checkedSets, damaged));
    }

    /// <summary>Null if the set is whole, or what is wrong with it.</summary>
    private static string? CheckSet(RunContext run, Target target, StoredSet set)
    {
        var access = run.Access?.WithSession(target.Session);
        List<ManifestFile> files;
        try
        {
            files = target.Session.Run(() => SetCatalog.ReadManifest(set, access).Files);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArchiveException)
        {
            return ex.Message;
        }

        if (set.Storage == SetStorage.Archive)
        {
            var expected = files.Where(f => f.InSet && f.Sha256 is not null)
                .ToDictionary(f => ArchivePaths.ToWindows(f.Path), f => f.Sha256!, StringComparer.OrdinalIgnoreCase);
            return CheckArchive(run, access!, set.Format ?? ArchiveFormats.SevenZip, set.Parts, expected);
        }

        foreach (var file in files.Where(f => f.InSet && f.Sha256 is not null))
        {
            run.Cancellation.ThrowIfCancellationRequested();
            if (CheckFile(run, target.Session, Path.Combine(set.Location, file.Path), file.Sha256!) is { } problem)
            {
                return Messages.Format(Messages.VerifyFileProblem, file.Path, problem);
            }
        }

        return null;
    }

    /// <summary>Null if the file has this SHA-256, or what is wrong with it.</summary>
    private static string? CheckFile(RunContext run, INetworkSession session, string path, string expected)
    {
        try
        {
            using var stream = session.Run(() => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan));
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[BufferSize];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                run.Cancellation.ThrowIfCancellationRequested();
                hash.AppendData(buffer, 0, read);
            }

            return string.Equals(SetManifest.ToHex(hash.GetHashAndReset()), expected, StringComparison.OrdinalIgnoreCase) ? null : Messages.VerifyContentDiffers;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return Messages.VerifyFileMissing;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// Unpacks every item of an archive into nowhere. Null if all is well: every item passes its own check, every
    /// expected file is there with its SHA-256. Otherwise the first problem, in words.
    /// </summary>
    private static string? CheckArchive(RunContext run, ArchiveAccess access, string format, IReadOnlyList<string> parts, IReadOnlyDictionary<string, string> expected)
    {
        try
        {
            using var reader = access.Open(format, parts);
            var sinks = new Dictionary<int, HashingStream>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string? problem = null;
            try
            {
                reader.Extract(
                    [.. reader.Entries.Where(e => !e.IsFolder).Select(e => e.Index)],
                    entry => sinks[entry.Index] = new HashingStream(),
                    (entry, outcome) =>
                    {
                        string? hash = sinks.Remove(entry.Index, out var sink) ? sink.Finish() : null;
                        if (outcome != ExtractOutcome.Ok)
                        {
                            problem ??= Messages.Format(Messages.VerifyFileProblem, entry.Path, OutcomeText(outcome));
                            return;
                        }

                        if (expected.TryGetValue(entry.Path, out string? want))
                        {
                            seen.Add(entry.Path);
                            if (!string.Equals(hash, want, StringComparison.OrdinalIgnoreCase))
                            {
                                problem ??= Messages.Format(Messages.VerifyFileProblem, entry.Path, Messages.VerifyContentDiffers);
                            }
                        }
                    },
                    run.Cancellation);
            }
            finally
            {
                foreach (var sink in sinks.Values)
                {
                    sink.Dispose();
                }
            }

            if (problem is null && expected.Keys.FirstOrDefault(k => !seen.Contains(k)) is { } missing)
            {
                problem = Messages.Format(Messages.VerifyFileProblem, missing, Messages.VerifyFileMissing);
            }

            return problem;
        }
        catch (Exception ex) when (ex is ArchiveException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return ex.Message;
        }
    }

    internal static string OutcomeText(ExtractOutcome outcome) => outcome switch
    {
        ExtractOutcome.WrongPassword => Messages.OutcomeWrongPassword,
        ExtractOutcome.Unsupported => Messages.OutcomeUnsupported,
        ExtractOutcome.Truncated => Messages.OutcomeTruncated,
        _ => Messages.OutcomeDamaged,
    };
}

/// <summary>A stream that only computes the SHA-256 of what is written into it.</summary>
public sealed class HashingStream : Stream
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private long _length;

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => _length;

    public override long Position
    {
        get => _length;
        set => throw new NotSupportedException();
    }

    /// <summary>The SHA-256 in lower-case hex.</summary>
    public string Finish() => SetManifest.ToHex(_hash.GetHashAndReset());

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        _hash.AppendData(buffer);
        _length += buffer.Length;
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hash.Dispose();
        }

        base.Dispose(disposing);
    }
}
