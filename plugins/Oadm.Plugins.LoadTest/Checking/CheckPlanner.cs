using System.Collections.Concurrent;
using System.Globalization;

using Oadm.Sdk.Devices;

namespace Oadm.Plugins.LoadTest.Checking;

/// <summary>One camera stream a check measures: the device, its source and the batch it runs in.</summary>
internal sealed record PlannedStream(IDeviceInfo Device, StreamProbeSource Source, string Codec, int Batch, int IndexInBatch, double ExpectedMbps)
{
    /// <summary>"&lt;device id&gt;/&lt;camera&gt;".</summary>
    public string StreamId { get; } = Device.Id.ToString("D") + "/" + Source.Camera.ToString(CultureInfo.InvariantCulture);
}

/// <summary>The plan of a check: what the page shows and what the runner runs.</summary>
internal sealed record PlannedCheck(CheckPlan Plan, IReadOnlyList<PlannedStream> Streams, int BatchSize, int PhaseMinutes)
{
    /// <summary>The streams of each batch, in order.</summary>
    public IReadOnlyList<IReadOnlyList<PlannedStream>> BatchList { get; } =
        [.. Streams.GroupBy(s => s.Batch).OrderBy(g => g.Key).Select(g => (IReadOnlyList<PlannedStream>)[.. g])];
}

/// <summary>
/// Plans a check (read only): the video sources of the chosen devices through <see cref="IDeviceStreamProbes.ListSourcesAsync"/>
/// (<see cref="LoadTestOptions.ListParallelism"/> at a time, kept for <see cref="LoadTestOptions.SourceCacheTime"/>
/// so Start does not ask again), the left-out devices with the reason, the batches, the time and the expected rate.
/// </summary>
internal sealed class CheckPlanner(IDeviceStreamProbes probes, LoadTestOptions options)
{
    /// <summary>A device without video, or without a source the check can stream.</summary>
    public const string NoVideo = "No video";

    /// <summary>A source list without H.264 or H.265.</summary>
    public const string NoCodec = "No H.264 or H.265 stream";

    private readonly ConcurrentDictionary<Guid, CachedSources> _cache = new();

    /// <summary>The device's source list (or the reason it is left out), possibly from the cache.</summary>
    private sealed record CachedSources(DateTimeOffset ReadUtc, IReadOnlyList<StreamProbeSource>? Sources, string? Reason);

    /// <summary>Validates the request's numbers (before any device is read).</summary>
    public static void Validate(int deviceCount, int batchSize, int phaseMinutes)
    {
        if (deviceCount is 0 or > DeviceLimits.MaxDevices)
        {
            throw new ArgumentException(DeviceLimits.ChooseRangeText("cameras"));
        }

        if (batchSize is < 1 or > LoadTestInfo.MaxBatchSize)
        {
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"Choose 1 to {LoadTestInfo.MaxBatchSize} cameras at once."));
        }

        if (!LoadTestInfo.PhaseMinutes.Contains(phaseMinutes))
        {
            throw new ArgumentException("Choose 1, 2 or 5 minutes per step.");
        }
    }

    /// <summary>Plans a check of <paramref name="devices"/> (already checked against the caller's devices), in their order.</summary>
    public async Task<PlannedCheck> PlanAsync(IReadOnlyList<IDeviceInfo> devices, int batchSize, int phaseMinutes, CancellationToken ct)
    {
        Validate(devices.Count, batchSize, phaseMinutes);

        // Each video device gives at least one stream: over the limit without reading a single device.
        var video = devices.Count(d => d.HasVideo);
        if (video > LoadTestInfo.MaxStreams)
        {
            return new PlannedCheck(new CheckPlan(video, 0, TimeSpan.Zero, 0, 0, [], LoadTestInfo.TooManyStreams), [], batchSize, phaseMinutes);
        }

        var results = new CachedSources?[devices.Count];
        var now = options.Time.GetUtcNow();
        var toRead = new List<int>();
        for (var i = 0; i < devices.Count; i++)
        {
            var device = devices[i];
            if (!device.HasVideo)
            {
                results[i] = new CachedSources(now, null, NoVideo);
            }
            else if (DeviceMessages.Refusal(device, refuseUnreachable: true) is { } refusal)
            {
                results[i] = new CachedSources(now, null, refusal);
            }
            else if (_cache.TryGetValue(device.Id, out var cached) && now - cached.ReadUtc < options.SourceCacheTime)
            {
                results[i] = cached;
            }
            else
            {
                toRead.Add(i);
            }
        }

        await Parallel.ForEachAsync(
            toRead,
            new ParallelOptions { MaxDegreeOfParallelism = options.ListParallelism, CancellationToken = ct },
            async (i, token) =>
            {
                var read = await ReadAsync(devices[i].Id, token).ConfigureAwait(false);
                _cache[devices[i].Id] = read;
                results[i] = read;
            }).ConfigureAwait(false);

        var leftOut = new List<LeftOutDevice>();
        var streams = new List<PlannedStream>();
        for (var i = 0; i < devices.Count; i++)
        {
            var device = devices[i];
            var result = results[i]!;
            if (result.Reason is { } reason)
            {
                leftOut.Add(new LeftOutDevice(device.Id, NameOf(device), reason));
                continue;
            }

            var added = 0;
            foreach (var source in result.Sources!)
            {
                if (CodecOf(source) is not { } codec)
                {
                    continue;
                }

                var index = streams.Count;
                streams.Add(new PlannedStream(device, source, codec, (index / batchSize) + 1, index % batchSize, BitrateEstimates.Mbps(source.Width, source.Height, source.MaxFps, codec)));
                added++;
            }

            if (added == 0)
            {
                leftOut.Add(new LeftOutDevice(device.Id, NameOf(device), result.Sources!.Count == 0 ? NoVideo : NoCodec));
            }
        }

        var batches = streams.Count == 0 ? 0 : streams[^1].Batch;
        var peak = 0.0;
        foreach (var batch in streams.GroupBy(s => s.Batch))
        {
            peak = Math.Max(peak, batch.Sum(s => s.ExpectedMbps));
        }

        var duration = TimeSpan.FromMinutes((double)batches * Enum.GetValues<CheckPhase>().Length * phaseMinutes);
        var tooMany = streams.Count > LoadTestInfo.MaxStreams ? LoadTestInfo.TooManyStreams : null;
        var plan = new CheckPlan(streams.Count, batches, duration, Math.Round(peak, 1), Math.Round(peak * BitrateEstimates.LowLightFactor, 1), leftOut, tooMany);
        return new PlannedCheck(plan, streams, batchSize, phaseMinutes);
    }

    /// <summary>The device's name in the check: its host name, else its address.</summary>
    public static string NameOf(IDeviceInfo device) => string.IsNullOrWhiteSpace(device.HostName) ? device.Address : device.HostName;

    /// <summary>H.265 when the source lists it, else H.264, else none (no MJPEG).</summary>
    private static string? CodecOf(StreamProbeSource source) =>
        source.Codecs.Contains("h265", StringComparer.OrdinalIgnoreCase) ? "h265"
        : source.Codecs.Contains("h264", StringComparer.OrdinalIgnoreCase) ? "h264"
        : null;

    private async Task<CachedSources> ReadAsync(Guid deviceId, CancellationToken ct)
    {
        var now = options.Time.GetUtcNow();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(options.ListTimeout);
            var sources = await probes.ListSourcesAsync(deviceId, timeout.Token).ConfigureAwait(false);
            return new CachedSources(now, sources, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new CachedSources(now, null, DeviceMessages.Timeout(options.ListTimeout));
        }
        catch (DeviceStreamException ex)
        {
            return new CachedSources(now, null, ex.Error == DeviceStreamError.NotSupported ? NoVideo : ex.Message);
        }
        catch (KeyNotFoundException)
        {
            return new CachedSources(now, null, DeviceMessages.Removed);
        }
        catch (HttpRequestException ex)
        {
            return new CachedSources(now, null, DeviceMessages.Unreachable(ex.Message));
        }
    }
}
