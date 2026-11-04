using Microsoft.Extensions.Time.Testing;
using Oadm.Plugins.Ocr.Model;
using Oadm.Plugins.Ocr.Recognition;
using SkiaSharp;

namespace Oadm.Plugins.Ocr.Tests.Recognition;

/// <summary>
/// Where the text finder runs (docs/spec/ocr.md "GPU"): an area read in tiles (Search) goes to the card in calls that fit its
/// memory cap whatever the picture's size, a fixed-place area stays in one piece. The load counts the CPU workers and the
/// card together. Fake runners and fake sessions, no graphics card.
/// </summary>
public sealed class SearchRoutingTests
{
    private static readonly Area FixedPlace = new() { Name = "Brand", Box = new RelativeBox(0.28, 0.54, 0.16, 0.10) };

    private static readonly Area Search = new() { Name = "Intensity", Expected = ExpectedText.WithFormat("#/##") };

    /// <summary>A text finder that records its calls and finds nothing.</summary>
    private class FakeFinder : ITextFinder
    {
        public List<(int Count, int Height, int Width)> Calls { get; } = [];

        public virtual bool BatchTiles => false;

        public virtual int DetectionSide(int pixels) => OnnxTextRecognizer.RoundUp32(pixels);

        public Task<PooledFloats> DetectAsync(PooledFloats input, int count, int height, int width, CancellationToken ct)
        {
            lock (Calls)
            {
                Calls.Add((count, height, width));
                OnCall(count, height, width);
            }

            return Task.FromResult(PooledFloats.Rent(count * height * width, clear: true));
        }

        protected virtual void OnCall(int count, int height, int width)
        {
        }
    }

    /// <summary>The graphics card's text finder on DirectML: stepped sides, calls within the cap, each size a session in a real budget.</summary>
    private sealed class FakeCard(GpuSessionBudget budget) : FakeFinder, ITextFinder
    {
        public long MostBytes { get; private set; }

        public override bool BatchTiles => true;

        public override int DetectionSide(int pixels) => CardModels.SteppedSide(pixels);

        public int TilesPerCall(int tiles, int tileWidth, int tileHeight) => GpuSessionBudget.TilesPerCall(tiles, tileWidth, tileHeight);

        protected override void OnCall(int count, int height, int width)
        {
            budget.Get(this, $"{count}x{height}x{width}", GpuSessionBudget.Estimate((long)count * height * width), () => new Session());
            MostBytes = Math.Max(MostBytes, budget.Bytes);
        }

        private sealed class Session : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }

    private sealed class FakeRunner(ITextFinder? search) : FakeFinder, IModelRunner
    {
        public CharacterList Characters { get; } = CharacterList.Load(Path.Combine(RecognizerFixture.ModelFolder, ModelFiles.RecognitionLatinCharacters));

        public ITextFinder Search => search ?? this;

        public bool SearchOnCard => search is not null;

        public Task<float[]> UpsideDownAsync(IReadOnlyList<SKBitmap> lines, CancellationToken ct) => Task.FromResult(new float[lines.Count]);

        public Task<LineScores[]> RecognizeAsync(IReadOnlyList<SKBitmap> lines, CancellationToken ct) => throw new InvalidOperationException("Nothing is found, nothing is read.");
    }

    [Fact]
    public async Task With_a_graphics_card_a_fixed_place_area_runs_on_the_cpu_and_search_on_the_card()
    {
        using var budget = new GpuSessionBudget(time: new FakeTimeProvider());
        var card = new FakeCard(budget);
        var runner = new FakeRunner(card);
        using var picture = Blank(1920, 1080);

        await OnnxTextRecognizer.ReadWithAsync(picture, [FixedPlace], runner, CancellationToken.None);
        Assert.Single(runner.Calls);
        Assert.Empty(card.Calls);

        await OnnxTextRecognizer.ReadWithAsync(picture, [Search], runner, CancellationToken.None);
        Assert.Single(runner.Calls);

        // Full HD: 6 tiles of 768 x 768, one per call (one 6-tile call would need about 5 GB, 2 tiles more than half the cap).
        Assert.Equal(Enumerable.Repeat((1, 768, 768), 6), card.Calls);
    }

    [Fact]
    public async Task Without_a_graphics_card_search_runs_tile_by_tile_on_the_cpu()
    {
        var runner = new FakeRunner(null);
        using var picture = Blank(1920, 1080);

        await OnnxTextRecognizer.ReadWithAsync(picture, [Search, FixedPlace], runner, CancellationToken.None);

        Assert.Equal(7, runner.Calls.Count); // 6 tiles and the fixed-place area
        Assert.All(runner.Calls, c => Assert.Equal(1, c.Count));
    }

    [Theory]
    [InlineData(2560, 1440, 6)]
    [InlineData(3840, 2160, 15)]
    public async Task A_search_over_a_larger_picture_stays_within_the_cap(int width, int height, int tiles)
    {
        using var budget = new GpuSessionBudget(time: new FakeTimeProvider());
        var card = new FakeCard(budget);
        var runner = new FakeRunner(card);
        using var picture = Blank(width, height);

        await OnnxTextRecognizer.ReadWithAsync(picture, [Search], runner, CancellationToken.None);

        Assert.Equal(tiles, card.Calls.Sum(c => c.Count));
        Assert.All(card.Calls, c => Assert.True(GpuSessionBudget.Estimate((long)c.Count * c.Height * c.Width) <= GpuSessionBudget.MaxCallBytes, $"{c}"));
        Assert.Single(card.Calls.Distinct()); // one size: one session
        Assert.True(card.MostBytes <= GpuSessionBudget.MaxBytes);
    }

    [Fact]
    public async Task Searches_of_full_hd_qhd_and_4k_one_after_the_other_never_exceed_the_cap()
    {
        using var budget = new GpuSessionBudget(time: new FakeTimeProvider());
        var card = new FakeCard(budget);
        var runner = new FakeRunner(card);
        using var fullHd = Blank(1920, 1080);
        using var qhd = Blank(2560, 1440);
        using var uhd = Blank(3840, 2160);

        for (var round = 0; round < 3; round++)
        {
            foreach (var picture in new[] { fullHd, qhd, uhd })
            {
                await OnnxTextRecognizer.ReadWithAsync(picture, [Search], runner, CancellationToken.None);
            }
        }

        Assert.True(card.MostBytes <= GpuSessionBudget.MaxBytes, $"{card.MostBytes}");

        // Two Search sizes (768 x 768 and 1024 x 1024) fit together: none pushed out at every picture.
        Assert.Equal(0, budget.Evicted);
        Assert.Equal(2, budget.Count);
    }

    [Fact]
    public void The_load_counts_the_cpu_workers_and_the_card_whichever_is_busier()
    {
        var time = new FakeTimeProvider();
        var cpu = new BusyMeter(time);
        var card = new BusyMeter(time);
        var load = new CombinedBusy(cpu, card, workers: 4);

        // 2 s on the CPU workers, the card 1 s of 1 s: the card is full (4 s of worker time).
        Busy(time, cpu, TimeSpan.FromSeconds(2));
        Busy(time, card, TimeSpan.FromSeconds(1));
        Assert.Equal(TimeSpan.FromSeconds(4), load.Busy);

        // Then only the CPU: 3 s.
        Busy(time, cpu, TimeSpan.FromSeconds(3));
        Assert.Equal(TimeSpan.FromSeconds(7), load.Busy);

        // Nothing: unchanged.
        Assert.Equal(TimeSpan.FromSeconds(7), load.Busy);
    }

    [Fact]
    public void Runs_on_names_the_cpu_and_the_card_of_search()
    {
        Assert.Equal("CPU (24 cores) · NVIDIA GeForce RTX 5070 Ti", OnnxTextRecognizer.CardName(24, "NVIDIA GeForce RTX 5070 Ti"));
    }

    private static void Busy(FakeTimeProvider time, BusyMeter meter, TimeSpan length)
    {
        var started = meter.Start();
        time.Advance(length);
        meter.Stop(started);
    }

    private static Picture Blank(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(SKColors.White);
        using var data = bitmap.Encode(SKEncodedImageFormat.Jpeg, 80);
        return Picture.Decode(data.ToArray());
    }
}
