using System.Net;

using Oadm.Contracts.SiteNode;
using Oadm.SiteNode.Admin;
using Oadm.SiteNode.Config;
using Oadm.SiteNode.Devices;
using Oadm.SiteNode.Discovery;
using Oadm.SiteNode.Links;
using Oadm.SiteNode.Logging;
using Oadm.SiteNode.Networks;
using Oadm.SiteNode.Tests.Support;

namespace Oadm.SiteNode.Tests;

/// <summary>mDNS browse and announce, the range scan, DNS names and server addresses.</summary>
public sealed class DiscoveryTests
{
    private static readonly IPAddress Camera = IPAddress.Parse("192.168.0.20");

    [Fact]
    public void AnAxisAnswerBecomesADeviceOnAReachedNetwork()
    {
        var aggregator = new AxisAggregator(a => a.ToString().StartsWith("192.168.0.", StringComparison.Ordinal));
        var instance = "AXIS P3265-V - B8A44F631339._axis-video._tcp.local";
        var answer = DnsWire.Parse(DnsWire.Response(0, [],
        [
            new DnsRecord(AxisAggregator.Service, DnsType.DomainPointer, 120) { Target = instance },
            new DnsRecord(instance, DnsType.Srv, 120) { Target = "axis-b8a44f631339.local", Port = 80 },
            new DnsRecord(instance, DnsType.Txt, 120) { Texts = ["macaddress=B8A44F631339"] },
            new DnsRecord("axis-b8a44f631339.local", DnsType.A, 120) { Address = Camera },
            new DnsRecord("axis-b8a44f631339.local", DnsType.A, 120) { Address = IPAddress.Parse("169.254.10.20") },
        ]))!;

        var (found, followUp) = aggregator.Add(answer, Camera);

        var device = Assert.Single(found);
        Assert.Empty(followUp);
        Assert.Equal("B8A44F631339", device.Serial);
        Assert.Equal("AXIS P3265-V - B8A44F631339", device.InstanceName);
        Assert.Equal(["192.168.0.20"], device.Addresses);
        Assert.Equal(80, device.Port);
        Assert.Equal("axis-b8a44f631339.local", device.HostName);
        Assert.Empty(aggregator.Add(answer, Camera).Found); // the same answer again: nothing new
    }

    [Fact]
    public void ADeviceOutsideTheReachedNetworksIsNotReported()
    {
        var aggregator = new AxisAggregator(_ => false);
        var instance = "AXIS M1065 - ACCC8E000001._axis-video._tcp.local";
        var answer = DnsWire.Parse(DnsWire.Response(0, [],
        [
            new DnsRecord(instance, DnsType.Srv, 120) { Target = "axis-accc8e000001.local", Port = 80 },
            new DnsRecord("axis-accc8e000001.local", DnsType.A, 120) { Address = IPAddress.Parse("10.0.0.30") },
        ]))!;

        Assert.Empty(aggregator.Add(answer, IPAddress.Parse("10.0.0.30")).Found);
    }

    [Fact]
    public void APointerAloneAsksForTheRest()
    {
        var aggregator = new AxisAggregator(_ => true);
        var answer = DnsWire.Parse(DnsWire.Response(0, [], [new DnsRecord(AxisAggregator.Service, DnsType.DomainPointer, 120) { Target = "X - ACCC8E000001._axis-video._tcp.local" }]))!;

        var (found, followUp) = aggregator.Add(answer, Camera);

        Assert.Empty(found);
        Assert.Contains(followUp, q => q.Type == DnsType.Srv);
    }

    [Fact]
    public void TheSerialComesFromTheTxtRecordOrTheInstanceName()
    {
        Assert.Equal("ACCC8E000001", AxisAggregator.Serial("X - ACCC8E000001._axis-video._tcp.local", []));
        Assert.Equal("B8A44F631339", AxisAggregator.Serial("Garage._axis-video._tcp.local", ["macaddress=b8:a4:4f:63:13:39"]));
        Assert.Null(AxisAggregator.Serial("Garage._axis-video._tcp.local", ["other=1"]));
    }

    [Fact]
    public void AWaitingNodeAnswersQueriesForItsServiceWithoutTheServerName()
    {
        var self = new Announcement("a1b2c3d4", "site-pc", "3.7.0", "unbound", 5081, [IPAddress.Parse("10.0.0.7"), IPAddress.Parse("192.168.0.5")]);
        var query = DnsWire.Parse(DnsWire.Query(77, new DnsQuestion(NodeAnnouncer.Service, DnsType.DomainPointer)))!;

        var answer = DnsWire.Parse(NodeAnnouncer.Answer(query, self)!)!;

        Assert.True(answer.IsResponse);
        Assert.Equal(77, answer.Id);
        Assert.Contains(answer.Records, r => r.Type == DnsType.DomainPointer && r.Target == "site-pc - a1b2c3d4._oadm-site._tcp.local");
        Assert.Contains(answer.Records, r => r.Type == DnsType.Srv && r.Port == 5081 && r.Target == "oadm-site-a1b2c3d4.local");
        var txt = Assert.Single(answer.Records, r => r.Type == DnsType.Txt).Texts;
        Assert.Equal(["id=a1b2c3d4", "version=3.7.0", "state=unbound"], txt);
        Assert.Equal(2, answer.Records.Count(r => r.Type == DnsType.A));
        Assert.Null(NodeAnnouncer.Answer(DnsWire.Parse(DnsWire.Query(1, new DnsQuestion(AxisAggregator.Service, DnsType.DomainPointer)))!, self));
    }

    [Fact]
    public void BrokenPacketsAreIgnored()
    {
        Assert.Null(DnsWire.Parse([1, 2, 3]));
        var loop = new byte[] { 0, 0, 0x84, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0xC0, 12, 0, 12, 0, 1 };
        Assert.Null(DnsWire.Parse(loop));
    }

    [Fact]
    public async Task ARangeScanFindsOpenPortsOnlyOnReachedNetworksAndAllowedPorts()
    {
        using var folder = new TempFolder();
        var store = new NodeStore(folder.Path);
        store.Update(c => c.Allowed.RemoveAll(p => p.Port == 80));
        await using var https = new EchoServer();
        var connector = new MapConnector();
        connector.Map[new IPEndPoint(IPAddress.Parse("127.0.0.5"), 443)] = new IPEndPoint(IPAddress.Loopback, https.Port);
        var closed = Ports.FreeTcp();
        for (var i = 1; i <= 9; i++)
        {
            if (i != 5)
                connector.Map[new IPEndPoint(IPAddress.Parse($"127.0.0.{i}"), 443)] = new IPEndPoint(IPAddress.Loopback, closed);
        }

        await using var log = new ConnectionLog(null, TimeProvider.System);
        var scanner = new RangeScanner(new PortPolicy(store), new NetworkScope(store, FakeCards.LoopbackSite()), connector, log, TimeProvider.System);
        Assert.True(RangeScanner.TryParse("127.0.0.1", "127.0.0.9", out var first, out var last, out _));

        var results = new List<RangeScanResult>();
        await foreach (var result in scanner.ScanAsync(first, last, 4, TimeSpan.FromSeconds(10), CancellationToken.None))
            results.Add(result);

        var found = Assert.Single(results, r => r.Address.Length > 0);
        Assert.Equal("127.0.0.5", found.Address);
        Assert.Equal([443], found.OpenPorts);
        Assert.True(results[^1].Finished);
        Assert.Equal(9, results[^1].Tried);
        Assert.Equal("1 answered", log.Newest(1)[0].Result);
    }

    [Theory]
    [InlineData("10.0.0.1", "10.0.0.254", true)]
    [InlineData("10.0.0.254", "10.0.0.1", false)]
    [InlineData("10.0.0.1", "10.2.0.1", false)]
    [InlineData("10.0.1", "10.0.0.9", false)]
    [InlineData("fd00::1", "fd00::9", false)]
    public void RangesAreChecked(string from, string to, bool valid) =>
        Assert.Equal(valid, RangeScanner.TryParse(from, to, out _, out _, out _));

    [Fact]
    public async Task ReverseLookupKeepsOnlyFullNamesThatResolveBack()
    {
        var dns = new FakeDns();
        dns.Reverse[Camera] = "Axis-B8A44F631339.Site.Example.";
        dns.Names["axis-b8a44f631339.site.example"] = [Camera];
        dns.Reverse[IPAddress.Parse("192.168.0.21")] = "axis-accc8e000001.local";
        dns.Reverse[IPAddress.Parse("192.168.0.22")] = "other.site.example";
        dns.Names["other.site.example"] = [IPAddress.Parse("192.168.0.99")];

        Assert.Equal("axis-b8a44f631339.site.example", await DnsNameRules.ReverseLookupAsync(dns, Camera, TimeSpan.FromSeconds(30), CancellationToken.None));
        Assert.Null(await DnsNameRules.ReverseLookupAsync(dns, IPAddress.Parse("192.168.0.21"), TimeSpan.FromSeconds(30), CancellationToken.None));
        Assert.Null(await DnsNameRules.ReverseLookupAsync(dns, IPAddress.Parse("192.168.0.22"), TimeSpan.FromSeconds(30), CancellationToken.None));
        Assert.Null(await DnsNameRules.ReverseLookupAsync(dns, IPAddress.Parse("192.168.0.23"), TimeSpan.FromSeconds(30), CancellationToken.None));
        Assert.Null(DnsNameRules.Normalize("camera"));
        Assert.Null(DnsNameRules.Normalize("20.0.168.192.in-addr.arpa"));
    }

    [Theory]
    [InlineData("oadm.example.com", "oadm.example.com", 5081)]
    [InlineData("10.0.0.5", "10.0.0.5", 5081)]
    [InlineData("10.0.0.5:5090", "10.0.0.5", 5090)]
    [InlineData("[fd00::5]:5090", "fd00::5", 5090)]
    [InlineData("fd00::5", "fd00::5", 5081)]
    [InlineData("https://oadm.example.com:5082/", "oadm.example.com", 5082)]
    [InlineData("", null, 0)]
    [InlineData("oadm.example.com:99999", null, 0)]
    [InlineData("bad host", null, 0)]
    public void ServerAddressesAreParsed(string text, string? host, int port)
    {
        var parsed = OutboundConnector.ParseAddress(text, NodeConfig.DefaultPort);
        Assert.Equal(host, parsed?.Host);
        if (host is not null)
            Assert.Equal(port, parsed!.Value.Port);
    }

    [Fact]
    public void TheTypedAddressComesFirstThenTheServersOwn()
    {
        var config = new NodeConfig
        {
            ServerAddress = "oadm.example.com",
            ServerAddresses = new ServerAddressList { DnsName = "oadm-srv.corp.example", Addresses = ["10.0.0.5", "oadm.example.com"], Port = 5081 },
        };

        Assert.Equal(["oadm.example.com:5081", "oadm-srv.corp.example:5081", "10.0.0.5:5081"], OutboundConnector.Targets(config).Select(t => $"{t.Host}:{t.Port}"));
    }

    [Fact]
    public async Task PingFallsBackToTcpWhenTheOsCannotPing()
    {
        await using var server = new TestServer();
        await using var https = new EchoServer();
        var connector = new MapConnector();
        connector.Map[new IPEndPoint(IPAddress.Parse("127.0.0.7"), 443)] = new IPEndPoint(IPAddress.Loopback, https.Port);
        connector.Map[new IPEndPoint(IPAddress.Parse("127.0.0.7"), 80)] = new IPEndPoint(IPAddress.Loopback, Ports.FreeTcp());
        connector.Map[new IPEndPoint(IPAddress.Parse("127.0.0.8"), 443)] = new IPEndPoint(IPAddress.Loopback, Ports.FreeTcp());
        connector.Map[new IPEndPoint(IPAddress.Parse("127.0.0.8"), 80)] = new IPEndPoint(IPAddress.Loopback, Ports.FreeTcp());
        await using var node = await TestNode.StartAsync(c => c.ServerAddress = server.Address, o => o.Connector = connector);
        await using var link = await server.NextLinkAsync();
        await link.BindAsync(node.Code);

        Assert.True((await link.Client.PingAsync(new PingRequest { Address = "127.0.0.7", TimeoutMs = 10_000 })).Answered);
        Assert.False((await link.Client.PingAsync(new PingRequest { Address = "127.0.0.8", TimeoutMs = 10_000 })).Answered);
        Assert.True((await link.Client.ProbeTcpAsync(new ProbeTcpRequest { Address = "127.0.0.7", Port = 443, TimeoutMs = 10_000 })).Open);
        var outside = await Assert.ThrowsAsync<Grpc.Core.RpcException>(async () => await link.Client.PingAsync(new PingRequest { Address = "10.0.0.20" }));
        Assert.Equal(AdminTexts.OutsideNetworks, outside.Status.Detail);
        Assert.Equal("127.0.0.1", (await link.Client.LocalAddressForAsync(new LocalAddressForRequest { Address = "127.0.0.40" })).Address);
        Assert.Empty((await link.Client.LocalAddressForAsync(new LocalAddressForRequest { Address = "10.0.0.40" })).Address);
    }

    [Fact]
    public async Task ResolveAndReverseLookupRunOnTheNode()
    {
        await using var server = new TestServer();
        var dns = new FakeDns();
        dns.Names["camera.site.example"] = [Camera];
        dns.Reverse[Camera] = "camera.site.example";
        await using var node = await TestNode.StartAsync(c => c.ServerAddress = server.Address, o => o.Dns = dns);
        await using var link = await server.NextLinkAsync();
        await link.BindAsync(node.Code);

        Assert.Equal(["192.168.0.20"], (await link.Client.ResolveAsync(new ResolveRequest { Name = "camera.site.example" })).Addresses);
        Assert.Empty((await link.Client.ResolveAsync(new ResolveRequest { Name = "unknown" })).Addresses);
        Assert.Equal("camera.site.example", (await link.Client.ReverseLookupAsync(new ReverseLookupRequest { Address = "192.168.0.20" })).Name);
    }

    [Fact]
    public async Task ARangeScanRunsOverTheLink()
    {
        await using var server = new TestServer();
        await using var https = new EchoServer();
        var connector = new MapConnector();
        connector.Map[new IPEndPoint(IPAddress.Parse("127.0.0.3"), 443)] = new IPEndPoint(IPAddress.Loopback, https.Port);
        var closed = Ports.FreeTcp();
        foreach (var i in new[] { 1, 2, 4 })
        {
            connector.Map[new IPEndPoint(IPAddress.Parse($"127.0.0.{i}"), 443)] = new IPEndPoint(IPAddress.Loopback, closed);
            connector.Map[new IPEndPoint(IPAddress.Parse($"127.0.0.{i}"), 80)] = new IPEndPoint(IPAddress.Loopback, closed);
        }

        connector.Map[new IPEndPoint(IPAddress.Parse("127.0.0.3"), 80)] = new IPEndPoint(IPAddress.Loopback, closed);
        await using var node = await TestNode.StartAsync(c => c.ServerAddress = server.Address, o => o.Connector = connector);
        await using var link = await server.NextLinkAsync();
        await link.BindAsync(node.Code);

        using var call = link.Client.RangeScan(new RangeScanRequest { From = "127.0.0.1", To = "127.0.0.4", Parallelism = 2, TimeoutMs = 10_000 });
        var results = new List<RangeScanResult>();
        while (await call.ResponseStream.MoveNext(CancellationToken.None))
            results.Add(call.ResponseStream.Current);

        Assert.Equal("127.0.0.3", Assert.Single(results, r => r.Address.Length > 0).Address);
        Assert.True(results[^1].Finished);
        var invalid = await Assert.ThrowsAsync<Grpc.Core.RpcException>(async () =>
        {
            using var bad = link.Client.RangeScan(new RangeScanRequest { From = "127.0.0.9", To = "127.0.0.1" });
            await bad.ResponseStream.MoveNext(CancellationToken.None);
        });
        Assert.Equal(Grpc.Core.StatusCode.InvalidArgument, invalid.StatusCode);
    }
}
