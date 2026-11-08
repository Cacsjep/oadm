using System.Reflection;
using System.Security.Cryptography;

namespace Oadm.Plugins.Vision.Tests.RealModels;

/// <summary>
/// The real model and photos of the RealModel tests (RealModels/RealModels.targets): their cache paths and SHA-256 from the
/// assembly metadata "RealModelFile" (name|path|sha256). Nothing here downloads.
/// </summary>
internal static class RealModelFiles
{
    public const string Category = "RealModel";

    public const string RfDetrMedium = "rf-detr-medium.onnx";

    public const string RfDetrSmall = "rf-detr-small.onnx";

    /// <summary>facebook/detr-resnet-50 as transformers.js exports it (Xenova): <c>logits</c>, <c>pred_boxes</c>, a free input size.</summary>
    public const string DetrResNet50 = "detr-resnet-50.onnx";

    /// <summary>The same export quantized (dynamic int8 weights).</summary>
    public const string DetrResNet50Quantized = "detr-resnet-50-quantized.onnx";

    /// <summary>PekingU/rtdetr_v2_r18vd as onnx-community exports it: <c>logits</c> (sigmoid, 80 classes), <c>pred_boxes</c>.</summary>
    public const string RtDetrV2R18 = "rtdetr-v2-r18vd.onnx";

    /// <summary>ustc-community/dfine_s_coco as onnx-community exports it, the same outputs as RT-DETR.</summary>
    public const string DFineSmall = "dfine-s-coco.onnx";

    /// <summary>microsoft/resnet-50 as transformers.js exports it (Xenova): <c>logits [1, 1000]</c>.</summary>
    public const string ResNet50 = "resnet-50.onnx";

    /// <summary>The config.json of the ResNet-50 export: its ImageNet class names (<c>id2label</c>) for a labels file.</summary>
    public const string ResNet50Config = "resnet-50-config.json";

    /// <summary>MobileNetV2 of the ONNX Model Zoo (opset 12).</summary>
    public const string MobileNetV2 = "mobilenetv2-12.onnx";

    /// <summary>Ultralytics YOLO11n-cls on ImageNet, the ONNX file of mobilint's repository.</summary>
    public const string Yolo11Cls = "yolo11n-cls.onnx";

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

    /// <summary>The two COCO photos every test needs.</summary>
    public static readonly string[] Photos = [Skaters, SkateBowl];

    /// <summary>Why the tests skip: of <paramref name="names"/> (every file when none) and the photos, the ones not in the cache; null when all are there.</summary>
    public static string? Missing(params string[] names)
    {
        var wanted = names.Length == 0 ? Files.Keys.ToHashSet() : names.Concat(Photos).ToHashSet();
        var missing = Files.Where(f => wanted.Contains(f.Key) && !File.Exists(f.Value.Path)).Select(f => f.Key)
            .Concat(wanted.Where(w => !Files.ContainsKey(w))).ToList();
        return Files.Count == 0
            ? "No real model files are declared (RealModels.targets not imported)."
            : missing.Count == 0
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

/// <summary>A fact that needs downloaded real models and the photos; skipped with the reason when they are not in the cache.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class RealModelFactAttribute : FactAttribute
{
    /// <param name="files">The model files the test needs besides the photos; none: every file.</param>
    public RealModelFactAttribute(params string[] files)
    {
        if (RealModelFiles.Missing(files) is { } reason)
        {
            Skip = reason;
        }
    }
}
