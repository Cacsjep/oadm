using Microsoft.Extensions.Time.Testing;

using Oadm.Plugins.Ocr.Api;
using Oadm.Plugins.Ocr.Model;
using Oadm.Plugins.Ocr.Readers;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Ocr.Tests.Readers;

/// <summary>
/// The runtime stats (docs/spec/ocr.md "Runtime stats"): the 10 s window over samples every 2 s, the load, the server CPU,
/// the readers' rates, the traffic light with its 30 s red rule, the live event only while a page keeps it on, device groups.
/// All time is a <see cref="FakeTimeProvider"/>.
/// </summary>
public sealed class StatsTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 3, 10, 0, 0, TimeSpan.Zero);

    private sealed class FakeLoad : IRecognizerLoad
    {
        public TimeSpan BusyTime { get; set; }

        public int Capacity { get; set; } = 4;
    }

    private sealed class Harness
    {
        public FakeTimeProvider Time { get; } = new(Start);

        public FakeLoad Load { get; } = new();

        public TimeSpan Cpu { get; set; }

        public ReaderStats Stats { get; }

        public Harness() => Stats = new ReaderStats(Time, () => Cpu, cores: 4);

        /// <summary>2 s pass with <paramref name="busy"/> of recognition, <paramref name="cpu"/> of process time and the pictures given.</summary>
        public void Step(double busy = 0, double cpu = 0, int pictures = 0, double readMs = 0, string? reader = null)
        {
            for (var i = 0; i < pictures; i++)
            {
                Stats.CountPicture(TimeSpan.FromMilliseconds(readMs));
                if (reader is not null)
                {
                    Stats.CountReading(reader);
                }
            }

            Time.Advance(ReaderStats.SampleInterval);
            Load.BusyTime += TimeSpan.FromSeconds(busy);
            Cpu += TimeSpan.FromSeconds(cpu);
            Stats.Sample(Load);
        }
    }

    [Fact]
    public void TheFiguresAreMeasuredOverTheLastTenSeconds()
    {
        var h = new Harness();
        h.Stats.Sample(h.Load);
        Assert.Equal(0, h.Stats.Figures().Load); // one sample: nothing measured yet

        // 4 workers, 4 s of their time per 2 s: 50 %; 1.6 s of process time per 2 s on 4 cores: 20 %; 4 pictures of 50 ms per 2 s.
        for (var i = 0; i < 5; i++)
        {
            h.Step(busy: 4, cpu: 1.6, pictures: 4, readMs: 50);
        }

        var figures = h.Stats.Figures();
        Assert.Equal(50, figures.Load, 6);
        Assert.Equal(20, figures.ServerCpu, 6);
        Assert.Equal(2, figures.PicturesPerSecond, 6);
        Assert.Equal(50, figures.AverageReadMs, 6);

        // Idle afterwards: the window forgets the busy seconds once they are older than 10 s.
        for (var i = 0; i < 4; i++)
        {
            h.Step();
        }

        Assert.Equal(10, h.Stats.Figures().Load, 6); // 2 s of the 10 still busy
        h.Step();
        Assert.Equal(0, h.Stats.Figures().Load, 6);
        Assert.Equal(0, h.Stats.Figures().AverageReadMs);

        // A graphics card runs its calls one at a time: 1 s busy per 2 s is 50 %.
        h.Load.Capacity = 1;
        for (var i = 0; i < 5; i++)
        {
            h.Step(busy: 1);
        }

        Assert.Equal(50, h.Stats.Figures().Load, 6);
    }

    [Fact]
    public void AReaderIsJudgedOnlyAfterAWholeWindowAndFromItsOwnStart()
    {
        var h = new Harness();
        h.Stats.Sample(h.Load);
        h.Step();
        h.Stats.ReaderStarted("r1");

        // 6 pictures in 4 s: 1.5 per second, not judged before it watched for 10 s.
        h.Step(pictures: 3, reader: "r1");
        h.Step(pictures: 3, reader: "r1");
        var (reads, judged) = h.Stats.Figures().Rates["r1"];
        Assert.Equal(1.5, reads, 6);
        Assert.False(judged);

        for (var i = 0; i < 3; i++)
        {
            h.Step(pictures: 3, reader: "r1");
        }

        (reads, judged) = h.Stats.Figures().Rates["r1"];
        Assert.Equal(1.5, reads, 6);
        Assert.True(judged);

        // Stopped: gone from the rates; started again: counts from its new start.
        h.Stats.ReaderStopped("r1");
        h.Step();
        Assert.False(h.Stats.Figures().Rates.ContainsKey("r1"));
        h.Stats.ReaderStarted("r1");
        h.Step(pictures: 8, reader: "r1");
        Assert.Equal(4, h.Stats.Figures().Rates["r1"].Reads, 6);
    }

    [Fact]
    public void TheTrafficLightFollowsTheLoadAndTheReaders()
    {
        Assert.Equal((LoadState.Ok, "All readers keep up"), ReaderStats.Light(74.9, false, [(8, 7.2, true), (2, 1.8, true)]));

        // Orange: a reader below 90 % (one or several), or only the load at 75 % or more.
        Assert.Equal((LoadState.Busy, "Busy: 1 reader reads slower than set"), ReaderStats.Light(10, false, [(8, 7.1, true), (2, 2, true)]));
        Assert.Equal((LoadState.Busy, "Busy: 2 readers read slower than set"), ReaderStats.Light(80, false, [(8, 5.1, true), (2, 1.7, true)]));
        Assert.Equal((LoadState.Busy, "Busy: recognition is at 82 %"), ReaderStats.Light(82.4, false, [(8, 8, true)]));
        Assert.Equal((LoadState.Busy, "Busy: recognition is at 97 %"), ReaderStats.Light(97, false, []));

        // A reader not judged yet (watching less than 10 s) never turns it orange.
        Assert.Equal(LoadState.Ok, ReaderStats.Light(0, false, [(8, 0, false)]).State);

        // Red: a reader below half of its rate, or the load held at 95 % for 30 s.
        var red = (LoadState.Overloaded, "Overloaded: lower the frames per second or the number of readers");
        Assert.Equal(red, ReaderStats.Light(10, false, [(8, 3.9, true)]));
        Assert.Equal(red, ReaderStats.Light(96, true, []));
    }

    [Fact]
    public void RedNeedsTheLoadAt95PercentForThirtySeconds()
    {
        var h = new Harness();
        h.Stats.Sample(h.Load);

        // Full load from now on: the window (shorter than 10 s at the start) is at 100 % with the next sample, red 30 s after it.
        var samples = 0;
        while (h.Stats.Figures().Load < ReaderStats.OverloadLoad)
        {
            h.Step(busy: 8);
            samples++;
        }

        Assert.Equal(1, samples);
        for (var i = 0; i < 14; i++)
        {
            h.Step(busy: 8);
            Assert.False(h.Stats.Figures().Overloaded, $"red after {(i + 1) * 2} s");
        }

        h.Step(busy: 8);
        Assert.True(h.Stats.Figures().Overloaded);

        // A dip below 95 % starts the 30 s again.
        h.Step(busy: 2);
        Assert.False(h.Stats.Figures().Overloaded);
        h.Step(busy: 8);
        Assert.False(h.Stats.Figures().Overloaded);
    }

    [Fact]
    public async Task ThePageGetsTheStatsLiveOnlyWhileItAsksForThem()
    {
        await using var ocr = await OcrHarness.StartAsync();
        var reader = await ocr.SaveAsync(ocr.NewReader("Gate", ReaderMode.Watching) with { Fps = 2 });
        await Eventually.TrueAsync(() => ocr.Frames.Open == 1, "the reader watches");
        int Published() => ocr.Context.PublishedEvents.Count(e => e.Topic == OcrMethods.StatsTopic);

        // Nobody asked: no live stats.
        ocr.Time.Advance(TimeSpan.FromSeconds(4));
        Assert.Equal(0, Published());

        var stats = OcrJson.Deserialize<OcrStats>(await ocr.CallAsync(OcrMethods.GetStats));
        Assert.Equal((1, 16, 0, 8, 1), (stats.Watching, stats.MaxWatching, stats.Previews, stats.MaxPreviews, stats.Streams));
        var rate = Assert.Single(stats.Readers);
        Assert.Equal((reader.Id, 2, false), (rate.ReaderId, rate.Fps, rate.Slow));
        Assert.Empty(stats.Sites);
        Assert.Equal(LoadState.Ok, stats.State);

        // Every 2 s while asked; a reader that reads 1 of 2 pictures per second turns orange once it watched 10 s.
        for (var i = 0; i < 6; i++)
        {
            await ocr.SendAndWaitAsync(ocr.Inside.Id, "A");
            await ocr.SendAndWaitAsync(ocr.Inside.Id, "A");
            ocr.Time.Advance(ReaderStats.SampleInterval);
        }

        Assert.Equal(6, Published());
        var live = OcrJson.Deserialize<OcrStats>(ocr.Context.PublishedEvents.Last(e => e.Topic == OcrMethods.StatsTopic).PayloadJson);
        Assert.Equal(1, Assert.Single(live.Readers).Reads, 1);
        Assert.True(live.Readers[0].Slow);
        Assert.Equal((LoadState.Busy, ReaderRules.BusySlow(1)), (live.State, live.StateText)); // half its rate: orange, below half: red
        Assert.True(live.PicturesPerSecond > 0);

        // 30 s without a call: the live stats stop.
        ocr.Time.Advance(TimeSpan.FromSeconds(30));
        var after = Published();
        ocr.Time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(after, Published());
        Assert.Empty(ocr.Plugin.Runtime!.StatsWatchers);
    }

    [Fact]
    public async Task AGroupSeesOnlyItsOwnReadersAndTheServerWideLoad()
    {
        await using var ocr = await OcrHarness.StartAsync();
        var site = Guid.NewGuid();
        var harbour = ocr.Context.Add(new FakeDevice { Address = "10.1.0.1", SiteId = site, SiteName = "Harbour" });
        var key = ocr.Context.FakeScopes.Add("Harbour team", [harbour.Id]);
        var group = FakeCallContext.WithGroup(ocr.Context, [harbour.Id], "operator", key);
        var mine = await ocr.SaveAsync(ocr.NewReader("Crane", ReaderMode.Watching, device: harbour.Id), group);
        await ocr.SaveAsync(ocr.NewReader("Lobby", ReaderMode.Watching));
        await Eventually.TrueAsync(() => ocr.Frames.Open == 2, "both readers watch");

        var theirs = OcrJson.Deserialize<OcrStats>(await ocr.CallAsync(OcrMethods.GetStats, caller: group));
        var everyone = OcrJson.Deserialize<OcrStats>(await ocr.CallAsync(OcrMethods.GetStats));

        Assert.Equal(mine.Id, Assert.Single(theirs.Readers).ReaderId);
        Assert.Equal((1, 1), (theirs.Watching, theirs.Streams));
        var harbourStreams = Assert.Single(theirs.Sites);
        Assert.Equal(("Harbour", 1, 4), (harbourStreams.Name, harbourStreams.Streams, harbourStreams.Max));
        Assert.Equal((2, 2), (everyone.Watching, everyone.Readers.Count));
        Assert.Equal(everyone.Load, theirs.Load);
        Assert.Equal(everyone.ServerCpu, theirs.ServerCpu);

        // The live stats of each scope go only to that scope.
        ocr.Time.Advance(ReaderStats.SampleInterval);
        var seen = ocr.Context.EventsSeenBy(group).Where(e => e.Topic == OcrMethods.StatsTopic).ToList();
        Assert.NotEmpty(seen);
        Assert.All(seen, e => Assert.Equal(mine.Id, Assert.Single(OcrJson.Deserialize<OcrStats>(e.PayloadJson).Readers).ReaderId));
        Assert.Contains(ocr.Context.PublishedEvents, e => e.Topic == OcrMethods.StatsTopic && e.Audience.Kind == PluginEventAudienceKind.AllDevices);
    }
}
