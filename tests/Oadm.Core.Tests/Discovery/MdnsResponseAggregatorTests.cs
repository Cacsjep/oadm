using System.Net;

using Oadm.Core.Discovery.Mdns;

namespace Oadm.Core.Tests.Discovery;

public class MdnsResponseAggregatorTests
{
    private const string Service = "_axis-video._tcp.local";
    private static readonly IPAddress Nic = IPAddress.Parse("10.0.0.17");

    [Fact]
    public void ResolvesInstanceFromSingleUnicastAnswer()
    {
        var agg = new MdnsResponseAggregator(Service);

        var result = agg.Process(Parse("mdns-unicast-p3265v-10.0.0.48.bin"), IPAddress.Parse("10.0.0.48"), Nic);

        var i = Assert.Single(result.Resolved);
        Assert.Empty(result.FollowUpQuestions);
        Assert.Equal("AXIS P3265-V - B8A44F631339", i.DisplayName);
        Assert.Equal("axis-b8a44f631339", i.HostName);
        Assert.Equal(80, i.Port);
        Assert.Equal("B8A44F631339", i.Serial);
        Assert.Equal([IPAddress.Parse("10.0.0.48")], i.Addresses);
        Assert.Equal("B8A44F631339", i.Txt["macaddress"]);
        Assert.Equal(Nic, i.LocalAddress);
    }

    [Fact]
    public void RepeatedIdenticalAnswerIsNotEmittedAgain()
    {
        var agg = new MdnsResponseAggregator(Service);
        var msg = Parse("mdns-unicast-m3206lve-10.0.0.201.bin");

        Assert.Single(agg.Process(msg, IPAddress.Parse("10.0.0.201"), Nic).Resolved);
        Assert.Empty(agg.Process(msg, IPAddress.Parse("10.0.0.201"), Nic).Resolved);
    }

    [Fact]
    public void IgnoresIpv6AndKeepsLinkLocalAfterLanAddress()
    {
        var agg = new MdnsResponseAggregator(Service);

        var i = Assert.Single(agg.Process(Parse("mdns-unicast-m3206lve-10.0.0.201.bin"), IPAddress.Parse("10.0.0.201"), Nic).Resolved);

        Assert.Equal(["10.0.0.201", "169.254.247.105"], i.Addresses.Select(a => a.ToString()));
        Assert.Equal("B8A44F504193", i.Serial);
    }

    [Fact]
    public void CacheFlushKeepsAllAddressesOfTheSamePacket()
    {
        var agg = new MdnsResponseAggregator(Service);

        var i = Assert.Single(agg.Process(Parse("mdns-multicast-announce-m3206lve-10.0.0.200.bin"), IPAddress.Parse("10.0.0.200"), null).Resolved);

        Assert.Equal(["10.0.0.200", "169.254.21.227"], i.Addresses.Select(a => a.ToString()));
        Assert.Equal("B8A44F3B34BB", i.Serial);
        Assert.Null(i.LocalAddress);
    }

    [Fact]
    public void CacheFlushInALaterPacketReplacesOldAddresses()
    {
        var agg = new MdnsResponseAggregator(Service);
        const string instance = "AXIS M3206-LVE - B8A44F3B34BB." + Service;
        agg.Process(Parse("mdns-multicast-announce-m3206lve-10.0.0.200.bin"), null, null);

        var moved = DnsMessage.Parse(new DnsResponseBuilder().A("axis-b8a44f3b34bb.local", "10.0.0.99").Build());
        var i = Assert.Single(agg.Process(moved, IPAddress.Parse("10.0.0.99"), null).Resolved);

        Assert.Equal(instance, i.InstanceName);
        Assert.Equal([IPAddress.Parse("10.0.0.99")], i.Addresses);
    }

    [Fact]
    public void PtrOnlyAnswerRequestsSrvAndTxt()
    {
        var agg = new MdnsResponseAggregator(Service);
        var msg = DnsMessage.Parse(new DnsResponseBuilder().Ptr(Service, "Cam - AABBCCDDEEFF." + Service).Build());

        var result = agg.Process(msg, IPAddress.Parse("10.0.0.5"), Nic);

        Assert.Empty(result.Resolved);
        Assert.Equal(
            [("Cam - AABBCCDDEEFF." + Service, DnsRecordType.Srv), ("Cam - AABBCCDDEEFF." + Service, DnsRecordType.Txt)],
            result.FollowUpQuestions.Select(q => (q.Name, q.Type)));
    }

    [Fact]
    public void MissingARecordFallsBackToResponderAndAsksForA()
    {
        var agg = new MdnsResponseAggregator(Service);
        var instance = "Cam - AABBCCDDEEFF." + Service;
        var msg = DnsMessage.Parse(new DnsResponseBuilder()
            .Ptr(Service, instance)
            .Srv(instance, "axis-aabbccddeeff.local", 443)
            .Build());

        var result = agg.Process(msg, IPAddress.Parse("10.0.0.5"), Nic);

        var i = Assert.Single(result.Resolved);
        Assert.Equal([IPAddress.Parse("10.0.0.5")], i.Addresses);
        Assert.Equal(443, i.Port);
        Assert.Equal("AABBCCDDEEFF", i.Serial); // no TXT: serial from instance name
        var q = Assert.Single(result.FollowUpQuestions);
        Assert.Equal(("axis-aabbccddeeff.local", DnsRecordType.A), (q.Name, q.Type));
    }

    [Fact]
    public void RecordsSplitOverPacketsAreCombined()
    {
        var agg = new MdnsResponseAggregator(Service);
        var instance = "Cam." + Service;

        Assert.Empty(agg.Process(DnsMessage.Parse(new DnsResponseBuilder().Ptr(Service, instance).Build()), null, Nic).Resolved);
        Assert.Empty(agg.Process(DnsMessage.Parse(new DnsResponseBuilder().Srv(instance, "host.local", 80).Txt(instance, "macaddress=ac:cc:8e:01:02:03").Build()), null, Nic).Resolved);
        var i = Assert.Single(agg.Process(DnsMessage.Parse(new DnsResponseBuilder().A("host.local", "10.0.0.9").Build()), null, Nic).Resolved);

        Assert.Equal("ACCC8E010203", i.Serial);
        Assert.Equal("host", i.HostName);
        Assert.Equal([IPAddress.Parse("10.0.0.9")], i.Addresses);
    }

    [Fact]
    public void IgnoresOtherServicesQueriesAndGoodbyePackets()
    {
        var agg = new MdnsResponseAggregator(Service);

        Assert.Same(MdnsAggregationResult.Empty, agg.Process(Parse("mdns-foreign-query-10.0.0.67.bin"), null, Nic));

        var other = DnsMessage.Parse(new DnsResponseBuilder()
            .Ptr("_http._tcp.local", "Printer._http._tcp.local")
            .Srv("Printer._http._tcp.local", "printer.local", 80)
            .A("printer.local", "10.0.0.50")
            .Build());
        Assert.Empty(agg.Process(other, null, Nic).Resolved);

        var goodbye = DnsMessage.Parse(new DnsResponseBuilder().Ptr(Service, "Gone." + Service, ttl: 0).Build());
        var result = agg.Process(goodbye, null, Nic);
        Assert.Empty(result.Resolved);
        Assert.Empty(result.FollowUpQuestions);
    }

    private static DnsMessage Parse(string fixture) => DnsMessage.Parse(Fixture.Bytes(fixture));
}
