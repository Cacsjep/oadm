using System.Collections.Concurrent;

using Oadm.Plugins.Ocr.Api;
using Oadm.Plugins.Ocr.Model;

namespace Oadm.Plugins.Ocr.Readers;

/// <summary>
/// The runtime stats' measuring (docs/spec/ocr.md "Runtime stats"). The readings only add to counters (one interlocked add,
/// no allocation per picture); every <see cref="SampleInterval"/> a sample takes the counters, the recognition's busy time and
/// the process's CPU time, and the figures come from the newest sample against the oldest of the last <see cref="Window"/>.
/// All time through the <see cref="TimeProvider"/>.
/// </summary>
internal sealed class ReaderStats
{
    /// <summary>How often a sample is taken and the live stats go to the pages.</summary>
    public static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(2);

    /// <summary>The window the figures are measured over.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(10);

    /// <summary>The load must stay at <see cref="OverloadLoad"/> or more this long for red.</summary>
    public static readonly TimeSpan OverloadHold = TimeSpan.FromSeconds(30);

    /// <summary>From this load (percent) on: orange.</summary>
    public const double BusyLoad = 75;

    /// <summary>From this load (percent) on, held for <see cref="OverloadHold"/>: red.</summary>
    public const double OverloadLoad = 95;

    /// <summary>A reader below this share of its frame rate reads slower than set (orange).</summary>
    public const double SlowShare = 0.9;

    /// <summary>A reader below this share of its frame rate: red.</summary>
    public const double VerySlowShare = 0.5;

    private readonly TimeProvider _time;
    private readonly Func<TimeSpan> _processorTime;
    private readonly int _cores;
    private readonly ConcurrentDictionary<string, ReaderCounter> _readers = new(StringComparer.Ordinal);
    private readonly Queue<Snapshot> _samples = new();
    private long _pictures;
    private long _readTicks;
    private DateTimeOffset? _highSince;

    /// <param name="time">The clock.</param>
    /// <param name="processorTime">The OADM server process's CPU time so far (<see cref="Environment.CpuUsage"/>).</param>
    /// <param name="cores">The processor count.</param>
    public ReaderStats(TimeProvider time, Func<TimeSpan> processorTime, int cores)
    {
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _processorTime = processorTime ?? throw new ArgumentNullException(nameof(processorTime));
        _cores = Math.Max(1, cores);
    }

    /// <summary>A picture was read (any reader, previews too) in <paramref name="readTime"/>.</summary>
    public void CountPicture(TimeSpan readTime)
    {
        Interlocked.Increment(ref _pictures);
        Interlocked.Add(ref _readTicks, readTime.Ticks);
    }

    /// <summary>A watching reader's loop started (its rate counts from now).</summary>
    public void ReaderStarted(string readerId) => _readers[readerId] = new ReaderCounter(_time.GetUtcNow());

    /// <summary>A watching reader's loop stopped.</summary>
    public void ReaderStopped(string readerId) => _readers.TryRemove(readerId, out _);

    /// <summary>A watching reader read a picture.</summary>
    public void CountReading(string readerId)
    {
        if (_readers.TryGetValue(readerId, out var counter))
        {
            counter.Add();
        }
    }

    /// <summary>Takes a sample now; <paramref name="load"/> is the recognition's (null: no load measured).</summary>
    public void Sample(IRecognizerLoad? load)
    {
        var now = _time.GetUtcNow();
        var counts = new Dictionary<string, long>(_readers.Count, StringComparer.Ordinal);
        foreach (var (id, counter) in _readers)
        {
            counts[id] = counter.Count;
        }

        var sample = new Snapshot(
            now,
            Interlocked.Read(ref _pictures),
            Interlocked.Read(ref _readTicks),
            load?.BusyTime ?? TimeSpan.Zero,
            Math.Max(1, load?.Capacity ?? 1),
            SafeProcessorTime(),
            counts);
        lock (_samples)
        {
            _samples.Enqueue(sample);
            // Keep the newest sample at or before the window's start as the base; older ones go.
            while (_samples.Count > 1 && _samples.ElementAt(1).Time <= now - Window)
            {
                _samples.Dequeue();
            }

            var figures = Measure(_samples.Peek(), sample);
            if (figures.Load >= OverloadLoad)
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
    public StatsFigures Figures()
    {
        lock (_samples)
        {
            if (_samples.Count == 0)
            {
                return new StatsFigures(0, 0, 0, 0, false, new Dictionary<string, (double, bool)>());
            }

            var newest = _samples.Last();
            var figures = Measure(_samples.Peek(), newest);
            var rates = new Dictionary<string, (double Reads, bool Judged)>(newest.Readers.Count, StringComparer.Ordinal);
            foreach (var (id, count) in newest.Readers)
            {
                if (!_readers.TryGetValue(id, out var counter))
                {
                    continue;
                }

                // Watching since before the window: from the window's first sample; started inside it: from its start.
                var oldest = _samples.Peek();
                var (baseTime, baseCount) = oldest.Time >= counter.Since && oldest.Readers.TryGetValue(id, out var atStart)
                    ? (oldest.Time, atStart)
                    : (counter.Since, 0L);

                var seconds = (newest.Time - baseTime).TotalSeconds;
                var reads = seconds > 0 ? (count - baseCount) / seconds : 0;
                rates[id] = (reads, newest.Time - counter.Since >= Window);
            }

            var overloaded = _highSince is { } since && newest.Time - since >= OverloadHold;
            return figures with { Overloaded = overloaded, Rates = rates };
        }
    }

    private StatsFigures Measure(Snapshot oldest, Snapshot newest)
    {
        var seconds = (newest.Time - oldest.Time).TotalSeconds;
        if (seconds <= 0)
        {
            return new StatsFigures(0, 0, 0, 0, false, new Dictionary<string, (double, bool)>());
        }

        var pictures = newest.Pictures - oldest.Pictures;
        var load = (newest.Busy - oldest.Busy).TotalSeconds / (seconds * newest.Capacity) * 100;
        var cpu = (newest.ProcessorTime - oldest.ProcessorTime).TotalSeconds / (seconds * _cores) * 100;
        return new StatsFigures(
            Math.Clamp(load, 0, 100),
            pictures / seconds,
            pictures > 0 ? TimeSpan.FromTicks(newest.ReadTicks - oldest.ReadTicks).TotalMilliseconds / pictures : 0,
            Math.Clamp(cpu, 0, 100),
            false,
            new Dictionary<string, (double, bool)>());
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

    /// <summary>
    /// The traffic light (docs/spec/ocr.md "Runtime stats"): red when the load held <see cref="OverloadLoad"/> for
    /// <see cref="OverloadHold"/> or a reader reads below <see cref="VerySlowShare"/> of its rate; orange when a reader reads
    /// below <see cref="SlowShare"/> or the load is <see cref="BusyLoad"/> or more; green otherwise. Only readers that watched
    /// for the whole window are judged.
    /// </summary>
    public static (LoadState State, string Text) Light(double load, bool overloaded, IEnumerable<(int Fps, double Reads, bool Judged)> readers)
    {
        var judged = readers.Where(r => r.Judged && r.Fps > 0).ToList();
        if (overloaded || judged.Any(r => r.Reads < VerySlowShare * r.Fps))
        {
            return (LoadState.Overloaded, ReaderRules.Overloaded);
        }

        var slow = judged.Count(r => r.Reads < SlowShare * r.Fps);
        if (slow > 0)
        {
            return (LoadState.Busy, ReaderRules.BusySlow(slow));
        }

        return load >= BusyLoad ? (LoadState.Busy, ReaderRules.BusyLoad(load)) : (LoadState.Ok, ReaderRules.KeepUp);
    }

    /// <summary>A watching reader's readings since its loop started.</summary>
    private sealed class ReaderCounter(DateTimeOffset since)
    {
        private long _count;

        public DateTimeOffset Since { get; } = since;

        public long Count => Interlocked.Read(ref _count);

        public void Add() => Interlocked.Increment(ref _count);
    }

    private sealed record Snapshot(
        DateTimeOffset Time,
        long Pictures,
        long ReadTicks,
        TimeSpan Busy,
        int Capacity,
        TimeSpan ProcessorTime,
        Dictionary<string, long> Readers);
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
    IReadOnlyDictionary<string, (double Reads, bool Judged)> Rates);
