using System.Globalization;
using Custos.Core.Archives;
using Custos.Core.Databases;
using Custos.Core.Model;
using Custos.Core.Text;

namespace Custos.Core.Backup;

/// <summary>
/// Dumps of the databases on this computer (section 6.2): MySQL, MariaDB and PostgreSQL, for example those of OSPanel.
/// </summary>
/// <remarks>
/// At the start of the run every server is asked for its databases; one that cannot be reached is an error, and the
/// rest of the run goes on. Each database is then dumped whole, in every run, straight from the output of the dump
/// program into the set: <c>db/&lt;engine&gt;/&lt;name&gt;.sql</c>, plus <c>_users.sql</c> with the users and rights
/// of the server. Dumps come before the files. A dump counts only with exit code 0 and its end line (the rule of
/// custos-helper). A run with a failed dump is an error: its set is kept, but nothing is committed or deleted, so the
/// next run takes the changes again.
/// </remarks>
public sealed partial class BackupEngine
{
    private static bool HasDatabases(TaskDefinition task) => task.DbServers.Count > 0;

    /// <summary>Asks every database server of the task which databases it has and plans their dumps.</summary>
    private void PlanDatabases(RunContext run)
    {
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var server in run.Task.DbServers)
        {
            run.Cancellation.ThrowIfCancellationRequested();
            string folder = server.Engine;
            for (int i = 2; !folders.Add(folder); i++)
            {
                // Two servers of one engine (for example MySQL 5.7 and 8.0 of OSPanel) get folders of their own.
                folder = server.Engine + "-" + i.ToString(CultureInfo.InvariantCulture);
            }

            string where = DbServerLabel(server);
            try
            {
                if (!DbEngines.IsKnown(server.Engine))
                {
                    throw new BackupException(Messages.Format(Messages.DbEngineUnknown, server.Engine));
                }

                var account = server.CredentialId is { } id ? _platform.GetDbAccount(id) : null;
                var client = _platform.CreateDbClient(server, account);
                var info = client.Inspect(run.Cancellation);
                var names = SelectDatabases(run, server, info, where);
                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { DbTools.UsersFileStem };
                foreach (string name in names)
                {
                    run.Dumps.Add(new PlannedDump(server, client, info.Version, name, $"{DbTools.SetFolder}/{folder}/{DbTools.FileName(name, used)}.sql", where));
                }

                run.Dumps.Add(new PlannedDump(server, client, info.Version, null, $"{DbTools.SetFolder}/{folder}/{DbTools.UsersFileStem}.sql", where));
                _log.Write(RunLogLevel.Info, Messages.Format(Messages.DbServerReady, where, info.Version, names.Count,
                    names.Count == 0 ? Messages.DbNoDatabases : string.Join(", ", names)));
                if (info.DataDir is { } dataDir && run.Task.Sources.Any(s => IsInside(dataDir, s.Path) || IsInside(s.Path, dataDir)))
                {
                    _log.Write(RunLogLevel.Info, Messages.Format(Messages.DbDataDirInSources, dataDir));
                }
            }
            catch (Exception ex) when (ex is DbException or BackupException)
            {
                DbFailed(run, Messages.Format(Messages.DbServerUnavailable, where, ex.Message));
                if (!server.AllDatabases)
                {
                    foreach (string name in server.Include)
                    {
                        run.Dumps.Add(new PlannedDump(server, null, string.Empty, name, null, where) { Ok = false, Error = ex.Message });
                    }
                }
            }
        }
    }

    /// <summary>The databases to dump: all but the system ones and the exceptions, or only the listed ones.</summary>
    private List<string> SelectDatabases(RunContext run, TaskDatabaseServer server, DbServerInfo info, string where)
    {
        var comparer = DbTools.NameComparer(server.Engine);
        var system = DbTools.SystemDatabases(server.Engine);
        var present = info.Databases.ToHashSet(comparer);
        if (server.AllDatabases)
        {
            var excluded = server.Exclude.ToHashSet(comparer);
            return [.. info.Databases.Where(n => !system.Contains(n, comparer) && !excluded.Contains(n)).Distinct(comparer)];
        }

        var chosen = new List<string>();
        foreach (string name in server.Include.Distinct(comparer))
        {
            if (present.Contains(name))
            {
                chosen.Add(info.Databases.First(n => comparer.Equals(n, name)));
                continue;
            }

            string message = Messages.Format(Messages.DbNotFound, name, where);
            DbFailed(run, message);
            run.Dumps.Add(new PlannedDump(server, null, info.Version, name, null, where) { Ok = false, Error = message });
        }

        return chosen;
    }

    /// <summary>The dumps still to take (not failed while planning).</summary>
    private static IEnumerable<PlannedDump> DumpsToTake(RunContext run) => run.Dumps.Where(d => d.File is not null && d.Ok != false);

    /// <summary>Plain sets: every dump into <c>db\…</c> of each destination, before the files.</summary>
    private void WriteDumps(RunContext run)
    {
        var buffer = new byte[BufferSize];
        foreach (var dump in DumpsToTake(run).ToList())
        {
            run.Pause.WaitWhilePaused(run.Cancellation);
            run.Cancellation.ThrowIfCancellationRequested();
            if (run.ActiveTargets.Count == 0)
            {
                break;
            }

            string relative = LocalSetPath(dump.File!);
            using var reader = OpenDump(run, dump);
            if (reader is null)
            {
                continue;
            }

            var outputs = new List<Output>();
            try
            {
                foreach (var target in run.ActiveTargets)
                {
                    string final = Path.Combine(target.WriteFolder, relative);
                    string temp = final + TempSuffix;
                    try
                    {
                        var stream = target.Session.Run(() =>
                        {
                            CreateFolder(target, Path.GetDirectoryName(final)!);
                            return Lowered(new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1));
                        });
                        outputs.Add(new Output(target, stream, temp, final));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        FailTarget(run, target, Messages.Format(Messages.DestinationWriteFailed, target.Label, ex.Message));
                    }
                }

                int read;
                while (outputs.Count > 0 && (read = reader.Read(buffer, 0, buffer.Length)) > 0)
                {
                    WriteOutputs(run, outputs, buffer, read);
                }

                if (outputs.Count == 0)
                {
                    // Every destination failed and says why.
                    break;
                }

                // A failed dump is not kept: the file is removed, the manifest says why.
                DumpDone(run, dump, reader, stored: reader.Error is null);
                if (dump.Ok != true)
                {
                    AbandonOutputs(outputs);
                    continue;
                }

                for (int i = outputs.Count - 1; i >= 0; i--)
                {
                    var output = outputs[i];
                    try
                    {
                        output.Stream.Dispose();
                        output.Target.Session.Run(() =>
                        {
                            if (File.Exists(output.Final))
                            {
                                File.SetAttributes(output.Final, FileAttributes.Normal);
                            }

                            File.Move(output.Temp, output.Final, overwrite: true);
                        });
                        output.Target.Files++;
                        output.Target.Bytes += dump.Bytes;
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
            }
        }

        lock (_sync)
        {
            _progress.CurrentFile = null;
        }

        Report(force: true);
    }

    /// <summary>Archives: one item per dump, opened when the writer gets to it. The dumps come first in ZIP; 7-Zip
    /// packs them after the manifest (sorted by name), which then lists them without a result.</summary>
    private List<ArchiveItem> DumpItems(RunContext run) =>
    [
        .. DumpsToTake(run).Select(dump => new ArchiveItem
        {
            Path = dump.File!,
            ModifiedUtc = run.Request.StartedAt.UtcDateTime,
            Open = () => OpenDumpForArchive(run, dump),
        }),
    ];

    private DumpStream? OpenDumpForArchive(RunContext run, PlannedDump dump)
    {
        var reader = OpenDump(run, dump);
        if (reader is null)
        {
            return null;
        }

        if (!reader.Prefetch())
        {
            // Failed before giving anything: nothing goes into the archive.
            DumpDone(run, dump, reader, stored: false);
            reader.Dispose();
            return null;
        }

        // Whatever comes now is in the archive; a failure shows in its last line.
        reader.Completed += (_, _) => DumpDone(run, dump, reader, stored: true);
        return reader;
    }

    /// <summary>Starts a dump program. Null if it could not be started (the reason is recorded).</summary>
    private DumpStream? OpenDump(RunContext run, PlannedDump dump)
    {
        run.Pause.WaitWhilePaused(run.Cancellation);
        run.Cancellation.ThrowIfCancellationRequested();
        lock (_sync)
        {
            _progress.CurrentFile = ArchivePaths.ToWindows(dump.File!);
        }

        Report(force: true);
        try
        {
            var started = dump.Client!.StartDump(dump.Database, dump.Version, run.Cancellation);
            return new DumpStream(started, dump.Server.Engine,
                () =>
                {
                    run.Pause.WaitWhilePaused(run.Cancellation);
                    run.Cancellation.ThrowIfCancellationRequested();
                },
                DumpRead);
        }
        catch (DbException ex)
        {
            Record(run, dump, ok: false, 0, null, ex.Message);
            return null;
        }
    }

    private void DumpRead(int count)
    {
        lock (_sync)
        {
            _progress.BytesDone += count;
            _speed.Add(count);
        }

        Report(force: false);
    }

    /// <summary>Records how a dump that was read to its end (or failed at its start) went.</summary>
    /// <param name="stored">Its data stays in the set.</param>
    private void DumpDone(RunContext run, PlannedDump dump, DumpStream reader, bool stored)
    {
        run.Cancellation.ThrowIfCancellationRequested();
        Record(run, dump, reader.Error is null, reader.Bytes, stored ? reader.Sha256 : null, reader.Error);
    }

    /// <param name="sha256">SHA-256 of what lies in the set; null when nothing does.</param>
    private void Record(RunContext run, PlannedDump dump, bool ok, long bytes, string? sha256, string? error)
    {
        lock (_sync)
        {
            dump.Ok = ok;
            dump.Bytes = bytes;
            dump.Sha256 = sha256;
            dump.Error = error;
            dump.InSet = sha256 is not null;
            _progress.CurrentFile = null;
            if (ok)
            {
                run.Result.SourceFiles++;
                run.Result.SourceBytes += bytes;
                run.Result.FilesProcessed++;
                run.StoredBytes += bytes;
                if (run.Access is null)
                {
                    run.Result.BytesWritten += bytes;
                }

                _log.Write(RunLogLevel.Info, Messages.Format(Messages.DumpWritten, DumpTitle(dump), Sizes.Format(bytes)));
            }
            else
            {
                DbFailed(run, Messages.Format(Messages.DumpFailed, DumpTitle(dump), error));
            }
        }
    }

    /// <summary>A database problem: the run becomes an error, the rest goes on.</summary>
    private void DbFailed(RunContext run, string message)
    {
        lock (_sync)
        {
            run.DbErrors++;
            run.Result.Errors++;
            run.Result.ErrorMessage ??= message;
            _log.Write(RunLogLevel.Error, message);
        }
    }

    /// <summary>The dumps as the manifest lists them.</summary>
    private static List<ManifestDatabase> ManifestDatabases(RunContext run) =>
    [
        .. run.Dumps.Select(d => new ManifestDatabase
        {
            Engine = d.Server.Engine,
            Name = d.Database,
            File = d.File is not null && (d.Ok is null || d.InSet) ? d.File : null,
            Server = d.Address ?? DbAddress(d.Server),
            Bytes = d.Ok is null ? null : d.Bytes,
            Sha256 = d.Sha256,
            Ok = d.Ok,
            Error = d.Error,
        }),
    ];

    /// <summary>The databases of the run for its record and the server (A.5); dumps never taken count as failed.</summary>
    private static void FinishDatabases(RunContext run)
    {
        foreach (var dump in run.Dumps.Where(d => d.Database is not null))
        {
            run.Result.Databases.Add(new DatabaseResult
            {
                Engine = dump.Server.Engine,
                Name = dump.Database!,
                Bytes = dump.Bytes,
                Ok = dump.Ok == true,
                Error = dump.Ok == true ? null : dump.Error ?? Messages.DumpNotTaken,
            });
        }
    }

    /// <summary>"MySQL 127.0.0.1:3306".</summary>
    internal static string DbServerLabel(TaskDatabaseServer server) => string.Concat(DbEngines.Title(server.Engine), " ", DbAddress(server));

    /// <summary>"127.0.0.1:3306".</summary>
    internal static string DbAddress(TaskDatabaseServer server) =>
        string.Concat(server.Host, ":", (server.Port ?? DbEngines.DefaultPort(server.Engine)).ToString(CultureInfo.InvariantCulture));

    private static string DumpTitle(PlannedDump dump) => dump.Database is null
        ? Messages.Format(Messages.DumpUsersTitle, dump.Where)
        : Messages.Format(Messages.DumpDatabaseTitle, dump.Database, dump.Where);

    /// <summary>Whether <paramref name="path"/> is <paramref name="folder"/> or lies inside it.</summary>
    private static bool IsInside(string path, string folder)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(folder))
        {
            return false;
        }

        string a = path.Replace('/', '\\').TrimEnd('\\') + "\\";
        string b = folder.Replace('/', '\\').TrimEnd('\\') + "\\";
        return a.StartsWith(b, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>One dump of the run.</summary>
    private sealed class PlannedDump(TaskDatabaseServer server, IDbClient? client, string version, string? database, string? file, string where)
    {
        public TaskDatabaseServer Server { get; } = server;

        public IDbClient? Client { get; } = client;

        public string Version { get; } = version;

        /// <summary>Null = the users and rights of the server.</summary>
        public string? Database { get; } = database;

        /// <summary>Path in the set ("db/mysql/site_db.sql"); null when there is nothing to take.</summary>
        public string? File { get; } = file;

        /// <summary>"MySQL 127.0.0.1:3306".</summary>
        public string Where { get; } = where;

        /// <summary>The server in the manifest; null = host:port of <see cref="Server"/> (a Linux server: its address).</summary>
        public string? Address { get; init; }

        /// <summary>Null until the dump is taken.</summary>
        public bool? Ok { get; set; }

        /// <summary>Stored in the set (a failed dump in an archive is, with its failure line).</summary>
        public bool InSet { get; set; }

        public long Bytes { get; set; }

        public string? Sha256 { get; set; }

        public string? Error { get; set; }
    }
}
