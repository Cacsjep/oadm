using System.Collections.Concurrent;

using Oadm.Plugins.Vision.Api;
using Oadm.Plugins.Vision.Model;

namespace Oadm.Plugins.Vision.Pipelines;

/// <summary>
/// The runtime stats' measuring (docs/spec/vision-center.md "Load", the rules of Text recognition's "Runtime stats"): the
/// frames only add to counters; every <see cref="SampleInterval"/> a sample takes them with the models' busy time, and the
/// figures come from the newest sample against the oldest of the last <see cref="Window"/>. All time through the
/// <see cref="TimeProvider"/>.
/// </summary>
internal sealed class PipelineStats(TimeProvider time)
{
    public static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(2);

    public static readonly TimeSpan Window = TimeSpan.FromSeconds(10);

    /// <summary>The load must stay at <see cref="OverloadLoad"/> or more this long for red.</summary>
    public static readonly TimeSpan OverloadHold = TimeSpan.FromSeconds(30);

    public const double BusyLoad = 75;

    public const double OverloadLoad = 95;

    /// <summary>A pipeline below this share of its frame rate runs slower than set (orange).</summary>
    public const double SlowShare = 0.9;

    private readonly ConcurrentDictionary<string, Counter> _pipelines = new(StringComparer.Ordinal);
    private readonly Queue<Snapshot> _samples = new();
    private DateTimeOffset? _highSince;

    public void Started(string pipelineId) => _pipelines[pipelineId] = new Counter(time.GetUtcNow());

    public void Stopped(string pipelineId) => _pipelines.TryRemove(pipelineId, out _);

    /// <summary>A watching pipeline ran a frame.</summary>
    public void CountFrame(string pipelineId)
    {
        if (_pipelines.TryGetValue(pipelineId, out var counter))
        {
            counter.Add();
        }
    }

    public void Sample(IInference? inference)
    {
        var now = time.GetUtcNow();
        var counts = new Dictionary<string, long>(_pipelines.Count, StringComparer.Ordinal);
        foreach (var (id, counter) in _pipelines)
        {
            counts[id] = counter.Count;
        }

        TimeSpan busy;
        int capacity;
        try
        {
            busy = inference?.BusyTime ?? TimeSpan.Zero;
            capacity = Math.Max(1, inference?.Capacity ?? 1);
        }
#pragma warning disable CA1031 // A load the inference cannot tell is no load.
        catch (Exception)
#pragma warning restore CA1031
        {
            (busy, capacity) = (TimeSpan.Zero, 1);
        }

        var sample = new Snapshot(now, busy, capacity, counts);
        lock (_samples)
        {
            _samples.Enqueue(sample);
            while (_samples.Count > 1 && _samples.ElementAt(1).Time <= now - Window)
            {
                _samples.Dequeue();
            }

            if (Load(_samples.Peek(), sample) >= OverloadLoad)
            {
                _highSince ??= now;
            }
            else
            {
                _highSince = null;
            }
        }
    }

    /// <summary>The load of the last window (0 to 100), whether it held <see cref="OverloadLoad"/> long enough, and per watching pipeline its frames per second and whether it watched the whole window.</summary>
    public (double Load, bool Overloaded, IReadOnlyDictionary<string, (double Rate, bool Judged)> Rates) Figures()
    {
        lock (_samples)
        {
            var rates = new Dictionary<string, (double, bool)>(StringComparer.Ordinal);
            if (_samples.Count == 0)
            {
                return (0, false, rates);
            }

            var oldest = _samples.Peek();
            var newest = _samples.Last();
            foreach (var (id, count) in newest.Pipelines)
            {
                if (!_pipelines.TryGetValue(id, out var counter))
                {
                    continue;
                }

                var (baseTime, baseCount) = oldest.Time >= counter.Since && oldest.Pipelines.TryGetValue(id, out var atStart)
                    ? (oldest.Time, atStart)
                    : (counter.Since, 0L);
                var seconds = (newest.Time - baseTime).TotalSeconds;
                rates[id] = (seconds > 0 ? (count - baseCount) / seconds : 0, newest.Time - counter.Since >= Window);
            }

            return (Load(oldest, newest), _highSince is { } since && newest.Time - since >= OverloadHold, rates);
        }
    }

    /// <summary>
    /// The traffic light: red when the load held <see cref="OverloadLoad"/> for <see cref="OverloadHold"/> and a pipeline falls
    /// behind (or none is judged); orange when a pipeline runs below <see cref="SlowShare"/> of its rate or the load is
    /// <see cref="BusyLoad"/> or more; green otherwise.
    /// </summary>
    public static (LoadState State, string Text) Light(double load, bool overloaded, IEnumerable<(int Fps, double Rate, bool Judged)> pipelines)
    {
        var judged = pipelines.Where(p => p.Judged && p.Fps > 0).ToList();
        if (overloaded && (judged.Count == 0 || judged.Any(p => p.Rate < SlowShare * p.Fps)))
        {
            return (LoadState.Overloaded, PipelineRules.Overloaded);
        }

        var slow = judged.Count(p => p.Rate < SlowShare * p.Fps);
        if (slow > 0)
        {
            return (LoadState.Busy, PipelineRules.BusySlow(slow));
        }

        return load >= BusyLoad ? (LoadState.Busy, PipelineRules.BusyLoad(load)) : (LoadState.Ok, PipelineRules.KeepUp);
    }

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

    private sealed record Snapshot(DateTimeOffset Time, TimeSpan Busy, int Capacity, Dictionary<string, long> Pipelines);
}
