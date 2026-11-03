using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Oadm.Plugins.Ocr.Matching;
using Oadm.Plugins.Ocr.Model;
using SkiaSharp;

namespace Oadm.Plugins.Ocr.Recognition;

/// <summary>How the recognizer starts.</summary>
public sealed record OcrEngineOptions
{
    /// <summary>The folder with the model files (<see cref="ModelFiles.Folder"/>).</summary>
    public required string ModelDirectory { get; init; }

    /// <summary>The page's "Use the graphics card" (on by default); off forces the CPU.</summary>
    public bool UseGraphicsCard { get; init; } = true;

    /// <summary>The clock of the load measurement (tests pass a fake one).</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>The processor count the worker limit is computed from (<see cref="Environment.ProcessorCount"/>).</summary>
    public int ProcessorCount { get; init; } = Environment.ProcessorCount;

    /// <summary>The models on the CPU (tests compare the sizes).</summary>
    internal ModelChoice CpuModels { get; init; } = ModelChoice.Mobile;

    /// <summary>The models for Accurate; its text finder is the graphics card's for Search.</summary>
    internal ModelChoice AccurateModels { get; init; } = ModelChoice.Server;

    /// <summary>The graphics card's memory cap (<see cref="GpuSessionBudget.MaxBytes"/>; measurements: less).</summary>
    internal long GpuMaxBytes { get; init; } = GpuSessionBudget.MaxBytes;
}

/// <summary>
/// Reads the text in a picture with ONNX Runtime (docs/spec/ocr.md "Reading", "Accuracy" and "GPU"): decodes the JPEG and cuts
/// out each area at full resolution (SkiaSharp), finds the lines (PP-OCRv5 detection; areas larger than
/// <see cref="TileSize"/> pixels in overlapping tiles, boxes found twice merged), turns each line level (90 degrees for tall
/// boxes, the angle model for upside down), scales it to the reader's 48 pixels (small text enlarged, text below
/// <see cref="MinTextHeight"/> pixels left out), reads it (PP-OCRv5 Latin, CTC) and, with a format, decodes the format's
/// match with the characters limited per position. Every model call on the CPU (a tile, a batch of lines) takes one of
/// <see cref="OcrLimits.Workers"/> workers with sessions of its own of <see cref="OcrLimits.ThreadsPerWorker"/> threads, so an
/// idle server reads the tiles of one picture at once. With a graphics card only the text finder of Search (an area read in
/// tiles) runs there, one call at a time within a memory cap by picture size (<see cref="GpuSessionBudget"/>); fixed-place
/// areas, the reader and the angle model stay on the CPU workers (measured: the card is faster only for Search). At most
/// <see cref="OcrLimits.Workers"/> pictures are in work at once.
/// </summary>
public sealed class OnnxTextRecognizer : IDetailedTextRecognizer, IEngineInfo, IRecognizerLoad, IDisposable
{

    /// <summary>The tile size for large areas.</summary>
    public const int TileSize = 960;

    /// <summary>Text finder calls of one area prepared for the graphics card at once (one runs, the next ones get ready).</summary>
    private const int CardCallsAhead = 3;

    /// <summary>The overlap of neighbouring tiles.</summary>
    public const double TileOverlap = 0.15;

    /// <summary>Text lower than this (pixels in the picture) is left out.</summary>
    public const float MinTextHeight = 10;

    /// <summary>The page's text when lines were left out for their size.</summary>
    public const string TextTooSmall = "Text too small. Draw a smaller area or choose a higher resolution.";

    /// <summary>Lines whose mean character probability is below this (0 to 1) are dropped.</summary>
    private const float MinMeanProbability = (float)(OcrLimits.MinConfidence / 100);

    /// <summary>The area's margin for the text finder (text at the area's edge is still found).</summary>
    private const int Margin = 16;

    /// <summary>The smallest side the text finder gets (smaller areas are scaled up).</summary>
    private const int MinDetectionSide = 64;

    /// <summary>From this probability of the angle model on, a line is read both ways.</summary>
    private const float UpsideDownCandidate = 0.3f;

    /// <summary>The most accurate sets on the CPU at once (the server models are large).</summary>
    private const int MaxAccurateCpuSets = 2;

    private readonly ILogger _logger;
    private readonly SemaphoreSlim _pictures;
    private readonly CpuWorkers<ModelSet> _cpu;
    private readonly CharacterList _cpuCharacters;
    private readonly CharacterList _accurateCharacters;
    private readonly CardTextFinder? _card;
    private readonly GpuSessionBudget? _gpuBudget;
    private readonly RecognitionDevice _cpuDevice;
    private readonly UnchangedAreas _unchanged = new();
    private readonly RecognitionDevice? _gpuDevice;
    private readonly BusyMeter _cpuBusy;
    private readonly BusyMeter _cardBusy;
    private readonly CombinedBusy _busy;
    private volatile bool _useGpu = true;
    private bool _disposed;

    public OnnxTextRecognizer(OcrEngineOptions options, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger ?? NullLogger.Instance;
        Workers = OcrLimits.Workers(options.ProcessorCount);
        _pictures = new SemaphoreSlim(Workers, Workers);
        _cpuBusy = new BusyMeter(options.Time);
        _cardBusy = new BusyMeter(options.Time);
        _busy = new CombinedBusy(_cpuBusy, _cardBusy, Workers);
        var folder = options.ModelDirectory;
        _cpuCharacters = CharacterList.Load(Path.Combine(folder, options.CpuModels.Characters));
        _accurateCharacters = CharacterList.Load(Path.Combine(folder, options.AccurateModels.Characters));
        _cpu = new CpuWorkers<ModelSet>(
            Workers,
            Math.Min(Workers, MaxAccurateCpuSets),
            accurate => new ModelSet(folder, accurate ? options.AccurateModels : options.CpuModels, OcrLimits.ThreadsPerWorker),
            _cpuBusy);

        var gpu = options.UseGraphicsCard
            ? SessionFactory.TryGraphicsCard(Path.Combine(folder, ModelFiles.DetectionMobile), _logger)
            : null;
        if (gpu is { } target)
        {
            try
            {
                _gpuBudget = target == ExecutionTarget.DirectMl
                    ? new GpuSessionBudget(options.GpuMaxBytes, options.Time, max => RecognitionLog.GraphicsCardSessionsCapped(_logger, max / (1024 * 1024)))
                    : null;
                _card = new CardTextFinder(Path.Combine(folder, options.AccurateModels.Detection), target, _cardBusy, _gpuBudget);
                _gpuDevice = new RecognitionDevice(true, SearchOnCardName(options.ProcessorCount, SessionFactory.GraphicsCardName(target)));
            }
#pragma warning disable CA1031 // A graphics card that fails with the real models falls back to the CPU too.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                RecognitionLog.GraphicsCardFailed(_logger, ex.Message);
                _card?.Dispose();
                _card = null;
                _gpuBudget?.Dispose();
                _gpuBudget = null;
                _gpuDevice = null;
            }
        }

        _cpuDevice = new RecognitionDevice(false, CpuName(options.ProcessorCount));
        RecognitionLog.RunsOn(_logger, RunsOn.Name);
    }

    private static string CpuName(int cores) => $"CPU ({cores} cores)";

    /// <summary>What reads with a graphics card: "CPU (24 cores) · search on NVIDIA GeForce RTX 5070 Ti".</summary>
    internal static string SearchOnCardName(int cores, string card) => $"{CpuName(cores)} · search on {card}";

    /// <summary>
    /// What runs the recognition now: the CPU, with Search on the graphics card while there is one and "Use the graphics card"
    /// is on ("CPU (24 cores) · search on NVIDIA GeForce RTX 5070 Ti").
    /// </summary>
    public RecognitionDevice RunsOn => OnGpu ? _gpuDevice! : _cpuDevice;

    /// <inheritdoc />
    string IEngineInfo.RunsOn => RunsOn.Name;

    /// <summary>A graphics card passed the test reading and holds the models.</summary>
    public bool GpuAvailable => _card is not null;

    /// <summary>True while the text finder of Search runs on the graphics card.</summary>
    public bool OnGpu => _useGpu && _card is not null;

    /// <summary>
    /// "Use the graphics card" (docs/spec/ocr.md "GPU"): off sends the next readings to the CPU, on back to the graphics card
    /// when there is one. Readings in work finish where they started; the graphics card keeps its models until disposed.
    /// </summary>
    public void SetUseGpu(bool useGpu)
    {
        if (_useGpu == useGpu)
        {
            return;
        }

        _useGpu = useGpu;
        RecognitionLog.RunsOn(_logger, RunsOn.Name);
    }

    /// <summary>
    /// The time model calls held the CPU workers or the graphics card, in worker time (<see cref="CombinedBusy"/>: per sample
    /// the larger of the two, the card's time counted <see cref="Workers"/> times as its calls run one at a time).
    /// </summary>
    public TimeSpan BusyTime => _busy.Busy;

    /// <summary>Model calls at once: the CPU workers (the graphics card's time is counted in worker time).</summary>
    public int Capacity => Workers;

    /// <summary>The graphics card's sessions (tests, measurements); null without DirectML.</summary>
    internal GpuSessionBudget? GpuBudget => _gpuBudget;

    /// <summary>CPU workers: model calls at once (<see cref="OcrLimits.Workers"/>), and pictures in work at once.</summary>
    public int Workers { get; }

    /// <summary>The CPU workers (tests: the gate).</summary>
    internal CpuWorkers<ModelSet> CpuWorkers => _cpu;

    /// <inheritdoc />
    public async Task<RecognizedPicture> ReadAsync(ReadOnlyMemory<byte> jpeg, IReadOnlyList<Area> areas, CancellationToken ct) =>
        (await ReadDetailedAsync(jpeg, areas, RecognitionOptions.Default, ct).ConfigureAwait(false)).ToPicture();

    /// <inheritdoc />
    public async Task<RecognitionResult> ReadDetailedAsync(ReadOnlyMemory<byte> jpeg, IReadOnlyList<Area> areas, RecognitionOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(areas);
        ArgumentNullException.ThrowIfNull(options);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (areas.Count == 0)
        {
            // Text is read only inside areas: nothing to do, the picture is not decoded.
            return new RecognitionResult(default, []);
        }

        IModelRunner runner = new CpuRunner(_cpu, options.Accurate ? _accurateCharacters : _cpuCharacters, options.Accurate, _useGpu ? _card : null);
        await _pictures.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // On a pool thread: the caller's thread never carries the work.
            return await Task.Run(
                async () =>
                {
                    var decodeStart = Stopwatch.GetTimestamp();
                    using var picture = Picture.Decode(jpeg, (w, h) => NeededRegion(areas, w, h));
                    StageClock.Add(Stage.Decode, decodeStart);
                    return options.ReuseUnchanged
                        ? await ReadChangedAsync(picture, areas, runner, options, ct).ConfigureAwait(false)
                        : await ReadWithAsync(picture, areas, runner, ct).ConfigureAwait(false);
                },
                ct).ConfigureAwait(false);
        }
        finally
        {
            _pictures.Release();
        }
    }

    /// <summary>Reads only the areas whose pixels changed since their last reading; the others give that reading again.</summary>
    private async Task<RecognitionResult> ReadChangedAsync(Picture picture, IReadOnlyList<Area> areas, IModelRunner runner, RecognitionOptions options, CancellationToken ct)
    {
        var results = new AreaRecognition?[areas.Count];
        var keys = new UnchangedAreas.Key[areas.Count];
        var prints = new float[areas.Count][];
        var changed = new List<int>();
        for (var a = 0; a < areas.Count; a++)
        {
            var area = areas[a];
            var rect = PixelRect(area.Box, picture.Width, picture.Height);
            keys[a] = new UnchangedAreas.Key(options.Source, rect, picture.Width, picture.Height, area.Expected.Kind, area.Expected.Format, options.Accurate, runner.SearchOnCard);
            prints[a] = UnchangedAreas.Print(picture, rect);
            results[a] = _unchanged.TryReuse(keys[a], prints[a]);
            if (results[a] is null)
            {
                changed.Add(a);
            }
        }

        var tooMany = false;
        if (changed.Count > 0)
        {
            var fresh = await ReadWithAsync(picture, [.. changed.Select(a => areas[a])], runner, ct).ConfigureAwait(false);
            tooMany = fresh.TooManyTexts;
            for (var i = 0; i < changed.Count; i++)
            {
                var a = changed[i];
                results[a] = fresh.Areas[i];

                // A reading capped at MaxTextBoxes is never kept: the next picture reads again and carries the note again.
                if (!tooMany)
                {
                    _unchanged.Keep(keys[a], prints[a], fresh.Areas[i]);
                }
            }
        }

        return new RecognitionResult(new Resolution(picture.Width, picture.Height), [.. results.Select(r => r!)])
        {
            TooManyTexts = tooMany,
            ReusedAreas = areas.Count - changed.Count,
        };
    }

    /// <summary>The areas whose last reading was used again (tests).</summary>
    internal UnchangedAreas Unchanged => _unchanged;

    internal static async Task<RecognitionResult> ReadWithAsync(Picture picture, IReadOnlyList<Area> areas, IModelRunner set, CancellationToken ct)
    {
        var results = new AreaRecognition[areas.Count];
        var lineBitmaps = new List<SKBitmap>();
        var lineBoxes = new List<(int Area, RotatedBox Box)>();
        var tooSmall = new int[areas.Count];
        var tooMany = false;
        try
        {
            // The areas' text finding at once (on the CPU each call takes a worker of its own).
            var found = await Task.WhenAll(areas.Select(area =>
            {
                var rect = PixelRect(area.Box, picture.Width, picture.Height);
                return rect.Width < 4 || rect.Height < 4 ? Task.FromResult(new List<RotatedBox>()) : FindLinesAsync(picture, rect, set, ct);
            })).ConfigureAwait(false);
            var candidates = new List<(int Area, RotatedBox Box)>();
            for (var a = 0; a < areas.Count; a++)
            {
                foreach (var box in found[a])
                {
                    if (box.TextHeight > 0 && box.TextHeight < MinTextHeight)
                    {
                        tooSmall[a]++;
                        continue;
                    }

                    candidates.Add((a, box));
                }
            }

            // At most MaxTextBoxes over all areas together, the highest detection scores first (a busy picture never floods
            // the reader); the picture's reading says so.
            (candidates, tooMany) = Strongest(candidates, OcrLimits.MaxTextBoxes);
            var cutStart = Stopwatch.GetTimestamp();
            foreach (var (a, box) in candidates)
            {
                lineBitmaps.Add(CutLine(picture, box));
                lineBoxes.Add((a, box));
            }

            StageClock.Add(Stage.CutLines, cutStart);

            var own = lineBitmaps.Count;
            var scores = own == 0 ? [] : await set.RecognizeAsync(lineBitmaps, ct).ConfigureAwait(false);

            // Upside down lines (a receipt laid the other way round): the angle model names the candidates among the lines not
            // read surely upright; each candidate is read turned too and the reading the reader is surer of wins (the angle
            // model alone turns some coloured print).
            var unsure = Enumerable.Range(0, own).Where(i => !SurelyUpright(scores[i], set.Characters)).ToList();
            if (unsure.Count > 0)
            {
                var upsideDown = await set.UpsideDownAsync([.. unsure.Select(i => lineBitmaps[i])], ct).ConfigureAwait(false);
                var maybeTurned = unsure.Where((_, k) => upsideDown[k] >= UpsideDownCandidate).ToList();
                if (maybeTurned.Count > 0)
                {
                    var turned = maybeTurned.Select(i => Turn180(lineBitmaps[i])).ToList();
                    lineBitmaps.AddRange(turned);
                    var turnedScores = await set.RecognizeAsync(turned, ct).ConfigureAwait(false);
                    for (var k = 0; k < maybeTurned.Count; k++)
                    {
                        var i = maybeTurned[k];
                        if (Sureness(turnedScores[k], set.Characters) > Sureness(scores[i], set.Characters))
                        {
                            scores[i] = turnedScores[k];
                        }
                    }
                }
            }

            var perArea = new List<RecognizedLine>[areas.Count];
            for (var a = 0; a < areas.Count; a++)
            {
                perArea[a] = [];
            }

            var decodingStart = Stopwatch.GetTimestamp();
            var patterns = areas.Select(a => a.Expected.Kind == ExpectedTextKind.Format ? FormatPattern.Parse(a.Expected.Format) : null).ToArray();
            for (var i = 0; i < scores.Length; i++)
            {
                var (a, box) = lineBoxes[i];
                var line = LineReader.Read(scores[i], set.Characters, patterns[a], ToRelative(box, picture.Width, picture.Height));
                if (line is not null)
                {
                    perArea[a].Add(line);
                }
            }

            StageClock.Add(Stage.Decoding, decodingStart);

            await ReadWholeAreasAsync(picture, areas, patterns, perArea, set, ct).ConfigureAwait(false);
            for (var a = 0; a < areas.Count; a++)
            {
                results[a] = new AreaRecognition(Order(perArea[a]), tooSmall[a]);
            }

            return new RecognitionResult(new Resolution(picture.Width, picture.Height), results.Select(r => r ?? new AreaRecognition([], 0)).ToList())
            {
                TooManyTexts = tooMany,
            };
        }
        finally
        {
            foreach (var bitmap in lineBitmaps)
            {
                bitmap.Dispose();
            }
        }
    }

    /// <summary>
    /// The <paramref name="max"/> boxes with the highest detection scores (in their order), and whether any were dropped.
    /// </summary>
    internal static (List<(int Area, RotatedBox Box)> Kept, bool Dropped) Strongest(List<(int Area, RotatedBox Box)> boxes, int max)
    {
        if (boxes.Count <= max)
        {
            return (boxes, false);
        }

        var keep = Enumerable.Range(0, boxes.Count)
            .OrderByDescending(i => boxes[i].Box.Score)
            .ThenBy(i => i)
            .Take(max)
            .Order()
            .Select(i => boxes[i])
            .ToList();
        return (keep, true);
    }

    /// <summary>
    /// A fixed-place area with a format whose lines gave no match is read once more as a single line: the text finder can miss a
    /// part of a value (a dark digit drum of a meter), the reader on the whole strip often still reads it. Only strips (wider
    /// than tall) up to <see cref="TileSize"/> pixels; the extra reading is taken only when its format match holds.
    /// </summary>
    private static async Task ReadWholeAreasAsync(Picture picture, IReadOnlyList<Area> areas, FormatPattern?[] patterns, List<RecognizedLine>[] perArea, IModelRunner set, CancellationToken ct)
    {
        var strips = new List<(int Area, RotatedBox Box, SKBitmap Line)>();
        try
        {
            for (var a = 0; a < areas.Count; a++)
            {
                if (patterns[a] is null || perArea[a].Any(l => l.FormatMatch is not null))
                {
                    continue;
                }

                var rect = PixelRect(areas[a].Box, picture.Width, picture.Height);
                if (rect.Width < 1.5 * rect.Height || rect.Width > TileSize || rect.Height < MinTextHeight)
                {
                    continue;
                }

                var box = new RotatedBox(rect.MidX, rect.MidY, rect.Width, rect.Height, 0, 1) { TextHeight = rect.Height };
                strips.Add((a, box, CutLine(picture, box)));
            }

            if (strips.Count == 0)
            {
                return;
            }

            var scores = await set.RecognizeAsync(strips.Select(s => s.Line).ToList(), ct).ConfigureAwait(false);
            for (var i = 0; i < strips.Count; i++)
            {
                var (a, box, _) = strips[i];
                var line = LineReader.Read(scores[i], set.Characters, patterns[a], ToRelative(box, picture.Width, picture.Height));
                if (line?.FormatMatch is not null)
                {
                    perArea[a].Add(line);
                }
            }
        }
        finally
        {
            foreach (var strip in strips)
            {
                strip.Line.Dispose();
            }
        }
    }

    /// <summary>
    /// The part of the picture the areas need (only it is decoded): every area with a margin of <see cref="Margin"/> plus its
    /// shorter side, enough for the line boxes, which grow past the area's edge by less than a line height.
    /// </summary>
    internal static SKRectI NeededRegion(IReadOnlyList<Area> areas, int width, int height)
    {
        var region = SKRectI.Empty;
        foreach (var area in areas)
        {
            var rect = PixelRect(area.Box, width, height);
            var pad = Margin + Math.Min(rect.Width, rect.Height);
            rect = new SKRectI(rect.Left - pad, rect.Top - pad, rect.Right + pad, rect.Bottom + pad);
            region = region.IsEmpty ? rect : SKRectI.Union(region, rect);
        }

        return SKRectI.Intersect(region, new SKRectI(0, 0, width, height));
    }

    /// <summary>The line boxes of an area in picture pixels: in one piece up to <see cref="TileSize"/>, else in tiles.</summary>
    private static async Task<List<RotatedBox>> FindLinesAsync(Picture picture, SKRectI rect, IModelRunner set, CancellationToken ct)
    {
        if (Math.Max(rect.Width, rect.Height) <= TileSize)
        {
            var scale = Math.Max(1f, MinDetectionSide / (float)Math.Min(rect.Width, rect.Height));
            scale = Math.Min(scale, TileSize / (float)Math.Max(rect.Width, rect.Height));
            scale = Math.Max(scale, 1f);
            var w = set.DetectionSide((int)MathF.Ceiling(rect.Width * scale) + 2 * Margin);
            var h = set.DetectionSide((int)MathF.Ceiling(rect.Height * scale) + 2 * Margin);
            var matrix = SKMatrix.Concat(SKMatrix.CreateTranslation(Margin, Margin), SKMatrix.Concat(SKMatrix.CreateScale(scale, scale), SKMatrix.CreateTranslation(-rect.Left, -rect.Top)));
            var inputStart = Stopwatch.GetTimestamp();
            var fill = picture.BorderColor(rect);
            using var bitmap = picture.Render(matrix, w, h, scale, fill, new SKRect(Margin, Margin, Margin + rect.Width * scale, Margin + rect.Height * scale));
            using var input = PooledFloats.Rent(3 * w * h, clear: false);
            Tensors.Write(bitmap, input.Array, 0, h, w, Tensors.ImageNet);
            StageClock.Add(Stage.DetectInput, inputStart);
            using var map = await set.DetectAsync(input, 1, h, w, ct).ConfigureAwait(false);
            var boxesStart = Stopwatch.GetTimestamp();
            var boxes = BoxMerger.Merge(DbPostProcess.Boxes(map.Span, w, h).Select(b => b.Map(1 / scale, rect.Left - Margin / scale, rect.Top - Margin / scale)));
            StageClock.Add(Stage.DetectBoxes, boxesStart);
            return boxes;
        }

        // Tiles of TileSize at full resolution, overlapping by TileOverlap, on the Search text finder (the graphics card's when
        // it is used).
        var finder = set.Search;
        var (xs, tileW) = Tiles(rect.Left, rect.Width, finder.DetectionSide);
        var (ys, tileH) = Tiles(rect.Top, rect.Height, finder.DetectionSide);
        var tiles = new List<(int X, int Y)>();
        foreach (var y in ys)
        {
            foreach (var x in xs)
            {
                tiles.Add((x, y));
            }
        }

        var fillColor = picture.BorderColor(rect);
        var tileSize = tileW * tileH;
        var found = new List<RotatedBox>();
        if (finder.BatchTiles)
        {
            // A graphics card: the tiles in calls of one size that fits its memory cap (blank tiles fill the last one up), so
            // an area uses one session and no call exceeds the cap whatever the picture's size. The calls' inputs and boxes are
            // made on the CPU while the card runs another call (at most CardCallsAhead calls' buffers at once).
            var batch = finder.TilesPerCall(tiles.Count, tileW, tileH);
            using var ahead = new SemaphoreSlim(CardCallsAhead, CardCallsAhead);
            var calls = Enumerable.Range(0, (tiles.Count + batch - 1) / batch).Select(call => Task.Run(
                async () =>
                {
                    var chunk = tiles.GetRange(call * batch, Math.Min(batch, tiles.Count - (call * batch)));
                    await ahead.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        var inputStart = Stopwatch.GetTimestamp();
                        using var input = PooledFloats.Rent(batch * 3 * tileSize, clear: batch > chunk.Count);
                        Parallel.For(0, chunk.Count, i => picture.WriteTile(input.Array, i, chunk[i].X, chunk[i].Y, tileW, tileH, rect, fillColor, Tensors.ImageNet));
                        StageClock.Add(Stage.DetectInput, inputStart);
                        using var maps = await finder.DetectAsync(input, batch, tileH, tileW, ct).ConfigureAwait(false);
                        var boxesStart = Stopwatch.GetTimestamp();
                        var perTile = new List<RotatedBox>[chunk.Count];
                        Parallel.For(0, chunk.Count, i => perTile[i] = DbPostProcess.Boxes(maps.Array.AsSpan(i * tileSize, tileSize), tileW, tileH).Select(b => b.Map(1, chunk[i].X, chunk[i].Y)).ToList());
                        StageClock.Add(Stage.DetectBoxes, boxesStart);
                        return perTile.SelectMany(b => b).ToList();
                    }
                    finally
                    {
                        ahead.Release();
                    }
                },
                ct)).ToList();
            foreach (var boxes in await Task.WhenAll(calls).ConfigureAwait(false))
            {
                found.AddRange(boxes);
            }
        }
        else
        {
            // The CPU: each tile a call of its own, on as many workers as are free.
            var calls = tiles.Select(tile => Task.Run(
                async () =>
                {
                    var inputStart = Stopwatch.GetTimestamp();
                    using var input = PooledFloats.Rent(3 * tileSize, clear: false);
                    picture.WriteTile(input.Array, 0, tile.X, tile.Y, tileW, tileH, rect, fillColor, Tensors.ImageNet);
                    StageClock.Add(Stage.DetectInput, inputStart);
                    using var map = await finder.DetectAsync(input, 1, tileH, tileW, ct).ConfigureAwait(false);
                    var boxesStart = Stopwatch.GetTimestamp();
                    var boxes = DbPostProcess.Boxes(map.Span, tileW, tileH).Select(b => b.Map(1, tile.X, tile.Y)).ToList();
                    StageClock.Add(Stage.DetectBoxes, boxesStart);
                    return boxes;
                },
                ct));
            foreach (var boxes in await Task.WhenAll(calls).ConfigureAwait(false))
            {
                found.AddRange(boxes);
            }
        }

        return BoxMerger.Merge(found);
    }

    /// <summary>
    /// The tiles along one side: as few as cover it with tiles of at most <see cref="TileSize"/> pixels overlapping by at least
    /// <see cref="TileOverlap"/>, all of one size (a multiple of 32) and spread evenly, so no pixel is read more often than
    /// needed (1920 x 1080: 3 x 2 tiles of 736 x 608). <paramref name="side"/> turns a size into the text finder's input side
    /// (DirectML: in steps); a stepped tile overlaps its neighbours more, never pads.
    /// </summary>
    /// <returns>The left (or top) edges and the tile size (the text finder's input side).</returns>
    internal static (List<int> Starts, int Size) Tiles(int start, int length, Func<int, int>? side = null)
    {
        side ??= RoundUp32;
        if (length <= TileSize)
        {
            return ([start], side(length));
        }

        var n = (int)Math.Ceiling((length - TileOverlap * TileSize) / (TileSize * (1 - TileOverlap)));
        var size = side(Math.Min(TileSize, RoundUp32((int)Math.Ceiling(length / (n - (n - 1) * TileOverlap)))));
        if (size >= length)
        {
            return ([start], size);
        }

        var step = (length - size) / (double)(n - 1);
        var starts = new List<int>(n);
        for (var i = 0; i < n; i++)
        {
            starts.Add(start + (int)Math.Round(i * step));
        }

        return (starts, size);
    }

    /// <summary>
    /// The line cut out level and scaled to the reader's height: along its long side, a box clearly taller than wide (text
    /// turned by 90 degrees) turned upright first.
    /// </summary>
    private static SKBitmap CutLine(Picture picture, RotatedBox box)
    {
        var angle = box.Angle;
        var w = box.Width;
        var h = box.Height;
        if (h >= 1.5f * w)
        {
            // Turned by 90 degrees: read along the long side (the angle model turns it round when it is the other way).
            angle += MathF.PI / 2;
            (w, h) = (h, w);
        }

        var scale = ModelSet.LineHeight / h;
        var outW = Math.Clamp((int)MathF.Round(w * scale), 8, ModelSet.MaxLineWidth);
        var scaleX = outW / w;
        var matrix = SKMatrix.CreateTranslation(-box.CenterX, -box.CenterY);
        matrix = SKMatrix.Concat(SKMatrix.CreateRotation(-angle), matrix);
        matrix = SKMatrix.Concat(SKMatrix.CreateScale(scaleX, scale), matrix);
        matrix = SKMatrix.Concat(SKMatrix.CreateTranslation(outW / 2f, ModelSet.LineHeight / 2f), matrix);
        var line = picture.Render(matrix, outW, ModelSet.LineHeight, Math.Min(scale, scaleX), SKColors.Black);
        Levels.Stretch(line, Levels.LineContrast);
        return line;
    }

    /// <summary>Characters that read as another plausible character upside down (6 and 9, n and u, digits that look like letters).</summary>
    private const string Turnable = "0123456789HINOSXZMWnuoszxlpdbqhy-.,:;/'\" ";

    /// <summary>
    /// True when a line is read surely the right way up, so the angle model is not needed: at least 3 characters, every one
    /// at least 90 % likely, and at least 2 of them letters that turned upside down read as nothing plausible (a line of digits
    /// such as a meter always asks the angle model).
    /// </summary>
    internal static bool SurelyUpright(LineScores scores, CharacterList characters)
    {
        var chars = CtcDecoder.Greedy(scores, characters);
        if (chars.Count < 3)
        {
            return false;
        }

        var telling = 0;
        foreach (var c in chars)
        {
            if (c.Probability < 0.9f)
            {
                return false;
            }

            if (c.Text.Length == 1 && char.IsLetter(c.Text[0]) && !Turnable.Contains(c.Text[0], StringComparison.Ordinal))
            {
                telling++;
            }
        }

        return telling >= 2;
    }

    /// <summary>How sure the reader is of a line: the mean probability of its characters, less for fewer than 3 characters.</summary>
    private static double Sureness(LineScores scores, CharacterList characters)
    {
        var chars = CtcDecoder.Greedy(scores, characters);
        return chars.Count == 0 ? 0 : chars.Average(c => c.Probability) * Math.Min(1, chars.Count / 3.0);
    }

    private static SKBitmap Turn180(SKBitmap line)
    {
        var turned = new SKBitmap(line.Info);
        using var canvas = new SKCanvas(turned);
        canvas.RotateDegrees(180, line.Width / 2f, line.Height / 2f);
        canvas.DrawBitmap(line, 0, 0);
        canvas.Flush();
        return turned;
    }

    /// <summary>The lines in the matcher's reading order (<see cref="LineOrder"/>: rows top to bottom, in a row left to right).</summary>
    private static List<RecognizedLine> Order(List<RecognizedLine> lines)
    {
        var byLine = new Dictionary<TextLine, RecognizedLine>(ReferenceEqualityComparer.Instance);
        foreach (var line in lines)
        {
            byLine[line.Line] = line;
        }

        return [.. LineOrder.Order([.. lines.Select(l => l.Line)]).Select(l => byLine[l])];
    }

    private static SKRectI PixelRect(RelativeBox box, int width, int height)
    {
        var left = Math.Clamp((int)Math.Round(box.X * width), 0, width);
        var top = Math.Clamp((int)Math.Round(box.Y * height), 0, height);
        var right = Math.Clamp((int)Math.Round((box.X + box.Width) * width), left, width);
        var bottom = Math.Clamp((int)Math.Round((box.Y + box.Height) * height), top, height);
        return new SKRectI(left, top, right, bottom);
    }

    private static RelativeBox ToRelative(RotatedBox box, int width, int height)
    {
        var (l, t, r, b) = box.Bounds();
        var left = Math.Clamp(l, 0, width);
        var top = Math.Clamp(t, 0, height);
        var right = Math.Clamp(r, left, width);
        var bottom = Math.Clamp(b, top, height);
        return new RelativeBox(left / (double)width, top / (double)height, Math.Max(1e-6, (right - left) / (double)width), Math.Max(1e-6, (bottom - top) / (double)height));
    }

    internal static int RoundUp32(int value) => Math.Max(32, (value + 31) / 32 * 32);

    /// <summary>
    /// Waits for the readings in work (at most <see cref="DisposeWait"/>), then frees the models; readings asked for later end
    /// with <see cref="ObjectDisposedException"/>. The graphics card's text finder still in use after the wait is left to the process
    /// rather than freed under a running call.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var taken = 0;
        while (taken < Workers && _pictures.Wait(DisposeWait))
        {
            taken++;
        }

        // The card's calls run inside the readings: once all readings are out, none runs.
        if (taken == Workers)
        {
            _card?.Dispose();
            _gpuBudget?.Dispose();
        }
        else
        {
            RecognitionLog.StillReading(_logger);
        }

        // In-use CPU sets are freed when their call ends.
        _cpu.Dispose();

        // Readings that waited for a slot get one, see the recognizer disposed and end.
        if (taken > 0)
        {
            _pictures.Release(taken);
        }
    }

    /// <summary>How long <see cref="Dispose"/> waits for the readings in work.</summary>
    internal static readonly TimeSpan DisposeWait = TimeSpan.FromSeconds(30);

    /// <summary>Lines whose mean probability is below the spec's 50 % are dropped (used by <see cref="LineReader"/>).</summary>
    internal static float DropBelow => MinMeanProbability;
}
