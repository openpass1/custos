using System.Globalization;
using System.Text;
using Custos.Core;
using Custos.Core.Backup;
using Custos.Core.Ipc;
using Custos.Core.Model;
using Custos.Core.Monitor;
using Custos.Core.Text;
using Custos.Service.Platform;

namespace Custos.Linux.Cli;

/// <summary>
/// The command <c>custos</c> of a Linux server: what the window is on Windows, for a terminal. It talks to the service
/// through its socket as the window does, so it shows the same state and may do what the account may do (root and the
/// members of sudo or wheel everything, the others look). Tasks are made and changed on the monitoring site.
/// </summary>
internal static partial class Commands
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);

    public static async Task<int> RunAsync(string[] args)
    {
        string command = args.Length == 0 ? "help" : args[0].TrimStart('-').ToLowerInvariant();
        string[] rest = args.Length > 1 ? args[1..] : [];
        try
        {
            return command switch
            {
                "status" => await StatusAsync().ConfigureAwait(false),
                "snapshots" => await SnapshotsAsync().ConfigureAwait(false),
                "run" => await RunTaskAsync(rest).ConfigureAwait(false),
                "stop" => await StopAsync(rest).ConfigureAwait(false),
                "log" => await LogAsync(rest).ConfigureAwait(false),
                "restore" => await RestoreAsync(rest).ConfigureAwait(false),
                "cloud" => await CloudAsync(rest).ConfigureAwait(false),
                "connect" => await ConnectAsync(rest).ConfigureAwait(false),
                "disconnect" => await DisconnectAsync().ConfigureAwait(false),
                "version" or "v" => Version(),
                "help" or "h" or "?" => Help(),
                _ => Fail(LinuxStrings.Format(LinuxStrings.CliUnknownCommand, args[0])),
            };
        }
        catch (TimeoutException)
        {
            return Fail(LinuxStrings.CliServiceDown);
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException)
        {
            return Fail(LinuxStrings.Format(LinuxStrings.CliConnectionLost, ex.Message));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Fail(ex.Message);
        }
        catch (ServiceRequestException ex)
        {
            return Fail(ex.Code == PipeProtocol.Errors.Forbidden ? LinuxStrings.CliForbidden : ex.Message);
        }
    }

    private static int Help()
    {
        Console.WriteLine(LinuxStrings.Format(LinuxStrings.CliHelp, CustosInfo.Version));
        return 0;
    }

    private static int Version()
    {
        Console.WriteLine(CustosInfo.Version);
        return 0;
    }

    private static async Task<int> StatusAsync()
    {
        await using var session = await Session.OpenAsync().ConfigureAwait(false);
        var state = await session.SnapshotAsync().ConfigureAwait(false);
        var output = new StringBuilder();
        output.AppendLine(LinuxStrings.Format(LinuxStrings.CliStatusService, session.Hello.ServiceVersion, Time(session.Hello.StartedAt)));
        output.AppendLine(LinuxStrings.Format(LinuxStrings.CliStatusSite, Site(state.Server)));
        output.AppendLine();
        output.AppendLine(state.Tasks.Count == 0 ? LinuxStrings.CliNoTasks : LinuxStrings.CliTasks);
        foreach (var task in state.Tasks)
        {
            var taskState = state.States.FirstOrDefault(s => s.TaskId == task.Id);
            string last = taskState?.LastRun is { } run
                ? $"{Words.Status(run.Status)} {Time(run.StartedAt)}" + (run.ErrorMessage is { Length: > 0 } error ? $" ({Shorten(error, 100)})" : string.Empty)
                : LinuxStrings.CliNever;
            string next = !task.Enabled ? LinuxStrings.CliDisabled
                : taskState?.NextRun is { } at ? Time(at)
                : "—";
            string mark = taskState is { Running: true } ? "▶" : taskState is { Queued: true } ? "…" : task.Enabled ? "•" : "○";
            output.AppendLine(LinuxStrings.Format(LinuxStrings.CliTaskLine, mark, task.Name, last, next));
        }

        if (state.Current is { } current)
        {
            output.AppendLine();
            output.AppendLine(LinuxStrings.Format(LinuxStrings.CliRunning, current.TaskName, Progress(current.Progress)));
        }

        var problems = state.Snapshots.Where(s => SnapshotStates.IsProblem(s.State) && s.State != SnapshotStates.Unsupported).ToList();
        if (problems.Count > 0)
        {
            output.AppendLine();
            foreach (var problem in problems)
            {
                output.AppendLine(LinuxStrings.Format(LinuxStrings.CliSnapshotWarning, problem.Mount, StateWords(problem.State)));
            }

            output.AppendLine(LinuxStrings.CliSnapshotsMore);
        }

        foreach (string problem in state.Problems.Concat(state.SevenZipProblem is { } sevenZip ? [sevenZip] : []))
        {
            output.AppendLine("! " + problem);
        }

        Console.Write(output.ToString());
        return 0;
    }

    private static async Task<int> SnapshotsAsync()
    {
        await using var session = await Session.OpenAsync().ConfigureAwait(false);
        var state = await session.SnapshotAsync().ConfigureAwait(false);
        if (state.Snapshots.Count == 0)
        {
            Console.WriteLine(LinuxStrings.CliNoSnapshotTasks);
            return 0;
        }

        foreach (var volume in state.Snapshots)
        {
            string where = volume.LogicalVolume is not null ? $"{volume.Mount} ({volume.VolumeGroup}/{volume.LogicalVolume}, {volume.FileSystem})"
                : $"{volume.Mount} ({volume.Device}, {volume.FileSystem})";
            Console.WriteLine($"{where}: {MethodWords(volume.Method)}, {StateWords(volume.State)}");
            if (volume.FreeBytes is { } free)
            {
                Console.WriteLine("  " + LinuxStrings.Format(LinuxStrings.CliSnapshotRoom, Sizes.Format(free),
                    volume.LastUsedBytes is { } used ? Sizes.Format(used) : "—"));
            }

            var names = volume.TaskIds.Select(id => state.Tasks.FirstOrDefault(t => t.Id == id)?.Name ?? id);
            Console.WriteLine("  " + LinuxStrings.Format(LinuxStrings.CliSnapshotTasks, string.Join(", ", names)));
            if (volume.Detail is { } detail)
            {
                Console.WriteLine("  " + detail);
            }

            Console.WriteLine();
        }

        return state.Snapshots.Any(s => SnapshotStates.IsProblem(s.State) && s.State != SnapshotStates.Unsupported) ? 2 : 0;
    }

    private static async Task<int> RunTaskAsync(string[] rest)
    {
        if (rest.Length == 0)
        {
            return Fail(LinuxStrings.CliRunWhich);
        }

        await using var session = await Session.OpenAsync().ConfigureAwait(false);
        if (rest[0] is "--all" or "-a")
        {
            var all = await session.RequestAsync(PipeProtocol.Methods.RunAll, EmptyRequest.Instance, IpcJsonContext.Default.EmptyRequest, IpcJsonContext.Default.OkResult).ConfigureAwait(false);
            Console.WriteLine(all.Message ?? LinuxStrings.CliQueued);
            return 0;
        }

        var task = await session.FindTaskAsync(string.Join(' ', rest)).ConfigureAwait(false);
        if (task is null)
        {
            return Fail(LinuxStrings.Format(LinuxStrings.CliNoSuchTask, string.Join(' ', rest)));
        }

        var result = await session.RequestAsync(PipeProtocol.Methods.TaskRun, new TaskRequest { TaskId = task.Id },
            IpcJsonContext.Default.TaskRequest, IpcJsonContext.Default.OkResult).ConfigureAwait(false);
        Console.WriteLine(result.Message ?? LinuxStrings.Format(LinuxStrings.CliTaskQueued, task.Name));
        return result.Ok ? 0 : 1;
    }

    private static async Task<int> StopAsync(string[] rest)
    {
        await using var session = await Session.OpenAsync().ConfigureAwait(false);
        string? taskId = null;
        if (rest.Length > 0)
        {
            var task = await session.FindTaskAsync(string.Join(' ', rest)).ConfigureAwait(false);
            if (task is null)
            {
                return Fail(LinuxStrings.Format(LinuxStrings.CliNoSuchTask, string.Join(' ', rest)));
            }

            taskId = task.Id;
        }

        var result = await session.RequestAsync(PipeProtocol.Methods.RunStop, new RunControlRequest { TaskId = taskId },
            IpcJsonContext.Default.RunControlRequest, IpcJsonContext.Default.OkResult).ConfigureAwait(false);
        Console.WriteLine(result.Message ?? LinuxStrings.CliStopping);
        return result.Ok ? 0 : 1;
    }

    private static async Task<int> LogAsync(string[] rest)
    {
        bool follow = rest.Contains("-f") || rest.Contains("--follow");
        int count = 40;
        int n = Array.IndexOf(rest, "-n");
        if (n >= 0 && n + 1 < rest.Length && int.TryParse(rest[n + 1], NumberStyles.None, CultureInfo.InvariantCulture, out int wanted))
        {
            count = Math.Max(1, wanted);
        }

        long lastSeq = -1;
        void Print(IEnumerable<LogLine> lines)
        {
            foreach (var line in lines.Where(l => l.Seq > Interlocked.Read(ref lastSeq)).OrderBy(l => l.Seq))
            {
                Interlocked.Exchange(ref lastSeq, line.Seq);
                string task = line.TaskName is { Length: > 0 } name ? $" [{name}]" : string.Empty;
                string level = line.Level == LogLineLevels.Info ? " " : line.Level == LogLineLevels.Warning ? "!" : "×";
                Console.WriteLine($"{line.Time.ToLocalTime().ToString("dd.MM HH:mm:ss", CultureInfo.InvariantCulture)} {level}{task} {line.Text}");
            }
        }

        var printLock = new Lock();
        await using var session = await Session.OpenAsync(follow ? message =>
        {
            if (message.Event == PipeProtocol.Events.LogLines)
            {
                var lines = message.GetData(IpcJsonContext.Default.LogResult).Lines;
                lock (printLock)
                {
                    Print(lines);
                }
            }
        }
        : null).ConfigureAwait(false);
        var recent = await session.RequestAsync(PipeProtocol.Methods.LogRecent, EmptyRequest.Instance, IpcJsonContext.Default.EmptyRequest, IpcJsonContext.Default.LogResult).ConfigureAwait(false);
        lock (printLock)
        {
            Print(recent.Lines.TakeLast(count));
        }

        if (!follow)
        {
            return 0;
        }

        // Until Ctrl+C or the service goes away.
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };
        await session.Completion.WaitAsync(stop.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        return 0;
    }

    private static async Task<int> ConnectAsync(string[] rest)
    {
        string line = string.Join(' ', rest).Trim();
        if (line.Length == 0)
        {
            return Fail(LinuxStrings.CliConnectWhat);
        }

        if (!RegistrationCode.TrySplitConnection(line, out string url, out string code) || RegistrationCode.TryParse(code, out _) is null)
        {
            return Fail(LinuxStrings.CliConnectBadLine);
        }

        await using var session = await Session.OpenAsync().ConfigureAwait(false);
        await session.RequestAsync(PipeProtocol.Methods.ServerConnect, new ServerConnectRequest { ServerUrl = url, Code = code },
            IpcJsonContext.Default.ServerConnectRequest, IpcJsonContext.Default.OkResult).ConfigureAwait(false);

        // The registration goes on in the service: wait a little for its answer.
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        ServerLinkInfo server;
        do
        {
            await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            server = (await session.SnapshotAsync().ConfigureAwait(false)).Server;
        }
        while (server.State is ServerLinkStates.Connecting or ServerLinkStates.NoCode && DateTime.UtcNow < until);

        Console.WriteLine(LinuxStrings.Format(LinuxStrings.CliStatusSite, Site(server)));
        return server.State switch
        {
            ServerLinkStates.Approved => 0,
            ServerLinkStates.Pending => Done(LinuxStrings.CliConnectPending),
            ServerLinkStates.Connecting or ServerLinkStates.Offline => 0,
            _ => 1,
        };
    }

    private static async Task<int> DisconnectAsync()
    {
        await using var session = await Session.OpenAsync().ConfigureAwait(false);
        var result = await session.RequestAsync(PipeProtocol.Methods.ServerReset, EmptyRequest.Instance,
            IpcJsonContext.Default.EmptyRequest, IpcJsonContext.Default.OkResult).ConfigureAwait(false);
        Console.WriteLine(result.Message ?? LinuxStrings.CliDisconnected);
        return 0;
    }

    private static string Site(ServerLinkInfo server)
    {
        if (server.State == ServerLinkStates.Off)
        {
            return LinuxStrings.CliSiteOff;
        }

        string state = server.State switch
        {
            ServerLinkStates.Approved => LinuxStrings.CliSiteApproved,
            ServerLinkStates.Pending => LinuxStrings.CliSitePending,
            ServerLinkStates.Connecting => LinuxStrings.CliSiteConnecting,
            ServerLinkStates.Offline => LinuxStrings.CliSiteOffline,
            _ => server.State,
        };
        string contact = server.LastContact is { } at ? ", " + LinuxStrings.Format(LinuxStrings.CliSiteContact, Time(at)) : string.Empty;
        string detail = server.Detail is { Length: > 0 } text ? $" ({text})" : string.Empty;
        return $"{server.ServerHost ?? server.ServerUrl}: {state}{contact}{detail}";
    }

    private static string Progress(BackupProgress? progress)
    {
        if (progress is null)
        {
            return string.Empty;
        }

        string text = $"{progress.Phase} {progress.Percent.ToString("0", CultureInfo.InvariantCulture)}%, {progress.FilesDone}/{progress.FilesTotal}";
        return progress.SpeedBps > 0 ? text + ", " + Sizes.FormatSpeed(progress.SpeedBps) : text;
    }

    private static string StateWords(string state) => state switch
    {
        SnapshotStates.Ok => LinuxStrings.CliSnapOk,
        SnapshotStates.LowSpace => LinuxStrings.CliSnapLow,
        SnapshotStates.NoSpace => LinuxStrings.CliSnapNoSpace,
        SnapshotStates.Overflowed => LinuxStrings.CliSnapOverflowed,
        SnapshotStates.Unsupported => LinuxStrings.CliSnapUnsupported,
        _ => state,
    };

    private static string MethodWords(string method) => method switch
    {
        SnapshotMethods.Lvm => "LVM",
        SnapshotMethods.Btrfs => "btrfs",
        SnapshotMethods.Zfs => "ZFS",
        _ => LinuxStrings.CliSnapNone,
    };

    private static string Time(DateTimeOffset time) => time.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);

    private static string Shorten(string text, int length) => text.Length <= length ? text : text[..length] + "…";

    private static int Done(string message)
    {
        Console.WriteLine(message);
        return 0;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine("custos: " + message);
        return 1;
    }

    /// <summary>One connection to the service, greeted.</summary>
    private sealed class Session : IAsyncDisposable
    {
        private readonly ServiceClient _client;

        private Session(ServiceClient client, HelloResult hello)
        {
            _client = client;
            Hello = hello;
        }

        public HelloResult Hello { get; }

        public Task Completion => _client.Completion;

        public static async Task<Session> OpenAsync(Action<PipeMessage>? onEvent = null)
        {
            var client = await ServiceClient.ConnectAsync(onEvent, ConnectTimeout, CancellationToken.None).ConfigureAwait(false);
            try
            {
                var hello = await client.RequestAsync(PipeProtocol.Methods.Hello,
                    new HelloRequest { Protocol = PipeProtocol.Version, ClientVersion = CustosInfo.Version },
                    IpcJsonContext.Default.HelloRequest, IpcJsonContext.Default.HelloResult, CancellationToken.None).WaitAsync(RequestTimeout).ConfigureAwait(false);
                return new Session(client, hello);
            }
            catch
            {
                await client.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        public async Task<TResult> RequestAsync<TParams, TResult>(string method, TParams parameters,
            System.Text.Json.Serialization.Metadata.JsonTypeInfo<TParams> parametersType, System.Text.Json.Serialization.Metadata.JsonTypeInfo<TResult> resultType,
            TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(timeout ?? RequestTimeout);
            try
            {
                return await _client.RequestAsync(method, parameters, parametersType, resultType, limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && limit.IsCancellationRequested)
            {
                throw new TimeoutException();
            }
        }

        public Task<AgentSnapshot> SnapshotAsync() =>
            RequestAsync(PipeProtocol.Methods.Snapshot, EmptyRequest.Instance, IpcJsonContext.Default.EmptyRequest, IpcJsonContext.Default.AgentSnapshot);

        /// <summary>A task by its id or name (the name in any case); null if none.</summary>
        public async Task<TaskDefinition?> FindTaskAsync(string text)
        {
            var tasks = (await SnapshotAsync().ConfigureAwait(false)).Tasks;
            return tasks.FirstOrDefault(t => string.Equals(t.Id, text, StringComparison.OrdinalIgnoreCase))
                ?? tasks.FirstOrDefault(t => string.Equals(t.Name, text, StringComparison.Ordinal))
                ?? tasks.FirstOrDefault(t => string.Equals(t.Name, text, StringComparison.OrdinalIgnoreCase));
        }

        public ValueTask DisposeAsync() => _client.DisposeAsync();
    }
}
