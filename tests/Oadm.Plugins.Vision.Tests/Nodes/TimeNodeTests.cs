using System.Text.Json.Nodes;

using Oadm.Plugins.Vision.Model;

namespace Oadm.Plugins.Vision.Tests.Nodes;

/// <summary>Timer and Time window from frame times only (no clock), in UTC and in a zone with daylight saving.</summary>
public sealed class TimeNodeTests
{
    /// <summary>Central European time: +1, +2 from the last Sunday of March 02:00 to the last Sunday of October 03:00.</summary>
    private static readonly TimeZoneInfo Cet = TimeZoneInfo.CreateCustomTimeZone("Test CET", TimeSpan.FromHours(1), "Test CET", "Test CET", "Test CEST",
    [
        TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            DateTime.MinValue.Date, DateTime.MaxValue.Date, TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 5, DayOfWeek.Sunday),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 3, 0, 0), 10, 5, DayOfWeek.Sunday)),
    ]);

    /// <summary>A pipeline with one Timer (shown) and the camera; frames at the given instants.</summary>
    private static (EvaluatorHarness H, string Timer) TimerOf(JsonObject settings, TimeZoneInfo? zone = null)
    {
        var b = new PipelineBuilder();
        b.Add(NodeKinds.Camera);
        var timer = b.Add(NodeKinds.Timer, settings: settings, show: true);
        return (new EvaluatorHarness(b.Build(), zone: zone), timer);
    }

    private static double At(EvaluatorHarness h, DateTimeOffset when) => (when - EvaluatorHarness.T0).TotalSeconds;

    /// <summary>The instants of the frames that pulsed, frames every <paramref name="step"/> from <paramref name="from"/> to <paramref name="to"/>.</summary>
    private static async Task<List<DateTimeOffset>> PulsesAsync(EvaluatorHarness h, string timer, DateTimeOffset from, DateTimeOffset to, TimeSpan step)
    {
        var pulses = new List<DateTimeOffset>();
        for (var t = from; t <= to; t += step)
        {
            var r = await h.FrameAsync(At(h, t));
            if (r.Nodes[timer].On == true)
            {
                pulses.Add(t);
            }
        }

        return pulses;
    }

    private static DateTimeOffset Utc(int month, int day, int hour, int minute, int second = 0) => new(2026, month, day, hour, minute, second, TimeSpan.Zero);

    [Fact]
    public async Task A_daily_timer_pulses_at_the_first_frame_at_or_after_its_time_across_midnight()
    {
        var (h, timer) = TimerOf(new JsonObject { [NodeKinds.EveryKey] = NodeKinds.EveryDay, [NodeKinds.AtKey] = "00:00" });
        var pulses = await PulsesAsync(h, timer, Utc(11, 8, 23, 59, 0), Utc(11, 10, 0, 1, 0), TimeSpan.FromSeconds(25));
        Assert.Equal([Utc(11, 9, 0, 0, 15), Utc(11, 10, 0, 0, 15)], pulses);
        Assert.Equal("Next 00:00", (await h.FrameAsync(At(h, Utc(11, 10, 0, 2)))).Nodes[timer].Live);
    }

    [Fact]
    public async Task The_first_frame_never_pulses_and_missed_times_are_not_repeated()
    {
        var (h, timer) = TimerOf(new JsonObject { [NodeKinds.EveryKey] = NodeKinds.EveryHour });
        Assert.False((await h.FrameAsync(At(h, Utc(11, 8, 13, 0)))).Nodes[timer].On); // the first frame at 13:00 sets the next
        Assert.Equal("Next 14:00", (await h.FrameAsync(At(h, Utc(11, 8, 13, 30)))).Nodes[timer].Live);

        // No frames from 13:30 to 17:10 (the stream was down): one pulse, then the next full hour.
        var r = await h.FrameAsync(At(h, Utc(11, 8, 17, 10)));
        Assert.True(r.Nodes[timer].On);
        Assert.Equal("Next 18:00", r.Nodes[timer].Live);
        Assert.False((await h.FrameAsync(At(h, Utc(11, 8, 17, 11)))).Nodes[timer].On);
    }

    [Fact]
    public async Task A_few_minutes_timer_pulses_on_the_minutes_of_the_hour_it_divides()
    {
        var (h, timer) = TimerOf(new JsonObject { [NodeKinds.EveryKey] = NodeKinds.EveryMinutes, [NodeKinds.MinutesKey] = 20 });
        var pulses = await PulsesAsync(h, timer, Utc(11, 8, 12, 5), Utc(11, 8, 14, 0), TimeSpan.FromMinutes(1));
        Assert.Equal([Utc(11, 8, 12, 20), Utc(11, 8, 12, 40), Utc(11, 8, 13, 0), Utc(11, 8, 13, 20), Utc(11, 8, 13, 40), Utc(11, 8, 14, 0)], pulses);
    }

    [Fact]
    public async Task A_weekly_timer_pulses_on_its_day()
    {
        // 2026-11-08 is a Sunday.
        var (h, timer) = TimerOf(new JsonObject { [NodeKinds.EveryKey] = NodeKinds.EveryWeek, [NodeKinds.DayKey] = "tue", [NodeKinds.AtKey] = "06:30" });
        var pulses = await PulsesAsync(h, timer, Utc(11, 8, 0, 0), Utc(11, 25, 0, 0), TimeSpan.FromMinutes(30));
        Assert.Equal([Utc(11, 10, 6, 30), Utc(11, 17, 6, 30), Utc(11, 24, 6, 30)], pulses);
        Assert.Equal("Next Tue 06:30", (await h.FrameAsync(At(h, Utc(11, 25, 1, 0)))).Nodes[timer].Live);
    }

    [Fact]
    public async Task A_daily_timer_follows_daylight_saving()
    {
        var (h, timer) = TimerOf(new JsonObject { [NodeKinds.EveryKey] = NodeKinds.EveryDay, [NodeKinds.AtKey] = "06:00" }, Cet);

        // Around the October change (Sunday 25th): 06:00 local is 04:00 UTC before, 05:00 UTC after.
        var pulses = await PulsesAsync(h, timer, Utc(10, 23, 12, 0), Utc(10, 27, 12, 0), TimeSpan.FromMinutes(15));
        Assert.Equal([Utc(10, 24, 4, 0), Utc(10, 25, 5, 0), Utc(10, 26, 5, 0), Utc(10, 27, 5, 0)], pulses);
    }

    [Fact]
    public async Task A_time_skipped_by_daylight_saving_pulses_right_after_the_change()
    {
        // 29 March 2026: local 02:00 jumps to 03:00 (01:00 UTC); 02:30 does not exist that night.
        var (h, timer) = TimerOf(new JsonObject { [NodeKinds.EveryKey] = NodeKinds.EveryDay, [NodeKinds.AtKey] = "02:30" }, Cet);
        var pulses = await PulsesAsync(h, timer, Utc(3, 28, 12, 0), Utc(3, 30, 12, 0), TimeSpan.FromMinutes(5));
        Assert.Equal([Utc(3, 29, 1, 0), Utc(3, 30, 0, 30)], pulses);
    }

    /// <summary>A Time window (shown) with its settings, in UTC.</summary>
    private static (EvaluatorHarness H, string Window) WindowOf(JsonObject settings)
    {
        var b = new PipelineBuilder();
        b.Add(NodeKinds.Camera);
        var window = b.Add(NodeKinds.TimeWindow, settings: settings, show: true);
        return (new EvaluatorHarness(b.Build()), window);
    }

    [Fact]
    public async Task A_time_window_says_yes_between_its_times_with_the_next_change()
    {
        var (h, w) = WindowOf(new JsonObject { [NodeKinds.FromKey] = "08:00", [NodeKinds.ToKey] = "18:00" });
        var r = await h.FrameAsync(At(h, Utc(11, 9, 7, 59, 59)));
        Assert.Equal((false, "No until 08:00"), (r.Nodes[w].On!.Value, r.Nodes[w].Live));
        r = await h.FrameAsync(At(h, Utc(11, 9, 8, 0)));
        Assert.Equal((true, "Yes until 18:00"), (r.Nodes[w].On!.Value, r.Nodes[w].Live));
        Assert.False((await h.FrameAsync(At(h, Utc(11, 9, 18, 0)))).Nodes[w].On);
    }

    [Fact]
    public async Task A_time_window_over_midnight_belongs_to_the_day_it_starts()
    {
        // Friday 22:00 to 06:00: Friday night and the early Saturday, not the early Friday.
        var (h, w) = WindowOf(new JsonObject { [NodeKinds.FromKey] = "22:00", [NodeKinds.ToKey] = "06:00", [NodeKinds.DaysKey] = new JsonArray("fri") });
        Assert.False((await h.FrameAsync(At(h, Utc(11, 13, 5, 0)))).Nodes[w].On); // Friday early
        Assert.True((await h.FrameAsync(At(h, Utc(11, 13, 23, 0)))).Nodes[w].On); // Friday night
        var r = await h.FrameAsync(At(h, Utc(11, 14, 5, 59)));
        Assert.Equal((true, "Yes until 06:00"), (r.Nodes[w].On!.Value, r.Nodes[w].Live)); // Saturday early
        r = await h.FrameAsync(At(h, Utc(11, 14, 6, 0)));
        Assert.Equal((false, "No until Fri 22:00"), (r.Nodes[w].On!.Value, r.Nodes[w].Live));
    }

    [Fact]
    public async Task A_time_window_on_chosen_days_only()
    {
        var (h, w) = WindowOf(new JsonObject { [NodeKinds.DaysKey] = new JsonArray("mon", "tue") });
        Assert.True((await h.FrameAsync(At(h, Utc(11, 9, 12, 0)))).Nodes[w].On); // Monday
        Assert.False((await h.FrameAsync(At(h, Utc(11, 11, 12, 0)))).Nodes[w].On); // Wednesday
    }
}
