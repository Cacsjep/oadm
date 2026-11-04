using System.Collections.Concurrent;

using Oadm.Plugins.Ocr.Api;
using Oadm.Plugins.Ocr.Model;
using Oadm.Plugins.Shared;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Ocr.Readers;

/// <summary>
/// The runtime stats of the page (docs/spec/ocr.md "Runtime stats"): a sample every <see cref="ReaderStats.SampleInterval"/>
/// (<see cref="ReaderStats"/>), and the live event <see cref="OcrMethods.StatsTopic"/> to each device scope whose page asked
/// for the stats within <see cref="OcrLimits.PreviewLease"/>. A scope sees the server-wide load, CPU, pictures per second and
/// reading time, and only its own readers, previews and streams.
/// </summary>
internal sealed partial class ReaderRuntime
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _statsLeases = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The scopes that get the live stats now (tests).</summary>
    public IReadOnlyCollection<string> StatsWatchers => [.. _statsLeases.Keys];

    /// <summary>
    /// The stats of the scope <paramref name="owner"/> (<see cref="OwnerScope.Of"/>) and the live event for it during the next
    /// <see cref="OcrLimits.PreviewLease"/>.
    /// </summary>
    public OcrStats GetStats(string owner)
    {
        _statsLeases[owner] = _time.GetUtcNow() + OcrLimits.PreviewLease;
        return StatsFor(owner);
    }

    /// <summary>The timer: a sample, then the live stats to the scopes whose page keeps them on.</summary>
    private void TickStats()
    {
        try
        {
            _stats.Sample(_recognizer as IRecognizerLoad);
            if (_ctx.Events is not { } events || _statsLeases.IsEmpty)
            {
                return;
            }

            var now = _time.GetUtcNow();
            foreach (var (owner, until) in _statsLeases)
            {
                if (until <= now)
                {
                    _statsLeases.TryRemove(new KeyValuePair<string, DateTimeOffset>(owner, until));
                    continue;
                }

                var audience = owner == ICallContext.AllDevicesScopeKey ? PluginEventAudience.AllDevices : PluginEventAudience.DeviceScope(owner);
                events.Publish(OcrMethods.StatsTopic, OcrJson.Serialize(StatsFor(owner)), audience);
            }
        }
#pragma warning disable CA1031 // A timer callback never throws; the next tick tries again.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogBackgroundFailed(_logger, "the runtime stats", ex);
        }
    }

    /// <summary>The stats as the scope <paramref name="owner"/> sees them.</summary>
    internal OcrStats StatsFor(string owner)
    {
        var all = owner == ICallContext.AllDevicesScopeKey;
        bool Mine(string? scope) => all || string.Equals(OwnerScope.Of(scope), owner, StringComparison.OrdinalIgnoreCase);

        List<WatchLoop> loops;
        lock (_loops)
        {
            loops = [.. _loops.Values.Where(l => Mine(l.Reader.OwnerScopeKey))];
        }

        List<(Guid? Site, string? SiteName)> previewStreams;
        int previews;
        lock (_previews)
        {
            var mine = _previews.Values.Where(p => Mine(p.Owner)).ToList();
            previews = mine.Count;
            previewStreams = [.. mine.Where(p => p.Loop is not null).Select(p => (p.Site, p.SiteName))];
        }

        var figures = _stats.Figures();
        var readers = new List<(ReaderRate Rate, double Reads, bool Judged)>(loops.Count);
        foreach (var loop in loops.OrderBy(l => l.Reader.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var (reads, judged) = figures.Rates.TryGetValue(loop.Reader.Id, out var rate) ? rate : (0, false);
            var fps = loop.Reader.Fps;
            readers.Add((new ReaderRate
            {
                ReaderId = loop.Reader.Id,
                Fps = fps,
                Reads = Math.Round(reads, 1),
                Slow = judged && reads < ReaderStats.SlowShare * fps,
            }, reads, judged));
        }

        var (state, text) = Starting
            ? (LoadState.Starting, ReaderRules.Starting)
            : ReaderStats.Light(figures.Load, figures.Overloaded, readers.Select(r => (r.Rate.Fps, r.Reads, r.Judged)));
        var sites = loops.Select(l => (l.Site, l.SiteName)).Concat(previewStreams)
            .Where(s => s.Site is not null)
            .GroupBy(s => s.Site!.Value)
            .Select(g => new SiteStreams { Name = g.First().SiteName ?? string.Empty, Streams = g.Count(), Max = ReaderRules.MaxWatchingPerSite })
            .OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return new OcrStats
        {
            State = state,
            StateText = text,
            Load = Math.Round(figures.Load, 1),
            PicturesPerSecond = Math.Round(figures.PicturesPerSecond, 1),
            UnchangedPerSecond = Math.Round(figures.ReusedPerSecond, 1),
            AverageReadMs = Math.Round(figures.AverageReadMs),
            Watching = loops.Count,
            MaxWatching = MaxWatching,
            Previews = previews,
            MaxPreviews = OcrLimits.MaxPreviews,
            Streams = loops.Count + previewStreams.Count,
            Sites = sites,
            ServerCpu = Math.Round(figures.ServerCpu, 1),
            Readers = [.. readers.Select(r => r.Rate)],
        };
    }
}
