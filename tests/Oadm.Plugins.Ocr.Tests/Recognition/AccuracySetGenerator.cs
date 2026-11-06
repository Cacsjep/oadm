using System.Globalization;
using System.Text;
using Oadm.Plugins.Ocr.Recognition;
using SkiaSharp;

namespace Oadm.Plugins.Ocr.Tests.Recognition;

/// <summary>
/// Makes the generated pictures of the accuracy set (docs/spec/ocr.md "Accuracy"): receipts with an order code laid anywhere
/// on a counter and turned (Search), container numbers with valid ISO 6346 check digits (fixed place and Search), meter
/// digits and signs, each in clean and degraded variants (blur, noise, dim, slant, strong JPEG). Deterministic (fixed seed);
/// the pictures are committed, so this runs only when the set is made anew (<see cref="AccuracySetTests"/>,
/// OADM_OCR_REGENERATE). Fonts are the ones of the machine that made them (Windows: Arial, Consolas).
/// </summary>
internal static class AccuracySetGenerator
{
    private static readonly string[] Owners = ["MSCU", "TGHU", "CMAU", "HLXU", "MAEU", "OOLU", "CSQU", "TCNU"];

    private enum Damage
    {
        None,
        Blur,
        Noise,
        Dim,
        Slant,
        Jpeg,
        HeavyBlur,
        Motion,
        Glare,
        Perspective,
    }

    /// <summary>Writes the generated pictures to <paramref name="folder"/> and returns their manifest entries.</summary>
    public static List<AccuracyPicture> Generate(string folder)
    {
        Directory.CreateDirectory(folder);
        var random = new Random(6346);
        var pictures = new List<AccuracyPicture>();
        Damage[] damages = [Damage.None, Damage.Blur, Damage.Noise, Damage.Dim, Damage.Slant, Damage.Jpeg];
        Damage[] hard = [Damage.HeavyBlur, Damage.Motion, Damage.Glare, Damage.Perspective, Damage.Dim, Damage.Noise];

        // Receipts on a counter, Search over the whole full HD picture: turned 0, 90, 180, 270 degrees plus a little.
        for (var i = 0; i < 12; i++)
        {
            var code = OrderCode(random);
            var turn = i % 4 * 90 + random.Next(-6, 7);
            var damage = damages[i % damages.Length];
            var name = $"receipt-{i + 1:00}.jpg";
            using var scene = Counter(1920, 1080, random);
            using var receipt = Receipt(code, random);
            var scale = 0.55f + (float)random.NextDouble() * 0.4f;
            Place(scene, receipt, random, turn, scale);
            Save(scene, Path.Combine(folder, name), damage, random);
            pictures.Add(new AccuracyPicture(name, "receipt-search", [new AccuracyArea("Order", [0, 0, 1, 1], "##@#@", code)]));
        }

        // Container numbers: fixed place (an area around the number) and Search (the whole picture).
        for (var i = 0; i < 10; i++)
        {
            var number = ContainerNumber(random);
            var search = i >= 6;
            var damage = damages[i % damages.Length];
            var name = $"container-{i + 1:00}.jpg";
            var (width, height) = search ? (1920, 1080) : (1280, 720);
            using var scene = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
            var box = ContainerScene(scene, number, random, search, 1);
            Save(scene, Path.Combine(folder, name), damage, random);
            var area = search ? new[] { 0.0, 0, 1, 1 } : box;
            pictures.Add(new AccuracyPicture(name, search ? "container-search" : "container-fixed", [new AccuracyArea("Container number", area, "@@@U ###### #", number)]));
        }

        // Meter digits, fixed place.
        for (var i = 0; i < 8; i++)
        {
            var digits = random.Next(0, 100000).ToString("00000", CultureInfo.InvariantCulture);
            var damage = damages[i % damages.Length];
            var name = $"meter-{i + 1:00}.jpg";
            using var scene = new SKBitmap(1280, 720, SKColorType.Rgba8888, SKAlphaType.Premul);
            var box = MeterScene(scene, digits, random, 1);
            Save(scene, Path.Combine(folder, name), damage, random);
            pictures.Add(new AccuracyPicture(name, "meter", [new AccuracyArea("Meter", box, "#####", digits)]));
        }

        // Signs, any text, fixed place.
        string[] signs = ["LOADING DOCK 3", "EXIT", "Ausgang Süd", "GATE 12", "NO PARKING", "Lager Nord", "DOCK 7", "Zufahrt Werk 2"];
        for (var i = 0; i < signs.Length; i++)
        {
            var damage = damages[i % damages.Length];
            var name = $"sign-{i + 1:00}.jpg";
            using var scene = new SKBitmap(1280, 720, SKColorType.Rgba8888, SKAlphaType.Premul);
            var box = SignScene(scene, signs[i], random, 1);
            Save(scene, Path.Combine(folder, name), damage, random);
            pictures.Add(new AccuracyPicture(name, "sign", [new AccuracyArea("Sign", box, null, signs[i])]));
        }

        // The hard part: small text and worse pictures (heavy blur, motion, glare, perspective, dark, noise).
        for (var i = 0; i < 8; i++)
        {
            var code = OrderCode(random);
            var turn = i % 4 * 90 + random.Next(-8, 9);
            var name = $"hard-receipt-{i + 1:00}.jpg";
            using var scene = Counter(1920, 1080, random);
            using var receipt = Receipt(code, random);
            Place(scene, receipt, random, turn, 0.38f + (float)random.NextDouble() * 0.12f);
            Save(scene, Path.Combine(folder, name), hard[i % hard.Length], random);
            pictures.Add(new AccuracyPicture(name, "hard-receipt-search", [new AccuracyArea("Order", [0, 0, 1, 1], "##@#@", code)]));
        }

        for (var i = 0; i < 6; i++)
        {
            var number = ContainerNumber(random);
            var name = $"hard-container-{i + 1:00}.jpg";
            using var scene = new SKBitmap(1280, 720, SKColorType.Rgba8888, SKAlphaType.Premul);
            var box = ContainerScene(scene, number, random, false, 0.5f);
            Save(scene, Path.Combine(folder, name), hard[i % hard.Length], random);
            pictures.Add(new AccuracyPicture(name, "hard-container", [new AccuracyArea("Container number", box, "@@@U ###### #", number)]));
        }

        for (var i = 0; i < 6; i++)
        {
            var digits = random.Next(0, 100000).ToString("00000", CultureInfo.InvariantCulture);
            var name = $"hard-meter-{i + 1:00}.jpg";
            using var scene = new SKBitmap(1280, 720, SKColorType.Rgba8888, SKAlphaType.Premul);
            var box = MeterScene(scene, digits, random, 0.55f);
            Save(scene, Path.Combine(folder, name), hard[i % hard.Length], random);
            pictures.Add(new AccuracyPicture(name, "hard-meter", [new AccuracyArea("Meter", box, "#####", digits)]));
        }

        string[] farSigns = ["LOADING DOCK 3", "Ausgang Süd", "GATE 12", "Lager Nord", "DOCK 7", "Zufahrt Werk 2"];
        for (var i = 0; i < farSigns.Length; i++)
        {
            var name = $"hard-sign-{i + 1:00}.jpg";
            using var scene = new SKBitmap(1280, 720, SKColorType.Rgba8888, SKAlphaType.Premul);
            var box = SignScene(scene, farSigns[i], random, 0.45f);
            Save(scene, Path.Combine(folder, name), hard[i % hard.Length], random);
            pictures.Add(new AccuracyPicture(name, "hard-sign", [new AccuracyArea("Sign", box, null, farSigns[i])]));
        }

        return pictures;
    }

    /// <summary>
    /// The pictures recorded read-only from the development cameras: the coffee box in front of 10.0.0.48 (full HD and 720p,
    /// one area per value) and the yard of 10.0.0.202 without any text.
    /// </summary>
    public static List<AccuracyPicture> CameraPictures()
    {
        var list = new List<AccuracyPicture>();
        foreach (var file in new[] { "box-1920x1080.jpg", "box-1280x720.jpg" })
        {
            list.Add(new AccuracyPicture(file, "camera-box",
            [
                new AccuracyArea("Brand", [0.28, 0.54, 0.16, 0.10], null, "BARISSIMO"),
                new AccuracyArea("Kind", [0.26, 0.63, 0.18, 0.155], null, "DECAF-FEINATO"),
                new AccuracyArea("Machine", [0.245, 0.80, 0.105, 0.095], null, "NESPRESSO® COMPATIBLE"),
                new AccuracyArea("Strength", [0.352, 0.805, 0.085, 0.105], null, "INTENSITY 4/12"),
            ]));
            list.Add(new AccuracyPicture(file, "camera-search", [new AccuracyArea("Intensity", [0, 0, 1, 1], "#/##", "4/12")]));
        }

        list.Add(new AccuracyPicture("yard-1280x720.jpg", "no-text",
        [
            new AccuracyArea("Anything", [0, 0, 1, 1], null, string.Empty),
            new AccuracyArea("Order", [0, 0, 1, 1], "##@#@", string.Empty),
        ]));
        return list;
    }

    /// <summary>
    /// A receipt of the set on its counter, in the middle of a full HD picture, as JPEG (the rows measurement,
    /// <see cref="RowsSweepTests"/>): turned by <paramref name="degrees"/>, or laid by <paramref name="pose"/> (the
    /// receipt's size in, its drawing matrix out); <paramref name="priceGap"/> more characters between an article and its
    /// price (a wide table).
    /// </summary>
    public static byte[] ReceiptPicture(string code, float degrees, float scale, int seed, int priceGap = 0, Func<SKBitmap, SKMatrix>? pose = null)
    {
        var random = new Random(seed);
        using var scene = Counter(1920, 1080, random);
        using var receipt = Receipt(code, random, priceGap);
        using (var canvas = new SKCanvas(scene))
        {
            if (pose is not null)
            {
                canvas.SetMatrix(pose(receipt));
            }
            else
            {
                canvas.Translate(960, 540);
                canvas.RotateDegrees(degrees);
                canvas.Scale(scale);
            }

            using var image = SKImage.FromBitmap(receipt);
            canvas.DrawImage(image, -receipt.Width / 2f, -receipt.Height / 2f, new SKSamplingOptions(SKCubicResampler.Mitchell));
        }

        using var data = scene.Encode(SKEncodedImageFormat.Jpeg, 80);
        return data.ToArray();
    }

    /// <summary>An order code of the format ##A#A ("36F1A").</summary>
    private static string OrderCode(Random random)
    {
        const string Letters = "ABCDEFGHJKLMNPRSTUVWXYZ";
        return string.Create(5, random, (span, r) =>
        {
            span[0] = (char)('0' + r.Next(10));
            span[1] = (char)('0' + r.Next(10));
            span[2] = Letters[r.Next(Letters.Length)];
            span[3] = (char)('0' + r.Next(10));
            span[4] = Letters[r.Next(Letters.Length)];
        });
    }

    /// <summary>A container number with a valid check digit: "MSCU 123456 7".</summary>
    private static string ContainerNumber(Random random)
    {
        var owner = Owners[random.Next(Owners.Length)];
        var serial = random.Next(0, 1000000).ToString("000000", CultureInfo.InvariantCulture);
        var check = Oadm.Plugins.Ocr.Matching.ContainerCheck.CheckDigit((owner + serial).AsSpan());
        return $"{owner} {serial} {check}";
    }

    private static SKBitmap Counter(int width, int height, Random random)
    {
        var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        var baseColor = new SKColor((byte)random.Next(90, 150), (byte)random.Next(60, 100), (byte)random.Next(30, 70));
        canvas.Clear(baseColor);
        using var grain = new SKPaint { IsAntialias = true, StrokeWidth = 2 };
        for (var y = 0; y < height; y += 3)
        {
            var shade = (byte)random.Next(0, 26);
            grain.Color = new SKColor(0, 0, 0, shade);
            canvas.DrawLine(0, y + (float)Math.Sin(y * 0.05) * 4, width, y + (float)Math.Cos(y * 0.03) * 4, grain);
        }

        // A cup and a phone on the counter: shapes that are not text.
        using var thing = new SKPaint { IsAntialias = true, Color = new SKColor(230, 230, 225) };
        canvas.DrawCircle(random.Next(100, width - 100), random.Next(100, height - 100), 70, thing);
        thing.Color = new SKColor(25, 25, 30);
        canvas.DrawRoundRect(SKRect.Create(random.Next(50, width - 250), random.Next(50, height - 450), 180, 360), 20, 20, thing);
        return bitmap;
    }

    /// <summary>The receipt's articles and their prices: what a row must keep together.</summary>
    public static readonly (string Article, string Price)[] ReceiptPairs =
        [("2x Espresso", "5.80"), ("1x Croissant", "2.90"), ("1x Water", "1.90"), ("TOTAL EUR", "10.60")];

    private static SKBitmap Receipt(string code, Random random, int priceGap = 0)
    {
        var gap = new string(' ', priceGap);
        var width = 340 + (11 * priceGap);
        string[] lines =
        [
            "CAFE CENTRAL",
            "Hauptstrasse 12",
            "--------------------",
            $"2x Espresso     {gap}5.80",
            $"1x Croissant    {gap}2.90",
            $"1x Water        {gap}1.90",
            $"TOTAL EUR      {gap}10.60",
            string.Empty,
            $"ORDER {code}",
            string.Empty,
            "Thank you!",
        ];
        var height = 40 + lines.Length * 34;
        var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(new SKColor(248, 247, 240));
        using var typeface = SKTypeface.FromFamilyName("Consolas", SKFontStyle.Normal);
        using var bold = SKTypeface.FromFamilyName("Consolas", SKFontStyle.Bold);
        using var paint = new SKPaint { IsAntialias = true, Color = new SKColor(30, 30, 35) };
        var y = 44f;
        foreach (var line in lines)
        {
            var isOrder = line.StartsWith("ORDER", StringComparison.Ordinal);
            using var font = new SKFont(isOrder ? bold : typeface, isOrder ? 30 : 20 + random.Next(0, 3));
            canvas.DrawText(line, 20, y, SKTextAlign.Left, font, paint);
            y += isOrder ? 38 : 34;
        }

        return bitmap;
    }

    private static void Place(SKBitmap scene, SKBitmap item, Random random, float degrees, float scale)
    {
        using var canvas = new SKCanvas(scene);
        // Inside the picture whatever the turn: half the diagonal from every edge.
        var half = (int)(MathF.Sqrt(item.Width * item.Width + item.Height * item.Height) * scale / 2) + 8;
        var cx = random.Next(half, Math.Max(half + 1, scene.Width - half));
        var cy = random.Next(Math.Min(half, scene.Height / 2), Math.Max(Math.Min(half, scene.Height / 2) + 1, scene.Height - half));
        canvas.Translate(cx, cy);
        canvas.RotateDegrees(degrees);
        canvas.Scale(scale);
        using var shadow = new SKPaint { Color = new SKColor(0, 0, 0, 60), ImageFilter = SKImageFilter.CreateBlur(6, 6) };
        canvas.DrawRect(SKRect.Create(-item.Width / 2f + 6, -item.Height / 2f + 6, item.Width, item.Height), shadow);
        using var image = SKImage.FromBitmap(item);
        canvas.DrawImage(image, -item.Width / 2f, -item.Height / 2f, new SKSamplingOptions(SKCubicResampler.Mitchell));
    }

    private static double[] ContainerScene(SKBitmap scene, string number, Random random, bool search, float sizeFactor)
    {
        using var canvas = new SKCanvas(scene);
        canvas.Clear(new SKColor(150, 170, 190));
        SKColor[] colors = [new(160, 40, 35), new(30, 80, 140), new(40, 110, 60), new(200, 120, 30), new(90, 90, 95)];
        var body = colors[random.Next(colors.Length)];
        var w = scene.Width;
        var h = scene.Height;
        var door = search ? SKRect.Create(w * 0.1f + random.Next(0, w / 3), h * 0.15f, w * 0.45f, h * 0.75f) : SKRect.Create(w * 0.05f, h * 0.05f, w * 0.9f, h * 0.9f);
        using var paint = new SKPaint { IsAntialias = true, Color = body };
        canvas.DrawRect(door, paint);
        paint.Color = body.WithAlpha(255);
        for (var x = door.Left; x < door.Right; x += 28)
        {
            paint.Color = new SKColor((byte)Math.Max(0, body.Red - 30), (byte)Math.Max(0, body.Green - 30), (byte)Math.Max(0, body.Blue - 30));
            canvas.DrawRect(SKRect.Create(x, door.Top, 9, door.Height), paint);
        }

        using var typeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold);
        var size = (search ? 44 + random.Next(0, 14) : 60 + random.Next(0, 20)) * sizeFactor;
        using var font = new SKFont(typeface, size);
        paint.Color = new SKColor(245, 245, 240);
        var text = number;
        var textWidth = font.MeasureText(text);
        var x0 = door.Right - textWidth - door.Width * 0.08f;
        var y0 = door.Top + door.Height * 0.12f + size;
        canvas.DrawText(text, x0, y0, SKTextAlign.Left, font, paint);

        // The size and type code below the number ("45G1"), as on real doors.
        using var small = new SKFont(typeface, size * 0.7f);
        canvas.DrawText("45G1", x0, y0 + size * 1.3f, SKTextAlign.Left, small, paint);
        var margin = size * 0.6f;
        return Relative(SKRect.Create(x0 - margin, y0 - size - margin * 0.6f, textWidth + 2 * margin, size + 1.2f * margin), scene);
    }

    private static double[] MeterScene(SKBitmap scene, string digits, Random random, float sizeFactor)
    {
        using var canvas = new SKCanvas(scene);
        canvas.Clear(new SKColor(200, 200, 195));
        using var paint = new SKPaint { IsAntialias = true, Color = new SKColor(60, 60, 65) };
        var cell = (int)((44 + random.Next(0, 24)) * sizeFactor);
        var x0 = random.Next(150, 1280 - 150 - 5 * cell);
        var y0 = random.Next(150, 720 - 200);
        canvas.DrawRoundRect(SKRect.Create(x0 - 60 * sizeFactor, y0 - 80 * sizeFactor, 5 * cell + 120 * sizeFactor, cell * 1.6f + 160 * sizeFactor), 16, 16, paint);
        using var typeface = SKTypeface.FromFamilyName("Consolas", SKFontStyle.Bold);
        using var font = new SKFont(typeface, cell * 1.05f);
        for (var i = 0; i < digits.Length; i++)
        {
            paint.Color = i == digits.Length - 1 ? new SKColor(170, 20, 20) : new SKColor(15, 15, 15);
            var r = SKRect.Create(x0 + i * cell + 2, y0, cell - 4, cell * 1.4f);
            canvas.DrawRect(r, paint);
            paint.Color = SKColors.White;
            canvas.DrawText(digits[i].ToString(), r.MidX, r.Bottom - cell * 0.3f, SKTextAlign.Center, font, paint);
        }

        using var labelFont = new SKFont(typeface, 26 * sizeFactor);
        paint.Color = new SKColor(230, 230, 230);
        canvas.DrawText("kWh", x0 + 5 * cell + 10, y0 + cell * 1.9f, SKTextAlign.Left, labelFont, paint);
        return Relative(SKRect.Create(x0 - 12, y0 - 12, 5 * cell + 24, cell * 1.4f + 24), scene);
    }

    private static double[] SignScene(SKBitmap scene, string text, Random random, float sizeFactor)
    {
        using var canvas = new SKCanvas(scene);
        canvas.Clear(new SKColor((byte)random.Next(120, 180), (byte)random.Next(120, 180), (byte)random.Next(120, 180)));
        (SKColor Back, SKColor Fore)[] styles = [(new(0, 120, 60), SKColors.White), (new(20, 60, 140), SKColors.White), (new(240, 200, 20), SKColors.Black), (SKColors.White, new(30, 30, 30))];
        var (back, fore) = styles[random.Next(styles.Length)];
        using var typeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold);
        var size = (40 + random.Next(0, 40)) * sizeFactor;
        using var font = new SKFont(typeface, size);
        var width = font.MeasureText(text) + size * 1.4f;
        var plate = SKRect.Create(random.Next(40, (int)Math.Max(41, 1280 - width - 40)), random.Next(60, (int)(720 - size * 3)), width, size * 1.9f);
        using var paint = new SKPaint { IsAntialias = true, Color = back };
        canvas.DrawRoundRect(plate, 10, 10, paint);
        paint.Color = fore;
        canvas.DrawText(text, plate.MidX, plate.MidY + size * 0.36f, SKTextAlign.Center, font, paint);
        var margin = size * 0.4f;
        return Relative(SKRect.Inflate(plate, margin, margin), scene);
    }

    private static double[] Relative(SKRect r, SKBitmap scene)
    {
        var left = Math.Clamp(r.Left / scene.Width, 0, 1);
        var top = Math.Clamp(r.Top / scene.Height, 0, 1);
        var right = Math.Clamp(r.Right / scene.Width, 0, 1);
        var bottom = Math.Clamp(r.Bottom / scene.Height, 0, 1);
        return [Math.Round(left, 4), Math.Round(top, 4), Math.Round(right - left, 4), Math.Round(bottom - top, 4)];
    }

    private static void Save(SKBitmap scene, string path, Damage damage, Random random)
    {
        using var result = new SKBitmap(scene.Info);
        using (var canvas = new SKCanvas(result))
        {
            using var paint = new SKPaint();
            switch (damage)
            {
                case Damage.Blur:
                    paint.ImageFilter = SKImageFilter.CreateBlur(1.6f, 1.6f);
                    break;
                case Damage.HeavyBlur:
                    paint.ImageFilter = SKImageFilter.CreateBlur(2.4f, 2.4f);
                    break;
                case Damage.Dim:
                    // A dark picture: a third of the light, a little lifted black.
                    paint.ColorFilter = SKColorFilter.CreateColorMatrix(
                    [
                        0.33f, 0, 0, 0, 0.03f,
                        0, 0.33f, 0, 0, 0.03f,
                        0, 0, 0.36f, 0, 0.04f,
                        0, 0, 0, 1, 0,
                    ]);
                    break;
                case Damage.Slant:
                    canvas.Clear(SKColors.Gray);
                    canvas.RotateDegrees(random.Next(0, 2) == 0 ? -7 : 7, scene.Width / 2f, scene.Height / 2f);
                    break;
                case Damage.Perspective:
                    // Seen from the side: the far edge smaller.
                    canvas.Clear(SKColors.Gray);
                    canvas.SetMatrix(new SKMatrix(1, 0.08f, 0, 0.02f, 1, 0, 0.00018f, 0, 1));
                    break;
                default:
                    break;
            }

            using var image = SKImage.FromBitmap(scene);
            if (damage == Damage.Motion)
            {
                // Moving sideways while the shutter is open: copies along 9 pixels.
                canvas.DrawImage(image, 0, 0);
                using var copy = new SKPaint { Color = SKColors.White.WithAlpha(255 / 5) };
                for (var dx = 1; dx < 9; dx++)
                {
                    canvas.DrawImage(image, dx - 4, 0, new SKSamplingOptions(SKFilterMode.Linear), copy);
                }
            }
            else
            {
                canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKCubicResampler.Mitchell), paint);
            }

            if (damage == Damage.Glare)
            {
                // A bright reflection over part of the picture.
                using var glare = new SKPaint
                {
                    Shader = SKShader.CreateRadialGradient(
                        new SKPoint(scene.Width * (0.3f + 0.4f * (float)random.NextDouble()), scene.Height * 0.45f),
                        scene.Width * 0.25f,
                        [SKColors.White.WithAlpha(190), SKColors.White.WithAlpha(0)],
                        SKShaderTileMode.Clamp),
                };
                canvas.ResetMatrix();
                canvas.DrawRect(SKRect.Create(scene.Width, scene.Height), glare);
            }
        }

        if (damage is Damage.Noise or Damage.Dim)
        {
            AddNoise(result, damage == Damage.Dim ? 6 : 12, random);
        }

        using var data = result.Encode(SKEncodedImageFormat.Jpeg, damage == Damage.Jpeg ? 35 : 80);
        File.WriteAllBytes(path, data.ToArray());
    }

    private static void AddNoise(SKBitmap bitmap, int sigma, Random random)
    {
        var pixels = bitmap.GetPixelSpan();
        var copy = pixels.ToArray();
        for (var i = 0; i < copy.Length; i += 4)
        {
            var n = (int)(Gaussian(random) * sigma);
            for (var c = 0; c < 3; c++)
            {
                copy[i + c] = (byte)Math.Clamp(copy[i + c] + n, 0, 255);
            }
        }

        copy.CopyTo(bitmap.GetPixelSpan());
    }

    private static double Gaussian(Random random) =>
        Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble());

    /// <summary>The manifest as JSON (one picture per line, for small diffs).</summary>
    public static string ToJson(IEnumerable<AccuracyPicture> pictures)
    {
        var sb = new StringBuilder("{\n  \"pictures\": [\n");
        var list = pictures.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            sb.Append("    ").Append(System.Text.Json.JsonSerializer.Serialize(list[i], AccuracySet.Json)).Append(i < list.Count - 1 ? ",\n" : "\n");
        }

        return sb.Append("  ]\n}\n").ToString();
    }
}
