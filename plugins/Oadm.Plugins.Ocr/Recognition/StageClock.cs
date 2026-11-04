using System.Diagnostics;

namespace Oadm.Plugins.Ocr.Recognition;

/// <summary>The stages of one reading whose time <see cref="StageClock"/> sums (the speed measurements break a picture down).</summary>
internal enum Stage
{
    Decode,
    DetectInput,
    DetectModel,
    DetectBoxes,
    CutLines,
    Angle,
    RecognizeInput,
    RecognizeModel,
    Decoding,

    /// <summary>Wall time of the phases of one reading (the stages above sum over threads).</summary>
    WallFind,
    WallRead,
    WallAngle,
    WallTurned,
    WallWhole,
    Count,
}

/// <summary>
/// The time spent per <see cref="Stage"/> over all readings since <see cref="Reset"/>, summed over threads (so parallel work
/// counts once per thread). Two timestamps and an add per stage: always on.
/// </summary>
internal static class StageClock
{
    private static readonly long[] Ticks = new long[(int)Stage.Count];

    /// <summary>Adds the time since <paramref name="start"/> (<see cref="Stopwatch.GetTimestamp"/>) to <paramref name="stage"/>.</summary>
    public static void Add(Stage stage, long start) => Interlocked.Add(ref Ticks[(int)stage], Stopwatch.GetTimestamp() - start);

    /// <summary>Milliseconds per stage since the last <see cref="Reset"/>.</summary>
    public static IReadOnlyDictionary<Stage, double> Snapshot()
    {
        var result = new Dictionary<Stage, double>();
        for (var i = 0; i < Ticks.Length; i++)
        {
            result[(Stage)i] = Interlocked.Read(ref Ticks[i]) * 1000.0 / Stopwatch.Frequency;
        }

        return result;
    }

    public static void Reset()
    {
        for (var i = 0; i < Ticks.Length; i++)
        {
            Interlocked.Exchange(ref Ticks[i], 0);
        }
    }
}
