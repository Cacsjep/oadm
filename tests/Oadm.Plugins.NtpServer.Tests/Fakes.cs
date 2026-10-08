using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;

using Oadm.Core.Plugins;
using Oadm.Plugins.NtpServer.Protocol;
using Oadm.Plugins.NtpServer.Upstream;
using Oadm.Sdk.Client;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Network;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Tasks;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.NtpServer.Tests;

/// <summary>How <see cref="FakeUpstream"/> answers.</summary>
public enum UpstreamBehavior
{
    Answer,
    NoAnswer,
    WrongOrigin,
    WrongOriginThenAnswer,
    FromOtherPort,
    KissRate,
    KissDeny,
    Unsynchronized,
}

/// <summary>A fake upstream NTP server on 127.0.0.1 and a random port (never port 123).</summary>
internal sealed class FakeUpstream : IAsyncDisposable
{
    private readonly Socket _socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    private readonly Socket _other = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private int _requests;

    public FakeUpstream(UpstreamBehavior behavior = UpstreamBehavior.Answer)
    {
        Behavior = behavior;
        _socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        _other.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        if (OperatingSystem.IsWindows())
        {
            _socket.IOControl(unchecked((int)0x9800000C), [0, 0, 0, 0], null);
        }

        _loop = Task.Run(LoopAsync);
    }

    public UpstreamBehavior Behavior { get; set; }

    /// <summary>Answer after this delay.</summary>
    public TimeSpan Delay { get; set; }

    /// <summary>Upstream clock minus real clock.</summary>
    public TimeSpan ClockOffset { get; set; }

    public byte Stratum { get; set; } = 2;

    public IPEndPoint EndPoint => (IPEndPoint)_socket.LocalEndPoint!;

    public string HostText => $"127.0.0.1:{EndPoint.Port}";

    public int Requests => Volatile.Read(ref _requests);

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _socket.Dispose();
        _other.Dispose();
        try
        {
            await _loop;
        }
        catch (OperationCanceledException)
        {
        }

        _cts.Dispose();
    }

    private async Task LoopAsync()
    {
        var buffer = new byte[1024];
        while (!_cts.IsCancellationRequested)
        {
            SocketReceiveFromResult received;
            try
            {
                received = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), _cts.Token);
            }
            catch (Exception) when (_cts.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }

            Interlocked.Increment(ref _requests);
            var t2 = DateTime.UtcNow + ClockOffset;
            if (!NtpPacket.TryRead(buffer.AsSpan(0, received.ReceivedBytes), out var request))
            {
                continue;
            }

            var behavior = Behavior;
            var delay = Delay;
            var remote = received.RemoteEndPoint;
            _ = Task.Run(async () =>
            {
                try
                {
                    if (delay > TimeSpan.Zero)
                    {
                        await Task.Delay(delay, _cts.Token);
                    }

                    switch (behavior)
                    {
                        case UpstreamBehavior.NoAnswer:
                            return;
                        case UpstreamBehavior.WrongOrigin:
                            await SendAsync(_socket, remote, Answer(request, t2, origin: request.TransmitTimestamp + 1));
                            return;
                        case UpstreamBehavior.WrongOriginThenAnswer:
                            await SendAsync(_socket, remote, Answer(request, t2, origin: request.TransmitTimestamp ^ 0xFFFF));
                            await SendAsync(_socket, remote, Answer(request, t2));
                            return;
                        case UpstreamBehavior.FromOtherPort:
                            await SendAsync(_other, remote, Answer(request, t2));
                            return;
                        case UpstreamBehavior.KissRate:
                            await SendAsync(_socket, remote, Kiss(request, "RATE"));
                            return;
                        case UpstreamBehavior.KissDeny:
                            await SendAsync(_socket, remote, Kiss(request, "DENY"));
                            return;
                        case UpstreamBehavior.Unsynchronized:
                            await SendAsync(_socket, remote, Answer(request, t2) with { Leap = NtpLeap.Unsynchronized, Stratum = 16 });
                            return;
                        default:
                            await SendAsync(_socket, remote, Answer(request, t2));
                            return;
                    }
                }
                catch (Exception)
                {
                    // test teardown
                }
            });
        }
    }

    private NtpPacket Answer(NtpPacket request, DateTime t2, ulong? origin = null) => new()
    {
        Version = request.Version,
        Mode = NtpMode.Server,
        Stratum = Stratum,
        Poll = request.Poll,
        Precision = -20,
        RootDelay = NtpTimestamp.ToShort(0.002),
        RootDispersion = NtpTimestamp.ToShort(0.003),
        ReferenceId = NtpPacket.AsciiId("GPS"),
        ReferenceTimestamp = NtpTimestamp.FromDateTime(t2.AddSeconds(-10)),
        OriginateTimestamp = origin ?? request.TransmitTimestamp,
        ReceiveTimestamp = NtpTimestamp.FromDateTime(t2),
        TransmitTimestamp = NtpTimestamp.FromDateTime(DateTime.UtcNow + ClockOffset),
    };

    private static NtpPacket Kiss(NtpPacket request, string code) => new()
    {
        Leap = NtpLeap.Unsynchronized,
        Version = request.Version,
        Mode = NtpMode.Server,
        Stratum = 0,
        ReferenceId = NtpPacket.AsciiId(code),
        OriginateTimestamp = request.TransmitTimestamp,
        ReceiveTimestamp = NtpTimestamp.FromDateTime(DateTime.UtcNow),
        TransmitTimestamp = NtpTimestamp.FromDateTime(DateTime.UtcNow),
    };

    private static async Task SendAsync(Socket socket, EndPoint remote, NtpPacket packet)
    {
        var bytes = new byte[NtpPacket.Length];
        packet.Write(bytes);
        await socket.SendToAsync(bytes, SocketFlags.None, remote);
    }
}

/// <summary>Fixed interfaces: loopback (127.0.0.1), an Ethernet adapter that is down and one that is up but unusable here.</summary>
internal sealed class FakeInterfaces : IServerNetworkInterfaces
{
    public static readonly ServerNetworkInterface Loopback = new("lo-id", "Loopback", "Software Loopback Interface", [IPAddress.Loopback], IsUp: true, IsLoopback: true);

    public List<ServerNetworkInterface> Items { get; } =
    [
        Loopback,
        new("eth-id", "Ethernet", "Intel(R) Ethernet Connection I219-LM", [IPAddress.Parse("192.0.2.17"), IPAddress.Parse("fe80::1")], IsUp: true, IsLoopback: false),
        new("wifi-id", "Wi-Fi", "Wi-Fi", [IPAddress.Parse("192.0.2.99")], IsUp: false, IsLoopback: false),
    ];

    public IReadOnlyList<ServerNetworkInterface> List() => Items;
}

/// <summary>A resolver that fails, hangs (ignoring its token) or answers, and counts calls.</summary>
internal sealed class FakeResolver : IUpstreamResolver
{
    public Func<string, IPAddress[]?> Answer { get; set; } = _ => [IPAddress.Loopback];

    public bool Hang { get; set; }

    public int Calls { get; private set; }

    public async Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
    {
        Calls++;
        if (Hang)
        {
            await Task.Delay(Timeout.Infinite, CancellationToken.None);
        }

        return Answer(host) ?? throw new SocketException((int)SocketError.HostNotFound);
    }
}

internal static class Options
{
    /// <summary>Loopback, random port, fast timings: never port 123, never a real interface.</summary>
    public static NtpServerOptions Test(IUpstreamResolver? resolver = null) => new()
    {
        Port = 0,
        Interfaces = new FakeInterfaces(),
        IncludeLoopback = true,
        Resolver = resolver ?? DnsUpstreamResolver.Instance,
        Upstream = FastUpstream,
        PublishInterval = TimeSpan.FromMilliseconds(50),
        RetryBindInterval = TimeSpan.FromMilliseconds(200),
        WindowsTime = new NoWindowsTime(),
    };

    public static UpstreamOptions FastUpstream { get; } = new()
    {
        QueryTimeout = TimeSpan.FromMilliseconds(300),
        DnsTimeout = TimeSpan.FromMilliseconds(300),
        FirstRetry = TimeSpan.FromMilliseconds(20),
        MinPoll = TimeSpan.FromMilliseconds(200),
        MaxPoll = TimeSpan.FromSeconds(2),
    };

    private sealed class NoWindowsTime : Status.IWindowsTimeProbe
    {
        public Task<bool> IsRunningAsync(CancellationToken ct) => Task.FromResult(false);
    }
}

/// <summary>Sends one SNTP request to the server under test and returns the parsed answer (or null on timeout).</summary>
internal static class NtpProbe
{
    public static async Task<(NtpPacket? Answer, ulong Origin, DateTime T1, DateTime T4)> QueryAsync(IPEndPoint server, TimeSpan timeout, byte version = 4)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var t1 = DateTime.UtcNow;
        var origin = NtpTimestamp.FromDateTime(t1);
        var request = new byte[NtpPacket.Length];
        new NtpPacket { Version = version, Mode = NtpMode.Client, Poll = 6, TransmitTimestamp = origin }.Write(request);
        await socket.SendToAsync(request, SocketFlags.None, server);
        using var cts = new CancellationTokenSource(timeout);
        var buffer = new byte[1024];
        try
        {
            var received = await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), cts.Token);
            var t4 = DateTime.UtcNow;
            return NtpPacket.TryRead(buffer.AsSpan(0, received.ReceivedBytes), out var answer) ? (answer, origin, t1, t4) : (null, origin, t1, t4);
        }
        catch (OperationCanceledException)
        {
            return (null, origin, t1, DateTime.UtcNow);
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
        {
            // Windows reports the ICMP "port unreachable" of a closed port on the next receive: no server answered.
            return (null, origin, t1, DateTime.UtcNow);
        }
    }
}

/// <summary>Minimal core plugin context: in-memory settings, the real event hub.</summary>
internal sealed class TestCoreContext(IPluginSettings settings, IPluginEvents? events, IFirewallRules? firewall = null) : ICorePluginContext
{
    public IFirewallRules? Firewall { get; } = firewall;

    public IDeviceRepository Devices => throw new NotSupportedException();

    public IVapixClientFactory Vapix => throw new NotSupportedException();

    public ITaskRunner Tasks => throw new NotSupportedException();

    public IPluginSettings Settings { get; } = settings;

    public Microsoft.Extensions.Logging.ILogger Logger { get; } = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    public IPluginEvents? Events { get; } = events;
}

/// <summary>The page context against the in-process plugin (InvokeAsync) and the real event hub (WatchEventsAsync).</summary>
internal sealed class PluginPageContext(NtpServerPlugin plugin, PluginEventHub hub) : ICorePluginClientContext
{
    public List<IDeviceInfo> DeviceList { get; } = [];

    public IReadOnlyList<IDeviceInfo> Devices => DeviceList;

    public event EventHandler? DevicesChanged;

    public Func<string, string?, Task<string?>>? Intercept { get; set; }

    public async Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct)
    {
        if (Intercept is { } intercept)
        {
            return await intercept(method, payloadJson);
        }

        return await plugin.InvokeAsync(method, payloadJson, ct);
    }

    public async IAsyncEnumerable<PluginEvent> WatchEventsAsync([EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var item in hub.WatchAsync(NtpServerPluginInfo.PluginId, ct))
        {
            yield return item;
        }
    }

    public void RaiseDevicesChanged() => DevicesChanged?.Invoke(this, EventArgs.Empty);
}

internal sealed record TestDevice(string Address, string? Model) : IDeviceInfo
{
    public Guid Id { get; } = Guid.NewGuid();

    public string Serial => "ACCC8E" + Address.GetHashCode(StringComparison.Ordinal).ToString("X6", System.Globalization.CultureInfo.InvariantCulture)[..6];

    public string? HostName => null;

    public string? FirmwareVersion => "12.11.77";

    public DeviceStatus Status => DeviceStatus.Ok;

    public DeviceCategory Category => DeviceCategory.Camera;

    public bool HasVideo => true;

    public IReadOnlyList<DeviceApi> Apis { get; init; } = [];
}

internal static class Wait
{
    public static async Task UntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var end = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (!condition())
        {
            if (DateTime.UtcNow > end)
            {
                throw new TimeoutException("Condition not met in time.");
            }

            await Task.Delay(10);
        }
    }
}
