using System.Globalization;

namespace Oadm.Plugins.Shared.Vision;

/// <summary>
/// The rules of the engine both vision add-ons follow (docs/spec/ocr.md "GPU"): when a model runs on the graphics card and
/// how "Runs on" reads.
/// </summary>
internal static class EngineRules
{
    /// <summary>A card may take this many times the CPU's time and still run the model: the CPU stays free.</summary>
    public const double MostSlower = 1.5;

    /// <summary>The card's test call in <paramref name="cardMs"/> runs the model there (null: the call failed).</summary>
    public static bool Worth(double? cardMs, double cpuMs) => cardMs is { } ms && ms <= cpuMs * MostSlower;

    /// <summary>"CPU (24 cores)".</summary>
    public static string CpuText(int cores) => string.Create(CultureInfo.InvariantCulture, $"CPU ({cores} {(cores == 1 ? "core" : "cores")})");

    /// <summary>What runs with a graphics card: "CPU (24 cores) · NVIDIA GeForce RTX 5070 Ti".</summary>
    public static string CardText(int cores, string card) => $"{CpuText(cores)} · {card}";
}
