using System.Net;

using Oadm.Core.Discovery.Mdns;

namespace Oadm.Core.Tests.Discovery;

// Fixtures captured on the development network (10.0.0.0/24) on 2026-10-07 with
// "tools/Oadm.DiscoveryProbe mdns 15 <dir>":
//   mdns-own-ptr-query.bin                          our PTR query as sent by MdnsBrowser
//   mdns-unicast-p3265v-10.0.0.48.bin               legacy unicast answer, P3265-V, AXIS OS 12.11
//   mdns-unicast-m3206lve-10.0.0.201.bin            legacy unicast answer, M3206-LVE 10.12, LAN + 169.254 A, fe80 AAAA
//   mdns-unicast-m3215lve-ipv6-10.0.0.202.bin       legacy unicast answer, M3215-LVE, one A and several AAAA
//   mdns-multicast-announce-m3206lve-10.0.0.200.bin multicast answer, cache-flush bits, two A in one packet, NSEC
//   mdns-foreign-query-10.0.0.67.bin                a query for another service from another host
public class DnsMessageTests
{
    [Fact]
    public void ParsesLegacyUnicastAnswerFromP3265V()
    {
        var msg = DnsMessage.Parse(Fixture.Bytes("mdns-unicast-p3265v-10.0.0.48.bin"));

        Assert.True(msg.IsResponse);
        var q = Assert.Single(msg.Questions);
        Assert.Equal("_axis-video._tcp.local", q.Name);
        Assert.Equal(DnsRecordType.Ptr, q.Type);

        var ptr = Assert.IsType<PtrRecord>(Assert.Single(msg.Answers));
        Assert.Equal("_axis-video._tcp.local", ptr.Name);
        Assert.Equal("AXIS P3265-V - B8A44F631339._axis-video._tcp.local", ptr.DomainName);
        Assert.Equal(10u, ptr.Ttl);

        Assert.Equal(3, msg.Additionals.Count);
        var srv = Assert.IsType<SrvRecord>(msg.Additionals[0]);
        Assert.Equal("AXIS P3265-V - B8A44F631339._axis-video._tcp.local", srv.Name);
        Assert.Equal("axis-b8a44f631339.local", srv.Target);
        Assert.Equal(80, srv.Port);

        var txt = Assert.IsType<TxtRecord>(msg.Additionals[1]);
        Assert.Equal(["macaddress=B8A44F631339"], txt.Entries);
        Assert.Equal("B8A44F631339", txt.ToDictionary()["MACADDRESS"]);

        var a = Assert.IsType<AddressRecord>(msg.Additionals[2]);
        Assert.Equal(DnsRecordType.A, a.Type);
        Assert.Equal("axis-b8a44f631339.local", a.Name);
        Assert.Equal(IPAddress.Parse("10.0.0.48"), a.Address);
    }

    [Fact]
    public void ParsesIpv4LinkLocalAndIpv6Addresses()
    {
        var msg = DnsMessage.Parse(Fixture.Bytes("mdns-unicast-m3206lve-10.0.0.201.bin"));

        var addresses = msg.Additionals.OfType<AddressRecord>().ToList();
        Assert.Equal(
            ["10.0.0.201", "169.254.247.105", "fe80::baa4:4fff:fe50:4193"],
            addresses.Select(a => a.Address.ToString()));
        Assert.Equal(DnsRecordType.Aaaa, addresses[2].Type);
    }

    [Fact]
    public void ParsesMulticastAnnouncementWithCacheFlushAndUnknownTypes()
    {
        var msg = DnsMessage.Parse(Fixture.Bytes("mdns-multicast-announce-m3206lve-10.0.0.200.bin"));

        Assert.True(msg.IsResponse);
        Assert.Empty(msg.Questions);
        var ptr = Assert.IsType<PtrRecord>(Assert.Single(msg.Answers));
        Assert.False(ptr.CacheFlush);
        Assert.Equal(4500u, ptr.Ttl);

        Assert.Equal(7, msg.Additionals.Count);
        Assert.All(msg.Additionals, r => Assert.True(r.CacheFlush));
        Assert.All(msg.Additionals, r => Assert.Equal(1, r.Class));
        Assert.Equal(2, msg.Additionals.OfType<UnknownRecord>().Count(r => (ushort)r.Type == 47)); // NSEC
        Assert.Equal(
            ["10.0.0.200", "169.254.21.227"],
            msg.Additionals.OfType<AddressRecord>().Where(a => a.Type == DnsRecordType.A).Select(a => a.Address.ToString()));
    }

    [Fact]
    public void ParsesForeignQueryAsNonResponse()
    {
        var msg = DnsMessage.Parse(Fixture.Bytes("mdns-foreign-query-10.0.0.67.bin"));

        Assert.False(msg.IsResponse);
        var q = Assert.Single(msg.Questions);
        Assert.Equal("SdipCore._sdipcore._tcp.local", q.Name);
        Assert.Equal(DnsRecordType.Any, q.Type);
        Assert.Empty(msg.AllRecords);
    }

    [Fact]
    public void BuildQueryMatchesCapturedQueryBytes()
    {
        var bytes = DnsMessage.BuildQuery(0, [new DnsQuestion("_axis-video._tcp.local", DnsRecordType.Ptr, false)]);

        Assert.Equal(Fixture.Bytes("mdns-own-ptr-query.bin"), bytes);
    }

    [Fact]
    public void BuildQuerySetsUnicastResponseBit()
    {
        var bytes = DnsMessage.BuildQuery(7, [new DnsQuestion("x.local.", DnsRecordType.Srv, true)]);
        var msg = DnsMessage.Parse(bytes);

        Assert.Equal(7, msg.Id);
        var q = Assert.Single(msg.Questions);
        Assert.Equal("x.local", q.Name);
        Assert.Equal(DnsRecordType.Srv, q.Type);
        Assert.True(q.UnicastResponse);
    }

    [Fact]
    public void TryParseRejectsEveryTruncationOfARealPacket()
    {
        var full = Fixture.Bytes("mdns-unicast-m3215lve-ipv6-10.0.0.202.bin");
        Assert.True(DnsMessage.TryParse(full, out _));

        for (var len = 0; len < full.Length; len++)
        {
            Assert.False(DnsMessage.TryParse(full.AsSpan(0, len), out var msg), $"length {len}");
            Assert.Null(msg);
        }
    }

    [Fact]
    public void TryParseRejectsCompressionPointerLoop()
    {
        // Header with one question whose name is a pointer to itself (offset 12).
        byte[] data = [0, 0, 0x84, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0xC0, 12, 0, 12, 0, 1];

        Assert.False(DnsMessage.TryParse(data, out _));
    }

    [Fact]
    public void TxtRecordDictionaryHandlesFlagsDuplicatesAndEmptyEntries()
    {
        var txt = new TxtRecord("n", 1, false, 10, ["", "flag", "a=1", "A=2", "b=x=y"]);

        var dict = txt.ToDictionary();

        Assert.Equal(string.Empty, dict["flag"]);
        Assert.Equal("1", dict["a"]);
        Assert.Equal("x=y", dict["b"]);
        Assert.Equal(3, dict.Count);
    }
}
