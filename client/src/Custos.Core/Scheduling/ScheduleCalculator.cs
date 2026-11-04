using System.Globalization;
using Custos.Core.Model;

namespace Custos.Core.Scheduling;

/// <summary>
/// Computes the slots of a schedule (section 6.7, A.9): the moments a task is due. Schedule times are local times of
/// the agent; slots are returned in UTC. A time skipped by a daylight saving change runs right after the change, a
/// time that happens twice runs the first time.
/// </summary>
public static class ScheduleCalculator
{
    /// <summary>How far ahead and back slots are searched. A monthly schedule on the 31st still finds one.</summary>
    private const int HorizonDays = 400;

    private const int MinutesPerDay = 24 * 60;

    /// <summary>The first slot strictly after <paramref name="after"/>, or null if the schedule has none.</summary>
    public static DateTimeOffset? Next(ScheduleOptions schedule, DateTimeOffset after, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        ArgumentNullException.ThrowIfNull(zone);
        if (!HasSlots(schedule))
        {
            return null;
        }

        var day = TimeZoneInfo.ConvertTime(after, zone).Date.AddDays(-1);
        for (int i = 0; i <= HorizonDays; i++, day = day.AddDays(1))
        {
            foreach (var slot in SlotsOfDay(schedule, day, zone))
            {
                if (slot > after)
                {
                    return slot;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The latest slot at or before <paramref name="notAfter"/> and strictly after <paramref name="after"/>,
    /// or null if there is none in between.
    /// </summary>
    public static DateTimeOffset? Last(ScheduleOptions schedule, DateTimeOffset notAfter, DateTimeOffset after, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        ArgumentNullException.ThrowIfNull(zone);
        if (!HasSlots(schedule) || notAfter <= after)
        {
            return null;
        }

        var day = TimeZoneInfo.ConvertTime(notAfter, zone).Date.AddDays(1);
        var firstDay = TimeZoneInfo.ConvertTime(after, zone).Date.AddDays(-1);
        for (int i = 0; i <= HorizonDays && day >= firstDay; i++, day = day.AddDays(-1))
        {
            DateTimeOffset? best = null;
            foreach (var slot in SlotsOfDay(schedule, day, zone))
            {
                if (slot <= notAfter && slot > after)
                {
                    best = slot;
                }
            }

            if (best is not null)
            {
                return best;
            }
        }

        return null;
    }

    /// <summary>The next <paramref name="count"/> slots after <paramref name="after"/>, for previews.</summary>
    public static IReadOnlyList<DateTimeOffset> Upcoming(ScheduleOptions schedule, DateTimeOffset after, TimeZoneInfo zone, int count)
    {
        var result = new List<DateTimeOffset>(count);
        var from = after;
        while (result.Count < count && Next(schedule, from, zone) is { } slot)
        {
            result.Add(slot);
            from = slot;
        }

        return result;
    }

    /// <summary>False for manual schedules and for schedules that can never fire.</summary>
    public static bool HasSlots(ScheduleOptions schedule)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        return schedule.Type switch
        {
            ScheduleTypes.Daily => ParseTimes(schedule.Times).Count > 0 && Weekdays(schedule).Count > 0,
            ScheduleTypes.Weekly => ParseTimes(schedule.Times).Count > 0 && schedule.DaysOfWeek.Any(IsWeekday),
            ScheduleTypes.Monthly => ParseTimes(schedule.Times).Count > 0 && schedule.DaysOfMonth.Any(IsMonthDay),
            ScheduleTypes.Interval => schedule.IntervalMinutes is > 0 and <= MinutesPerDay && Weekdays(schedule).Count > 0,
            _ => false,
        };
    }

    /// <summary>Parses "HH:mm" (also "H:mm"). Returns null for anything else.</summary>
    public static TimeSpan? ParseTime(string? text)
    {
        if (text is null)
        {
            return null;
        }

        string[] parts = text.Trim().Split(':');
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int hours)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int minutes)
            || parts[1].Length != 2 || hours > 23 || minutes > 59)
        {
            return null;
        }

        return new TimeSpan(hours, minutes, 0);
    }

    /// <summary>ISO weekday of a date: 1 = Monday … 7 = Sunday.</summary>
    public static int IsoWeekday(DateTime date) => ((int)date.DayOfWeek + 6) % 7 + 1;

    /// <summary>Converts a local wall-clock time of <paramref name="zone"/> to UTC (see the class remarks for DST).</summary>
    public static DateTimeOffset ToUtc(DateTime local, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local))
        {
            // Inside the gap of a "spring forward": run right after the clocks moved.
            var shifted = local;
            for (int i = 0; i < 8 && zone.IsInvalidTime(shifted); i++)
            {
                shifted = shifted.AddMinutes(30);
            }

            local = shifted;
        }

        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    private static IEnumerable<DateTimeOffset> SlotsOfDay(ScheduleOptions schedule, DateTime day, TimeZoneInfo zone)
    {
        IEnumerable<TimeSpan> times;
        switch (schedule.Type)
        {
            case ScheduleTypes.Daily:
            case ScheduleTypes.Weekly:
                if (!Weekdays(schedule).Contains(IsoWeekday(day)))
                {
                    yield break;
                }

                times = ParseTimes(schedule.Times);
                break;

            case ScheduleTypes.Monthly:
                if (!MatchesMonthDay(schedule.DaysOfMonth, day))
                {
                    yield break;
                }

                times = ParseTimes(schedule.Times);
                break;

            case ScheduleTypes.Interval:
                if (!Weekdays(schedule).Contains(IsoWeekday(day)))
                {
                    yield break;
                }

                // Counted from local midnight every day, so "every 60 minutes" runs on the hour.
                int step = schedule.IntervalMinutes;
                times = Enumerable.Range(0, (MinutesPerDay + step - 1) / step).Select(k => TimeSpan.FromMinutes(k * step));
                break;

            default:
                yield break;
        }

        DateTimeOffset? previous = null;
        foreach (var time in times)
        {
            var slot = ToUtc(day + time, zone);
            if (previous is null || slot > previous)
            {
                yield return slot;
                previous = slot;
            }
        }
    }

    private static List<TimeSpan> ParseTimes(IEnumerable<string> times) =>
        [.. times.Select(ParseTime).OfType<TimeSpan>().Distinct().Order()];

    /// <summary>Days of the week a daily, weekly or interval schedule runs on. Empty for daily means every day.</summary>
    private static HashSet<int> Weekdays(ScheduleOptions schedule)
    {
        var days = schedule.DaysOfWeek.Where(IsWeekday).ToHashSet();
        return days.Count == 0 && schedule.Type != ScheduleTypes.Weekly ? [1, 2, 3, 4, 5, 6, 7] : days;
    }

    private static bool MatchesMonthDay(List<int> daysOfMonth, DateTime day)
    {
        int last = DateTime.DaysInMonth(day.Year, day.Month);
        return daysOfMonth.Any(d => d == day.Day || (d == -1 && day.Day == last));
    }

    private static bool IsWeekday(int day) => day is >= 1 and <= 7;

    private static bool IsMonthDay(int day) => (day >= 1 && day <= 31) || day == -1;
}
