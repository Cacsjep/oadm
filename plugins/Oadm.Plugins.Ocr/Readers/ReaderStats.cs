using Oadm.Plugins.Ocr.Api;
using Oadm.Plugins.Ocr.Model;

namespace Oadm.Plugins.Ocr.Readers;

/// <summary>
/// The runtime stats' measuring (docs/spec/ocr.md "Runtime stats") on the shared window of both vision add-ons
/// (<see cref="StatsWindow"/>, <see cref="StatsRules"/>): the readings only add to counters; every <see cref="SampleInterval"/>
/// a sample takes the counters, the recognition's busy time and the process's CPU time, and the figures come from the newest
/// sample against the oldest of the last <see cref="Window"/>. All time through the <see cref="TimeProvider"/>.
/// </summary>
internal sealed class ReaderStats
{
    /// <inheritdoc cref="StatsRules.SampleInterval"/>
    public static readonly TimeSpan SampleInterval = StatsRules.SampleInterval;

    /// <inheritdoc cref="StatsRules.Window"/>
    public static readonly TimeSpan Window = StatsRules.Window;

    /// <inheritdoc cref="StatsRules.OverloadHold"/>
    public static readonly TimeSpan OverloadHold = StatsRules.OverloadHold;

    /// <inheritdoc cref="StatsRules.BusyLoad"/>
    public const double BusyLoad = StatsRules.BusyLoad;

    /// <inheritdoc cref="StatsRules.OverloadLoad"/>
    public const double OverloadLoad = StatsRules.OverloadLoad;

    /// <inheritdoc cref="StatsRules.SlowShare"/>
    public const double SlowShare = StatsRules.SlowShare;

    /// <inheritdoc cref="StatsRules.VerySlowShare"/>
    public const double VerySlowShare = StatsRules.VerySlowShare;

    /// <summary>The texts of Text recognition's traffic light.</summary>
    public static readonly LightTexts Texts = new(ReaderRules.KeepUp, ReaderRules.Overloaded, ReaderRules.BusySlow, ReaderRules.BusyLoad);

    private const int Pictures = 0;
    private const int Reused = 1;
    private const int ReadTicks = 2;

    private readonly Func<TimeSpan> _processorTime;
    private readonly int _cores;
    private readonly StatsWindow _window;

    /// <param name="time">The clock.</param>
    /// <param name="processorTime">The OADM server process's CPU time so far (<see cref="Environment.CpuUsage"/>).</param>
    /// <param name="cores">The processor count.</param>
    public ReaderStats(TimeProvider time, Func<TimeSpan> processorTime, int cores)
    {
        ArgumentNullException.ThrowIfNull(time);
        _processorTime = processorTime ?? throw new ArgumentNullException(nameof(processorTime));
        _cores = Math.Max(1, cores);
        _window = new StatsWindow(time, counters: 3);
    }

    /// <summary>A picture was read (any reader, previews too) in <paramref name="readTime"/>.</summary>
    public void CountPicture(TimeSpan readTime)
    {
        _window.Add(Pictures, 1);
        _window.Add(ReadTicks, readTime.Ticks);
    }

    /// <summary>
    /// A watching reader or preview gave every area's last reading again: no model ran. Counted apart from
    /// <see cref="CountPicture"/>, so pictures per second and the reading time stay the real readings.
    /// </summary>
    public void CountReused() => _window.Add(Reused, 1);

    /// <summary>A watching reader's loop started (its rate counts from now).</summary>
    public void ReaderStarted(string readerId) => _window.Started(readerId);

    /// <summary>A watching reader's loop stopped.</summary>
    public void ReaderStopped(string readerId) => _window.Stopped(readerId);

    /// <summary>A watching reader read a picture.</summary>
    public void CountReading(string readerId) => _window.Count(readerId);

    /// <summary>Takes a sample now; <paramref name="load"/> is the recognition's (null: no load measured).</summary>
    public void Sample(IRecognizerLoad? load) =>
        _window.Sample(load?.BusyTime ?? TimeSpan.Zero, Math.Max(1, load?.Capacity ?? 1), SafeProcessorTime());

    /// <summary>The figures of the last window (zero before two samples).</summary>
    public StatsFigures Figures()
    {
        var window = _window.Figures();
        var seconds = window.Seconds;
        if (seconds <= 0)
        {
            return new StatsFigures(0, 0, 0, 0, window.Overloaded, window.Rates);
        }

        var pictures = window.Counts[Pictures];
        return new StatsFigures(
            window.Load,
            pictures / seconds,
            pictures > 0 ? TimeSpan.FromTicks(window.Counts[ReadTicks]).TotalMilliseconds / pictures : 0,
            Math.Clamp(window.ProcessorTime.TotalSeconds / (seconds * _cores) * 100, 0, 100),
            window.Overloaded,
            window.Rates)
        {
            ReusedPerSecond = window.Counts[Reused] / seconds,
        };
    }

    private TimeSpan SafeProcessorTime()
    {
        try
        {
            return _processorTime();
        }
#pragma warning disable CA1031 // A platform without the process's CPU time: the stats show 0 %.
        catch (Exception)
#pragma warning restore CA1031
        {
            return TimeSpan.Zero;
        }
    }

    /// <summary>The traffic light (docs/spec/ocr.md "Runtime stats", <see cref="StatsRules.Light"/>) with Text recognition's texts.</summary>
    public static (LoadState State, string Text) Light(double load, bool overloaded, IEnumerable<(int Fps, double Reads, bool Judged)> readers)
    {
        var (level, text) = StatsRules.Light(load, overloaded, readers, Texts);
        return ((LoadState)(int)level, text);
    }
}

/// <summary>The figures of the last window.</summary>
/// <param name="Load">0 to 100.</param>
/// <param name="PicturesPerSecond">Pictures read per second.</param>
/// <param name="AverageReadMs">Milliseconds per picture.</param>
/// <param name="ServerCpu">0 to 100.</param>
/// <param name="Overloaded">The load held 95 % for 30 s.</param>
/// <param name="Rates">Per watching reader: pictures per second and whether it watched the whole window.</param>
internal sealed record StatsFigures(
    double Load,
    double PicturesPerSecond,
    double AverageReadMs,
    double ServerCpu,
    bool Overloaded,
    IReadOnlyDictionary<string, (double Reads, bool Judged)> Rates)
{
    /// <summary>Pictures per second whose areas were all unchanged (their last reading given again, no model ran).</summary>
    public double ReusedPerSecond { get; init; }
}
