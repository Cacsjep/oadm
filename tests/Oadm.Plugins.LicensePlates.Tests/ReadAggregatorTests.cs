using Microsoft.Extensions.Time.Testing;

using Oadm.Plugins.LicensePlates.Matching;
using Oadm.Plugins.LicensePlates.Reads;

namespace Oadm.Plugins.LicensePlates.Tests;

/// <summary>
/// docs/spec/license-plate-center.md "From events to a read" with event sequences as ALPV and the LPV Simulator send them
/// (New only; New and Updates; a late exact match; no Lost; two cars at once; carState "tracked") on a fake clock.
/// </summary>
public sealed class ReadAggregatorTests
{
    private static readonly Guid Camera = Guid.NewGuid();
    private static readonly Guid Tag = Guid.NewGuid();

    private sealed class Harness : IDisposable
    {
        private long _id;

        public Harness(params string[] allowed)
        {
            var plates = allowed.ToDictionary(p => p, p => new Plate { Text = p, Tags = [Tag] }, StringComparer.Ordinal);
            var matcher = new PlateMatcher(new Source(plates)) { Deviation = 1, RebuildInline = true };
            Aggregator = new ReadAggregator(Time, (_, text) => matcher.Match(text, new HashSet<Guid> { Tag }, Time.GetUtcNow().DateTime), () => TimeSpan.FromSeconds(3), Decided.Add, Changed.Add, () => ++_id);
        }

        public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 27, 12, 0, 0, TimeSpan.Zero));

        public ReadAggregator Aggregator { get; }

        public List<DecidedRead> Decided { get; } = [];

        public List<ReadChange> Changed { get; } = [];

        public void Send(string json, Guid? camera = null)
        {
            var e = AlpvEvent.Of(EventStreamJson.Parse(json).Notification!)!;
            Aggregator.Handle(camera ?? Camera, e);
        }

        public void Dispose() => Aggregator.Dispose();

        private sealed class Source(Dictionary<string, Plate> plates) : IPlateSource
        {
            public Plate? Find(string text) => plates.GetValueOrDefault(text);

            public IReadOnlyCollection<string> TextsWithAnyTag(IReadOnlySet<Guid> tags) => plates.Keys;
        }
    }

    [Fact]
    public void An_exact_match_in_New_fires_at_once()
    {
        using var h = new Harness("W12345A");

        h.Send(Fixture.Notify("New", "17", "W 12345 A", 0.91, state: "new"));

        var read = Assert.Single(h.Decided);
        Assert.Equal(ReadResult.Allowed, read.Match.Result);
        Assert.Equal("W12345A", read.Match.Plate!.Text);
        Assert.Equal(PlateDirection.In, read.Direction);
    }

    [Fact]
    public void New_only_without_a_match_is_decided_when_the_wait_ends()
    {
        using var h = new Harness("W12345A");

        h.Send(Fixture.Notify("New", "18", "K99999", 0.8));
        h.Time.Advance(TimeSpan.FromSeconds(2.9));
        Assert.Empty(h.Decided);

        h.Time.Advance(TimeSpan.FromSeconds(0.1));
        var read = Assert.Single(h.Decided);
        Assert.Equal(ReadResult.Unknown, read.Match.Result);
        Assert.Equal("K99999", read.Event.Text);
    }

    [Fact]
    public void New_and_updates_decide_from_the_best_read_at_the_end_of_the_wait()
    {
        using var h = new Harness("W12345A");

        h.Send(Fixture.Notify("New", "19", "W1Z345A", 0.55));
        h.Time.Advance(TimeSpan.FromSeconds(1));
        h.Send(Fixture.Notify("Update", "19", "W12345B", 0.88));
        h.Time.Advance(TimeSpan.FromSeconds(1));
        h.Send(Fixture.Notify("Update", "19", "WI2345A", 0.62));
        h.Time.Advance(TimeSpan.FromSeconds(1));

        var read = Assert.Single(h.Decided);
        Assert.Equal(0.88, read.Event.Confidence, 2);
        Assert.Equal(ReadResult.Allowed, read.Match.Result);
        Assert.Equal(ReadReason.Near, read.Match.Reason);
        Assert.Equal("W12345A", read.Match.Plate!.Text);
    }

    [Fact]
    public void A_late_exact_match_during_the_wait_decides_at_once()
    {
        using var h = new Harness("W12345A");

        h.Send(Fixture.Notify("New", "20", "W1234", 0.5));
        h.Time.Advance(TimeSpan.FromSeconds(1.5));
        Assert.Empty(h.Decided);

        h.Send(Fixture.Notify("Update", "20", "W12345A", 0.7));
        var read = Assert.Single(h.Decided);
        Assert.True(read.Match.Exact);

        // The end of the wait fires nothing more.
        h.Time.Advance(TimeSpan.FromSeconds(5));
        Assert.Single(h.Decided);
    }

    [Fact]
    public void Lost_decides_before_the_wait_ends_and_drops_the_car()
    {
        using var h = new Harness("W12345A");

        h.Send(Fixture.Notify("New", "21", "K11111", 0.8));
        h.Send(Fixture.Notify("Lost", "21", "K11112", 0.6, state: "lost"));

        var read = Assert.Single(h.Decided);
        Assert.Equal("K11111", read.Event.Text);
        Assert.Equal(0, h.Aggregator.Active);
        h.Time.Advance(TimeSpan.FromSeconds(10));
        Assert.Single(h.Decided);
    }

    [Fact]
    public void Without_Lost_nothing_is_lost_and_the_car_is_dropped_60_seconds_after_its_last_event()
    {
        using var h = new Harness("W12345A");

        // The LPV Simulator: New and Update, carState "tracked", never Lost.
        h.Send(Fixture.Notify("New", "22", "W12345A", 0.9));
        h.Time.Advance(TimeSpan.FromSeconds(30));
        h.Send(Fixture.Notify("Update", "22", "W12345A", 0.95));
        h.Time.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal(1, h.Aggregator.Active);

        h.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(0, h.Aggregator.Active);
        Assert.Single(h.Decided);
    }

    [Fact]
    public void Two_cars_at_once_are_two_reads()
    {
        using var h = new Harness("W12345A");

        h.Send(Fixture.Notify("New", "30", "K22222", 0.8));
        h.Send(Fixture.Notify("New", "31", "W12345A", 0.9));
        h.Send(Fixture.Notify("Update", "30", "K22223", 0.85));

        Assert.Equal("W12345A", Assert.Single(h.Decided).Match.Plate!.Text);
        h.Time.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(2, h.Decided.Count);
        Assert.Equal("K22223", h.Decided[1].Event.Text);
        Assert.NotEqual(h.Decided[0].ReadId, h.Decided[1].ReadId);
    }

    [Fact]
    public void The_same_car_id_on_two_cameras_is_two_cars()
    {
        using var h = new Harness("W12345A");

        h.Send(Fixture.Notify("New", "40", "W12345A", 0.9));
        h.Send(Fixture.Notify("New", "40", "W12345A", 0.9), Guid.NewGuid());

        Assert.Equal(2, h.Decided.Count);
    }

    [Fact]
    public void Later_events_of_a_decided_car_change_its_row_and_fire_nothing()
    {
        using var h = new Harness("W12345A");

        h.Send(Fixture.Notify("New", "50", "W12345A", 0.7, direction: "unknown"));
        h.Send(Fixture.Notify("Update", "50", "W12345A", 0.8, direction: "out", image: Fixture.JpegBase64));
        h.Send(Fixture.Notify("Update", "50", "W12345A", 0.6, direction: "out", image: Fixture.JpegBase64));

        Assert.Single(h.Decided);
        Assert.Equal(PlateDirection.Unknown, h.Decided[0].Direction);
        var change = Assert.Single(h.Changed); // the second update has the same direction and a worse picture
        Assert.Equal(h.Decided[0].ReadId, change.ReadId);
        Assert.Equal(PlateDirection.Out, change.Direction);
        Assert.Equal(Fixture.JpegBase64, change.Image);
    }

    [Fact]
    public void The_wait_counts_from_the_first_event()
    {
        using var h = new Harness();

        h.Send(Fixture.Notify("New", "60", "AA1111", 0.5));
        h.Time.Advance(TimeSpan.FromSeconds(2));
        h.Send(Fixture.Notify("Update", "60", "AA1112", 0.6));
        h.Time.Advance(TimeSpan.FromSeconds(1));

        Assert.Single(h.Decided);
    }

    [Theory]
    [InlineData("tnsaxis:CameraApplicationPlatform/ALPV.New", AlpvEventKind.New)]
    [InlineData("tnsaxis:CameraApplicationPlatform/ALPV/Update", AlpvEventKind.Update)]
    [InlineData("tnsaxis:CameraApplicationPlatform/ALPV.Lost", AlpvEventKind.Lost)]
    public void The_topics_name_the_kind(string topic, AlpvEventKind kind) => Assert.Equal(kind, AlpvEvent.KindOf(topic));

    [Fact]
    public void Other_topics_and_ALPVs_own_list_events_are_ignored()
    {
        Assert.Null(AlpvEvent.KindOf("tnsaxis:CameraApplicationPlatform/ALPV.Allow_list"));
        Assert.Null(AlpvEvent.KindOf("tns1:Device/tnsaxis:IO/VirtualInput"));
    }

    [Fact]
    public void An_event_reads_its_fields()
    {
        var e = AlpvEvent.Of(EventStreamJson.Parse(Fixture.Notify("New", "77", "w-123 45a", 0.876, direction: "OUT", roi: "roi2")).Notification!)!;

        Assert.Equal("W12345A", e.Text);
        Assert.Equal(88, e.Percent);
        Assert.Equal(PlateDirection.Out, e.Direction);
        Assert.Equal(2, e.Area);
        Assert.Equal("AUT", e.Country);
        Assert.Equal(("rear", "car", "red"), (e.View, e.VehicleType, e.VehicleColor));
    }
}
