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

    /// <summary>The processor count the worker limit is computed from (<see cref="Environment.ProcessorCount"/>).</summary>
    public int ProcessorCount { get; init; } = Environment.ProcessorCount;

    /// <summary>The models on the CPU (tests compare the sizes).</summary>
    internal ModelChoice CpuModels { get; init; } = ModelChoice.Mobile;

    /// <summary>The models on a graphics card and for Accurate.</summary>
    internal ModelChoice AccurateModels { get; init; } = ModelChoice.Server;
}

/// <summary>
/// Reads the text in a picture with ONNX Runtime (docs/spec/ocr.md "Reading", "Accuracy" and "GPU"): decodes the JPEG and cuts
/// out each area at full resolution (SkiaSharp), finds the lines (PP-OCRv5 detection; areas larger than
/// <see cref="TileSize"/> pixels in overlapping tiles, boxes found twice merged), turns each line level (90 degrees for tall
/// boxes, the angle model for upside down), scales it to the reader's 48 pixels (small text enlarged, text below
/// <see cref="MinTextHeight"/> pixels left out), reads it (PP-OCRv5 Latin, CTC) and, with a format, decodes the format's
/// match with the characters limited per position. On the CPU every model call (a tile, a batch of lines) takes one of
/// <see cref="OcrLimits.Workers"/> workers with sessions of its own of <see cref="OcrLimits.ThreadsPerWorker"/> threads, so an
/// idle server reads the tiles of one picture at once; on a graphics card one shared set whose model calls run one at a
/// time, the lines of all waiting pictures read together. At most <see cref="OcrLimits.Workers"/> pictures are in work at once.
/// </summary>
public sealed class OnnxTextRecognizer : IDetailedTextRecognizer, IEngineInfo, IDisposable
{

    /// <summary>The tile size for large areas.</summary>
    public const int TileSize = 960;

    /// <summary>Tiles per text finder call on a graphics card at most (full HD: 6 tiles, one call).</summary>
    private const int MaxTileBatch = 6;

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
    private readonly ModelSet? _gpuSet;
    private readonly LineBatcher? _gpuLines;
    private readonly RecognitionDevice _cpuDevice;
    private readonly UnchangedAreas _unchanged = new();
    private readonly RecognitionDevice? _gpuDevice;
    private volatile bool _useGpu = true;
    private bool _disposed;

    public OnnxTextRecognizer(OcrEngineOptions options, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger ?? NullLogger.Instance;
        Workers = OcrLimits.Workers(options.ProcessorCount);
        _pictures = new SemaphoreSlim(Workers, Workers);
        var folder = options.ModelDirectory;
        _cpuCharacters = CharacterList.Load(Path.Combine(folder, options.CpuModels.Characters));
        _accurateCharacters = CharacterList.Load(Path.Combine(folder, options.AccurateModels.Characters));
        _cpu = new CpuWorkers<ModelSet>(
            Workers,
            Math.Min(Workers, MaxAccurateCpuSets),
            accurate => new ModelSet(folder, accurate ? options.AccurateModels : options.CpuModels, ExecutionTarget.Cpu, OcrLimits.ThreadsPerWorker));

        var gpu = options.UseGraphicsCard
            ? SessionFactory.TryGraphicsCard(Path.Combine(folder, ModelFiles.DetectionMobile), _logger)
            : null;
        if (gpu is { } target)
        {
            try
            {
                _gpuSet = new ModelSet(folder, options.AccurateModels, target, OcrLimits.ThreadsPerWorker);
                _gpuLines = new LineBatcher(_gpuSet.RecognizeAsync);
                _gpuDevice = new RecognitionDevice(true, SessionFactory.GraphicsCardName(target));
            }
#pragma warning disable CA1031 // A graphics card that fails with the real models falls back to the CPU too.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                RecognitionLog.GraphicsCardFailed(_logger, ex.Message);
                _gpuSet?.Dispose();
                _gpuSet = null;
                _gpuLines?.Dispose();
                _gpuLines = null;
                _gpuDevice = null;
            }
        }

        _cpuDevice = new RecognitionDevice(false, $"CPU ({options.ProcessorCount} cores)");
        RecognitionLog.RunsOn(_logger, RunsOn.Name);
    }

    /// <summary>What runs the recognition now: the graphics card while there is one and "Use the graphics card" is on.</summary>
    public RecognitionDevice RunsOn => OnGpu ? _gpuDevice! : _cpuDevice;

    /// <inheritdoc />
    string IEngineInfo.RunsOn => RunsOn.Name;

    /// <summary>A graphics card passed the test reading and holds the models.</summary>
    public bool GpuAvailable => _gpuSet is not null;

    /// <summary>True while the readings run on the graphics card.</summary>
    public bool OnGpu => _useGpu && _gpuSet is not null;

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
            areas = [Area.WholePicture(string.Empty)];
        }

        IModelRunner runner = _useGpu && _gpuSet is { } gpuSet && _gpuLines is { } gpuLines
            ? new GpuRunner(gpuSet, gpuLines)
            : new CpuRunner(_cpu, options.Accurate ? _accurateCharacters : _cpuCharacters, options.Accurate);
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
            keys[a] = new UnchangedAreas.Key(options.Source, rect, picture.Width, picture.Height, area.Expected.Kind, area.Expected.Format, options.Accurate, runner is GpuRunner);
            prints[a] = UnchangedAreas.Print(picture, rect);
            results[a] = _unchanged.TryReuse(keys[a], prints[a]);
            if (results[a] is null)
            {
                changed.Add(a);
            }
        }

        if (changed.Count > 0)
        {
            var fresh = await ReadWithAsync(picture, [.. changed.Select(a => areas[a])], runner, ct).ConfigureAwait(false);
            for (var i = 0; i < changed.Count; i++)
            {
                var a = changed[i];
                results[a] = fresh.Areas[i];
                _unchanged.Keep(keys[a], prints[a], fresh.Areas[i]);
            }
        }

        return new RecognitionResult(new Resolution(picture.Width, picture.Height), [.. results.Select(r => r!)]);
    }

    /// <summary>The areas whose last reading was used again (tests).</summary>
    internal UnchangedAreas Unchanged => _unchanged;

    private static async Task<RecognitionResult> ReadWithAsync(Picture picture, IReadOnlyList<Area> areas, IModelRunner set, CancellationToken ct)
    {
        var results = new AreaRecognition[areas.Count];
        var lineBitmaps = new List<SKBitmap>();
        var lineBoxes = new List<(int Area, RotatedBox Box)>();
        var tooSmall = new int[areas.Count];
        try
        {
            // The areas' text finding at once (on the CPU each call takes a worker of its own).
            var found = await Task.WhenAll(areas.Select(area =>
            {
                var rect = PixelRect(area.Box, picture.Width, picture.Height);
                return rect.Width < 4 || rect.Height < 4 ? Task.FromResult(new List<RotatedBox>()) : FindLinesAsync(picture, rect, set, ct);
            })).ConfigureAwait(false);
            var cutStart = Stopwatch.GetTimestamp();
            for (var a = 0; a < areas.Count; a++)
            {
                foreach (var box in found[a])
                {
                    if (box.TextHeight > 0 && box.TextHeight < MinTextHeight)
                    {
                        tooSmall[a]++;
                        continue;
                    }

                    lineBitmaps.Add(CutLine(picture, box));
                    lineBoxes.Add((a, box));
                }
            }

            StageClock.Add(Stage.CutLines, cutStart);

            // Upside down lines (a receipt laid the other way round): the angle model names the candidates; each candidate is
            // read both ways and the reading the reader is surer of wins (the angle model alone turns some coloured print).
            var turnedOf = new int[lineBitmaps.Count];
            Array.Fill(turnedOf, -1);
            var own = lineBitmaps.Count;
            if (own > 0)
            {
                var upsideDown = await set.UpsideDownAsync(lineBitmaps, ct).ConfigureAwait(false);
                for (var i = 0; i < own; i++)
                {
                    if (upsideDown[i] >= UpsideDownCandidate)
                    {
                        turnedOf[i] = lineBitmaps.Count;
                        lineBitmaps.Add(Turn180(lineBitmaps[i]));
                    }
                }
            }

            var allScores = lineBitmaps.Count == 0 ? [] : await set.RecognizeAsync(lineBitmaps, ct).ConfigureAwait(false);
            var scores = new LineScores[own];
            for (var i = 0; i < own; i++)
            {
                scores[i] = turnedOf[i] >= 0 && Sureness(allScores[turnedOf[i]], set.Characters) > Sureness(allScores[i], set.Characters)
                    ? allScores[turnedOf[i]]
                    : allScores[i];
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

            return new RecognitionResult(new Resolution(picture.Width, picture.Height), results.Select(r => r ?? new AreaRecognition([], 0)).ToList());
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

        // Tiles of TileSize at full resolution, overlapping by TileOverlap; all tiles of the area in one call.
        var (xs, tileW) = Tiles(rect.Left, rect.Width, set.DetectionSide);
        var (ys, tileH) = Tiles(rect.Top, rect.Height, set.DetectionSide);
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
        if (set.BatchTiles)
        {
            // A graphics card: the tiles in calls of up to MaxTileBatch, each call's count in the runner's steps (blank tiles
            // fill it up), so the input shapes stay few.
            for (var first = 0; first < tiles.Count; first += MaxTileBatch)
            {
                var chunk = tiles.GetRange(first, Math.Min(MaxTileBatch, tiles.Count - first));
                var batch = set.TileBatch(chunk.Count);
                var inputStart = Stopwatch.GetTimestamp();
                using var input = PooledFloats.Rent(batch * 3 * tileSize, clear: batch > chunk.Count);
                Parallel.For(0, chunk.Count, i => picture.WriteTile(input.Array, i, chunk[i].X, chunk[i].Y, tileW, tileH, rect, fillColor, Tensors.ImageNet));
                StageClock.Add(Stage.DetectInput, inputStart);
                using var maps = await set.DetectAsync(input, batch, tileH, tileW, ct).ConfigureAwait(false);
                var boxesStart = Stopwatch.GetTimestamp();
                var perTile = new List<RotatedBox>[chunk.Count];
                Parallel.For(0, chunk.Count, i => perTile[i] = DbPostProcess.Boxes(maps.Array.AsSpan(i * tileSize, tileSize), tileW, tileH).Select(b => b.Map(1, chunk[i].X, chunk[i].Y)).ToList());
                found.AddRange(perTile.SelectMany(b => b));
                StageClock.Add(Stage.DetectBoxes, boxesStart);
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
                    using var map = await set.DetectAsync(input, 1, tileH, tileW, ct).ConfigureAwait(false);
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
    /// with <see cref="ObjectDisposedException"/>. A graphics card set still in use after the wait is left to the process
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

        var idle = taken == Workers;
        if (_gpuLines?.Stop(DisposeWait) == false)
        {
            idle = false;
        }

        if (idle)
        {
            _gpuSet?.Dispose();
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
