using System.Security.Cryptography;
using System.Text.Json;
using Custos.Core;
using Custos.Core.Agent;
using Custos.Core.Archives;
using Custos.Core.Backup;
using Custos.Core.Destinations;
using Custos.Core.Ipc;
using Custos.Core.Model;
using Custos.Core.Monitor;
using Custos.Core.Text;
using Custos.Service.Platform;
using Microsoft.Extensions.Logging;

namespace Custos.Service.Agent;

/// <summary>
/// The connection to the monitoring server (section 8, appendix A): the agent side of <see cref="ServerLink"/>, the
/// commands of the server and the reports of runs and destinations.
/// </summary>
internal sealed partial class AgentService : IMonitorHost
{
    /// <summary>Who the live log names for the commands of the server.</summary>
    private const string ServerUser = "server";

    /// <summary>A file in <c>config</c> the installer leaves with the address and the registration code (section 10.1).</summary>
    private static readonly string ServerSetupFile = Path.Combine(DataFolders.Config, "server-setup.json");

    private LinkStore? _linkStore;
    private ServerLink? _link;
    private Task _linkLoop = Task.CompletedTask;
    private int _fullInventoryRunning;

    /// <summary>A server address is set: reports are kept for it.</summary>
    private bool ServerConfigured => _link is not null && _config.GetSettings().ServerUrl is not null;

    // ---- Start -------------------------------------------------------------------------------------------------

    private void StartMonitor(CancellationToken stoppingToken)
    {
        var store = new LinkStore(DataFolders.Config, new DpapiProtector());
        store.Load();
        foreach (string problem in store.Problems)
        {
            ServiceLog.ConfigProblem(logger, problem);
            lock (_lock)
            {
                _problems.Add(problem);
            }
        }

        _linkStore = store;
        _link = new ServerLink(store, Database, this, new MonitorClient());
        TakeServerSetup();
        FinishPendingUpdate();
        _linkLoop = Task.Run(async () =>
        {
            try
            {
                await _link.RunAsync(stoppingToken).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // The backups go on without the server.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                ServiceLog.MonitorCrashed(logger, ex);
            }
        }, CancellationToken.None);
    }

    /// <summary>The address and the code typed into the installer: taken once, the file with the code in it is deleted.</summary>
    private void TakeServerSetup()
    {
        if (!File.Exists(ServerSetupFile))
        {
            return;
        }

        ServerSetup? setup = null;
        try
        {
            // As text: the installer writes it in UTF-8 with a BOM, which the byte reader of JSON does not skip.
            setup = JsonSerializer.Deserialize(File.ReadAllText(ServerSetupFile), MonitorJsonContext.Default.ServerSetup);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Log(LogLineLevels.Error, Messages.Format(Messages.MonitorSetupUnreadable, ex.Message));
        }
        finally
        {
            TryDeleteFile(ServerSetupFile);
        }

        if (setup?.ServerUrl is not { } url || string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            ConnectServer(url, setup.RegistrationCode ?? string.Empty, Messages.MonitorByInstaller);
        }
        catch (AgentRequestException ex)
        {
            Log(LogLineLevels.Error, Messages.Format(Messages.MonitorSetupUnreadable, ex.Message));
        }
    }

    // ---- Requests from the interface ---------------------------------------------------------------------------

    /// <summary>«Connect» in the settings: the address of the server and the registration code (A.3).</summary>
    public OkResult ConnectServer(string url, string code, string user)
    {
        var link = _link ?? throw new AgentRequestException(PipeProtocol.Errors.Internal, "The service is starting.");

        // The connection string of the site (address#code) may come in either field.
        if (RegistrationCode.TrySplitConnection(code, out string urlFromCode, out string codeOnly))
        {
            (url, code) = (urlFromCode, codeOnly);
        }
        else if (RegistrationCode.TrySplitConnection(url, out string urlOnly, out string codeFromUrl))
        {
            (url, code) = (urlOnly, codeFromUrl.Length > 0 ? codeFromUrl : code);
        }

        if (!MonitorClient.IsValidServerUrl(url, out string normalized))
        {
            throw new AgentRequestException(PipeProtocol.Errors.Invalid, Messages.Format(Messages.MonitorUrlBad, url));
        }

        if (RegistrationCode.TryParse(code, out string? problem) is null)
        {
            throw new AgentRequestException(PipeProtocol.Errors.Invalid, problem ?? Messages.MonitorCodeBad);
        }

        string? old = _config.GetSettings().ServerUrl;
        bool newServer = !string.Equals(old, normalized, StringComparison.OrdinalIgnoreCase);
        _config.SetServerUrl(normalized);
        link.Connect(code.Trim(), newServer);
        Log(LogLineLevels.Info, Messages.Format(Messages.MonitorConnectedBy, normalized, user));
        PublishSnapshot();
        return OkResult.Instance;
    }

    /// <summary>«Reset the binding to the server» (A.3): local, administrators only.</summary>
    public OkResult ResetServer(string user)
    {
        var link = _link ?? throw new AgentRequestException(PipeProtocol.Errors.Internal, "The service is starting.");
        _config.SetServerUrl(null);
        link.Reset();
        Log(LogLineLevels.Warning, Messages.Format(Messages.MonitorResetBy, user));
        PublishSnapshot();
        return OkResult.Instance;
    }

    // ---- What the configuration and the runs report -------------------------------------------------------------

    /// <summary>A change of the configuration here or by a command: noted for the next snapshot (A.6).</summary>
    private void NoteConfigChange(TaskDefinition? saved = null, string? deletedTaskId = null)
    {
        if (!ServerConfigured || _linkStore is null)
        {
            return;
        }

        try
        {
            if (saved is not null)
            {
                _linkStore.NoteChanged(new TaskChangeNote { TaskId = saved.Id, Revision = saved.Revision, UpdatedBy = saved.UpdatedBy, UpdatedAt = saved.UpdatedAt });
            }

            if (deletedTaskId is not null)
            {
                _linkStore.NoteDeleted(deletedTaskId);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ServiceLog.ConfigProblem(logger, ex.Message);
        }

        _link!.ConfigChanged();
    }

    private void ReportRunStart(RunRecord record, int taskRevision)
    {
        if (ServerConfigured)
        {
            Queue(RunReports.StartPath, JsonSerializer.SerializeToUtf8Bytes(RunReports.Start(record, taskRevision), MonitorJsonContext.Default.RunStartBody));
        }
    }

    private void ReportRunFinish(RunRecord record, BackupResult result, string logFile)
    {
        if (result.Verify.Checks.Count > 0)
        {
            TryDatabase(db =>
            {
                db.SaveSetChecks(record.TaskId, result.Verify.Checks, record.FinishedAt ?? DateTimeOffset.UtcNow);
                return true;
            });
        }

        if (!ServerConfigured)
        {
            return;
        }

        Queue(RunReports.FinishPath, JsonSerializer.SerializeToUtf8Bytes(RunReports.Finish(record, result, logFile), MonitorJsonContext.Default.RunFinishBody));
        StartInventory(record.TaskId);
    }

    private void Queue(string path, byte[] body, string? key = null)
    {
        try
        {
            _link!.Enqueue(path, body, key);
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            ServiceLog.DatabaseFailed(logger, ex);
        }
    }

    // ---- IMonitorHost ------------------------------------------------------------------------------------------

    string? IMonitorHost.ServerUrl => _config.GetSettings().ServerUrl;

    bool IMonitorHost.AllowRemoteEdit => _config.GetSettings().AllowRemoteEdit;

    RegisterBody IMonitorHost.DescribeMachine(string publicKeyPem) => new()
    {
        Hostname = Environment.MachineName,
        MachineGuid = MachineInfo.MachineGuid(),
        Os = MachineInfo.OsDescription(),
        AgentVersion = info.Version,
        TimeZone = MachineInfo.TimeZone(),
        PublicKey = publicKeyPem,
    };

    HeartbeatBody IMonitorHost.DescribeState()
    {
        ActiveRun? current;
        lock (_lock)
        {
            current = _current;
        }

        return new HeartbeatBody
        {
            AgentVersion = info.Version,
            SevenzipVersion = _platform?.Archives.SevenZipVersion,
            ConfigRevision = _config.ConfigRevision,
            TimeZone = MachineInfo.TimeZone(),
            ServiceStartedAt = info.StartedAt,
            State = current is null ? HeartbeatStates.Idle : current.Pause.IsPaused ? HeartbeatStates.Paused : HeartbeatStates.Running,
            Running = current is null ? null : new HeartbeatRun { RunId = current.RunId, TaskId = current.Task.Id, PausedAt = current.Pause.PausedAt },
            Queue = [.. _queue.Snapshot(_ => null).Select(q => q.TaskId)],
        };
    }

    byte[] IMonitorHost.BuildConfigSnapshot(LinkState notes) => _config.BuildSnapshot(notes);

    RunProgressBody? IMonitorHost.Progress()
    {
        ActiveRun? current;
        lock (_lock)
        {
            current = _current;
        }

        if (current is null || current.Pause.IsPaused || current.Progress is not { } progress)
        {
            return null;
        }

        string? file = progress.CurrentFile;
        return new RunProgressBody
        {
            RunId = current.RunId,
            Phase = progress.Phase,
            Percent = Math.Round(progress.Percent, 1),
            FilesDone = progress.FilesDone,
            FilesTotal = progress.FilesTotal,
            BytesDone = progress.BytesDone,
            BytesTotal = progress.BytesTotal,
            SpeedBps = progress.SpeedBps,
            EtaS = progress.EtaS,
            CurrentFile = file is { Length: > 4096 } ? file[..4096] : file,
        };
    }

    void IMonitorHost.RequestInventory() => StartInventory(null);

    Task<CommandOutcome?> IMonitorHost.ExecuteAsync(MonitorCommand command, CancellationToken cancellationToken) => command.Kind switch
    {
        CommandKinds.TaskRun => Sync(() => RemoteRun(command)),
        CommandKinds.TaskStop => Sync(() => RemoteStop(command)),
        CommandKinds.TaskPause => Sync(() => RemotePause(command, paused: true)),
        CommandKinds.TaskResume => Sync(() => RemotePause(command, paused: false)),
        CommandKinds.TaskEnable => Sync(() => RemoteEnable(command, enabled: true)),
        CommandKinds.TaskDisable => Sync(() => RemoteEnable(command, enabled: false)),
        CommandKinds.TaskUpsert => Sync(() => RemoteUpsert(command)),
        CommandKinds.TaskDelete => Sync(() => RemoteDelete(command)),
        CommandKinds.SettingsUpdate => Sync(() => RemoteSettings(command)),
        CommandKinds.LogFetch => SendLogAsync(command, cancellationToken),
        CommandKinds.InventoryRefresh => Sync(() => RemoteInventory()),
        CommandKinds.UpdateInstall => InstallUpdateAsync(command, cancellationToken),
        CommandKinds.SevenZipUpdate => RemoteSevenZipAsync(command, cancellationToken),
        _ => Sync(() => CommandOutcome.Rejected(Messages.Format(Messages.MonitorCommandUnknown, command.Kind))),
    };

    void IMonitorHost.Report(LinkEvent kind, string text)
    {
        switch (kind)
        {
            case LinkEvent.ConnectionLost:
                ServiceLog.ServerConnectionLost(logger, text);
                Log(LogLineLevels.Warning, text);
                break;
            case LinkEvent.ConnectionRestored:
                ServiceLog.ServerConnectionRestored(logger, text);
                Log(LogLineLevels.Info, text);
                break;
            case LinkEvent.Error:
                ServiceLog.MonitorMessage(logger, LogLevel.Error, text);
                Log(LogLineLevels.Error, text);
                break;
            case LinkEvent.Warning:
                ServiceLog.MonitorMessage(logger, LogLevel.Warning, text);
                Log(LogLineLevels.Warning, text);
                break;
            default:
                ServiceLog.MonitorMessage(logger, LogLevel.Information, text);
                Log(LogLineLevels.Info, text);
                break;
        }
    }

    void IMonitorHost.LinkChanged() => PublishSnapshot();

    // ---- Commands (A.4) ----------------------------------------------------------------------------------------

    private static Task<CommandOutcome?> Sync(Func<CommandOutcome> action) => Task.FromResult<CommandOutcome?>(action());

    private CommandOutcome RemoteRun(MonitorCommand command)
    {
        if (TaskOf(command) is not { } task)
        {
            return CommandOutcome.Failed(Messages.ConfigTaskGone);
        }

        string? refused = Enqueue(task, RunTriggers.Remote, null, ServerUser);
        return CommandOutcome.Done(refused);
    }

    private CommandOutcome RemoteStop(MonitorCommand command)
    {
        string? taskId = Text(command.Payload, "task_id");
        if (taskId is null)
        {
            return CommandOutcome.Failed(Messages.Format(Messages.MonitorPayloadMissing, "task_id"));
        }

        return Stop(taskId, ServerUser).Ok ? CommandOutcome.Done() : CommandOutcome.Done(Messages.MonitorNothingToStop);
    }

    private CommandOutcome RemotePause(MonitorCommand command, bool paused)
    {
        string? taskId = Text(command.Payload, "task_id");
        if (taskId is null)
        {
            return CommandOutcome.Failed(Messages.Format(Messages.MonitorPayloadMissing, "task_id"));
        }

        return SetPaused(taskId, paused, ServerUser).Ok ? CommandOutcome.Done() : CommandOutcome.Failed(Messages.MonitorTaskNotRunning);
    }

    private CommandOutcome RemoteEnable(MonitorCommand command, bool enabled)
    {
        string? taskId = Text(command.Payload, "task_id");
        if (taskId is null)
        {
            return CommandOutcome.Failed(Messages.Format(Messages.MonitorPayloadMissing, "task_id"));
        }

        TaskChange? change;
        try
        {
            change = _config.SetEnabled(taskId, enabled, "server:site", DateTimeOffset.UtcNow);
        }
        catch (ConfigException ex)
        {
            return CommandOutcome.Failed(ex.Message);
        }

        if (change is null)
        {
            return CommandOutcome.Done();
        }

        if (change.ScheduleReset)
        {
            SetLastSlot(change.Saved.Id, DateTimeOffset.UtcNow);
        }

        Log(LogLineLevels.Info, Messages.Format(enabled ? Messages.MonitorTaskEnabled : Messages.MonitorTaskDisabled, change.Saved.Name), change.Saved.Id);
        NoteConfigChange(change.Saved);
        PublishSnapshot();
        _scheduleWake.Release();
        return CommandOutcome.Done();
    }

    private CommandOutcome RemoteUpsert(MonitorCommand command)
    {
        var payload = command.Payload;
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("task", out var taskJson) || taskJson.ValueKind != JsonValueKind.Object)
        {
            return CommandOutcome.Failed(Messages.Format(Messages.MonitorPayloadMissing, "task"));
        }

        int baseRevision = payload.TryGetProperty("base_revision", out var revision) && revision.TryGetInt32(out int value) ? value : 0;
        var credentials = new List<ServerCredential>();
        if (payload.TryGetProperty("secrets", out var secrets) && secrets.ValueKind == JsonValueKind.Object)
        {
            foreach (var secret in secrets.EnumerateObject())
            {
                var item = secret.Value;
                string? encrypted = Text(item, "secret_enc");
                string plain;
                try
                {
                    plain = MonitorCrypto.DecryptSecret(_linkStore!.AgentKey, encrypted ?? string.Empty);
                }
                catch (CryptographicException ex)
                {
                    return CommandOutcome.Failed(Messages.Format(Messages.MonitorSecretUnreadable, secret.Name, ex.Message));
                }

                credentials.Add(new ServerCredential(secret.Name, Text(item, "kind") ?? string.Empty, Text(item, "user") ?? string.Empty, Text(item, "domain"), plain));
            }
        }

        ServerTaskChange change;
        try
        {
            change = _config.UpsertFromServer(taskJson, baseRevision, credentials, DateTimeOffset.UtcNow);
        }
        catch (ConfigException ex)
        {
            return CommandOutcome.Failed(ex.Message);
        }

        var saved = change.Saved;
        if (change.Change.ScheduleReset)
        {
            SetLastSlot(saved.Id, DateTimeOffset.UtcNow);
        }

        string? message = null;
        if (change.Overwritten is { } overwritten)
        {
            _linkStore!.NoteOverwritten(JsonSerializer.SerializeToElement(overwritten, Core.Json.ConfigJsonContext.Default.TaskDefinition));
            message = $"conflict: local revision {overwritten.Revision} overwritten";
            Log(LogLineLevels.Warning, Messages.Format(Messages.MonitorTaskOverwritten, saved.Name, overwritten.Revision, overwritten.UpdatedBy), saved.Id);
        }

        string by = saved.UpdatedBy?.StartsWith("server:", StringComparison.Ordinal) == true ? saved.UpdatedBy["server:".Length..] : ServerUser;
        Log(LogLineLevels.Info, Messages.Format(change.Previous is null ? Messages.MonitorTaskCreated : Messages.MonitorTaskChanged, saved.Name, by), saved.Id);
        NoteConfigChange(saved);
        PublishSnapshot();
        _scheduleWake.Release();
        return CommandOutcome.Done(message);
    }

    private CommandOutcome RemoteDelete(MonitorCommand command)
    {
        string? taskId = Text(command.Payload, "task_id");
        if (taskId is null)
        {
            return CommandOutcome.Failed(Messages.Format(Messages.MonitorPayloadMissing, "task_id"));
        }

        if (_config.GetTask(taskId) is null)
        {
            return CommandOutcome.Done(Messages.ConfigTaskGone);
        }

        try
        {
            DeleteTask(taskId, ServerUser);
        }
        catch (AgentRequestException ex)
        {
            return CommandOutcome.Failed(ex.Message);
        }

        return CommandOutcome.Done();
    }

    private CommandOutcome RemoteSettings(MonitorCommand command)
    {
        if (command.Payload.ValueKind != JsonValueKind.Object || !command.Payload.TryGetProperty("settings", out var settings))
        {
            return CommandOutcome.Failed(Messages.Format(Messages.MonitorPayloadMissing, "settings"));
        }

        List<string> changed;
        try
        {
            changed = _config.UpdateSettingsFromServer(settings);
        }
        catch (ConfigException ex)
        {
            return CommandOutcome.Failed(ex.Message);
        }

        ApplyLanguage();
        Log(LogLineLevels.Info, Messages.Format(Messages.MonitorSettingsChanged, string.Join(", ", changed)));
        NoteConfigChange();
        PublishSnapshot();
        return CommandOutcome.Done();
    }

    private CommandOutcome RemoteInventory()
    {
        StartInventory(null);
        return CommandOutcome.Done();
    }

    /// <summary><c>log.fetch</c> (A.7): the whole log of a run, if it is still kept.</summary>
    private async Task<CommandOutcome?> SendLogAsync(MonitorCommand command, CancellationToken cancellationToken)
    {
        string? runId = Text(command.Payload, "run_id");
        if (runId is null || !Guid.TryParse(runId, out _))
        {
            return CommandOutcome.Failed(Messages.Format(Messages.MonitorPayloadMissing, "run_id"));
        }

        string? file = RunLogFile.Find(DataFolders.RunLogs, runId);
        if (file is null)
        {
            return CommandOutcome.Failed(Messages.MonitorLogGone);
        }

        string? packed = RunReports.Pack(file);
        if (packed is null)
        {
            return CommandOutcome.Failed("too_large");
        }

        var body = JsonSerializer.SerializeToUtf8Bytes(new RunLogBody { LogGzB64 = packed }, MonitorJsonContext.Default.RunLogBody);
        var answer = await _link!.SendAsync($"agent/runs/{runId.ToLowerInvariant()}/log", body, cancellationToken).ConfigureAwait(false);
        return answer.Ok ? CommandOutcome.Done()
            : answer.Status == 413 ? CommandOutcome.Failed("too_large")
            : CommandOutcome.Failed(answer.Describe());
    }

    // ---- Inventory (A.8) ---------------------------------------------------------------------------------------

    /// <summary>Builds the inventory in the background and queues it: of one task after its run, of all otherwise.</summary>
    private void StartInventory(string? taskId)
    {
        if (!ServerConfigured)
        {
            return;
        }

        if (taskId is null && Interlocked.Exchange(ref _fullInventoryRunning, 1) == 1)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                var tasks = taskId is null ? _config.GetTasks() : _config.GetTask(taskId) is { } one ? [one] : [];
                var body = new InventoryBody();
                foreach (var task in tasks)
                {
                    body.Destinations.AddRange(InventoryOf(task));
                }

                if (taskId is null || body.Destinations.Count > 0)
                {
                    Queue(ServerLink.InventoryPath, JsonSerializer.SerializeToUtf8Bytes(body, MonitorJsonContext.Default.InventoryBody),
                        taskId is null ? ServerLink.InventoryKey : ServerLink.InventoryKey + ":" + taskId.ToLowerInvariant());
                }
            }
#pragma warning disable CA1031 // An inventory that failed is made again in 6 hours.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                ServiceLog.MonitorMessage(logger, LogLevel.Warning, Messages.Format(Messages.MonitorInventoryFailed, ex.Message));
            }
            finally
            {
                if (taskId is null)
                {
                    Interlocked.Exchange(ref _fullInventoryRunning, 0);
                }
            }
        }, CancellationToken.None);
    }

    /// <summary>
    /// The destinations of a task with their sets and free space. A destination that cannot be reached now is left
    /// out, so that the server keeps what it knew about it rather than seeing it empty.
    /// </summary>
    private List<InventoryDestination> InventoryOf(TaskDefinition task)
    {
        var result = new List<InventoryDestination>();
        var checks = TryDatabase(db => db.GetSetChecks(task.Id)) ?? [];
        string? password = null;
        if (task.Archive.Encrypt && task.Archive.PasswordCredentialId is { } credentialId)
        {
            try
            {
                password = Platform.GetArchivePassword(credentialId);
            }
            catch (BackupException)
            {
                // Archives with encrypted names are then not listed.
            }
        }

        var variables = PathVariables.For(Platform, task, DateTime.Now);
        foreach (var destination in task.Clone().Destinations)
        {
            if (!DestinationKinds.IsSupported(destination.Kind))
            {
                continue;
            }

            // What the server shows: the path as it was typed, or the address of a remote destination.
            string shownPath = DestinationKinds.IsFileSystem(destination.Kind) && destination.Kind != DestinationKinds.Usb
                ? destination.Path
                : DestinationStores.Label(destination);
            destination.Path = variables.Expand(destination.Path, out string? problem);
            if (problem is not null)
            {
                continue;
            }

            try
            {
                using var store = Platform.OpenStore(destination);
                if (store.HasFolders && !store.FolderExists(store.Root))
                {
                    continue;
                }

                var sets = SetCatalog.Scan(store, store.Root, task.Id, new ArchiveAccess(Platform.Archives, password, store), mirror: true);
                var space = store.GetSpace(store.Root);
                result.Add(new InventoryDestination
                {
                    TaskId = task.Id,
                    DestinationId = destination.Id,
                    Kind = destination.Kind,
                    Path = shownPath,
                    TotalBytes = space.TotalBytes,
                    FreeBytes = space.FreeBytes,
                    Sets =
                    [
                        .. sets.Select(set =>
                        {
                            var found = checks.TryGetValue((destination.Id.ToUpperInvariant(), set.Header.SetName), out var check);
                            return new InventorySet
                            {
                                Name = set.Header.SetName,
                                Type = set.Header.BackupType,
                                Time = set.Header.StartedAt,
                                Bytes = set.Storage == SetStorage.Archive ? set.Bytes : set.Header.SetBytes,
                                VerifiedAt = found ? check.CheckedAt : null,
                                VerifyOk = found ? check.Ok : null,
                            };
                        }),
                    ],
                });
            }
            catch (Exception ex) when (ex is BackupException or IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Not reachable now (a USB disk that is not plugged in, a share or a server that is down).
            }
        }

        return result;
    }

    // ---- 7-Zip from the server (A.10) --------------------------------------------------------------------------

    /// <summary><c>sevenzip.update</c>: the official installer the server keeps, checked by its SHA-256 and the signature.</summary>
    private async Task<CommandOutcome?> RemoteSevenZipAsync(MonitorCommand command, CancellationToken cancellationToken)
    {
        string? version = Text(command.Payload, "version");
        string? sha256 = Text(command.Payload, "sha256")?.ToLowerInvariant();
        string? signature = Text(command.Payload, "signature");
        if (CustosEdition.IsLegacy)
        {
            // The site keeps the 64-bit installer of 7-Zip; the 32-bit program updates 7z.dll with its own button.
            return CommandOutcome.Rejected(Messages.SevenZipRemoteNotInLegacy);
        }

        if (version is null || sha256 is null || !IsSha256(sha256) || !SevenZipVersionPattern().IsMatch(version))
        {
            return CommandOutcome.Rejected(Messages.MonitorPackageBadCommand);
        }

        if (!_link!.VerifyServerSignature($"7zip\n{version}\n{sha256}", signature))
        {
            return CommandOutcome.Rejected(Messages.MonitorPackageBadSignature);
        }

        Directory.CreateDirectory(DataFolders.Updates);
        string installer = Path.Combine(DataFolders.Updates, $"7z-server-{Guid.NewGuid():N}.exe");
        try
        {
            Log(LogLineLevels.Info, Messages.Format(Messages.MonitorSevenZipDownloading, version));
            var answer = await _link.DownloadAsync($"agent/sevenzip/{version}", installer, 100L * 1024 * 1024, cancellationToken).ConfigureAwait(false);
            if (!answer.Ok)
            {
                return CommandOutcome.Failed(Messages.Format(Messages.MonitorDownloadFailed, answer.Describe()));
            }

            if (!string.Equals(FileSha256(installer), sha256, StringComparison.Ordinal))
            {
                return CommandOutcome.Rejected(Messages.MonitorPackageBadHash);
            }

            // Like the button, but waiting for the tasks with 7z rather than refusing (A.10).
            var result = await Task.Run(() => InstallSevenZip(installer, ServerUser, cancellationToken, waitForRuns: true), cancellationToken).ConfigureAwait(false);
            return result.Ok ? CommandOutcome.Done(result.Message) : CommandOutcome.Failed(result.Message ?? Messages.SevenZipWindowsOnly);
        }
        finally
        {
            TryDeleteFile(installer);
        }
    }

    // ---- Helpers -----------------------------------------------------------------------------------------------

    private TaskDefinition? TaskOf(MonitorCommand command) => Text(command.Payload, "task_id") is { } id ? _config.GetTask(id) : null;

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool IsSha256(string text) => text.Length == 64 && text.All(Uri.IsHexDigit);

    private static string FileSha256(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static readonly System.Text.RegularExpressions.Regex SevenZipVersionPatternRegex = new(@"^\d{1,3}\.\d{1,3}$");

    private static System.Text.RegularExpressions.Regex SevenZipVersionPattern() => SevenZipVersionPatternRegex;
}
