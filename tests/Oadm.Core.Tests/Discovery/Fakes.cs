using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

using Oadm.Core.Discovery;
using Oadm.Core.Discovery.Mdns;

namespace Oadm.Core.Tests.Discovery;

/// <summary>Probe that answers from a table of address -> result, optionally with a delay.</summary>
internal sealed class FakeProbe(IReadOnlyDictionary<string, DeviceProbeResult> devices, TimeSpan delay = default) : IDeviceProbe
{
    private int _current;

    public ConcurrentBag<string> Probed { get; } = [];

    public int MaxConcurrency { get; private set; }

    public static DeviceProbeResult Device(string address, string serial, string? model = "P3265-V", DiscoveredDeviceStatus status = DiscoveredDeviceStatus.CredentialsRequired)
        => new(IPAddress.Parse(address), serial, "https", model, null, "12.11.77", status, false);

    public async Task<DeviceProbeResult?> ProbeAsync(IPAddress address, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var now = Interlocked.Increment(ref _current);
        lock (Probed)
        {
            MaxConcurrency = Math.Max(MaxConcurrency, now);
        }

        try
        {
            Probed.Add(address.ToString());
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
            }
            else
            {
                await Task.Yield();
            }

            return devices.TryGetValue(address.ToString(), out var r) ? r : null;
        }
        finally
        {
            Interlocked.Decrement(ref _current);
        }
    }
}

/// <summary>mDNS browser fed by the test through <see cref="Announce"/>.</summary>
internal sealed class FakeMdnsBrowser : IMdnsBrowser
{
    private readonly Channel<MdnsServiceInstance> _instances = Channel.CreateUnbounded<MdnsServiceInstance>();

    public void Announce(string address, string? serial, string host = "axis-host")
        => _instances.Writer.TryWrite(new MdnsServiceInstance(
            $"AXIS Cam - {serial}._axis-video._tcp.local",
            host,
            80,
            [IPAddress.Parse(address)],
            new Dictionary<string, string>(),
            serial,
            null));

    public async IAsyncEnumerable<MdnsServiceInstance> BrowseAsync(MdnsBrowseOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var i in _instances.Reader.ReadAllAsync(cancellationToken))
        {
            yield return i;
        }
    }
}
