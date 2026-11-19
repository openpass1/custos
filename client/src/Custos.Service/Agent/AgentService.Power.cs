using Custos.Core;
using Custos.Core.Backup;
using Custos.Core.Ipc;
using Custos.Core.Model;
using Custos.Core.Text;
using Custos.Service.Platform;

namespace Custos.Service.Agent;

/// <summary>
/// Shutdown, restart, sleep or hibernation after a run (section 6.9). The action waits until the queue is empty,
/// then counts down 60 seconds while every session shows a window with a cancel button. A run that starts in the
/// meantime puts the countdown off until the queue is empty again.
/// </summary>
internal sealed partial class AgentService
{
    private static readonly TimeSpan PowerDelay = TimeSpan.FromSeconds(60);

    /// <summary>The action asked for by the last run that had one; the latest wins.</summary>
    private (string Action, string TaskName)? _powerPending;

    /// <summary>The countdown in progress, shown to every session; null while runs are still waiting.</summary>
    private PowerCountdown? _powerCountdown;

    public OkResult CancelPower(string user)
    {
        (string Action, string TaskName) pending;
        lock (_lock)
        {
            if (_powerPending is not { } value)
            {
                return new OkResult { Ok = false, Message = Messages.PowerNothingToCancel };
            }

            pending = value;
            _powerPending = null;
            _powerCountdown = null;
        }

        string action = Words.PowerAction(pending.Action);
        Log(LogLineLevels.Info, Messages.Format(Messages.PowerCancelled, action, user));
        ServiceLog.PowerCancelled(logger, pending.TaskName, pending.Action, user);
        PublishSnapshot();
        return OkResult.Instance;
    }

    /// <summary>Remembers the action of a task after its run. A stopped run asks for nothing.</summary>
    private void NotePowerAction(TaskDefinition task, BackupResult result)
    {
        string action = task.Events.After;
        if (action is not (AfterActions.Shutdown or AfterActions.Restart or AfterActions.Sleep or AfterActions.Hibernate)
            || result.Status == RunStatuses.Aborted
            || _stopping.IsCancellationRequested)
        {
            return;
        }

        lock (_lock)
        {
            _powerPending = (action, task.Name);
        }
    }

    /// <summary>
    /// Called by the idle runner with an empty queue: starts the countdown if an action waits, and says how long
    /// to wait for the next run before <see cref="FirePowerIfDue"/>.
    /// </summary>
    private TimeSpan PowerWait()
    {
        PowerCountdown? started = null;
        TimeSpan wait;
        lock (_lock)
        {
            if (_powerPending is not { } pending)
            {
                return Timeout.InfiniteTimeSpan;
            }

            if (_powerCountdown is null)
            {
                _powerCountdown = started = new PowerCountdown
                {
                    Action = pending.Action,
                    TaskName = pending.TaskName,
                    Deadline = DateTimeOffset.UtcNow + PowerDelay,
                };
            }

            wait = _powerCountdown.Deadline - DateTimeOffset.UtcNow;
        }

        if (started is not null)
        {
            Log(LogLineLevels.Warning, Messages.Format(Messages.PowerCountdown, started.TaskName,
                Words.PowerAction(started.Action), (int)PowerDelay.TotalSeconds));
            PublishSnapshot();
        }

        // Past the deadline but held up (a restore, an update of 7z.dll): look again twice a second.
        return wait <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(500) : wait;
    }

    /// <summary>A run is about to start: the countdown stops and starts again once the queue is empty.</summary>
    private void PostponePower()
    {
        lock (_lock)
        {
            if (_powerCountdown is null)
            {
                return;
            }

            _powerCountdown = null;
        }

        PublishSnapshot();
    }

    /// <summary>The runner waited without a new run: if the countdown is over, the action happens now.</summary>
    private void FirePowerIfDue()
    {
        // Not in the middle of a restore or of an update of 7z.dll: it happens right after.
        lock (_restoreLock)
        {
            if (_restore is { Running: true } || _sevenZipUpdating)
            {
                return;
            }
        }

        PowerCountdown due;
        lock (_lock)
        {
            if (_powerCountdown is not { } countdown || countdown.Deadline > DateTimeOffset.UtcNow
                || _current is not null || _queue.Count > 0 || _stopping.IsCancellationRequested)
            {
                return;
            }

            due = countdown;
            _powerCountdown = null;
            _powerPending = null;
        }

        string action = Words.PowerAction(due.Action);
        Log(LogLineLevels.Warning, Messages.Format(Messages.PowerNow, action, due.TaskName));
        ServiceLog.PowerAction(logger, due.TaskName, due.Action);
        PublishSnapshot();
        try
        {
            HostPlatform.PowerAction(due.Action);
        }
        catch (BackupException ex)
        {
            Log(LogLineLevels.Error, ex.Message);
            ServiceLog.PowerFailed(logger, due.TaskName, due.Action, ex.Message);
        }
    }
}
