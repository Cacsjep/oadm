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

    /// <summary>Why the tests skip: the files that are not in the cache; null when all are there.</summary>
    public static string? Missing()
    {
        var missing = Files.Where(f => !File.Exists(f.Value.Path)).Select(f => f.Key).ToList();
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

/// <summary>A fact that needs the downloaded real model and photos; skipped with the reason when they are not in the cache.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class RealModelFactAttribute : FactAttribute
{
    public RealModelFactAttribute()
    {
        if (RealModelFiles.Missing() is { } reason)
        {
            Skip = reason;
        }
    }
}
