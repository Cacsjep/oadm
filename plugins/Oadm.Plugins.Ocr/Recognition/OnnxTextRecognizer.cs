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
            // Text is read only inside areas: nothing to do, the picture is not decoded.
            return new RecognitionResult(default, []);
        }

        IModelRunner runner = _useGpu && _gpuSet is { } gpuSet && _gpuLines is { } gpuLines
            ? new GpuRunner(gpuSet, gpuLines)
            : new CpuRunner(_cpu, options.Accurate ? _accurateCharacters : _cpuCharacters, options.Accurate);
        await _pictures.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // On a pool thread: the caller's thread never carries the work.
            return await Task.Run(
                async () =>
                {
                    using var picture = Picture.Decode(jpeg);
                    return await ReadWithAsync(picture, areas, runner, ct).ConfigureAwait(false);
                },
                ct).ConfigureAwait(false);
        }
        finally
        {
            _pictures.Release();
        }
    }

    private static async Task<RecognitionResult> ReadWithAsync(Picture picture, IReadOnlyList<Area> areas, IModelRunner set, CancellationToken ct)
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
            foreach (var (a, box) in candidates)
            {
                lineBitmaps.Add(CutLine(picture, box));
                lineBoxes.Add((a, box));
            }

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
            var fill = picture.BorderColor(rect);
            using var bitmap = picture.Render(matrix, w, h, scale, fill, new SKRect(Margin, Margin, Margin + rect.Width * scale, Margin + rect.Height * scale));
            var input = new float[3 * w * h];
            Tensors.Write(bitmap, input, 0, h, w, Tensors.ImageNet);
            var map = await set.DetectAsync(input, 1, h, w, ct).ConfigureAwait(false);
            return BoxMerger.Merge(DbPostProcess.Boxes(map, w, h).Select(b => b.Map(1 / scale, rect.Left - Margin / scale, rect.Top - Margin / scale)));
        }

        // Tiles of TileSize at full resolution, overlapping by TileOverlap; all tiles of the area in one call.
        var (xs, tw) = Tiles(rect.Left, rect.Width);
        var (ys, th) = Tiles(rect.Top, rect.Height);
        var tileW = RoundUp32(tw);
        var tileH = RoundUp32(th);
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
            // A graphics card: every tile in one call.
            var input = new float[tiles.Count * 3 * tileSize];
            Parallel.For(0, tiles.Count, i => picture.WriteTile(input, i, tiles[i].X, tiles[i].Y, tileW, tileH, rect, fillColor, Tensors.ImageNet));
            var maps = await set.DetectAsync(input, tiles.Count, tileH, tileW, ct).ConfigureAwait(false);
            var perTile = new List<RotatedBox>[tiles.Count];
            Parallel.For(0, tiles.Count, i => perTile[i] = DbPostProcess.Boxes(maps.AsSpan(i * tileSize, tileSize), tileW, tileH).Select(b => b.Map(1, tiles[i].X, tiles[i].Y)).ToList());
            found.AddRange(perTile.SelectMany(b => b));
        }
        else
        {
            // The CPU: each tile a call of its own, on as many workers as are free.
            var calls = tiles.Select(tile => Task.Run(
                async () =>
                {
                    var input = new float[3 * tileSize];
                    picture.WriteTile(input, 0, tile.X, tile.Y, tileW, tileH, rect, fillColor, Tensors.ImageNet);
                    var map = await set.DetectAsync(input, 1, tileH, tileW, ct).ConfigureAwait(false);
                    return DbPostProcess.Boxes(map, tileW, tileH).Select(b => b.Map(1, tile.X, tile.Y)).ToList();
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
    /// needed (1920 x 1080: 3 x 2 tiles of 736 x 608).
    /// </summary>
    /// <returns>The left (or top) edges and the tile size.</returns>
    internal static (List<int> Starts, int Size) Tiles(int start, int length)
    {
        if (length <= TileSize)
        {
            return ([start], length);
        }

        var n = (int)Math.Ceiling((length - TileOverlap * TileSize) / (TileSize * (1 - TileOverlap)));
        var size = Math.Min(TileSize, RoundUp32((int)Math.Ceiling(length / (n - (n - 1) * TileOverlap))));
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

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gpuLines?.Dispose();
        _gpuSet?.Dispose();
        _cpu.Dispose();
        _pictures.Dispose();
    }

    /// <summary>Lines whose mean probability is below the spec's 50 % are dropped (used by <see cref="LineReader"/>).</summary>
    internal static float DropBelow => MinMeanProbability;
}
