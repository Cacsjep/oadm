using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Oadm.Plugins.Ocr.Model;
using Oadm.Plugins.Ocr.Recognition;
using Oadm.Tests.Shared;
using SkiaSharp;

namespace Oadm.Plugins.Ocr.Tests.Recognition;

/// <summary>One text of the angle sweep: what is written, its format (null: any text) and the thing it is printed on.</summary>
internal sealed record SweepText(string Name, string Text, string? Format, SweepBackground Background);

/// <summary>What a text is printed on: a label on a box, a sign plate, a receipt, a container door.</summary>
internal enum SweepBackground
{
    Label,
    Sign,
    Receipt,
    Door,
}

/// <summary>The small damage a camera picture has: none, a little blur, sensor noise, strong JPEG.</summary>
internal enum SweepDamage
{
    None,
    Blur,
    Noise,
    Jpeg,
}

/// <summary>How the text is seen: turned in the picture, the plane it sits on tilted away around the vertical or horizontal axis.</summary>
/// <param name="Rotation">Degrees, clockwise in the picture (the text turned on its plane).</param>
/// <param name="TiltAroundVertical">Degrees the plane is turned away around the vertical axis (seen from the side).</param>
/// <param name="TiltAroundHorizontal">Degrees the plane is tilted back around the horizontal axis (seen from above or below).</param>
internal readonly record struct SweepPose(float Rotation, float TiltAroundVertical = 0, float TiltAroundHorizontal = 0)
{
    public string Label => TiltAroundVertical == 0 && TiltAroundHorizontal == 0
        ? string.Create(CultureInfo.InvariantCulture, $"{Rotation:0}°")
        : string.Create(CultureInfo.InvariantCulture, $"{(TiltAroundVertical != 0 ? "side" : "top")} {TiltAroundVertical + TiltAroundHorizontal:0}°{(Rotation != 0 ? $" + {Rotation:0}° turn" : string.Empty)}");
}

/// <summary>One variant of a text: font, size and damage (the same for every angle, so only the angle changes).</summary>
internal sealed record SweepVariant(int Index, string Font, float CapHeight, SweepDamage Damage);

/// <summary>One reading of the sweep.</summary>
internal sealed record SweepSample(SweepText Text, SweepPose Pose, SweepVariant Variant, bool Search, string Value, string Expected, int Errors, string Lines)
{
    public bool Exact => Errors == 0;
}

/// <summary>
/// The rotation and perspective sweep (docs/spec/ocr.md "Rotation and perspective"): a text on a realistic plate (label, sign,
/// receipt, container door) drawn in the middle of a full HD picture, turned in the picture and on a plane tilted away from the
/// camera (a pinhole projection with the plate at the focal distance of a 60° lens, the near edge kept at its size), read on
/// the CPU through one fixed-place area large enough for any turn (or Search over the whole picture). Pictures are made in
/// memory; fonts from the repository (Roboto) and, for variety, the machine's (Arial, Consolas; another font elsewhere).
/// </summary>
internal static class AngleSweep
{
    public const int Width = 1920;
    public const int Height = 1080;

    /// <summary>The focal length in pixels of a 60° horizontal field of view at <see cref="Width"/>: the plate sits at this depth.</summary>
    private static readonly float Focal = Width / 2f / MathF.Tan(30 * MathF.PI / 180);

    private static readonly ConcurrentDictionary<string, SKTypeface> Typefaces = new();

    /// <summary>A container number with its valid check digit ("MSKU 123456 x").</summary>
    public static readonly string Container = "MSKU 123456 " + Oadm.Plugins.Ocr.Matching.ContainerCheck.CheckDigit("MSKU123456".AsSpan()).ToString(CultureInfo.InvariantCulture);

    public static readonly SweepText[] Texts =
    [
        new("container", Container, "@@@U ###### #", SweepBackground.Door),
        new("4/12", "4/12", "#/##", SweepBackground.Label),
        new("sign", "LOADING DOCK 3", null, SweepBackground.Sign),
        new("order", "36F1A", "##@#@", SweepBackground.Receipt),
    ];

    /// <summary>Roboto from the repository: the same pixels on every platform.</summary>
    public const string RobotoBold = "Roboto Bold";
    public const string RobotoRegular = "Roboto Regular";

    /// <summary>
    /// Twenty variants per text: each of four fonts with each cap height 24 to 40 px once, in blocks of five with no damage,
    /// a little blur, noise and strong JPEG.
    /// </summary>
    public static readonly SweepVariant[] Variants = MakeVariants();

    private static SweepVariant[] MakeVariants()
    {
        string[] fonts = [RobotoBold, RobotoRegular, "Arial Bold", "Consolas Bold"];
        SweepDamage[] damages = [SweepDamage.None, SweepDamage.Blur, SweepDamage.Noise, SweepDamage.Jpeg];
        return [.. Enumerable.Range(0, 20).Select(i => new SweepVariant(i, fonts[i % 4], 24 + 4 * (i % 5), damages[i / 5]))];
    }

    /// <summary>0 to 345° in 15° steps plus 5° and 10° either side of 0, 90, 180 and 270.</summary>
    public static IReadOnlyList<float> RotationAngles()
    {
        var angles = new SortedSet<float>();
        for (var a = 0; a < 360; a += 15)
        {
            angles.Add(a);
        }

        foreach (var c in new[] { 0, 90, 180, 270 })
        {
            foreach (var d in new[] { -10, -5, 5, 10 })
            {
                angles.Add((c + d + 360) % 360);
            }
        }

        return [.. angles];
    }

    /// <summary>The out-of-plane tilts: around the vertical and horizontal axis 0 to 60°, then the same with a 15° turn.</summary>
    public static IReadOnlyList<SweepPose> PerspectivePoses()
    {
        int[] tilts = [0, 10, 20, 30, 40, 50, 60];
        var poses = new List<SweepPose>();
        foreach (var rotation in new[] { 0f, 15f })
        {
            poses.AddRange(tilts.Select(t => new SweepPose(rotation, TiltAroundVertical: t)));
            poses.AddRange(tilts.Where(t => t != 0).Select(t => new SweepPose(rotation, TiltAroundHorizontal: t)));
        }

        return poses;
    }

    /// <summary>Reads with the recognizer as the readers do.</summary>
    public static Func<byte[], Area, CancellationToken, Task<IReadOnlyList<TextLine>>> With(ITextRecognizer recognizer) =>
        async (jpeg, area, ct) => (await recognizer.ReadAsync(jpeg, [area], ct)).LinesPerArea[0];

    /// <summary>Reads every text in every pose and variant, at most <paramref name="parallel"/> pictures made and read at once.</summary>
    public static async Task<List<SweepSample>> RunAsync(Func<byte[], Area, CancellationToken, Task<IReadOnlyList<TextLine>>> read, IEnumerable<(SweepText Text, SweepPose Pose, SweepVariant Variant)> cases, bool search, int parallel, CancellationToken ct)
    {
        var list = cases.ToList();
        var samples = new SweepSample[list.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, list.Count), new ParallelOptions { MaxDegreeOfParallelism = parallel, CancellationToken = ct }, async (i, token) =>
        {
            var (text, pose, variant) = list[i];
            var (jpeg, box) = Render(text, pose, variant, search);
            var area = new Area
            {
                Name = text.Name,
                Box = box,
                Expected = text.Format is null ? ExpectedText.AnyText : ExpectedText.WithFormat(text.Format),
            };
            var lines = await read(jpeg, area, token);
            var value = AccuracySet.ValueOf(lines, text.Format);
            var expected = AccuracySet.Normalize(text.Text, text.Format);
            samples[i] = new SweepSample(text, pose, variant, search, value, expected, AccuracySet.Levenshtein(value, expected),
                string.Join(" | ", lines.Select(l => string.Create(CultureInfo.InvariantCulture, $"{l.Text} {l.Confidence:0}"))));
        });
        return [.. samples];
    }

    /// <summary>
    /// The picture as JPEG and the area to read: the plate in the middle, the area a square around it large enough for any
    /// turn (the whole picture for Search, the plate then off the centre).
    /// </summary>
    public static (byte[] Jpeg, RelativeBox Area) Render(SweepText text, SweepPose pose, SweepVariant variant, bool search)
    {
        var random = new Random(text.Text.Sum(c => c) * 31 + variant.Index); // never HashCode: it differs per process
        using var plate = Plate(text, variant, random);
        using var scene = new SKBitmap(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(scene))
        {
            Background(canvas, random);
            var centre = search ? new SKPoint(Width * 0.62f, Height * 0.42f) : new SKPoint(Width / 2f, Height / 2f);
            canvas.SetMatrix(Pose(pose, plate.Width, plate.Height, centre));
            using var image = SKImage.FromBitmap(plate);
            using var shadow = new SKPaint { Color = new SKColor(0, 0, 0, 50), ImageFilter = SKImageFilter.CreateBlur(5, 5) };
            canvas.DrawRect(SKRect.Create(-plate.Width / 2f + 5, -plate.Height / 2f + 5, plate.Width, plate.Height), shadow);
            canvas.DrawImage(image, -plate.Width / 2f, -plate.Height / 2f, new SKSamplingOptions(SKCubicResampler.Mitchell));
        }

        var side = MathF.Sqrt(plate.Width * plate.Width + plate.Height * plate.Height) * 1.1f + 32;
        var area = search
            ? new RelativeBox(0, 0, 1, 1)
            : new RelativeBox((Width - side) / 2 / Width, (Height - side) / 2 / Height, side / Width, side / Height);
        return (Damage(scene, variant.Damage, random), area);
    }

    /// <summary>
    /// The plate's drawing matrix: turned on its plane, then the plane turned away around the vertical or horizontal axis and
    /// projected (pinhole at the focal distance), scaled so the near edge keeps its size, its box centred on <paramref name="centre"/>.
    /// </summary>
    public static SKMatrix Pose(SweepPose pose, float plateWidth, float plateHeight, SKPoint centre)
    {
        var rotation = SKMatrix.CreateRotationDegrees(pose.Rotation);
        var yaw = pose.TiltAroundVertical * MathF.PI / 180;
        var pitch = pose.TiltAroundHorizontal * MathF.PI / 180;

        // A point (x, y) of the plane: x' = x cos(yaw), y' = y cos(pitch), depth 1 + (x sin(yaw) + y sin(pitch)) / focal.
        var projection = new SKMatrix(MathF.Cos(yaw), 0, 0, 0, MathF.Cos(pitch), 0, MathF.Sin(yaw) / Focal, MathF.Sin(pitch) / Focal, 1);
        var matrix = SKMatrix.Concat(projection, rotation);

        var corners = new[]
        {
            new SKPoint(-plateWidth / 2, -plateHeight / 2), new SKPoint(plateWidth / 2, -plateHeight / 2),
            new SKPoint(plateWidth / 2, plateHeight / 2), new SKPoint(-plateWidth / 2, plateHeight / 2),
        };

        // The near edge (smallest depth) keeps its size: scale by the smallest depth of the corners.
        var nearest = corners.Min(c => 1 + (MathF.Sin(yaw) * (c.X * MathF.Cos(pose.Rotation * MathF.PI / 180) - c.Y * MathF.Sin(pose.Rotation * MathF.PI / 180))
            + MathF.Sin(pitch) * (c.X * MathF.Sin(pose.Rotation * MathF.PI / 180) + c.Y * MathF.Cos(pose.Rotation * MathF.PI / 180))) / Focal);
        matrix = SKMatrix.Concat(SKMatrix.CreateScale(nearest, nearest), matrix);
        var mapped = matrix.MapPoints(corners);
        var midX = (mapped.Min(p => p.X) + mapped.Max(p => p.X)) / 2;
        var midY = (mapped.Min(p => p.Y) + mapped.Max(p => p.Y)) / 2;
        return SKMatrix.Concat(SKMatrix.CreateTranslation(centre.X - midX, centre.Y - midY), matrix);
    }

    /// <summary>The text on its plate, upright, at its cap height.</summary>
    private static SKBitmap Plate(SweepText text, SweepVariant variant, Random random)
    {
        var typeface = Typeface(variant.Font);
        using var font = new SKFont(typeface, variant.CapHeight / CapRatio(typeface));
        var textWidth = font.MeasureText(text.Text);
        var cap = variant.CapHeight;
        using var small = new SKFont(typeface, font.Size * 0.55f);
        var (back, fore, padX, padY, second) = text.Background switch
        {
            SweepBackground.Door => (new SKColor(30, 80, 140), new SKColor(245, 245, 240), cap * 1.2f, cap * 1.0f, "45G1"),
            SweepBackground.Label => (new SKColor(250, 250, 246), new SKColor(25, 25, 30), cap * 0.9f, cap * 0.8f, "INTENSITY"),
            SweepBackground.Sign => (new SKColor(0, 120, 60), SKColors.White, cap * 0.8f, cap * 0.7f, null),
            _ => (new SKColor(248, 247, 240), new SKColor(30, 30, 35), cap * 0.9f, cap * 0.9f, "TOTAL EUR 10.60"),
        };

        var secondWidth = second is null ? 0 : small.MeasureText(second);
        var width = (int)MathF.Ceiling(Math.Max(textWidth, secondWidth) + 2 * padX);
        var lineGap = cap * 0.9f;
        var height = (int)MathF.Ceiling(cap + 2 * padY + (second is null ? 0 : lineGap + cap * 0.55f));
        var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(back);
        using var paint = new SKPaint { IsAntialias = true, Color = fore };
        if (text.Background == SweepBackground.Door)
        {
            // Corrugated steel: darker ribs across the door.
            using var rib = new SKPaint { Color = new SKColor(20, 62, 115) };
            for (var x = 0f; x < width; x += cap * 0.8f)
            {
                canvas.DrawRect(SKRect.Create(x, 0, cap * 0.25f, height), rib);
            }
        }
        else if (text.Background == SweepBackground.Label)
        {
            using var border = new SKPaint { IsAntialias = true, Color = new SKColor(60, 60, 60), Style = SKPaintStyle.Stroke, StrokeWidth = 2 };
            canvas.DrawRect(SKRect.Create(3, 3, width - 6, height - 6), border);
        }

        // Paper or paint is never perfectly flat.
        using var speck = new SKPaint { Color = new SKColor(0, 0, 0, 14) };
        for (var i = 0; i < width * height / 400; i++)
        {
            canvas.DrawRect(SKRect.Create(random.Next(width), random.Next(height), 2, 2), speck);
        }

        var y = padY + cap;
        if (second is not null && text.Background == SweepBackground.Label)
        {
            // The small word above the value ("INTENSITY" then "4/12").
            canvas.DrawText(second, padX, padY + cap * 0.55f, SKTextAlign.Left, small, paint);
            y += lineGap + cap * 0.55f;
            canvas.DrawText(text.Text, padX, y, SKTextAlign.Left, font, paint);
        }
        else
        {
            canvas.DrawText(text.Text, padX, y, SKTextAlign.Left, font, paint);
            if (second is not null)
            {
                canvas.DrawText(second, padX, y + lineGap + cap * 0.55f, SKTextAlign.Left, small, paint);
            }
        }

        return bitmap;
    }

    /// <summary>A wall or counter: a tone, grain and a few shapes that are not text.</summary>
    private static void Background(SKCanvas canvas, Random random)
    {
        canvas.Clear(new SKColor((byte)random.Next(110, 160), (byte)random.Next(100, 140), (byte)random.Next(80, 120)));
        using var grain = new SKPaint { IsAntialias = true, StrokeWidth = 2 };
        for (var y = 0; y < Height; y += 4)
        {
            grain.Color = new SKColor(0, 0, 0, (byte)random.Next(0, 22));
            canvas.DrawLine(0, y + MathF.Sin(y * 0.05f) * 4, Width, y + MathF.Cos(y * 0.03f) * 4, grain);
        }

        using var thing = new SKPaint { IsAntialias = true, Color = new SKColor(225, 225, 220) };
        canvas.DrawCircle(random.Next(80, 300), random.Next(80, Height - 80), 60, thing);
        thing.Color = new SKColor(30, 30, 35);
        canvas.DrawRoundRect(SKRect.Create(random.Next(Width - 320, Width - 200), random.Next(60, Height - 400), 150, 300), 18, 18, thing);
    }

    private static byte[] Damage(SKBitmap scene, SweepDamage damage, Random random)
    {
        using var result = new SKBitmap(scene.Info);
        using (var canvas = new SKCanvas(result))
        using (var image = SKImage.FromBitmap(scene))
        {
            using var paint = new SKPaint();
            if (damage == SweepDamage.Blur)
            {
                paint.ImageFilter = SKImageFilter.CreateBlur(1.2f, 1.2f);
            }

            canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest), paint);
        }

        if (damage == SweepDamage.Noise)
        {
            var pixels = result.GetPixelSpan();
            var copy = pixels.ToArray();
            for (var i = 0; i < copy.Length; i += 4)
            {
                var n = (int)(Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble()) * 8);
                for (var c = 0; c < 3; c++)
                {
                    copy[i + c] = (byte)Math.Clamp(copy[i + c] + n, 0, 255);
                }
            }

            copy.CopyTo(result.GetPixelSpan());
        }

        using var data = result.Encode(SKEncodedImageFormat.Jpeg, damage == SweepDamage.Jpeg ? 40 : 85);
        return data.ToArray();
    }

    private static SKTypeface Typeface(string name) => Typefaces.GetOrAdd(name, n => n switch
    {
        RobotoBold => SKTypeface.FromFile(TestRepository.Path("plugins", "Oadm.Plugins.SnapshotReport", "Fonts", "Roboto-Bold.ttf")),
        RobotoRegular => SKTypeface.FromFile(TestRepository.Path("plugins", "Oadm.Plugins.SnapshotReport", "Fonts", "Roboto-Regular.ttf")),
        "Arial Bold" => SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold),
        _ => SKTypeface.FromFamilyName("Consolas", SKFontStyle.Bold),
    });

    /// <summary>The cap height per font size (the height of "H").</summary>
    private static float CapRatio(SKTypeface typeface)
    {
        using var font = new SKFont(typeface, 100);
        font.MeasureText("H", out var bounds);
        return bounds.Height / 100;
    }

    /// <summary>A table: one row per pose, per text the exact values and character errors, then the totals with and without a format.</summary>
    public static string Table(IReadOnlyList<SweepSample> samples)
    {
        var texts = samples.Select(s => s.Text).Distinct().ToList();
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"| {"Pose",-22} |");
        foreach (var t in texts)
        {
            sb.Append(CultureInfo.InvariantCulture, $" {t.Name,-13} |");
        }

        sb.AppendLine(" Format exact | Any exact |");
        sb.Append("|---|").Append(string.Concat(Enumerable.Repeat("---|", texts.Count))).AppendLine("---|---|");
        foreach (var group in samples.GroupBy(s => s.Pose))
        {
            sb.Append(CultureInfo.InvariantCulture, $"| {group.Key.Label,-22} |");
            foreach (var t in texts)
            {
                var cell = group.Where(s => s.Text == t).ToList();
                sb.Append(CultureInfo.InvariantCulture, $" {Rate(cell),5:P0} {Cer(cell),6:P1} |");
            }

            sb.Append(CultureInfo.InvariantCulture, $" {Rate(group.Where(s => s.Text.Format is not null).ToList()),12:P1} |");
            sb.AppendLine(CultureInfo.InvariantCulture, $" {Rate(group.Where(s => s.Text.Format is null).ToList()),9:P1} |");
        }

        return sb.ToString();
    }

    public static double Rate(IReadOnlyCollection<SweepSample> samples) => samples.Count == 0 ? 1 : samples.Count(s => s.Exact) / (double)samples.Count;

    public static double Cer(IReadOnlyCollection<SweepSample> samples) =>
        samples.Count == 0 ? 0 : samples.Sum(s => s.Errors) / (double)samples.Sum(s => Math.Max(1, s.Expected.Length));
}
