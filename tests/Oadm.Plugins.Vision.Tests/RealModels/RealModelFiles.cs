using System.Reflection;
using System.Security.Cryptography;

namespace Oadm.Plugins.Vision.Tests.RealModels;

/// <summary>
/// The real models and photos of the RealModel tests (RealModels/RealModels.targets): their cache paths and SHA-256 from the
/// assembly metadata "RealModelFile" (name|path|sha256). Nothing here downloads.
/// </summary>
internal static class RealModelFiles
{
    public const string Category = "RealModel";

    public const string RfDetrMedium = "rf-detr-medium.onnx";

    public const string Yolov8n = "yolov8n.onnx";

    public const string Yolo11n = "yolo11n.onnx";

    /// <summary>YOLO11n with half precision input and output (Unity's export).</summary>
    public const string Yolo11nFp16 = "yolo11n-fp16.onnx";

    /// <summary>YOLO11n without the metadata names (Kalray's export).</summary>
    public const string Yolo11nNoNames = "yolo11n-no-names.onnx";

    /// <summary>YOLOv5n of the v7.0 release: half precision input and output.</summary>
    public const string Yolov5n = "yolov5n.onnx";

    /// <summary>YOLOv10n of onnx-community: end to end, its metadata names are the ids.</summary>
    public const string Yolov10n = "yolov10n.onnx";

    public const string Yolo26n = "yolo26n.onnx";

    public const string YoloxNano = "yolox_nano.onnx";

    public const string YoloxTiny = "yolox_tiny.onnx";

    /// <summary>COCO val2017 278705: two skateboarders, two parked cars.</summary>
    public const string Skaters = "coco-000000278705.jpg";

    /// <summary>COCO val2017 65350: three people at a skate bowl, cars and bicycles behind a fence.</summary>
    public const string SkateBowl = "coco-000000065350.jpg";

    private static readonly Dictionary<string, (string Path, string Sha256)> Files = typeof(RealModelFiles).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .Where(a => a.Key == "RealModelFile" && a.Value is not null)
        .Select(a => a.Value!.Split('|'))
        .ToDictionary(p => p[0], p => (Path.GetFullPath(p[1]), p[2]));

    public static IReadOnlyCollection<string> Names => Files.Keys;

    public static string Sha256Of(string name) => Files[name].Sha256;

    /// <summary>Why the tests of <paramref name="needed"/> (and the photos) skip: the files that are not in the cache; null when all are there.</summary>
    public static string? Missing(params string[] needed)
    {
        if (Files.Count == 0)
        {
            return "No real model files are declared (RealModels.targets not imported).";
        }

        var missing = needed.Append(Skaters).Append(SkateBowl).Distinct()
            .Where(n => !Files.TryGetValue(n, out var f) || !File.Exists(f.Path)).ToList();
        return missing.Count == 0
            ? null
            : $"Not downloaded: {string.Join(", ", missing)}. Run 'manage test models' (or build with -p:OadmRealModels=true).";
    }

    /// <summary>The cached file, checked against its SHA-256 (a changed cache file fails clearly instead of testing another model).</summary>
    public static string PathOf(string name)
    {
        var (path, sha256) = Files[name];
        using var stream = File.OpenRead(path);
        var actual = Convert.ToHexStringLower(SHA256.HashData(stream));
        return actual == sha256
            ? path
            : throw new InvalidOperationException($"{path} has the SHA-256 {actual}, expected {sha256}. Delete it and run 'manage test models' again.");
    }
}

/// <summary>A fact that needs the downloaded model files <paramref name="needed"/> and the photos; skipped with the reason when they are not in the cache.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class RealModelFactAttribute : FactAttribute
{
    public RealModelFactAttribute(params string[] needed)
    {
        if (RealModelFiles.Missing(needed) is { } reason)
        {
            Skip = reason;
        }
    }
}
