using Oadm.Plugins.Shared;
using Oadm.Plugins.Vision.Api;
using Oadm.Plugins.Vision.Model;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Vision.Pipelines;

/// <summary>
/// The runtime stats of the page (the rules of Text recognition's "Runtime stats"): a sample every
/// <see cref="PipelineStats.SampleInterval"/>, and the live event <see cref="VisionMethods.StatsTopic"/> to each device scope
/// whose page asked for the stats within <see cref="VisionLimits.PreviewLease"/>. A scope sees the server-wide load and only
/// its own pipelines and previews.
/// </summary>
internal sealed partial class PipelineRuntime
{
    /// <summary>The scopes whose page keeps the live stats on (shared with Text recognition).</summary>
    private readonly StatsLeases _statsLeases;

    /// <summary>The scopes that get the live stats now (tests).</summary>
    public IReadOnlyCollection<string> StatsWatchers => [.. _statsLeases.Keys];

    /// <summary>The stats of the scope <paramref name="owner"/>, and the live event for it during the next <see cref="VisionLimits.PreviewLease"/>.</summary>
    public VisionStats GetStats(string owner)
    {
        _statsLeases.Keep(owner);
        return StatsFor(owner);
    }

    /// <summary>A watching pipeline's frames per second over the last window (null: not judged yet) and whether it runs slower than set.</summary>
    public (double? Reads, bool Slow) RateOf(Pipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        if (WatchOf(pipeline.Id) is null)
        {
            return (null, false);
        }

        var rates = _stats.Figures().Rates;
        if (!rates.TryGetValue(pipeline.Id, out var rate) || !rate.Judged)
        {
            return (null, false);
        }

        return (Math.Round(rate.Rate, 1), rate.Rate < PipelineStats.SlowShare * PipelineRules.StreamFps(pipeline));
    }

    internal VisionStats StatsFor(string owner)
    {
        var all = owner == ICallContext.AllDevicesScopeKey;
        bool Mine(string? scope) => all || string.Equals(OwnerScope.Of(scope), owner, StringComparison.OrdinalIgnoreCase);

        List<Pipeline> loops;
        lock (_loops)
        {
            loops = [.. _loops.Values.Select(l => l.Pipeline).Where(p => Mine(p.OwnerScopeKey))];
        }

        int previews;
        lock (_previews)
        {
            previews = _previews.Values.Count(p => Mine(p.Owner));
        }

        var figures = _stats.Figures();
        var (load, overloaded, rates) = (figures.Load, figures.Overloaded, figures.Rates);
        var (state, text) = Starting
            ? (LoadState.Starting, PipelineRules.Starting)
            : PipelineStats.Light(load, overloaded, loops.Select(p =>
            {
                var (rate, judged) = rates.TryGetValue(p.Id, out var r) ? r : (0, false);
                return (PipelineRules.StreamFps(p), rate, judged);
            }));
        return new VisionStats
        {
            State = state,
            StateText = text,
            Load = Math.Round(load, 1),
            Watching = loops.Count,
            MaxWatching = MaxWatching,
            Previews = previews,
            MaxPreviews = VisionLimits.MaxPreviews,
        };
    }

    private void TickStats()
    {
        try
        {
            Sample();
            if (_statsLeases.IsEmpty)
            {
                return;
            }

            foreach (var owner in _statsLeases.Live())
            {
                var audience = owner == ICallContext.AllDevicesScopeKey ? PluginEventAudience.AllDevices : PluginEventAudience.DeviceScope(owner);
                _ctx.Events?.Publish(VisionMethods.StatsTopic, VisionJson.Serialize(StatsFor(owner)), audience);
            }
        }
#pragma warning disable CA1031 // A timer callback never throws; the next tick tries again.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogBackgroundFailed(_logger, "the runtime stats", ex);
        }
    }

    /// <summary>A sample of the models' busy time and capacity (an inference that cannot tell has no load).</summary>
    private void Sample()
    {
        TimeSpan busy;
        int capacity;
        try
        {
            busy = _inference.BusyTime;
            capacity = _inference.Capacity;
        }
#pragma warning disable CA1031 // A load the inference cannot tell is no load.
        catch (Exception)
#pragma warning restore CA1031
        {
            (busy, capacity) = (TimeSpan.Zero, 1);
        }

        _stats.Sample(busy, capacity, TimeSpan.Zero);
    }
}
