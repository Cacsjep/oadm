using System.Diagnostics;
using System.Globalization;
using Oadm.Plugins.Ocr.Api;
using Oadm.Plugins.Ocr.Matching;
using Oadm.Plugins.Ocr.Model;
using Oadm.Plugins.Ocr.Readers;
using Oadm.Plugins.Ocr.Recognition;
using Oadm.Plugins.Ocr.Tests.Readers;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;
using SkiaSharp;
using Xunit.Abstractions;

namespace Oadm.Plugins.Ocr.Tests.Recognition;

/// <summary>
/// One reader on a belt of parcels (docs/spec/ocr.md "Measured: one reader"): the time of each stage of one picture with about
/// 15 lines in view, a real watching reader at 15 frames per second with the real models on the CPU (reads per second, load,
/// the state chip), 20 readers at once and the process's memory. The numbers go to the test output (and to the file in
/// OADM_SPEED_OUT); the budgets only catch a hang. Run in Release, one test per process for the memory figure.
/// </summary>
[Trait("Category", "Perf")]
public sealed class OneReaderSpeedTests(ITestOutputHelper output)
{
    internal const string Tracking = "@@-####-##";

    /// <summary>A format no line of the labels has: the usual picture on a belt between two labels.</summary>
    internal const string NoMatch = "@@@U ###### #";

    private static readonly RelativeBox Belt = new(660 / 1920.0, 190 / 1080.0, 600 / 1920.0, 700 / 1080.0);

    private void Say(string line)
    {
        output.WriteLine(line);
        if (Environment.GetEnvironmentVariable("OADM_SPEED_OUT") is { Length: > 0 } file)
        {
            File.AppendAllText(file, line + Environment.NewLine);
        }
    }

    /// <summary>The CPU, or the graphics card with OADM_SPEED_CARD=1 (only by hand: never in a test run of the suite).</summary>
    private static OnnxTextRecognizer Cpu() => new(new OcrEngineOptions { ModelDirectory = RecognizerFixture.ModelFolder, UseGraphicsCard = Environment.GetEnvironmentVariable("OADM_SPEED_CARD") == "1" });

    private static Area[] Areas(string format) => [new() { Name = "Parcel", Box = Belt, Expected = ExpectedText.WithFormat(format) }];

    [Fact]
    public async Task One_picture_per_stage()
    {
        await using var recognizer = Cpu();
        await recognizer.CardTry;
        foreach (var (name, width, height) in new[] { ("1080p", 1920, 1080), ("720p", 1280, 720) })
        {
            var frames = ParcelScene.Frames(width, height, 32);
            Say($"{name}: JPEG {frames.Average(f => f.Length) / 1024:0} KB");
            foreach (var format in new[] { Tracking, NoMatch })
            {
                await MeasureAsync(recognizer, $"{name} {format}", frames, Areas(format));
            }
        }

        var search = new Area[] { new() { Name = "Search", Box = new RelativeBox(0.05, 0.05, 0.9, 0.9), Expected = ExpectedText.WithFormat(Tracking) } };
        await MeasureAsync(recognizer, "1080p Search", ParcelScene.Frames(1920, 1080, 16), search);
    }

    private async Task MeasureAsync(OnnxTextRecognizer recognizer, string name, List<byte[]> frames, Area[] areas)
    {
        foreach (var frame in frames.Take(4))
        {
            await recognizer.ReadDetailedAsync(frame, areas, RecognitionOptions.Default, CancellationToken.None);
        }

        var counter = new CountingRunner(new CpuRunner(recognizer.CpuWorkers, CharacterList.Load(Path.Combine(RecognizerFixture.ModelFolder, ModelFiles.RecognitionLatinCharacters)), false));
        var matched = 0;
        foreach (var frame in frames)
        {
            using var picture = Picture.Decode(frame);
            var result = await OnnxTextRecognizer.ReadWithAsync(picture, areas, counter, CancellationToken.None);
            matched += result.Areas[0].Lines.Any(l => l.FormatMatch is not null) ? 1 : 0;
        }

        var matcher = new TextMatcher();
        var matchWatch = Stopwatch.StartNew();
        StageClock.Reset();
        var cpuBefore = Environment.CpuUsage.TotalTime;
        var busyBefore = recognizer.BusyTime;
        var allocated = GC.GetTotalAllocatedBytes(false);
        var (g0, g1, g2) = (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
        var watch = Stopwatch.StartNew();
        matchWatch.Reset();
        foreach (var frame in frames)
        {
            var result = await recognizer.ReadDetailedAsync(frame, areas, RecognitionOptions.Default, CancellationToken.None);
            matchWatch.Start();
            matcher.Match([.. result.Areas[0].Lines.Select(l => l.Line)], areas[0].Expected);
            matchWatch.Stop();
        }

        var wall = watch.Elapsed.TotalMilliseconds / frames.Count;
        var stages = StageClock.Snapshot().ToDictionary(e => e.Key, e => e.Value / frames.Count);
        var cpu = (Environment.CpuUsage.TotalTime - cpuBefore).TotalMilliseconds / frames.Count;
        var busy = (recognizer.BusyTime - busyBefore).TotalMilliseconds / frames.Count;
        var bytes = (GC.GetTotalAllocatedBytes(false) - allocated) / (double)frames.Count;
        Say(string.Create(CultureInfo.InvariantCulture, $"{name}: {wall:0.0} ms per picture ({1000 / wall:0.0} per second, one at a time), CPU {cpu:0} ms, worker time {busy:0} ms, match in {matched}/{frames.Count}"));
        Say(string.Create(CultureInfo.InvariantCulture, $"  per picture: {counter.Lines / (double)frames.Count:0.0} lines read, {counter.Angle / (double)frames.Count:0.0} to the angle model, {counter.Turned / (double)frames.Count:0.0} read again; {bytes / 1024:0} KB allocated, GC {GC.CollectionCount(0) - g0}/{GC.CollectionCount(1) - g1}/{GC.CollectionCount(2) - g2} in {frames.Count} pictures"));
        Say("  wall: " + string.Join(", ", new[] { Stage.Decode, Stage.WallFind, Stage.CutLines, Stage.WallRead, Stage.WallAngle, Stage.WallTurned, Stage.WallWhole, Stage.Decoding }.Select(s => string.Create(CultureInfo.InvariantCulture, $"{s} {stages[s]:0.0}"))) + string.Create(CultureInfo.InvariantCulture, $", Match {matchWatch.Elapsed.TotalMilliseconds / frames.Count:0.0}"));
        Say("  sum over threads: " + string.Join(", ", new[] { Stage.DetectInput, Stage.DetectModel, Stage.DetectBoxes, Stage.Angle, Stage.RecognizeInput, Stage.RecognizeModel }.Select(s => string.Create(CultureInfo.InvariantCulture, $"{s} {stages[s]:0.0}"))));
    }

    [Theory]
    [InlineData(1, 1920, 1080, 15, false)]
    [InlineData(1, 1280, 720, 15, false)]
    [InlineData(1, 1920, 1080, 15, true)]
    [InlineData(10, 1920, 1080, 8, false)]
    [InlineData(10, 1920, 1080, 4, false)]
    [InlineData(10, 1920, 1080, 15, false)]
    public async Task Watching_readers_at_work(int count, int width, int height, int fps, bool search)
    {
        var frames = ParcelScene.Frames(width, height, 64);
        await using var recognizer = Cpu();
        await recognizer.CardTry;
        await using var ocr = new OcrHarness(recognizer: new Borrowed(recognizer), matcher: new TextMatcher(), processorCount: Environment.ProcessorCount, clock: TimeProvider.System, processorTime: () => Environment.CpuUsage.TotalTime);
        await ocr.Plugin.StartAsync(ocr.Context, CancellationToken.None);
        var box = search ? new RelativeBox(0.05, 0.05, 0.9, 0.9) : Belt;
        var cameras = new List<Guid>();
        for (var i = 0; i < count; i++)
        {
            var camera = i == 0 ? ocr.Inside : ocr.Context.Add(new FakeDevice { Address = "10.0.1." + (i + 1) });
            cameras.Add(camera.Id);
            await ocr.SaveAsync(ocr.NewReader("Belt " + i, ReaderMode.Watching, camera.Id, new Area { Name = "Parcel", Box = box, Expected = ExpectedText.WithFormat(Tracking) }) with { Fps = fps, Resolution = new Resolution(width, height) });
        }

        await Eventually.TrueAsync(() => ocr.Frames.Open == count, "the readers watch");

        const int Seconds = 18;
        const int Warm = 4;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1.0 / fps));
        var watch = Stopwatch.StartNew();
        var sent = 0;
        var kept = 0;
        var cpuAtWarm = TimeSpan.Zero;
        var allocatedAtWarm = 0L;
        var pauseAtWarm = TimeSpan.Zero;
        var wallAtWarm = 0.0;
        while (watch.Elapsed < TimeSpan.FromSeconds(Seconds) && await timer.WaitForNextTickAsync())
        {
            if (wallAtWarm == 0 && watch.Elapsed >= TimeSpan.FromSeconds(Warm))
            {
                wallAtWarm = watch.Elapsed.TotalSeconds;
                cpuAtWarm = Environment.CpuUsage.TotalTime;
                allocatedAtWarm = GC.GetTotalAllocatedBytes(false);
                pauseAtWarm = GC.GetTotalPauseDuration();
                kept = ocr.Readings;
            }

            for (var i = 0; i < cameras.Count; i++)
            {
                ocr.Frames.Send(cameras[i], frames[(sent + (i * 5)) % frames.Count]);
            }

            sent++;
        }

        var seconds = watch.Elapsed.TotalSeconds - wallAtWarm;
        var allocated = (GC.GetTotalAllocatedBytes(false) - allocatedAtWarm) / seconds;
        var pause = (GC.GetTotalPauseDuration() - pauseAtWarm).TotalMilliseconds / seconds;
        var coreMs = (Environment.CpuUsage.TotalTime - cpuAtWarm).TotalMilliseconds / seconds;
        var pictures = (ocr.Readings - kept) / seconds;
        var stats = ocr.Plugin.Runtime!.StatsFor(ICallContext.AllDevicesScopeKey);
        var reads = stats.Readers.Average(r => r.Reads);
        Say(string.Create(CultureInfo.InvariantCulture, $"  sent {sent / watch.Elapsed.TotalSeconds:0.0} per camera per second"));
        var values = ocr.TextRead.Select(e => e.Data[OcrInfo.TextField]).Where(t => t.Length > 0).Distinct(StringComparer.Ordinal).Count();
        Say($"  runs on {recognizer.RunsOn.Name}; card {CardNow()}");
        Say(string.Create(CultureInfo.InvariantCulture, $"{count} x {width}x{height} {(search ? "Search" : "fixed")} at {fps} fps: reads {reads:0.0} each, {pictures:0.0} pictures per second in all, load {stats.Load:0.0} %, reading {stats.AverageReadMs:0} ms, {stats.State}: {stats.StateText}"));
        Say(string.Create(CultureInfo.InvariantCulture, $"  CPU {coreMs / 1000:0.00} cores of {Environment.ProcessorCount} ({coreMs:0} core-ms per second, {coreMs / Math.Max(1, pictures):0} core-ms per picture), {values} different values published, {allocated / (1024 * 1024):0.0} MB allocated per second, GC pause {pause:0.0} ms per second, {Memory()}"));
        Assert.True(ocr.Readings > 0);
    }

    /// <summary>
    /// What each stage burns: its own work one picture at a time, the process's CPU time over the run (spinning threads count).
    /// The model stages run alone from captured inputs, so the figure says what a move of that stage would free.
    /// </summary>
    [Fact]
    public async Task Cpu_time_per_stage()
    {
        await using var recognizer = Cpu();
        await recognizer.CardTry;
        foreach (var (name, width, height) in new[] { ("1080p", 1920, 1080), ("720p", 1280, 720) })
        {
            var frames = ParcelScene.Frames(width, height, 16);
            var areas = Areas(Tracking);
            var characters = CharacterList.Load(Path.Combine(RecognizerFixture.ModelFolder, ModelFiles.RecognitionLatinCharacters));
            var capture = new CapturingRunner(new CpuRunner(recognizer.CpuWorkers, characters, false));
            foreach (var frame in frames)
            {
                using var picture = Picture.Decode(frame);
                await OnnxTextRecognizer.ReadWithAsync(picture, areas, capture, CancellationToken.None);
            }

            var runner = new CpuRunner(recognizer.CpuWorkers, characters, false);
            var whole = await CpuAsync(frames.Count, async i => await recognizer.ReadDetailedAsync(frames[i], areas, RecognitionOptions.Default, CancellationToken.None));
            StageClock.Reset();
            foreach (var frame in frames)
            {
                await recognizer.ReadDetailedAsync(frame, areas, RecognitionOptions.Default, CancellationToken.None);
            }

            var sums = StageClock.Snapshot().ToDictionary(e => e.Key, e => e.Value / frames.Count);
            var decode = await CpuAsync(frames.Count, i =>
            {
                using var picture = Picture.Decode(frames[i], (w, h) => OnnxTextRecognizer.NeededRegion(areas, w, h));
                return Task.CompletedTask;
            });
            var find = await CpuAsync(capture.Inputs.Count, async i =>
            {
                var (input, h, w) = capture.Inputs[i];
                using var buffer = PooledFloats.Rent(input.Length, clear: false);
                input.CopyTo(buffer.Array, 0);
                using var map = await runner.DetectAsync(buffer, 1, h, w, CancellationToken.None);
            });
            var read = await CpuAsync(capture.Lines.Count, async i => await runner.RecognizeAsync(capture.Lines[i], CancellationToken.None));
            var angle = capture.Unsure.Count == 0 ? (0.0, 0.0) : await CpuAsync(capture.Unsure.Count, async i => await runner.UpsideDownAsync(capture.Unsure[i], CancellationToken.None));
            Say(string.Create(CultureInfo.InvariantCulture, $"{name}, core-ms per picture (wall ms): whole picture {whole.Cpu:0} ({whole.Wall:0}); alone: decode {decode.Cpu:0.0} ({decode.Wall:0.0}), text finder model {find.Cpu:0.0} ({find.Wall:0.0}), reader model and its input {read.Cpu:0.0} ({read.Wall:0.0}), angle model and its input {angle.Item1 * capture.Unsure.Count / frames.Count:0.0} ({angle.Item2 * capture.Unsure.Count / frames.Count:0.0})"));
            Say(string.Create(CultureInfo.InvariantCulture, $"  managed, one thread each: finder input {sums[Stage.DetectInput]:0.0}, finder boxes {sums[Stage.DetectBoxes]:0.0}, cut lines {sums[Stage.CutLines]:0.0}, decoding {sums[Stage.Decoding]:0.0}; {capture.Unsure.Count} of {frames.Count} pictures asked the angle model"));
            capture.DisposeLines();
        }
    }

    private static async Task<(double Cpu, double Wall)> CpuAsync(int items, Func<int, Task> work)
    {
        const int Rounds = 4;
        var cpu = Environment.CpuUsage.TotalTime;
        var watch = Stopwatch.StartNew();
        for (var round = 0; round < Rounds; round++)
        {
            for (var i = 0; i < items; i++)
            {
                await work(i);
            }
        }

        return ((Environment.CpuUsage.TotalTime - cpu).TotalMilliseconds / (items * Rounds), watch.Elapsed.TotalMilliseconds / (items * Rounds));
    }

    /// <summary>Keeps copies of what a picture hands the models.</summary>
    private sealed class CapturingRunner(IModelRunner inner) : IModelRunner
    {
        private int _calls;

        public List<(float[] Input, int Height, int Width)> Inputs { get; } = [];

        public List<List<SKBitmap>> Lines { get; } = [];

        public List<List<SKBitmap>> Unsure { get; } = [];

        public CharacterList Characters => inner.Characters;

        public ITextFinder Search => this;

        public bool SearchOnCard => false;

        public bool BatchTiles => false;

        public int DetectionSide(int pixels) => inner.DetectionSide(pixels);

        public Task<PooledFloats> DetectAsync(PooledFloats input, int count, int height, int width, CancellationToken ct)
        {
            lock (Inputs)
            {
                Inputs.Add((input.Span.ToArray(), height, width));
            }

            _calls = 0;
            return inner.DetectAsync(input, count, height, width, ct);
        }

        public Task<float[]> UpsideDownAsync(IReadOnlyList<SKBitmap> lines, CancellationToken ct)
        {
            Unsure.Add([.. lines.Select(l => l.Copy())]);
            return inner.UpsideDownAsync(lines, ct);
        }

        public Task<LineScores[]> RecognizeAsync(IReadOnlyList<SKBitmap> lines, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                Lines.Add([.. lines.Select(l => l.Copy())]);
            }

            return inner.RecognizeAsync(lines, ct);
        }

        public void DisposeLines()
        {
            foreach (var bitmap in Lines.Concat(Unsure).SelectMany(l => l))
            {
                bitmap.Dispose();
            }
        }
    }

    /// <summary>
    /// The price of speed on moving text (docs/spec/ocr.md "Measured: one reader"): the belt sharp, blurred by its motion and with
    /// smaller labels. Every frame's tracking code whose whole width lies inside the area must be read; any other match is wrong.
    /// </summary>
    [Fact]
    public async Task Moving_and_blurred_labels_read_their_values()
    {
        await using var recognizer = Cpu();
        await recognizer.CardTry;
        var characters = 0;
        var variants = new (string Name, int Width, int Height, double Scale, float Blur, int Quality)[]
        {
            ("sharp", 1920, 1080, 1, 0, 80),
            ("motion blur 2 px", 1920, 1080, 1, 2, 80),
            ("motion blur 4 px", 1920, 1080, 1, 4, 80),
            ("small labels", 1920, 1080, 0.7, 0, 80),
            ("small labels, blur 2 px", 1920, 1080, 0.7, 2, 80),
            ("very small labels", 1920, 1080, 0.55, 0, 80),
            ("very small, strong JPEG", 1920, 1080, 0.55, 0, 45),
            ("720p", 1280, 720, 1, 0, 80),
            ("720p, blur 2 px", 1280, 720, 1, 2, 80),
        };
        foreach (var (name, width, height, scale, blur, quality) in variants)
        {
            var frames = ParcelScene.Frames(width, height, 32, scale, blur, quality);
            var seen = 0;
            var read = 0;
            var wrong = 0;
            var other = 0;
            var lines = 0;
            for (var i = 0; i < frames.Count; i++)
            {
                var result = await recognizer.ReadDetailedAsync(frames[i], Areas(Tracking), RecognitionOptions.Default, CancellationToken.None);
                var found = result.Areas[0].Lines.Select(l => l.FormatMatch).OfType<string>().ToHashSet(StringComparer.Ordinal);
                lines += result.Areas[0].Lines.Count;
                var inside = ParcelScene.CodesInView(i, scale, 660, 1260);
                var any = ParcelScene.CodesTouching(i, 660, 1260);
                seen += inside.Count;
                read += inside.Count(found.Contains);
                wrong += found.Count(f => f.StartsWith("DE", StringComparison.Ordinal) && !any.Contains(f));
                other += found.Count(f => !f.StartsWith("DE", StringComparison.Ordinal));
            }

            characters++;
            Say(string.Create(CultureInfo.InvariantCulture, $"{name}: {read}/{seen} values read ({(seen == 0 ? 0 : 100.0 * read / seen):0.0} %), {wrong} wrong matches, {other} matches of other lines, {lines / (double)frames.Count:0.0} lines per picture"));
        }

        Assert.Equal(variants.Length, characters);
    }

    /// <summary>
    /// Formats with dashes on a label that also has a barcode line and a reference without dashes (docs/spec/ocr.md "Measured: one
    /// reader"): two whole parcels pass, every frame a watching reader would read at 15 frames per second. Per frame the area's
    /// value against the label; per reader the rows a reader adds, with the vote and with Moving objects, also when only every
    /// second frame gets read (a CPU at 8 per second).
    /// </summary>
    [Fact]
    public async Task Dash_formats_on_moving_labels()
    {
        await using var recognizer = Cpu();
        await recognizer.CardTry;
        const int Count = 110;
        var variants = new (string Name, int Width, int Height, double Scale, float Blur, int Quality)[]
        {
            ("sharp", 1920, 1080, 1, 0, 80),
            ("motion blur 2 px", 1920, 1080, 1, 2, 80),
            ("motion blur 4 px", 1920, 1080, 1, 4, 80),
            ("small labels, blur 2 px", 1920, 1080, 0.7, 2, 80),
            ("very small, strong JPEG", 1920, 1080, 0.55, 0, 45),
            ("720p, blur 2 px", 1280, 720, 1, 2, 80),
        };
        var formats = new (string Format, Func<int, string> Truth)[]
        {
            (Tracking, ParcelScene.CodeOf),
            ("####-####", ParcelScene.OrderOf),
            ("####-##-##", _ => "2026-11-04"),
        };
        var totals = new int[5];
        var misread = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var (name, width, height, scale, blur, quality) in variants)
        {
            var frames = ParcelScene.Frames(width, height, Count, scale, blur, quality, barcode: true);
            foreach (var (format, truth) in formats)
            {
                var results = await Task.WhenAll(frames.Select(f => recognizer.ReadDetailedAsync(f, Areas(format), RecognitionOptions.Default, CancellationToken.None)));
                var expected = ExpectedText.WithFormat(format);
                var values = new List<AreaResult>(results.Length);
                var (seen, read, wrong) = (0, 0, 0);
                for (var i = 0; i < results.Length; i++)
                {
                    var match = TextMatcher.MatchAll([.. results[i].Areas[0].Lines.Select(l => l.Line)], expected);
                    var result = new AreaResult { Area = "Parcel", Text = match.Text, TextRead = match.RawText, Confidence = match.Confidence, MatchesFormat = match.MatchesFormat, Lines = match.Lines };
                    values.Add(result);
                    var inside = ParcelScene.ParcelsInView(i, scale, 660, 1260).Select(truth).ToList();
                    var touching = ParcelScene.ParcelsTouching(i, 660, 1260).Select(truth).ToHashSet(StringComparer.Ordinal);
                    seen += inside.Count;
                    read += inside.Contains(match.Text, StringComparer.Ordinal) ? 1 : 0;
                    if (match.Text.Length > 0 && !touching.Contains(match.Text))
                    {
                        wrong++;
                        misread[match.Text] = misread.GetValueOrDefault(match.Text) + 1;
                    }
                }

                totals[0] += seen;
                totals[1] += read;
                totals[2] += wrong;
                var right = Enumerable.Range(-1, 6).Select(truth).ToHashSet(StringComparer.Ordinal);
                var vote = Rows(values, expected, moving: false, step: 1, right);
                var moving = Rows(values, expected, moving: true, step: 1, right);
                var votedSlow = Rows(values, expected, moving: false, step: 2, right);
                var movingSlow = Rows(values, expected, moving: true, step: 2, right);
                totals[3] += vote.Wrong;
                totals[4] += moving.Wrong;
                Say(string.Create(CultureInfo.InvariantCulture, $"{name} {format}: frames {read}/{seen} read, {wrong} wrong; rows at 15 fps vote {vote.Right} right {vote.Wrong} wrong, moving {moving.Right} right {moving.Wrong} wrong; every second frame vote {votedSlow.Right}/{votedSlow.Wrong}, moving {movingSlow.Right}/{movingSlow.Wrong}"));
            }
        }

        Say(string.Create(CultureInfo.InvariantCulture, $"All: frames {totals[1]}/{totals[0]} read, {totals[2]} wrong; rows with a wrong value: vote {totals[3]}, moving {totals[4]}"));
        Say("Wrong values: " + string.Join(", ", misread.OrderByDescending(m => m.Value).Take(24).Select(m => $"{m.Key} ({m.Value})")));
    }

    /// <summary>The rows a watching reader adds over <paramref name="values"/> and how many hold a value of no label.</summary>
    private static (int Right, int Wrong) Rows(List<AreaResult> values, ExpectedText expected, bool moving, int step, HashSet<string> right)
    {
        var watch = new AreaWatch(expected, moving);
        var last = string.Empty;
        var (good, bad) = (0, 0);
        for (var i = 0; i < values.Count; i += step)
        {
            var added = watch.Add(values[i]);
            if (added.Publish && added.Voted.Text.Length > 0 && added.Voted.Text != last)
            {
                _ = right.Contains(added.Voted.Text) ? good++ : bad++;
            }

            last = added.Publish ? added.Voted.Text : last;
        }

        return (good, bad);
    }

    [Fact]
    public async Task Twenty_readers_at_once()
    {
        await using var recognizer = Cpu();
        await recognizer.CardTry;
        var box = await File.ReadAllBytesAsync(Path.Combine(AccuracySet.Folder, "box-1920x1080.jpg"));
        Area[] small = [new() { Name = "Brand", Box = new RelativeBox(0.28, 0.54, 0.16, 0.10) }];
        var frames = ParcelScene.Frames(1920, 1080, 8);
        await recognizer.ReadAsync(box, small, CancellationToken.None);
        await recognizer.ReadAsync(frames[0], Areas(Tracking), CancellationToken.None);

        foreach (var round in new[] { 1, 2 })
        {
            var watch = Stopwatch.StartNew();
            await Task.WhenAll(Enumerable.Range(0, 20).Select(async _ =>
            {
                for (var i = 0; i < 10; i++)
                {
                    await recognizer.ReadAsync(box, small, CancellationToken.None);
                }
            }));
            Say(string.Create(CultureInfo.InvariantCulture, $"20 fixed-place readers at once, round {round}: {200 / watch.Elapsed.TotalSeconds:0.0} pictures per second in all"));

            watch.Restart();
            await Task.WhenAll(Enumerable.Range(0, 20).Select(async reader =>
            {
                for (var i = 0; i < 4; i++)
                {
                    await recognizer.ReadAsync(frames[(reader + i) % frames.Count], Areas(Tracking), CancellationToken.None);
                }
            }));
            Say(string.Create(CultureInfo.InvariantCulture, $"20 parcel readers at once, round {round}: {80 / watch.Elapsed.TotalSeconds:0.0} pictures per second in all"));
        }
    }

    /// <summary>The process's memory while every worker is busy: run this test alone, in a process of its own.</summary>
    [Fact]
    public async Task Memory_with_every_worker_busy()
    {
        Say(string.Create(CultureInfo.InvariantCulture, $"Before the models: {Memory()}"));
        await using var recognizer = Cpu();
        await recognizer.CardTry;
        var frames = ParcelScene.Frames(1920, 1080, 8);
        var search = new Area[] { new() { Name = "Search", Box = new RelativeBox(0.05, 0.05, 0.9, 0.9), Expected = ExpectedText.WithFormat(Tracking) } };
        await recognizer.ReadAsync(frames[0], Areas(Tracking), CancellationToken.None);
        Say($"After one picture: {Memory()}");
        for (var round = 0; round < 3; round++)
        {
            await Task.WhenAll(Enumerable.Range(0, 24).Select(async reader =>
            {
                for (var i = 0; i < 6; i++)
                {
                    await recognizer.ReadAsync(frames[(reader + i) % frames.Count], Areas(Tracking), CancellationToken.None);
                }
            }));
            Say($"After 24 readers, round {round + 1}: {Memory()}");
        }

        await Task.WhenAll(Enumerable.Range(0, 12).Select(reader => recognizer.ReadAsync(frames[reader % frames.Count], search, CancellationToken.None)));
        Say($"After 12 Search readings: {Memory()}");
        using var process = Process.GetCurrentProcess();
        Say(string.Create(CultureInfo.InvariantCulture, $"Peak working set {process.PeakWorkingSet64 / (1024.0 * 1024):0} MB"));
    }

    private static string CardNow()
    {
        try
        {
            using var smi = Process.Start(new ProcessStartInfo("nvidia-smi", "--query-gpu=utilization.gpu,memory.used --format=csv,noheader") { RedirectStandardOutput = true, UseShellExecute = false })!;
            var text = smi.StandardOutput.ReadToEnd().Trim();
            smi.WaitForExit();
            return text;
        }
#pragma warning disable CA1031 // Only a figure of a measurement.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return ex.Message;
        }
    }

    private static string Memory()
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        return string.Create(CultureInfo.InvariantCulture, $"working set {process.WorkingSet64 / (1024.0 * 1024):0} MB, private {process.PrivateMemorySize64 / (1024.0 * 1024):0} MB, GC heap {GC.GetTotalMemory(false) / (1024.0 * 1024):0} MB, threads {process.Threads.Count}");
    }

    /// <summary>The fixture's recognizer, never disposed by the plugin's stop; its load reaches the stats.</summary>
    private sealed class Borrowed(OnnxTextRecognizer inner) : ITextRecognizer, IRecognizerLoad
    {
        public TimeSpan BusyTime => inner.BusyTime;

        public int Capacity => inner.Capacity;

        public bool HasRoom => inner.HasRoom;

        public Task<RecognizedPicture> ReadAsync(ReadOnlyMemory<byte> jpeg, IReadOnlyList<Area> areas, CancellationToken ct) => inner.ReadAsync(jpeg, areas, ct);

        public Task<RecognizedPicture> ReadAsync(ReadOnlyMemory<byte> jpeg, IReadOnlyList<Area> areas, string? reuseSource, CancellationToken ct) =>
            ((ITextRecognizer)inner).ReadAsync(jpeg, areas, reuseSource, ct);
    }

    /// <summary>Counts what a picture asks of the models.</summary>
    private sealed class CountingRunner(IModelRunner inner) : IModelRunner
    {
        private int _calls;

        public int Lines;
        public int Angle;
        public int Turned;

        public CharacterList Characters => inner.Characters;

        public ITextFinder Search => inner.Search;

        public bool SearchOnCard => inner.SearchOnCard;

        public bool BatchTiles => inner.BatchTiles;

        public int DetectionSide(int pixels) => inner.DetectionSide(pixels);

        public int TilesPerCall(int tiles, int tileWidth, int tileHeight) => inner.TilesPerCall(tiles, tileWidth, tileHeight);

        public Task<PooledFloats> DetectAsync(PooledFloats input, int count, int height, int width, CancellationToken ct)
        {
            _calls = 0;
            return inner.DetectAsync(input, count, height, width, ct);
        }

        public Task<float[]> UpsideDownAsync(IReadOnlyList<SKBitmap> lines, CancellationToken ct)
        {
            Interlocked.Add(ref Angle, lines.Count);
            return inner.UpsideDownAsync(lines, ct);
        }

        public Task<LineScores[]> RecognizeAsync(IReadOnlyList<SKBitmap> lines, CancellationToken ct)
        {
            // The first call of a picture reads its lines, a later one the turned lines or the whole area.
            var call = Interlocked.Increment(ref _calls);
            if (call == 1)
            {
                Interlocked.Add(ref Lines, lines.Count);
            }
            else
            {
                Interlocked.Add(ref Turned, lines.Count);
            }

            return inner.RecognizeAsync(lines, ct);
        }
    }
}

/// <summary>
/// A belt of parcels (docs/spec/ocr.md "Measured: one reader"): a label of 15 lines on each parcel, in the middle of the
/// label a tracking code of the format <see cref="OneReaderSpeedTests.Tracking"/>, moving 12 px per frame in full HD.
/// </summary>
internal static class ParcelScene
{
    private const int Pitch = 640;

    private const int Speed = 12;

    private const int LabelLeft = 82;

    private const int LabelFont = 38;

    /// <param name="scale">The labels' text against the normal size.</param>
    /// <param name="blur">The belt's motion blur: the standard deviation along the belt, in pixels.</param>
    /// <param name="quality">The JPEG quality.</param>
    /// <param name="barcode">The label's last lines are a barcode line and a reference, both without dashes.</param>
    public static List<byte[]> Frames(int width, int height, int count, double scale = 1, float blur = 0, int quality = 80, bool barcode = false)
    {
        using var belt = BeltTexture();
        var frames = new byte[count][];
        Parallel.For(0, count, i =>
        {
            using var sharp = new SKBitmap(new SKImageInfo(1920, 1080, SKColorType.Rgba8888, SKAlphaType.Premul));
            using (var canvas = new SKCanvas(sharp))
            {
                canvas.DrawBitmap(belt, 0, 0);
                var offset = OffsetOf(i);
                for (var parcel = -1; parcel < 5; parcel++)
                {
                    DrawParcel(canvas, parcel * Pitch + offset - Pitch, parcel, (float)scale, barcode);
                }
            }

            using var full = blur > 0 ? Blurred(sharp, blur) : sharp.Copy();
            using var scaled = width == 1920 ? full.Copy() : full.Resize(new SKImageInfo(width, height), new SKSamplingOptions(SKCubicResampler.Mitchell));
            using var data = scaled.Encode(SKEncodedImageFormat.Jpeg, quality);
            frames[i] = data.ToArray();
        });
        return [.. frames];
    }

    /// <summary>The tracking codes of frame <paramref name="frame"/> whose whole width lies between the two picture columns.</summary>
    public static List<string> CodesInView(int frame, double scale, int left, int right)
    {
        var codes = new List<string>();
        var offset = OffsetOf(frame);
        for (var parcel = -1; parcel < 5; parcel++)
        {
            var x = parcel * Pitch + offset - Pitch + LabelLeft;
            var code = Code(parcel);
            if (x >= left + 8 && x + (LabelFont * 0.62 * code.Length * scale) <= right - 8)
            {
                codes.Add(code);
            }
        }

        return codes;
    }

    /// <summary>Every code on a parcel that touches the columns: a match of a cut-off code is no wrong value of another parcel.</summary>
    public static HashSet<string> CodesTouching(int frame, int left, int right)
    {
        var codes = new HashSet<string>(StringComparer.Ordinal);
        var offset = OffsetOf(frame);
        for (var parcel = -1; parcel < 5; parcel++)
        {
            var x = parcel * Pitch + offset - Pitch + LabelLeft;
            if (x < right && x + 400 > left)
            {
                codes.Add(Code(parcel));
            }
        }

        return codes;
    }

    /// <summary>The parcels of frame <paramref name="frame"/> whose code lies whole between the two picture columns.</summary>
    public static List<int> ParcelsInView(int frame, double scale, int left, int right)
    {
        var offset = OffsetOf(frame);
        return [.. Enumerable.Range(-1, 6).Where(parcel =>
        {
            var x = parcel * Pitch + offset - Pitch + LabelLeft;
            return x >= left + 8 && x + (LabelFont * 0.62 * 10 * scale) <= right - 8;
        })];
    }

    /// <summary>The parcels of frame <paramref name="frame"/> that touch the two picture columns.</summary>
    public static List<int> ParcelsTouching(int frame, int left, int right)
    {
        var offset = OffsetOf(frame);
        return [.. Enumerable.Range(-1, 6).Where(parcel =>
        {
            var x = parcel * Pitch + offset - Pitch + LabelLeft;
            return x < right && x + 400 > left;
        })];
    }

    /// <summary>The tracking code of a parcel.</summary>
    public static string CodeOf(int parcel) => Code(parcel);

    /// <summary>The order number on a parcel's label.</summary>
    public static string OrderOf(int parcel) => NumberOf(parcel) + "-2208";

    private static int OffsetOf(int frame) => (frame * Speed) % (Pitch * 4);

    private static SKBitmap Blurred(SKBitmap sharp, float sigma)
    {
        var blurred = new SKBitmap(sharp.Info);
        using var canvas = new SKCanvas(blurred);
        using var filter = SKImageFilter.CreateBlur(sigma, 0);
        using var paint = new SKPaint { ImageFilter = filter };
        using var image = SKImage.FromBitmap(sharp);
        canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest), paint);
        return blurred;
    }

    private static SKBitmap BeltTexture()
    {
        var random = new Random(7);
        var belt = new SKBitmap(new SKImageInfo(1920, 1080, SKColorType.Rgba8888, SKAlphaType.Premul));
        var pixels = belt.GetPixelSpan();
        for (var y = 0; y < 1080; y++)
        {
            for (var x = 0; x < 1920; x++)
            {
                var i = (y * belt.RowBytes) + (x * 4);
                var level = (byte)(58 + random.Next(16) + ((x / 40) % 2 * 6));
                pixels[i] = level;
                pixels[i + 1] = level;
                pixels[i + 2] = level;
                pixels[i + 3] = 255;
            }
        }

        return belt;
    }

    private static void DrawParcel(SKCanvas canvas, int x, int number, float scale, bool barcode)
    {
        using var paint = new SKPaint { IsAntialias = true, Color = new SKColor(171, 134, 90) };
        canvas.DrawRect(x + 16, 120, Pitch - 32, 840, paint);
        paint.Color = new SKColor(248, 248, 244);
        canvas.DrawRect(x + 56, 180, 520, 720, paint);
        paint.Color = new SKColor(24, 24, 28);
        using var typeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Normal);
        using var bold = SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold);
        using var font = new SKFont(typeface, 27 * scale);
        using var large = new SKFont(bold, LabelFont * scale);
        var y = 226f;
        foreach (var line in Label(number, barcode))
        {
            var tracking = line.Length == 10 && line[2] == '-';
            canvas.DrawText(line, x + LabelLeft, y, tracking ? large : font, paint);
            y += (tracking ? 58 : 44) * scale;
        }
    }

    private static int NumberOf(int parcel) => 1000 + (Math.Abs(parcel) * 379);

    private static string Code(int parcel)
    {
        var n = NumberOf(parcel);
        return string.Create(CultureInfo.InvariantCulture, $"DE-{n % 9000 + 1000}-{Math.Abs(parcel) + 39}");
    }

    private static IEnumerable<string> Label(int parcel, bool barcode)
    {
        var n = NumberOf(parcel);
        IEnumerable<string> lines =
        [
            "FROM Nordic Parts GmbH",
            "Industriestrasse " + (n % 90),
            "70565 Stuttgart",
            "TO Harbour Logistics",
            "Attn Receiving Dock " + (n % 7),
            "Bahnhofstrasse " + (n % 300),
            "8001 Zurich",
            Code(parcel),
            "ORDER " + n + "-2208",
            string.Create(CultureInfo.InvariantCulture, $"WEIGHT {n % 40}.4 kg"),
            "PIECES 3 OF 5",
            "DATE 2026-11-04",
            "ROUTE " + (n % 80) + "B",
            "REF CP0" + n + "00",
            "HANDLE WITH CARE",
        ];
        return barcode ? lines.Take(13).Append("CP0" + n + "0 78 3 AT").Append(string.Create(CultureInfo.InvariantCulture, $"CX {n} {n % 9000 + 1000} 0 AT")) : lines;
    }
}
