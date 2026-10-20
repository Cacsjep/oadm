using System.Net;
using System.Net.Sockets;

using Oadm.Contracts.SiteNode;
using Oadm.Sdk.Testing;
using Oadm.SiteNode.Admin;
using Oadm.SiteNode.Config;
using Oadm.SiteNode.Devices;
using Oadm.SiteNode.Logging;
using Oadm.SiteNode.Networks;
using Oadm.SiteNode.Tests.Support;

namespace Oadm.SiteNode.Tests;

/// <summary><c>Dial</c> on real loopback sockets: the device's own errors apart from the node's, and the limits (with gates, no timing).</summary>
public sealed class DialerTests : IAsyncDisposable
{
    private readonly TempFolder _folder = new();
    private readonly NodeStore _store;
    private readonly FakeDns _dns = new();
    private readonly ConnectionLog _log;

    public DialerTests()
    {
        _store = new NodeStore(_folder.Path);
        _log = new ConnectionLog(Path.Combine(_folder.Path, "logs"), TimeProvider.System);
    }

    public async ValueTask DisposeAsync()
    {
        await _log.DisposeAsync();
        _folder.Dispose();
    }

    [Fact]
    public async Task AnAllowedPortOnAReachedNetworkConnects()
    {
        await using var echo = new EchoServer();
        _store.Update(c => Allow.Connect(c, echo.Port));

        var (result, connection) = await Dialer().OpenAsync(Open("127.0.0.1", echo.Port), "OADM-SRV", CancellationToken.None);

        using (connection)
        {
            Assert.Equal(DialError.None, result.Error);
            Assert.NotNull(connection);
            Assert.Equal(new IPEndPoint(IPAddress.Loopback, echo.Port).ToString(), result.RemoteAddress);
        }

        var line = Assert.Single(_log.Newest(10));
        Assert.Equal(("OADM-SRV", $"127.0.0.1:{echo.Port}", "Short", "Connected"), (line.Server, line.Target, line.Kind, line.Result));
    }

    [Fact]
    public async Task AClosedPortIsTheDevicesRefusal()
    {
        var closed = Ports.FreeTcp();
        _store.Update(c => Allow.Connect(c, closed));

        var (result, connection) = await Dialer().OpenAsync(Open("127.0.0.1", closed), "s", CancellationToken.None);

        Assert.Null(connection);
        Assert.Equal(DialError.DeviceRefused, result.Error);
        Assert.Equal("Refused", _log.Newest(1)[0].Result);
    }

    [Fact]
    public async Task ADeviceThatNeverAnswersTimesOut()
    {
        using var gate = new GateConnector();
        var dialer = Dialer(gate);
        var open = Open("127.0.0.1", 80);
        open.TimeoutMs = 200;

        var (result, connection) = await dialer.OpenAsync(open, "s", CancellationToken.None);

        Assert.Null(connection);
        Assert.Equal(DialError.DeviceTimedOut, result.Error);
        Assert.Equal(0, dialer.ShortConnections);
    }

    [Theory]
    [InlineData(SocketError.HostUnreachable, DialError.DeviceUnreachable)]
    [InlineData(SocketError.NetworkUnreachable, DialError.DeviceUnreachable)]
    [InlineData(SocketError.ConnectionReset, DialError.DeviceReset)]
    [InlineData(SocketError.ConnectionRefused, DialError.DeviceRefused)]
    [InlineData(SocketError.TimedOut, DialError.DeviceTimedOut)]
    public async Task SocketErrorsOfTheDeviceKeepTheirMeaning(SocketError socketError, DialError expected)
    {
        using var gate = new GateConnector { Fail = socketError };
        gate.Release(1);

        var (result, _) = await Dialer(gate).OpenAsync(Open("127.0.0.1", 80), "s", CancellationToken.None);

        Assert.Equal(expected, result.Error);
    }

    [Fact]
    public async Task TheNodesOwnProblemsAreSiteErrors()
    {
        var dialer = Dialer();
        _dns.Names["camera.site"] = [IPAddress.Parse("10.0.0.20")];
        _dns.Names["camera.lan"] = [IPAddress.Parse("127.0.0.9")];

        Assert.Equal(DialError.SitePortNotAllowed, (await dialer.OpenAsync(Open("127.0.0.1", 8443), "s", CancellationToken.None)).Result.Error);
        Assert.Equal(DialError.SiteOutsideNetworks, (await dialer.OpenAsync(Open("10.0.0.20", 80), "s", CancellationToken.None)).Result.Error);
        Assert.Equal(DialError.SiteOutsideNetworks, (await dialer.OpenAsync(Open("camera.site", 80), "s", CancellationToken.None)).Result.Error);
        Assert.Equal(DialError.DeviceHostNotFound, (await dialer.OpenAsync(Open("unknown.lan", 80), "s", CancellationToken.None)).Result.Error);
        var message = (await dialer.OpenAsync(Open("127.0.0.1", 8443), "s", CancellationToken.None)).Result.Message;
        Assert.Equal(AdminTexts.PortNotAllowed(PortProtocol.Tcp, 8443), message);
        Assert.Contains(_log.Newest(10), e => e.Result == "Port not allowed");
        Assert.Contains(_log.Newest(10), e => e.Result == "Outside the networks");
    }

    [Fact]
    public async Task ANameResolvesOnTheNodeAndTheReachedAddressIsUsed()
    {
        await using var echo = new EchoServer();
        _store.Update(c => Allow.Connect(c, echo.Port));
        _dns.Names["camera.lan"] = [IPAddress.Parse("10.0.0.20"), IPAddress.Loopback];

        var (result, connection) = await Dialer().OpenAsync(Open("camera.lan", echo.Port), "s", CancellationToken.None);

        using (connection)
            Assert.Equal(new IPEndPoint(IPAddress.Loopback, echo.Port).ToString(), result.RemoteAddress);
    }

    [Fact]
    public async Task ShortDialsAreCappedWhileConnectingAndPermanentOnesApart()
    {
        await using var echo = new EchoServer();
        using var gate = new GateConnector { Target = new IPEndPoint(IPAddress.Loopback, echo.Port) };
        var dialer = Dialer(gate, new DialLimits(ShortOpen: 10, ShortConnecting: 3, PermanentConnecting: 2));

        var shortOnes = Enumerable.Range(0, 3).Select(_ => dialer.OpenAsync(Open("127.0.0.1", 80), "s", CancellationToken.None)).ToList();
        var permanent = Enumerable.Range(0, 2).Select(_ => dialer.OpenAsync(Open("127.0.0.1", 80, DialKind.Permanent), "s", CancellationToken.None)).ToList();
        await Eventually.TrueAsync(() => gate.Waiting == 5, "five connects wait at the gate");

        Assert.Equal(DialError.SiteBusy, (await dialer.OpenAsync(Open("127.0.0.1", 80), "s", CancellationToken.None)).Result.Error);
        Assert.Equal(DialError.SiteBusy, (await dialer.OpenAsync(Open("127.0.0.1", 80, DialKind.Permanent), "s", CancellationToken.None)).Result.Error);
        Assert.Equal(3, dialer.ShortConnections);

        gate.Release(5);
        var connections = (await Task.WhenAll(shortOnes.Concat(permanent))).Select(r => r.Connection!).ToList();
        Assert.Equal(3, dialer.ShortConnections);
        Assert.Equal(2, dialer.PermanentConnections);

        // Permanent connections have no cap of their own once connected.
        var more = dialer.OpenAsync(Open("127.0.0.1", 80, DialKind.Permanent), "s", CancellationToken.None);
        gate.Release(1);
        connections.Add((await more).Connection!);
        Assert.Equal(3, dialer.PermanentConnections);

        foreach (var connection in connections)
            connection.Dispose();
        Assert.Equal(0, dialer.ShortConnections);
        Assert.Equal(0, dialer.PermanentConnections);
    }

    [Fact]
    public async Task OpenShortConnectionsAreCappedUntilOneCloses()
    {
        await using var echo = new EchoServer();
        _store.Update(c => Allow.Connect(c, echo.Port));
        var dialer = Dialer(limits: new DialLimits(ShortOpen: 2, ShortConnecting: 2, PermanentConnecting: 2));
        var first = (await dialer.OpenAsync(Open("127.0.0.1", echo.Port), "s", CancellationToken.None)).Connection!;
        using var second = (await dialer.OpenAsync(Open("127.0.0.1", echo.Port), "s", CancellationToken.None)).Connection!;

        Assert.Equal(DialError.SiteBusy, (await dialer.OpenAsync(Open("127.0.0.1", echo.Port), "s", CancellationToken.None)).Result.Error);
        first.Dispose();
        var (result, third) = await dialer.OpenAsync(Open("127.0.0.1", echo.Port), "s", CancellationToken.None);
        using (third)
            Assert.Equal(DialError.None, result.Error);
    }

    [Fact]
    public async Task ADialWhoseAnswerCannotBeWrittenClosesTheSocketAndFreesItsPlace()
    {
        await using var echo = new EchoServer();
        _store.Update(c => Allow.Connect(c, echo.Port));
        using var dialer = Dialer(limits: new DialLimits(ShortOpen: 1, ShortConnecting: 1, PermanentConnecting: 1));

        for (var i = 0; i < 3; i++)
        {
            await Assert.ThrowsAsync<IOException>(() =>
                DialPipe.RunAsync(dialer, Open("127.0.0.1", echo.Port), "s", new NoRequests(), new BrokenWriter(), CancellationToken.None));
            Assert.Equal(0, dialer.ShortConnections);
        }

        var (result, connection) = await dialer.OpenAsync(Open("127.0.0.1", echo.Port), "s", CancellationToken.None);
        using (connection)
            Assert.Equal(DialError.None, result.Error);
    }

    [Fact]
    public async Task ProbesWaitForAFreePlaceAndRangeScansAreCapped()
    {
        using var gate = new GateConnector { Fail = SocketError.ConnectionRefused };
        using var dialer = Dialer(gate, new DialLimits(Probes: 2, Scans: 1));
        var target = new IPEndPoint(IPAddress.Loopback, 80);

        var probes = Enumerable.Range(0, 3).Select(_ => dialer.ProbeAsync(target, TimeSpan.FromSeconds(30), CancellationToken.None)).ToList();
        await Eventually.TrueAsync(() => gate.Waiting == 2, "two probes connect");
        await Eventually.StaysTrueAsync(() => gate.Waiting == 2, "the third waits for a place", TimeSpan.FromMilliseconds(100));
        gate.Release(3);
        Assert.All(await Task.WhenAll(probes), r => Assert.Equal(ProbeResult.Refused, r));

        using (var scan = dialer.TryStartScan())
        {
            Assert.NotNull(scan);
            Assert.Null(dialer.TryStartScan());
        }

        using var again = dialer.TryStartScan();
        Assert.NotNull(again);
    }

    private sealed class NoRequests : Grpc.Core.IAsyncStreamReader<DialMessage>
    {
        public DialMessage Current => throw new InvalidOperationException();

        public Task<bool> MoveNext(CancellationToken cancellationToken) => Task.FromResult(false);
    }

    private sealed class BrokenWriter : Grpc.Core.IAsyncStreamWriter<DialMessage>
    {
        public Grpc.Core.WriteOptions? WriteOptions { get; set; }

        public Task WriteAsync(DialMessage message) => throw new IOException("The server went away.");
    }

    private Dialer Dialer(IDeviceConnector? connector = null, DialLimits? limits = null) =>
        new(new PortPolicy(_store), new NetworkScope(_store, FakeCards.LoopbackSite(), allowLoopback: true), connector ?? SystemDeviceConnector.Instance, _dns, _log, TimeProvider.System, limits);

    private static DialOpen Open(string host, int port, DialKind kind = DialKind.Short) => new() { Host = host, Port = port, Kind = kind };
}
