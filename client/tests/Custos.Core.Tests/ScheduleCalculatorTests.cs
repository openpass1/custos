using Custos.Core.Model;
using Custos.Core.Scheduling;

namespace Custos.Core.Tests;

public sealed class ScheduleCalculatorTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    /// <summary>Central European time with the EU rules, built here so that the test does not depend on the system.</summary>
    private static readonly TimeZoneInfo Cet = TimeZoneInfo.CreateCustomTimeZone(
        "Test CET", TimeSpan.FromHours(1), "Test CET", "CET", "CEST",
        [
            TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
                new DateTime(2000, 1, 1), new DateTime(2099, 12, 31), TimeSpan.FromHours(1),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 5, DayOfWeek.Sunday),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 3, 0, 0), 10, 5, DayOfWeek.Sunday)),
        ]);

    [Fact]
    public void Daily_runs_at_each_time_of_the_day()
    {
        var schedule = new ScheduleOptions { Type = ScheduleTypes.Daily, Times = ["14:30", "02:00"] };

        Assert.Equal(At(2026, 11, 4, 14, 30), ScheduleCalculator.Next(schedule, At(2026, 11, 4, 10, 0), Utc));
        Assert.Equal(At(2026, 11, 5, 2, 0), ScheduleCalculator.Next(schedule, At(2026, 11, 4, 14, 30), Utc));
    }

    [Fact]
    public void Daily_with_weekdays_skips_the_other_days()
    {
        var schedule = new ScheduleOptions { Type = ScheduleTypes.Daily, Times = ["02:00"], DaysOfWeek = [1, 2, 3, 4, 5] };

        // 2026-11-06 is a Friday.
        Assert.Equal(At(2026, 11, 9, 2, 0), ScheduleCalculator.Next(schedule, At(2026, 11, 6, 3, 0), Utc));
    }

    [Fact]
    public void Weekly_needs_at_least_one_day()
    {
        var schedule = new ScheduleOptions { Type = ScheduleTypes.Weekly, Times = ["02:00"], DaysOfWeek = [] };

        Assert.False(ScheduleCalculator.HasSlots(schedule));
        Assert.Null(ScheduleCalculator.Next(schedule, At(2026, 11, 4, 0, 0), Utc));

        schedule.DaysOfWeek = [7];
        Assert.Equal(At(2026, 11, 8, 2, 0), ScheduleCalculator.Next(schedule, At(2026, 11, 4, 0, 0), Utc));
    }

    [Fact]
    public void Monthly_handles_the_last_day_and_short_months()
    {
        var last = new ScheduleOptions { Type = ScheduleTypes.Monthly, Times = ["23:00"], DaysOfMonth = [-1] };
        var on31 = new ScheduleOptions { Type = ScheduleTypes.Monthly, Times = ["23:00"], DaysOfMonth = [31] };

        Assert.Equal(At(2026, 2, 28, 23, 0), ScheduleCalculator.Next(last, At(2026, 2, 10, 0, 0), Utc));

        // A month without the day runs on its last day: the 31st is the last day of every month.
        Assert.Equal(At(2026, 4, 30, 23, 0), ScheduleCalculator.Next(on31, At(2026, 4, 1, 0, 0), Utc));
        Assert.Equal(At(2026, 5, 31, 23, 0), ScheduleCalculator.Next(on31, At(2026, 4, 30, 23, 0), Utc));
    }

    [Theory]
    [InlineData(29, 2027, 28)]
    [InlineData(29, 2028, 29)]
    [InlineData(30, 2027, 28)]
    [InlineData(30, 2028, 29)]
    public void Monthly_on_the_29th_or_30th_does_not_skip_February(int day, int year, int februaryDay)
    {
        var schedule = new ScheduleOptions { Type = ScheduleTypes.Monthly, Times = ["02:00"], DaysOfMonth = [day] };

        var runs = ScheduleCalculator.Upcoming(schedule, At(year, 1, 1, 0, 0), Utc, 12);

        Assert.Equal(Enumerable.Range(1, 12), runs.Select(r => r.Month));
        Assert.Equal(At(year, 1, day, 2, 0), runs[0]);
        Assert.Equal(At(year, 2, februaryDay, 2, 0), runs[1]);
        Assert.Equal(At(year, 3, day, 2, 0), runs[2]);
    }

    [Fact]
    public void Monthly_runs_once_on_a_day_chosen_twice()
    {
        // 30 and the last day: one run on 28 February and on 30 April, two runs in March.
        var schedule = new ScheduleOptions { Type = ScheduleTypes.Monthly, Times = ["02:00"], DaysOfMonth = [30, -1] };

        var runs = ScheduleCalculator.Upcoming(schedule, At(2027, 2, 1, 0, 0), Utc, 4);

        Assert.Equal([At(2027, 2, 28, 2, 0), At(2027, 3, 30, 2, 0), At(2027, 3, 31, 2, 0), At(2027, 4, 30, 2, 0)], runs);
    }

    [Theory]
    [InlineData(15, 2027, 2, 15, true)]
    [InlineData(15, 2027, 2, 16, false)]
    [InlineData(31, 2027, 4, 30, true)]
    [InlineData(31, 2027, 5, 30, false)]
    [InlineData(30, 2027, 2, 28, true)]
    [InlineData(29, 2028, 2, 28, false)]
    [InlineData(-1, 2028, 2, 29, true)]
    [InlineData(0, 2027, 2, 1, false)]
    [InlineData(32, 2027, 1, 31, false)]
    public void A_day_of_the_month_falls_on_the_last_day_of_a_shorter_month(int monthDay, int year, int month, int day, bool expected) =>
        Assert.Equal(expected, ScheduleCalculator.IsMonthDay(monthDay, new DateTime(year, month, day)));

    [Fact]
    public void Interval_counts_from_local_midnight()
    {
        var schedule = new ScheduleOptions { Type = ScheduleTypes.Interval, IntervalMinutes = 90 };

        Assert.Equal(At(2026, 11, 4, 1, 30), ScheduleCalculator.Next(schedule, At(2026, 11, 4, 0, 0), Utc));
        Assert.Equal(At(2026, 11, 5, 0, 0), ScheduleCalculator.Next(schedule, At(2026, 11, 4, 22, 30), Utc));
        Assert.Equal(16, ScheduleCalculator.Upcoming(schedule, At(2026, 11, 3, 23, 59), Utc, 100).Count(s => s.Day == 4));
    }

    [Fact]
    public void Manual_has_no_slots()
    {
        var schedule = new ScheduleOptions { Type = ScheduleTypes.Manual };

        Assert.False(ScheduleCalculator.HasSlots(schedule));
        Assert.Null(ScheduleCalculator.Next(schedule, At(2026, 11, 4, 0, 0), Utc));
        Assert.Null(ScheduleCalculator.Last(schedule, At(2026, 11, 4, 0, 0), At(2026, 11, 1, 0, 0), Utc));
    }

    [Fact]
    public void Times_are_local_to_the_agent()
    {
        var schedule = new ScheduleOptions { Type = ScheduleTypes.Daily, Times = ["22:00"] };

        // Winter: CET = UTC+1; summer: CEST = UTC+2.
        Assert.Equal(At(2026, 1, 10, 21, 0), ScheduleCalculator.Next(schedule, At(2026, 1, 10, 12, 0), Cet));
        Assert.Equal(At(2026, 7, 10, 20, 0), ScheduleCalculator.Next(schedule, At(2026, 7, 10, 12, 0), Cet));
    }

    [Fact]
    public void A_time_skipped_by_the_clock_change_runs_right_after_it()
    {
        // On 2026-03-29 the clocks go from 02:00 straight to 03:00.
        var schedule = new ScheduleOptions { Type = ScheduleTypes.Daily, Times = ["02:30"] };

        Assert.Equal(At(2026, 3, 29, 1, 0), ScheduleCalculator.Next(schedule, At(2026, 3, 28, 12, 0), Cet));
    }

    [Fact]
    public void A_time_that_happens_twice_runs_once_the_first_time()
    {
        // On 2026-10-25 the clocks go from 03:00 back to 02:00: 02:30 happens at 00:30 and at 01:30 UTC.
        var schedule = new ScheduleOptions { Type = ScheduleTypes.Daily, Times = ["02:30"] };

        var first = ScheduleCalculator.Next(schedule, At(2026, 10, 24, 12, 0), Cet);
        Assert.Equal(At(2026, 10, 25, 0, 30), first);
        Assert.Equal(At(2026, 10, 26, 1, 30), ScheduleCalculator.Next(schedule, first!.Value, Cet));
    }

    [Fact]
    public void Last_finds_the_latest_missed_slot()
    {
        var schedule = new ScheduleOptions { Type = ScheduleTypes.Daily, Times = ["02:00", "14:00"] };

        Assert.Equal(At(2026, 11, 4, 2, 0), ScheduleCalculator.Last(schedule, At(2026, 11, 4, 10, 0), At(2026, 11, 1, 0, 0), Utc));
        Assert.Equal(At(2026, 11, 4, 2, 0), ScheduleCalculator.Last(schedule, At(2026, 11, 4, 2, 0), At(2026, 11, 1, 0, 0), Utc));
        Assert.Null(ScheduleCalculator.Last(schedule, At(2026, 11, 4, 10, 0), At(2026, 11, 4, 2, 0), Utc));
    }

    [Theory]
    [InlineData("07:05", 7, 5)]
    [InlineData("7:05", 7, 5)]
    [InlineData(" 23:59 ", 23, 59)]
    public void Times_are_parsed(string text, int hours, int minutes)
    {
        Assert.Equal(new TimeSpan(hours, minutes, 0), ScheduleCalculator.ParseTime(text));
    }

    [Theory]
    [InlineData("24:00")]
    [InlineData("12:5")]
    [InlineData("12:60")]
    [InlineData("noon")]
    [InlineData("-1:00")]
    [InlineData(null)]
    public void Bad_times_are_rejected(string? text)
    {
        Assert.Null(ScheduleCalculator.ParseTime(text));
    }

    private static DateTimeOffset At(int year, int month, int day, int hour, int minute) =>
        new(year, month, day, hour, minute, 0, TimeSpan.Zero);
}
