using Oadm.Sdk.Client.Controls;

namespace Oadm.Plugins.LoadTest.Client;

/// <summary>
/// The run's graph values, one per second, appended from the live events. Every change hands the charts new lists
/// (the charts redraw on a new list, never on one changed in place).
/// </summary>
internal sealed class CheckGraphs
{
    private readonly List<CheckPhase> _phases = [];
    private readonly List<double> _total = [];
    private readonly List<double> _averageLoss = [];
    private readonly List<double> _worstLoss = [];
    private readonly List<double> _jitter = [];
    private IReadOnlyList<StepResult> _steps = [];

    /// <summary>Seconds so far.</summary>
    public int Count => _total.Count;

    public void Clear()
    {
        _phases.Clear();
        _total.Clear();
        _averageLoss.Clear();
        _worstLoss.Clear();
        _jitter.Clear();
        _steps = [];
    }

    public void Replace(RunGraphs graphs)
    {
        ArgumentNullException.ThrowIfNull(graphs);
        Clear();
        Append(0, graphs);
    }

    /// <summary>Adds the points from <paramref name="fromSecond"/>; false when points are missing before it (read the run again).</summary>
    public bool Append(int fromSecond, RunGraphs points)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (fromSecond > Count)
        {
            return false;
        }

        // A repeated event (after a reconnect) overwrites what it covers.
        Trim(fromSecond);
        _phases.AddRange(points.Phases);
        _total.AddRange(points.TotalMbps);
        _averageLoss.AddRange(points.AverageLossPercent);
        _worstLoss.AddRange(points.WorstLossPercent);
        _jitter.AddRange(points.AverageJitterMs);
        if (points.Steps.Count > 0)
        {
            _steps = points.Steps;
        }

        return true;
    }

    /// <summary>The phase marking of the line charts: one band per run of equal phases.</summary>
    public IReadOnlyList<ChartBand> Bands() => BandsOf(_phases);

    public static IReadOnlyList<ChartBand> BandsOf(IReadOnlyList<CheckPhase> phases)
    {
        ArgumentNullException.ThrowIfNull(phases);
        var bands = new List<ChartBand>();
        var start = 0;
        for (var i = 1; i <= phases.Count; i++)
        {
            if (i == phases.Count || phases[i] != phases[start])
            {
                bands.Add(new ChartBand(start, i, CheckTexts.BarLabel(phases[start])));
                start = i;
            }
        }

        return bands;
    }

    public IReadOnlyList<ChartSeries> Rate() => [new("Total", [.. _total], ChartColor.Primary)];

    public IReadOnlyList<ChartSeries> Loss() =>
    [
        new("Average", [.. _averageLoss], ChartColor.Primary),
        new("Worst camera", [.. _worstLoss], ChartColor.Warning),
    ];

    public IReadOnlyList<ChartSeries> Jitter() => [new("Average", [.. _jitter], ChartColor.Primary)];

    /// <summary>One bar per step over all batches: the average loss weighted by the streams, red when a camera failed, else green.</summary>
    public IReadOnlyList<ChartBar> Steps()
    {
        var bars = new List<ChartBar>();
        foreach (var group in _steps.GroupBy(s => s.Phase).OrderBy(g => g.Key))
        {
            var streams = group.Sum(s => s.StreamsAtOnce);
            var loss = streams == 0 ? 0 : group.Sum(s => s.AverageLossPercent * s.StreamsAtOnce) / streams;
            var failed = group.Sum(s => s.FailedStreams);
            bars.Add(new ChartBar(CheckTexts.BarLabel(group.Key), loss, failed > 0 ? ChartColor.Error : ChartColor.Ok));
        }

        return bars;
    }

    private void Trim(int count)
    {
        if (count >= Count)
        {
            return;
        }

        _phases.RemoveRange(count, _phases.Count - count);
        _total.RemoveRange(count, _total.Count - count);
        _averageLoss.RemoveRange(count, _averageLoss.Count - count);
        _worstLoss.RemoveRange(count, _worstLoss.Count - count);
        _jitter.RemoveRange(count, _jitter.Count - count);
    }
}
