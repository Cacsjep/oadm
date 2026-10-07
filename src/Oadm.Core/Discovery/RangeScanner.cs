using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Oadm.Core.Discovery;

/// <summary>Options for <see cref="RangeScanner.ScanAsync"/>.</summary>
public sealed class RangeScanOptions
{
    /// <summary>Addresses probed at the same time (setting Scan.Parallelism, default 32).</summary>
    public int Parallelism { get; init; } = 32;

    /// <summary>Per-request timeout (setting Scan.TimeoutMs, default 1500 ms).</summary>
    public TimeSpan Timeout { get; init; } = VapixDeviceProbe.DefaultTimeout;
}

/// <summary>
/// Scans an IPv4 range with an <see cref="IDeviceProbe"/>. No ICMP, no ARP: every address gets
/// the HTTP(S) probe directly.
/// </summary>
public sealed class RangeScanner
{
    private readonly IDeviceProbe _probe;

    public RangeScanner(IDeviceProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        _probe = probe;
    }

    /// <summary>
    /// Probes every address of <paramref name="range"/> and yields each Axis device as soon as it
    /// is identified (order not guaranteed). <paramref name="progress"/> receives the completed
    /// percentage (0-100) whenever it changes, and always 100 at the end of a complete scan.
    /// </summary>
    public async IAsyncEnumerable<DeviceProbeResult> ScanAsync(
        Ipv4Range range,
        RangeScanOptions? options = null,
        IProgress<int>? progress = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(range);
        options ??= new RangeScanOptions();
        var parallelism = Math.Clamp(options.Parallelism, 1, 1024);
        var total = range.Count;
        var completed = 0;
        var lastPercent = 0;
        var progressLock = new Lock();
        progress?.Report(0);

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var results = Channel.CreateUnbounded<DeviceProbeResult>(new UnboundedChannelOptions { SingleReader = true });
        var producer = Task.Run(
            async () =>
            {
                try
                {
                    await Parallel.ForEachAsync(
                        range,
                        new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = stop.Token },
                        async (address, ct) =>
                        {
                            var result = await _probe.ProbeAsync(address, options.Timeout, ct).ConfigureAwait(false);
                            if (result is not null)
                            {
                                await results.Writer.WriteAsync(result, ct).ConfigureAwait(false);
                            }

                            var done = Interlocked.Increment(ref completed);
                            var percent = (int)((long)done * 100 / total);
                            lock (progressLock)
                            {
                                if (percent > lastPercent)
                                {
                                    lastPercent = percent;
                                    progress?.Report(percent);
                                }
                            }
                        }).ConfigureAwait(false);
                    results.Writer.TryComplete();
                }
                catch (Exception ex)
                {
                    results.Writer.TryComplete(ex);
                }
            },
            CancellationToken.None);

        var finished = false;
        try
        {
            await foreach (var result in results.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
            {
                yield return result;
            }

            finished = true;
        }
        finally
        {
            if (!finished)
            {
                // Consumer stopped early: stop probing.
                await stop.CancelAsync().ConfigureAwait(false);
            }

            await producer.ConfigureAwait(false);
        }
    }
}
