using System.Collections.Concurrent;

namespace Oadm.Plugins.Shared.Vision;

/// <summary>The traffic light's colour (each plugin maps it to its own page enum).</summary>
internal enum LightLevel
{
    Ok = 0,
    Busy = 1,
    Overloaded = 2,
}

/// <summary>The traffic light's texts of one plugin: Text recognition says readers, Vision Center pipelines.</summary>
/// <param name="KeepUp">Green.</param>
/// <param name="Overloaded">Red.</param>
/// <param name="BusySlow">Orange because this many fall behind.</param>
/// <param name="BusyLoad">Orange because of the load (0 to 100).</param>
internal sealed record LightTexts(string KeepUp, string Overloaded, Func<int, string> BusySlow, Func<double, string> BusyLoad);

/// <summary>
/// The rules of the runtime stats of both vision add-ons (docs/spec/ocr.md "Runtime stats", docs/spec/vision-center.md "Load"):
/// a sample every <see cref="SampleInterval"/>, the figures over the last <see cref="Window"/>, and the traffic light.
/// </summary>
internal static class StatsRules
{
    /// <summary>How often a sample is taken and the live stats go to the pages.</summary>
    public static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(2);

    /// <summary>The window the figures are measured over.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(10);

    /// <summary>The load must stay at <see cref="OverloadLoad"/> or more this long for red.</summary>
    public static readonly TimeSpan OverloadHold = TimeSpan.FromSeconds(30);

    /// <summary>How long a page's stats stay on after it asked (the live event per scope).</summary>
    public static readonly TimeSpan StatsLease = TimeSpan.FromSeconds(30);

    /// <summary>From this load (percent) on: orange.</summary>
    public const double BusyLoad = 75;

    /// <summary>From this load (percent) on, held for <see cref="OverloadHold"/>: red.</summary>
    public const double OverloadLoad = 95;

    /// <summary>Below this share of its frame rate a reader or pipeline runs slower than set (orange).</summary>
    public const double SlowShare = 0.9;

    /// <summary>Below this share of its frame rate: red.</summary>
    public const double VerySlowShare = 0.5;

    /// <summary>
    /// The traffic light: red when the load held <see cref="OverloadLoad"/> for <see cref="OverloadHold"/> and one falls behind
    /// (or none is judged: the machine has no room left); orange when one runs below <see cref="SlowShare"/> of its rate or the
    /// load is <see cref="BusyLoad"/> or more; green otherwise. Only those that ran for the whole window are judged.
    /// </summary>
    public static (LightLevel Level, string Text) Light(double load, bool overloaded, IEnumerable<(int Fps, double Rate, bool Judged)> items, LightTexts texts)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(texts);
        var judged = items.Where(r => r.Judged && r.Fps > 0).ToList();
        if (overloaded && (judged.Count == 0 || judged.Any(r => r.Rate < SlowShare * r.Fps)))
        {
            return (LightLevel.Overloaded, texts.Overloaded);
        }

        var slow = judged.Count(r => r.Rate < SlowShare * r.Fps);
        if (slow > 0)
        {
            return (LightLevel.Busy, texts.BusySlow(slow));
        }

        return load >= BusyLoad ? (LightLevel.Busy, texts.BusyLoad(load)) : (LightLevel.Ok, texts.KeepUp);
    }
}

/// <summary>The figures of the last window.</summary>
/// <param name="Load">The models' load, 0 to 100.</param>
/// <param name="Overloaded">The load held <see cref="StatsRules.OverloadLoad"/> for <see cref="StatsRules.OverloadHold"/>.</param>
/// <param name="Rates">Per running reader or pipeline: its frames per second and whether it ran for the whole window.</param>
/// <param name="Seconds">The window's length so far (0 before two samples).</param>
/// <param name="Counts">The growth of each extra counter over the window.</param>
/// <param name="ProcessorTime">The growth of the process's CPU time over the window.</param>
internal sealed record WindowFigures(
    double Load,
    bool Overloaded,
    IReadOnlyDictionary<string, (double Rate, bool Judged)> Rates,
    double Seconds,
    IReadOnlyList<long> Counts,
    TimeSpan ProcessorTime);

/// <summary>
/// The measuring of the runtime stats (<see cref="StatsRules"/>): the work only adds to counters (one interlocked add, no
/// allocation per picture); every sample takes them with the models' busy time and capacity, and the figures come from the
/// newest sample against the oldest of the last <see cref="StatsRules.Window"/>. All time through the <see cref="TimeProvider"/>.
/// </summary>
/// <param name="time">The clock.</param>
/// <param name="counters">How many extra counters the plugin adds to (<see cref="Add"/>).</param>
internal sealed class StatsWindow(TimeProvider time, int counters = 0)
{
    private readonly ConcurrentDictionary<string, Counter> _items = new(StringComparer.Ordinal);
    private readonly long[] _counts = new long[counters];
    private readonly Queue<Snapshot> _samples = new();
    private DateTimeOffset? _highSince;

    /// <summary>A reader or pipeline started (its rate counts from now).</summary>
    public void Started(string id) => _items[id] = new Counter(time.GetUtcNow());

    public void Stopped(string id) => _items.TryRemove(id, out _);

    /// <summary>A running reader or pipeline did a frame.</summary>
    public void Count(string id)
    {
        if (_items.TryGetValue(id, out var counter))
        {
            counter.Add();
        }
    }

    /// <summary>Adds <paramref name="amount"/> to the extra counter <paramref name="counter"/>.</summary>
    public void Add(int counter, long amount) => Interlocked.Add(ref _counts[counter], amount);

    /// <summary>Takes a sample now.</summary>
    public void Sample(TimeSpan busy, int capacity, TimeSpan processorTime)
    {
        var now = time.GetUtcNow();
        var counts = new Dictionary<string, long>(_items.Count, StringComparer.Ordinal);
        foreach (var (id, counter) in _items)
        {
            counts[id] = counter.Count;
        }

        var extra = new long[_counts.Length];
        for (var i = 0; i < extra.Length; i++)
        {
            extra[i] = Interlocked.Read(ref _counts[i]);
        }

        var sample = new Snapshot(now, busy, Math.Max(1, capacity), processorTime, extra, counts);
        lock (_samples)
        {
            _samples.Enqueue(sample);
            // Keep the newest sample at or before the window's start as the base; older ones go.
            while (_samples.Count > 1 && _samples.ElementAt(1).Time <= now - StatsRules.Window)
            {
                _samples.Dequeue();
            }

            if (Load(_samples.Peek(), sample) >= StatsRules.OverloadLoad)
            {
                _highSince ??= now;
            }
            else
            {
                _highSince = null;
            }
        }
    }

    /// <summary>The figures of the last window (zero before two samples).</summary>
    public WindowFigures Figures()
    {
        lock (_samples)
        {
            var rates = new Dictionary<string, (double, bool)>(StringComparer.Ordinal);
            if (_samples.Count == 0)
            {
                return new WindowFigures(0, false, rates, 0, new long[_counts.Length], TimeSpan.Zero);
            }

            var oldest = _samples.Peek();
            var newest = _samples.Last();
            foreach (var (id, count) in newest.Items)
            {
                if (!_items.TryGetValue(id, out var counter))
                {
                    continue;
                }

                // Running since before the window: from the window's first sample; started inside it: from its start.
                var (baseTime, baseCount) = oldest.Time >= counter.Since && oldest.Items.TryGetValue(id, out var atStart)
                    ? (oldest.Time, atStart)
                    : (counter.Since, 0L);
                var seconds = (newest.Time - baseTime).TotalSeconds;
                rates[id] = (seconds > 0 ? (count - baseCount) / seconds : 0, newest.Time - counter.Since >= StatsRules.Window);
            }

            var window = (newest.Time - oldest.Time).TotalSeconds;
            if (window <= 0)
            {
                return new WindowFigures(0, false, rates, 0, new long[_counts.Length], TimeSpan.Zero) with
                {
                    Overloaded = Overloaded(newest),
                };
            }

            return new WindowFigures(
                Load(oldest, newest),
                Overloaded(newest),
                rates,
                window,
                [.. newest.Counts.Zip(oldest.Counts, (n, o) => n - o)],
                newest.ProcessorTime - oldest.ProcessorTime);
        }
    }

    private bool Overloaded(Snapshot newest) => _highSince is { } since && newest.Time - since >= StatsRules.OverloadHold;

    private static double Load(Snapshot oldest, Snapshot newest)
    {
        var seconds = (newest.Time - oldest.Time).TotalSeconds;
        return seconds <= 0 ? 0 : Math.Clamp((newest.Busy - oldest.Busy).TotalSeconds / (seconds * newest.Capacity) * 100, 0, 100);
    }

    private sealed class Counter(DateTimeOffset since)
    {
        private long _count;

        public DateTimeOffset Since { get; } = since;

        public long Count => Interlocked.Read(ref _count);

        public void Add() => Interlocked.Increment(ref _count);
    }

    private sealed record Snapshot(DateTimeOffset Time, TimeSpan Busy, int Capacity, TimeSpan ProcessorTime, long[] Counts, Dictionary<string, long> Items);
}

/// <summary>
/// The device scopes whose page asked for the stats within <see cref="StatsRules.StatsLease"/>: each gets the live stats event
/// until its lease ends (a closed page asks no more).
/// </summary>
internal sealed class StatsLeases(TimeProvider time)
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _leases = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The scopes that get the live stats now.</summary>
    public IReadOnlyCollection<string> Keys => [.. _leases.Keys];

    public bool IsEmpty => _leases.IsEmpty;

    /// <summary>The page of <paramref name="scope"/> asked: its stats stay on for the next <see cref="StatsRules.StatsLease"/>.</summary>
    public void Keep(string scope) => _leases[scope] = time.GetUtcNow() + StatsRules.StatsLease;

    /// <summary>The scopes whose lease runs; ended ones go.</summary>
    public IReadOnlyList<string> Live()
    {
        var now = time.GetUtcNow();
        var live = new List<string>(_leases.Count);
        foreach (var (scope, until) in _leases)
        {
            if (until <= now)
            {
                _leases.TryRemove(new KeyValuePair<string, DateTimeOffset>(scope, until));
                continue;
            }

            live.Add(scope);
        }

        return live;
    }
}
