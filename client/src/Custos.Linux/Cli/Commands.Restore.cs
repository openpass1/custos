using Custos.Core;
using Custos.Core.Backup;
using Custos.Core.Ipc;
using Custos.Core.Model;
using Custos.Core.Text;
using Custos.Service.Platform;

namespace Custos.Linux.Cli;

internal static partial class Commands
{
    private static readonly TimeSpan RestoreSearchTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RestorePollInterval = TimeSpan.FromSeconds(1);

    private static async Task<int> RestoreAsync(string[] args)
    {
        if (args is [] or ["help"] or ["--help"] or ["-h"])
        {
            return Done(LinuxStrings.CliRestoreHelp);
        }

        if (args is ["status"] or ["stop"])
        {
            await using var control = await Session.OpenAsync().ConfigureAwait(false);
            if (args[0] == "status")
            {
                return ShowRestoreStatus(await ReadRestoreStatusAsync(control).ConfigureAwait(false));
            }

            var stopped = await StopRestoreAsync(control, null).ConfigureAwait(false);
            return stopped.Ok ? Done(LinuxStrings.CliRestoreStopping) : Fail(stopped.Message ?? Messages.RestoreNotRunning);
        }

        bool starting = args[0] == "start";
        if ((!starting && args[0] != "list") || !TryRestoreOptions(args, starting, out var options))
        {
            return Fail(LinuxStrings.CliRestoreHelp);
        }

        string? folder = options.GetValueOrDefault("--from");
        string? target = options.GetValueOrDefault("--to");
        string? dumps = options.GetValueOrDefault("--dumps-to");
        foreach (string? path in new[] { folder, target, dumps })
        {
            if (path is not null && !Path.IsPathFullyQualified(path))
            {
                return Fail(Messages.Format(Messages.PathNotAbsolute, path));
            }
        }

        using var interrupt = new CancellationTokenSource();
        void Cancel(object? _, ConsoleCancelEventArgs e)
        {
            e.Cancel = true;
            interrupt.Cancel();
        }

        Console.CancelKeyPress += Cancel;
        try
        {
            await using var session = await Session.OpenAsync().ConfigureAwait(false);
            if (!session.Hello.IsAdmin)
            {
                return Fail(LinuxStrings.CliForbidden);
            }

            string? taskId = null;
            if (options.GetValueOrDefault("--task") is { } taskName)
            {
                taskId = (await session.FindTaskAsync(taskName).ConfigureAwait(false))?.Id;
                if (taskId is null)
                {
                    return Fail(LinuxStrings.Format(LinuxStrings.CliNoSuchTask, taskName));
                }
            }

            string? password = null;
            if (options.ContainsKey("--password-stdin"))
            {
                if (!Console.IsInputRedirected)
                {
                    return Fail(LinuxStrings.CliRestorePasswordInput);
                }

                password = await Console.In.ReadLineAsync(interrupt.Token).ConfigureAwait(false);
                if (string.IsNullOrEmpty(password))
                {
                    return Fail(LinuxStrings.CliRestorePasswordMissing);
                }
            }

            var source = new RestoreListRequest
            {
                TaskId = taskId,
                Folder = folder,
                CredentialId = options.GetValueOrDefault("--account"),
                Password = password,
            };
            var list = await session.RequestAsync(PipeProtocol.Methods.RestoreList, source,
                IpcJsonContext.Default.RestoreListRequest, IpcJsonContext.Default.RestoreListResult,
                timeout: RestoreSearchTimeout, cancellationToken: interrupt.Token).ConfigureAwait(false);
            if (!starting)
            {
                return ShowRestoreSets(list);
            }

            string selection = options["--set"]!;
            string? destinationId = options.GetValueOrDefault("--destination");
            var matches = list.Sets.Where(set =>
                string.Equals(RestoreSelection(set), selection, string.IsNullOrEmpty(set.Header.RunId) ? PathNames.Comparison : StringComparison.OrdinalIgnoreCase)
                && (destinationId is null || string.Equals(set.DestinationId, destinationId, StringComparison.OrdinalIgnoreCase))).ToArray();
            if (matches.Length != 1)
            {
                return Fail(LinuxStrings.Format(matches.Length == 0 ? LinuxStrings.CliRestoreSetNotFound : LinuxStrings.CliRestoreSetAmbiguous, selection));
            }

            var chosen = matches[0];
            if (target is null && RestoreEngine.IsRemote(chosen.Header))
            {
                return Fail(Messages.RestoreRemoteNeedsFolder);
            }

            var request = new RestoreStartRequest
            {
                TaskId = source.TaskId,
                Folder = source.Folder,
                CredentialId = source.CredentialId,
                Password = source.Password,
                SetRunId = string.IsNullOrEmpty(chosen.Header.RunId) ? null : chosen.Header.RunId,
                Location = chosen.Location,
                Where = chosen.Where,
                DestinationId = chosen.DestinationId,
                TargetFolder = target,
                DumpsFolder = dumps,
                Existing = options.GetValueOrDefault("--existing") switch
                {
                    "overwrite" => RestoreExisting.Overwrite,
                    "overwrite-older" => RestoreExisting.OverwriteOlder,
                    _ => RestoreExisting.Skip,
                },
            };

            interrupt.Token.ThrowIfCancellationRequested();
            // Wait for the acknowledgement even during a slow rescan. The service may still start the restore after
            // the CLI stops listening, so Ctrl+C must wait for a job id and a lost reply means the outcome is unknown.
            RestoreStartResult started;
            try
            {
                started = await session.RequestAsync(PipeProtocol.Methods.RestoreStart, request,
                    IpcJsonContext.Default.RestoreStartRequest, IpcJsonContext.Default.RestoreStartResult,
                    timeout: Timeout.InfiniteTimeSpan).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or InvalidDataException)
            {
                return Fail(LinuxStrings.Format(LinuxStrings.CliRestoreStartUnknown, ex.Message));
            }

            Console.WriteLine(LinuxStrings.Format(LinuxStrings.CliRestoreStarted, chosen.Header.SetName, target ?? Messages.RestoreToOriginal));
            return await FollowRestoreAsync(session, started, interrupt.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (interrupt.IsCancellationRequested)
        {
            return 130;
        }
        finally
        {
            Console.CancelKeyPress -= Cancel;
        }
    }

    private static bool TryRestoreOptions(string[] args, bool starting, out Dictionary<string, string?> options)
    {
        options = new(StringComparer.Ordinal);
        for (int i = 1; i < args.Length; i++)
        {
            string option = args[i];
            bool allowed = option is "--task" or "--from" or "--account" or "--password-stdin"
                || starting && option is "--set" or "--destination" or "--to" or "--original" or "--dumps-to" or "--existing";
            if (!allowed || options.ContainsKey(option))
            {
                return false;
            }

            if (option is "--original" or "--password-stdin")
            {
                options.Add(option, null);
                continue;
            }

            if (++i == args.Length || string.IsNullOrWhiteSpace(args[i]) || args[i].StartsWith("--", StringComparison.Ordinal))
            {
                return false;
            }

            options.Add(option, args[i]);
        }

        bool fromFolder = options.ContainsKey("--from");
        if (options.ContainsKey("--task") == fromFolder || (options.ContainsKey("--account") && !fromFolder)
            || (options.ContainsKey("--destination") && fromFolder))
        {
            return false;
        }

        return !starting || (options.ContainsKey("--set") && options.ContainsKey("--to") != options.ContainsKey("--original")
            && options.GetValueOrDefault("--existing") is null or "skip" or "overwrite" or "overwrite-older");
    }

    private static string RestoreSelection(RestoreSetInfo set) => string.IsNullOrEmpty(set.Header.RunId) ? set.Location : set.Header.RunId;

    private static int ShowRestoreSets(RestoreListResult list)
    {
        foreach (string problem in list.Problems)
        {
            Console.Error.WriteLine(problem);
        }

        if (list.Sets.Count == 0)
        {
            Console.WriteLine(LinuxStrings.CliRestoreNoSets);
            return list.Problems.Count == 0 ? 0 : 1;
        }

        foreach (var set in list.Sets)
        {
            string destination = set.DestinationId is null ? set.Where : $"{set.DestinationId}  {set.Where}";
            Console.WriteLine(LinuxStrings.Format(LinuxStrings.CliRestoreListLine,
                RestoreSelection(set), Time(set.Header.StartedAt), set.Header.SetName, set.Storage, destination));
        }

        return 0;
    }

    private static Task<RestoreStatusResult> ReadRestoreStatusAsync(Session session) =>
        session.RequestAsync(PipeProtocol.Methods.RestoreStatus, EmptyRequest.Instance,
            IpcJsonContext.Default.EmptyRequest, IpcJsonContext.Default.RestoreStatusResult);

    private static Task<OkResult> StopRestoreAsync(Session session, string? jobId) =>
        session.RequestAsync(PipeProtocol.Methods.RestoreStop, new RestoreStopRequest { JobId = jobId },
            IpcJsonContext.Default.RestoreStopRequest, IpcJsonContext.Default.OkResult);

    private static async Task<int> FollowRestoreAsync(Session session, RestoreStartResult started, CancellationToken interrupt)
    {
        bool stopping = false;
        RestoreProgress? previous = null;
        while (true)
        {
            if (!stopping && interrupt.IsCancellationRequested)
            {
                stopping = true;
                if ((await StopRestoreAsync(session, started.JobId).ConfigureAwait(false)).Ok)
                {
                    Console.WriteLine(LinuxStrings.CliRestoreStopping);
                }
            }

            var status = await ReadRestoreStatusAsync(session).ConfigureAwait(false);
            if (status.JobId != started.JobId)
            {
                return Fail(LinuxStrings.Format(LinuxStrings.CliRestoreStatusReplaced, started.LogFile));
            }

            if (!status.Running)
            {
                return ShowRestoreStatus(status);
            }

            if (status.Progress is { } progress && (previous is null || previous.FilesDone != progress.FilesDone || previous.BytesDone != progress.BytesDone))
            {
                ShowRestoreProgress(status.SetName, progress);
                previous = progress;
            }

            await Task.Delay(RestorePollInterval, stopping ? CancellationToken.None : interrupt)
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private static int ShowRestoreStatus(RestoreStatusResult status)
    {
        if (status.Running)
        {
            if (status.Progress is { } progress)
            {
                ShowRestoreProgress(status.SetName, progress);
            }
            else
            {
                Console.WriteLine(LinuxStrings.Format(LinuxStrings.CliRestoreStarted, status.SetName, status.Target));
            }

            return 0;
        }

        if (status.Result is not { } result)
        {
            return Done(Messages.RestoreNotRunning);
        }

        Console.WriteLine(LinuxStrings.Format(LinuxStrings.CliRestoreResult, Words.Status(result.Status),
            result.FilesRestored, result.FilesSkipped, result.FilesFailed, result.Warnings));
        if (result.ErrorMessage is { } error)
        {
            Console.Error.WriteLine(error);
        }

        if (status.LogFile is { } log)
        {
            Console.WriteLine(LinuxStrings.Format(LinuxStrings.CliRestoreLog, log));
        }

        return result.Status switch
        {
            RunStatuses.Success => 0,
            RunStatuses.Aborted => 130,
            _ => 1,
        };
    }

    private static void ShowRestoreProgress(string? setName, RestoreProgress progress) =>
        Console.WriteLine(LinuxStrings.Format(LinuxStrings.CliRestoreProgress,
            setName, progress.FilesDone, progress.FilesTotal, progress.Percent, Sizes.Format(progress.BytesDone)));
}
