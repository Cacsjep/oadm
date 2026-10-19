using System.Net;
using System.Net.Sockets;

using Oadm.SiteNode.Devices;
using Oadm.SiteNode.Networks;

namespace Oadm.SiteNode.Tests.Support;

/// <summary>A unique data folder per test, removed at the end.</summary>
internal sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "oadm-site-node-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>A fixed list of network cards.</summary>
internal sealed class FakeCards(params NetworkCard[] cards) : INetworkCards
{
    public List<NetworkCard> Cards { get; } = [.. cards];

    public IReadOnlyList<NetworkCard> List() => Cards;

    /// <summary>Loopback as a camera network (reached by default), and an office card with the default router.</summary>
    public static FakeCards LoopbackSite() => new(
        new NetworkCard("cam", "Cameras", [new CardAddress(IPAddress.Loopback, 8)], HasDefaultRouter: false),
        new NetworkCard("office", "Office", [new CardAddress(IPAddress.Parse("10.0.0.7"), 24)], HasDefaultRouter: true));
}

/// <summary>DNS from a table.</summary>
internal sealed class FakeDns : INodeDns
{
    public Dictionary<string, IPAddress[]> Names { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<IPAddress, string> Reverse { get; } = [];

    public Task<IReadOnlyList<IPAddress>> ResolveAsync(string name, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<IPAddress>>(Names.TryGetValue(name, out var found) ? found : []);

    public Task<string?> ReverseAsync(IPAddress address, CancellationToken ct) =>
        Task.FromResult(Reverse.TryGetValue(address, out var name) ? name : null);
}

/// <summary>
/// Connects only when the test lets it: every connect waits at the gate (counted in <see cref="Waiting"/>), so limits are
/// tested without timing. Released connects go to <see cref="Target"/> (a real loopback listener) or fail as told.
/// </summary>
internal sealed class GateConnector : IDeviceConnector, IDisposable
{
    public void Dispose() => _gate.Dispose();

    private readonly SemaphoreSlim _gate = new(0);
    private int _waiting;

    public IPEndPoint? Target { get; set; }

    public SocketError? Fail { get; set; }

    public int Waiting => Volatile.Read(ref _waiting);

    public void Release(int count) => _gate.Release(count);

    public async Task<Socket> ConnectAsync(IPEndPoint target, CancellationToken ct)
    {
        Interlocked.Increment(ref _waiting);
        try
        {
            await _gate.WaitAsync(ct);
        }
        finally
        {
            Interlocked.Decrement(ref _waiting);
        }

        if (Fail is { } error)
            throw new SocketException((int)error);
        return await SystemDeviceConnector.Instance.ConnectAsync(Target ?? target, ct);
    }
}

/// <summary>Maps device addresses to loopback end points, like the in-process tests of the server (two sites may both have 192.168.0.20).</summary>
internal sealed class MapConnector : IDeviceConnector
{
    public Dictionary<IPEndPoint, IPEndPoint> Map { get; } = [];

    /// <summary>Addresses where nothing is: a connect fails like on a network without that host (unreachable, no reset).</summary>
    public HashSet<IPAddress> Dead { get; } = [];

    public Task<Socket> ConnectAsync(IPEndPoint target, CancellationToken ct) =>
        Dead.Contains(target.Address)
            ? Task.FromException<Socket>(new SocketException((int)SocketError.HostUnreachable))
            : SystemDeviceConnector.Instance.ConnectAsync(Map.TryGetValue(target, out var mapped) ? mapped : target, ct);
}

/// <summary>A pinger that never has the OS ping (the node falls back to TCP 80 / 443).</summary>
internal sealed class NoPinger : IPinger
{
    public Task<TimeSpan?> PingAsync(IPAddress address, TimeSpan timeout, CancellationToken ct) => throw new NotSupportedException();
}

/// <summary>A loopback TCP server that echoes until the client half-closes, then half-closes too.</summary>
internal sealed class EchoServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private int _accepted;

    public EchoServer()
    {
        _listener.Start();
        _loop = Task.Run(AcceptAsync);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public int Accepted => Volatile.Read(ref _accepted);

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        try
        {
            await _loop;
        }
        catch (OperationCanceledException)
        {
        }

        _stop.Dispose();
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            Socket socket;
            try
            {
                socket = await _listener.AcceptSocketAsync(_stop.Token);
            }
            catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }

            Interlocked.Increment(ref _accepted);
            _ = Task.Run(async () =>
            {
                using (socket)
                {
                    var buffer = new byte[16 * 1024];
                    try
                    {
                        while (true)
                        {
                            var read = await socket.ReceiveAsync(buffer, SocketFlags.None, _stop.Token);
                            if (read == 0)
                            {
                                socket.Shutdown(SocketShutdown.Send);
                                return;
                            }

                            await socket.SendAsync(buffer.AsMemory(0, read), SocketFlags.None, _stop.Token);
                        }
                    }
                    catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
                    {
                    }
                }
            });
        }
    }
}

internal static class Ports
{
    /// <summary>A loopback TCP port nothing listens on (taken and given back at once).</summary>
    public static int FreeTcp()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public static int FreeUdp()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }
}
