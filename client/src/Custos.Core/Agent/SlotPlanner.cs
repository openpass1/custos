using Custos.Core.Model;
using Custos.Core.Scheduling;

namespace Custos.Core.Agent;

public enum SlotDecision
{
    /// <summary>No slot is due.</summary>
    None,

    /// <summary>A slot is due now: run with trigger <c>schedule</c>.</summary>
    Run,

    /// <summary>A slot was missed while the computer was off or asleep: run once with trigger <c>catch_up</c>.</summary>
    CatchUp,

    /// <summary>A slot was missed and is not made up (catch-up off, or longer ago than <see cref="SlotPlanner.CatchUpWindow"/>).</summary>
    Missed,
}

/// <param name="Slot">The slot the decision is about; it becomes the task's last slot.</param>
public readonly record struct SlotCheck(SlotDecision Decision, DateTimeOffset? Slot);

/// <summary>
/// Decides what to do about the schedule of a task (section 6.7). The service remembers per task the last slot it has
/// dealt with; every slot after it up to now is due. Only the latest of them counts: several missed slots give one run.
/// </summary>
public static class SlotPlanner
{
    /// <summary>A slot this late still counts as on time (the service checks the schedule every few seconds).</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(5);

    /// <summary>Only a slot missed within this time is made up (section 6.7).</summary>
    public static readonly TimeSpan CatchUpWindow = TimeSpan.FromDays(7);

    /// <param name="lastSlot">The last slot dealt with. Null: never, which the caller should treat as "now".</param>
    public static SlotCheck Check(ScheduleOptions schedule, DateTimeOffset? lastSlot, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        if (lastSlot is not { } after || !ScheduleCalculator.HasSlots(schedule))
        {
            return new SlotCheck(SlotDecision.None, null);
        }

        if (ScheduleCalculator.Last(schedule, now, after, zone) is not { } due)
        {
            return new SlotCheck(SlotDecision.None, null);
        }

        var late = now - due;
        if (late <= Grace)
        {
            return new SlotCheck(SlotDecision.Run, due);
        }

        return schedule.CatchUp && late <= CatchUpWindow
            ? new SlotCheck(SlotDecision.CatchUp, due)
            : new SlotCheck(SlotDecision.Missed, due);
    }

    /// <summary>The next slot to show as "next run": after now and after the last slot dealt with.</summary>
    public static DateTimeOffset? NextRun(TaskDefinition task, DateTimeOffset? lastSlot, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (!task.Enabled)
        {
            return null;
        }

        var from = lastSlot is { } last && last > now ? last : now;
        return ScheduleCalculator.Next(task.Schedule, from, zone);
    }
}
