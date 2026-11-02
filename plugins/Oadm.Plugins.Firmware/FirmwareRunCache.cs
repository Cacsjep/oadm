using System.Collections.Concurrent;

using Oadm.Plugins.Firmware.Online;

namespace Oadm.Plugins.Firmware;

/// <summary>
/// What the device tasks of one run share (README "Payload"): the payload parsed and indexed once
/// (<see cref="FirmwareRunIndex"/>), and one read of axis.com per version (source, product, version), its failure too.
/// A run is its payload text; an entry lives <see cref="KeepFor"/> from its first use (a longer run parses and reads
/// again), at most <see cref="MaxRuns"/> at a time. Thread safe.
/// </summary>
internal sealed class FirmwareRunCache(TimeProvider time)
{
    /// <summary>How long a run's parsed payload and its reads of axis.com are kept.</summary>
    public static readonly TimeSpan KeepFor = TimeSpan.FromMinutes(10);

    /// <summary>Runs kept at the same time; the oldest goes first.</summary>
    public const int MaxRuns = 16;

    private readonly ConcurrentDictionary<string, FirmwareRun> _runs = new(StringComparer.Ordinal);

    /// <summary>The run of <paramref name="payloadJson"/>; throws <see cref="ArgumentException"/> like <see cref="FirmwarePayload.Parse"/>.</summary>
    public FirmwareRun For(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return new FirmwareRun(FirmwarePayload.Parse(payloadJson), time.GetUtcNow());
        }

        var now = time.GetUtcNow();
        if (_runs.TryGetValue(payloadJson, out var run) && now - run.Created < KeepFor)
        {
            return run;
        }

        var fresh = new FirmwareRun(FirmwarePayload.Parse(payloadJson), now);
        _runs[payloadJson] = fresh;
        Trim(now);
        return fresh;
    }

    /// <summary>Drops runs older than <see cref="KeepFor"/>, then the oldest above <see cref="MaxRuns"/>.</summary>
    private void Trim(DateTimeOffset now)
    {
        foreach (var (key, run) in _runs)
        {
            if (now - run.Created >= KeepFor)
            {
                _runs.TryRemove(new KeyValuePair<string, FirmwareRun>(key, run));
            }
        }

        while (_runs.Count > MaxRuns)
        {
            var oldest = _runs.MinBy(r => r.Value.Created);
            _runs.TryRemove(oldest);
        }
    }

    /// <summary>Runs kept now (tests).</summary>
    internal int Count => _runs.Count;
}

/// <summary>One run: its payload, the index of its choices and the shared reads of axis.com.</summary>
internal sealed class FirmwareRun
{
    private readonly ConcurrentDictionary<(string Source, string Product, string Version), Lazy<Task<OnlineFileInfo?>>> _files = new();

    public FirmwareRun(FirmwarePayload payload, DateTimeOffset created)
    {
        Payload = payload;
        Created = created;
        Online = payload.AllOnline();
        Files = payload.AllFiles();
        OnlineImages = [.. Online.Select(o => OnlineFirmware.ImageOf(o, 0))];
        Index = new FirmwareRunIndex(Online, Files);
    }

    public FirmwarePayload Payload { get; }

    public DateTimeOffset Created { get; }

    public IReadOnlyList<FirmwareOnline> Online { get; }

    public IReadOnlyList<FirmwareImageInfo> OnlineImages { get; }

    public IReadOnlyList<FirmwareFile> Files { get; }

    public FirmwareRunIndex Index { get; }

    /// <summary>
    /// The file of a version on axis.com, read once for the run (single flight: the devices that need it wait for one read
    /// and share its answer, a failure too). The read runs without a task's token (axis.com has 15 s per page), so a
    /// cancelled task never cancels it for the others; each task stops waiting at its own token.
    /// </summary>
    public async Task<OnlineFileInfo?> FindVersionAsync(OnlineFirmwareLookup lookup, FirmwareOnline entry, CancellationToken ct)
    {
        var key = (entry.Source, entry.Product, entry.Version);
        var lazy = _files.GetOrAdd(key, _ => new Lazy<Task<OnlineFileInfo?>>(() => lookup.FindVersionAsync(entry.Source, entry.Product, entry.Version, CancellationToken.None)));
        return await lazy.Value.WaitAsync(ct).ConfigureAwait(false);
    }
}
