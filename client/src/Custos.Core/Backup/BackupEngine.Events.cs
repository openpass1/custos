using Custos.Core.Model;

namespace Custos.Core.Backup;

/// <summary>
/// Events before and after a run (section 6.9): programs and scripts with their arguments and a timeout, checked by
/// their exit code, and Windows services stopped or started. What they print goes to the run log. A failed event
/// before stops the run if the task says so, otherwise it is a warning; a failed event after is a warning. The events
/// after run whatever happened once the events before were started, so that a service stopped before is started
/// again even after an error.
/// </summary>
public sealed partial class BackupEngine
{
    private const int MaxOutputLines = 200;

    /// <exception cref="BackupException">An event failed and the task stops then.</exception>
    private void RunPreEvents(RunContext run)
    {
        var events = run.Task.Events;
        run.EventsStarted = true;
        if (events.Pre.Count == 0)
        {
            return;
        }

        Phase(BackupPhases.PreEvents);
        foreach (var item in events.Pre)
        {
            run.Pause.WaitWhilePaused(run.Cancellation);
            if (RunEvent(item, run.Cancellation) is not { } problem)
            {
                continue;
            }

            if (events.AbortOnPreFailure)
            {
                throw new BackupException(Messages.Format(Messages.PreEventFailedAbort, problem));
            }

            run.Result.Warnings++;
            _log.Write(RunLogLevel.Warning, Messages.Format(Messages.PreEventFailed, problem));
        }
    }

    /// <summary>The events after; never throws. They are not stopped with the run, only by their own timeouts.</summary>
    private void RunPostEvents(RunContext run)
    {
        if (!run.EventsStarted || run.Task.Events.Post.Count == 0)
        {
            return;
        }

        Phase(BackupPhases.PostEvents);
        foreach (var item in run.Task.Events.Post)
        {
            try
            {
                if (RunEvent(item, CancellationToken.None) is { } problem)
                {
                    run.Result.Warnings++;
                    _log.Write(RunLogLevel.Warning, Messages.Format(Messages.PostEventFailed, problem));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                run.Result.Warnings++;
                _log.Write(RunLogLevel.Warning, Messages.Format(Messages.PostEventFailed, ex.Message));
            }
        }
    }

    /// <returns>Null if the event went well, or what went wrong, in words.</returns>
    private string? RunEvent(TaskEvent item, CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromSeconds(Math.Clamp(item.TimeoutS, 1, 7 * 24 * 3600));
        try
        {
            switch (item.Kind)
            {
                case TaskEventKinds.Run:
                    return RunProgram(item, timeout, cancellationToken);
                case TaskEventKinds.ServiceStop or TaskEventKinds.ServiceStart:
                    bool start = item.Kind == TaskEventKinds.ServiceStart;
                    string name = (item.Service ?? string.Empty).Trim();
                    _log.Write(RunLogLevel.Info, Messages.Format(start ? Messages.EventServiceStarting : Messages.EventServiceStopping, name));
                    _platform.ControlService(name, start, timeout, cancellationToken);
                    _log.Write(RunLogLevel.Info, Messages.Format(start ? Messages.EventServiceStarted : Messages.EventServiceStopped, name));
                    return null;
                default:
                    return Messages.Format(Messages.EventKindUnknown, item.Kind);
            }
        }
        catch (BackupException ex)
        {
            return ex.Message;
        }
    }

    private string? RunProgram(TaskEvent item, TimeSpan timeout, CancellationToken cancellationToken)
    {
        string command = Environment.ExpandEnvironmentVariables((item.Command ?? string.Empty).Trim().Trim('"'));
        if (command.Length == 0)
        {
            return Messages.EventNoCommand;
        }

        string arguments = Environment.ExpandEnvironmentVariables(item.Args ?? string.Empty);
        _log.Write(RunLogLevel.Info, Messages.Format(Messages.EventRunning, command, arguments).TrimEnd());
        int lines = 0;
        int code = _platform.RunProgram(command, arguments, timeout, line =>
        {
            if (++lines <= MaxOutputLines)
            {
                _log.Write(RunLogLevel.Info, Messages.Format(Messages.EventOutput, line));
            }
        }, cancellationToken);
        if (lines > MaxOutputLines)
        {
            _log.Write(RunLogLevel.Info, Messages.Format(Messages.EventOutputCut, lines - MaxOutputLines));
        }

        if (code != 0)
        {
            return Messages.Format(Messages.EventExitCode, command, code);
        }

        _log.Write(RunLogLevel.Info, Messages.Format(Messages.EventDone, command));
        return null;
    }
}
