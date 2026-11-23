using Oadm.Plugins.Schedules.Engine;

namespace Oadm.Plugins.Schedules.Tests.Server;

/// <summary>The starts, window ends and gaps of the four recurrences (docs/spec/schedules.md "A schedule", rule 6).</summary>
public sealed class TimesTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    // Monday 2026-11-23 10:00 UTC.
    private static readonly DateTimeOffset Monday10 = new(2026, 11, 23, 10, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset At(int day, int hour, int minute = 0) => new(2026, 11, day, hour, minute, 0, TimeSpan.Zero);

    [Fact]
    public void Once_runs_at_its_date_and_time_and_never_again()
    {
        var when = new ScheduleWhen { Recurrence = Recurrence.Once, Date = new DateOnly(2026, 11, 24), Time = new TimeOnly(2, 30) };
        Assert.Equal(At(24, 2, 30), ScheduleTimes.NextAfter(when, Monday10, Utc));
        Assert.Null(ScheduleTimes.NextAfter(when, At(24, 2, 30), Utc));
        Assert.Equal(At(24, 2, 30), ScheduleTimes.LatestDue(when, Monday10, At(25, 0), Utc));
        Assert.Null(ScheduleTimes.LatestDue(when, At(24, 2, 30), At(26, 0), Utc));
        Assert.Equal(ScheduleTimes.DateInPast, ScheduleTimes.Problem(when with { Date = new DateOnly(2026, 11, 22) }, Monday10, Utc));
        Assert.Null(ScheduleTimes.Problem(when with { Date = new DateOnly(2026, 11, 22) }, Monday10, Utc, checkPast: false));
        Assert.Equal(ScheduleTimes.ChooseDate, ScheduleTimes.Problem(when with { Date = null }, Monday10, Utc));
        Assert.Null(ScheduleTimes.ShortestGap(when));
    }

    [Fact]
    public void Daily_runs_today_when_the_time_is_ahead_else_tomorrow()
    {
        var when = new ScheduleWhen { Recurrence = Recurrence.Daily, Time = new TimeOnly(23, 0) };
        Assert.Equal(At(23, 23), ScheduleTimes.NextAfter(when, Monday10, Utc));
        Assert.Equal(At(24, 23), ScheduleTimes.NextAfter(when, At(23, 23), Utc));
        Assert.Equal([At(23, 23), At(24, 23), At(25, 23)], ScheduleTimes.Next(when, Monday10, Utc, 3));
        Assert.Equal(TimeSpan.FromDays(1), ScheduleTimes.ShortestGap(when));

        // Due: the latest start after the one last handled, never an older one.
        Assert.Equal(At(25, 23), ScheduleTimes.LatestDue(when, Monday10, At(26, 1), Utc));
        Assert.Null(ScheduleTimes.LatestDue(when, At(23, 23), At(23, 23, 30), Utc));
    }

    [Fact]
    public void Weekly_runs_on_its_days_only()
    {
        var when = new ScheduleWhen { Recurrence = Recurrence.Weekly, Days = [DayOfWeek.Friday, DayOfWeek.Monday], Time = new TimeOnly(1, 0) };
        Assert.Equal(At(27, 1), ScheduleTimes.NextAfter(when, Monday10, Utc));
        Assert.Equal(At(30, 1), ScheduleTimes.NextAfter(when, At(27, 1), Utc));
        Assert.Equal(TimeSpan.FromDays(3), ScheduleTimes.ShortestGap(when));
        Assert.Equal(ScheduleTimes.ChooseDays, ScheduleTimes.Problem(when with { Days = [] }, Monday10, Utc));
        Assert.Equal("Mondays, Fridays 01:00", ScheduleTimes.Describe(when));
    }

    [Fact]
    public void Monthly_runs_on_its_day_from_1_to_28()
    {
        var when = new ScheduleWhen { Recurrence = Recurrence.Monthly, DayOfMonth = 5, Time = new TimeOnly(3, 0) };
        Assert.Equal(new DateTimeOffset(2026, 12, 5, 3, 0, 0, TimeSpan.Zero), ScheduleTimes.NextAfter(when, Monday10, Utc));
        Assert.Equal(new DateTimeOffset(2027, 1, 5, 3, 0, 0, TimeSpan.Zero), ScheduleTimes.NextAfter(when, new DateTimeOffset(2026, 12, 5, 3, 0, 0, TimeSpan.Zero), Utc));
        Assert.Equal(ScheduleTimes.ChooseDayOfMonth, ScheduleTimes.Problem(when with { DayOfMonth = 29 }, Monday10, Utc));
        Assert.Equal(TimeSpan.FromDays(28), ScheduleTimes.ShortestGap(when));
    }

    [Fact]
    public void A_window_end_before_the_start_time_is_the_next_day()
    {
        var when = new ScheduleWhen { Recurrence = Recurrence.Daily, Time = new TimeOnly(23, 0), WindowEnd = new TimeOnly(5, 0) };
        Assert.Equal(At(24, 5), ScheduleTimes.WindowEnd(when, At(23, 23), Utc));
        Assert.Equal(At(23, 23, 30), ScheduleTimes.WindowEnd(when with { Time = new TimeOnly(22, 0), WindowEnd = new TimeOnly(23, 30) }, At(23, 22), Utc));
        Assert.Null(ScheduleTimes.WindowEnd(when with { WindowEnd = null }, At(23, 23), Utc));
        Assert.Equal(ScheduleTimes.WindowLikeStart, ScheduleTimes.Problem(when with { WindowEnd = new TimeOnly(23, 0) }, Monday10, Utc));
    }

    [Fact]
    public void Times_are_local_times_of_the_server_also_over_a_clock_change()
    {
        var vienna = TimeZoneInfo.CreateCustomTimeZone(
            "Test/Vienna",
            TimeSpan.FromHours(1),
            "Vienna",
            "CET",
            "CEST",
            [TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
                DateTime.MinValue.Date,
                DateTime.MaxValue.Date,
                TimeSpan.FromHours(1),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 5, DayOfWeek.Sunday),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 3, 0, 0), 10, 5, DayOfWeek.Sunday))]);
        var when = new ScheduleWhen { Recurrence = Recurrence.Daily, Time = new TimeOnly(2, 30) };

        // 2027-03-28 02:30 does not exist (clocks go forward at 02:00): the start moves to 03:00 local, 01:00 UTC.
        var spring = ScheduleTimes.NextAfter(when, new DateTimeOffset(2027, 3, 27, 12, 0, 0, TimeSpan.Zero), vienna);
        Assert.Equal(new DateTimeOffset(2027, 3, 28, 1, 0, 0, TimeSpan.Zero), spring!.Value.ToUniversalTime());

        // 2026-11-24 02:30 in winter is 01:30 UTC.
        Assert.Equal(new DateTimeOffset(2026, 11, 24, 1, 30, 0, TimeSpan.Zero), ScheduleTimes.NextAfter(when, Monday10, vienna)!.Value.ToUniversalTime());
    }
}
