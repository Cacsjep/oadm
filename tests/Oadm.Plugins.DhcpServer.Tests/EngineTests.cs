using System.Net;

using Microsoft.Extensions.Time.Testing;

using Oadm.Plugins.DhcpServer.Leases;
using Oadm.Plugins.DhcpServer.Protocol;
using Oadm.Plugins.DhcpServer.Serving;

namespace Oadm.Plugins.DhcpServer.Tests;

/// <summary>The RFC 2131 state machine on the engine (no sockets).</summary>
public sealed class EngineTests : IDisposable
{
    private static readonly DhcpNetworkInfo Network = new("eth-id", "Ethernet", "10.0.0.17", 24, "10.0.0.138", ["10.0.0.138"], "example.local");
    private static readonly ulong A = Mac.Of("AC:CC:8E:5F:60:71");
    private static readonly ulong B = Mac.Of("B8:A4:4F:63:13:39");
    private static uint s_xid = 1;

    private readonly FakeTimeProvider _time = Clock.New();
    private readonly LeaseStore _store = new();
    private readonly FakeProbe _probe = new();
    private readonly DhcpEngine _engine;

    public EngineTests()
    {
        _engine = new DhcpEngine(_store, _probe, new DhcpEngineOptions(), _time) { Scope = DhcpScope.Create(Network, Ip.Of("10.0.0.100"), Ip.Of("10.0.0.199")) };
    }

    public void Dispose() => _engine.Dispose();

    [Fact]
    public async Task Discover_gets_an_offer_with_all_derived_options()
    {
        var reply = await Handle(Msg(A, DhcpMessageType.Discover, m => m.HostName = "axis-accc8e5f6071"));
        Assert.NotNull(reply);
        var offer = reply.Message;
        Assert.Equal(DhcpMessageType.Offer, offer.MessageType);
        Assert.Equal(DhcpMessage.BootReply, offer.Op);
        Assert.Equal(Ip.Of("10.0.0.100"), offer.YourAddress);
        Assert.Equal(Ip.Of("10.0.0.17"), offer.ServerId);
        Assert.Equal(Ip.Of("255.255.255.0"), offer.SubnetMask);
        Assert.Equal([Ip.Of("10.0.0.138")], offer.Routers);
        Assert.Equal([Ip.Of("10.0.0.138")], offer.DnsServers);
        Assert.Equal("example.local", offer.DomainName);
        Assert.Equal(86_400u, offer.LeaseTime);
        Assert.Equal(43_200u, offer.RenewalTime);
        Assert.Equal(75_600u, offer.RebindingTime);
        Assert.Equal("axis-accc8e5f6071", offer.HostName);
        Assert.Equal(A, offer.Mac);
        Assert.Equal(new IPEndPoint(IPAddress.Broadcast, 68), reply.Destination);
        Assert.Equal(["10.0.0.100"], _probe.Probed);
    }

    [Fact]
    public async Task Parameter_request_list_is_honored()
    {
        var reply = await Handle(Msg(A, DhcpMessageType.Discover, m => m.ParameterRequestList = [DhcpOption.SubnetMask]));
        Assert.NotNull(reply!.Message.SubnetMask);
        Assert.Empty(reply.Message.Routers);
        Assert.Empty(reply.Message.DnsServers);
        Assert.Null(reply.Message.DomainName);
        Assert.NotNull(reply.Message.LeaseTime); // always sent
    }

    [Fact]
    public async Task Full_exchange_selecting_then_renew_then_rebind()
    {
        var offer = (await Handle(Msg(A, DhcpMessageType.Discover)))!.Message;
        var ack = await Handle(Msg(A, DhcpMessageType.Request, m => { m.ServerId = offer.ServerId; m.RequestedAddress = offer.YourAddress; }));
        Assert.Equal(DhcpMessageType.Ack, ack!.Message.MessageType);
        Assert.Equal(offer.YourAddress, ack.Message.YourAddress);
        Assert.Equal(LeaseInfo.Active, _store.Find(A)!.State);
        Assert.Equal(_time.GetUtcNow().UtcDateTime.AddHours(24), _store.Find(A)!.ExpiresUtc);

        // Renewing: unicast from the client's address, the reply goes to it.
        _time.Advance(TimeSpan.FromHours(12));
        var renew = await Handle(Msg(A, DhcpMessageType.Request, m => m.ClientAddress = offer.YourAddress));
        Assert.Equal(DhcpMessageType.Ack, renew!.Message.MessageType);
        Assert.Equal(offer.YourAddress, renew.Message.ClientAddress);
        Assert.Equal(new IPEndPoint(IPAddress.Parse("10.0.0.100"), 68), renew.Destination);
        Assert.Equal(_time.GetUtcNow().UtcDateTime.AddHours(24), _store.Find(A)!.ExpiresUtc);

        // Rebinding looks the same to the server (broadcast instead of unicast): extended as well.
        _time.Advance(TimeSpan.FromHours(21));
        var rebind = await Handle(Msg(A, DhcpMessageType.Request, m => { m.ClientAddress = offer.YourAddress; m.Flags = DhcpMessage.BroadcastFlag; }));
        Assert.Equal(DhcpMessageType.Ack, rebind!.Message.MessageType);
    }

    [Fact]
    public async Task Selecting_another_server_withdraws_our_offer_silently()
    {
        var offer = (await Handle(Msg(A, DhcpMessageType.Discover)))!.Message;
        Assert.Equal(1, _store.PendingOffers);
        var reply = await Handle(Msg(A, DhcpMessageType.Request, m => { m.ServerId = Ip.Of("10.0.0.1"); m.RequestedAddress = Ip.Of("10.0.0.250"); }));
        Assert.Null(reply);
        Assert.Equal(0, _store.PendingOffers);
        Assert.Null(_store.Find(A));
        _ = offer;
    }

    [Fact]
    public async Task Requesting_another_address_than_offered_is_nakked_by_broadcast()
    {
        await Handle(Msg(A, DhcpMessageType.Discover));
        var reply = await Handle(Msg(A, DhcpMessageType.Request, m => { m.ServerId = Ip.Of("10.0.0.17"); m.RequestedAddress = Ip.Of("10.0.0.150"); }));
        Assert.Equal(DhcpMessageType.Nak, reply!.Message.MessageType);
        Assert.Equal(new IPEndPoint(IPAddress.Broadcast, 68), reply.Destination);
        Assert.Null(reply.Message.LeaseTime);
        Assert.Equal(0u, reply.Message.YourAddress);
    }

    [Fact]
    public async Task Init_reboot_acks_its_lease_naks_a_wrong_one_and_stays_silent_for_strangers()
    {
        await Acquire(A);
        var ok = await Handle(Msg(A, DhcpMessageType.Request, m => m.RequestedAddress = Ip.Of("10.0.0.100")));
        Assert.Equal(DhcpMessageType.Ack, ok!.Message.MessageType);

        var wrong = await Handle(Msg(A, DhcpMessageType.Request, m => m.RequestedAddress = Ip.Of("10.0.0.120")));
        Assert.Equal(DhcpMessageType.Nak, wrong!.Message.MessageType);

        var otherNetwork = await Handle(Msg(B, DhcpMessageType.Request, m => m.RequestedAddress = Ip.Of("192.168.1.20")));
        Assert.Equal(DhcpMessageType.Nak, otherNetwork!.Message.MessageType);

        var stranger = await Handle(Msg(B, DhcpMessageType.Request, m => m.RequestedAddress = Ip.Of("10.0.0.130")));
        Assert.Null(stranger); // RFC 2131: no record of the client: remain silent

        var taken = await Handle(Msg(B, DhcpMessageType.Request, m => m.RequestedAddress = Ip.Of("10.0.0.100")));
        Assert.Equal(DhcpMessageType.Nak, taken!.Message.MessageType);
    }

    [Fact]
    public async Task Renewing_an_unknown_lease_is_nakked()
    {
        var reply = await Handle(Msg(B, DhcpMessageType.Request, m => m.ClientAddress = Ip.Of("10.0.0.150")));
        Assert.Equal(DhcpMessageType.Nak, reply!.Message.MessageType);
    }

    [Fact]
    public async Task Decline_marks_the_address_as_conflict_and_the_next_offer_is_another_one()
    {
        var first = await Acquire(A);
        await Handle(Msg(A, DhcpMessageType.Decline, m => { m.ServerId = Ip.Of("10.0.0.17"); m.RequestedAddress = first; }));
        Assert.Null(_store.Find(A));
        Assert.True(_store.IsConflict(first, _time.GetUtcNow().UtcDateTime));

        var offer = (await Handle(Msg(A, DhcpMessageType.Discover)))!.Message;
        Assert.NotEqual(first, offer.YourAddress);

        _time.Advance(TimeSpan.FromHours(1.1));
        _store.Sweep(_time.GetUtcNow().UtcDateTime);
        Assert.False(_store.IsConflict(first, _time.GetUtcNow().UtcDateTime));
    }

    [Fact]
    public async Task Release_keeps_the_record_and_the_client_gets_the_same_address_again()
    {
        var address = await Acquire(A);
        await Handle(Msg(A, DhcpMessageType.Release, m => { m.ServerId = Ip.Of("10.0.0.17"); m.ClientAddress = address; }));
        Assert.Equal(LeaseInfo.Released, _store.Find(A)!.State);

        await Acquire(B); // takes the next free one, not A's
        Assert.Equal(address, await Acquire(A));
    }

    [Fact]
    public async Task Inform_gets_the_options_without_a_lease_by_unicast()
    {
        var reply = await Handle(Msg(B, DhcpMessageType.Inform, m => m.ClientAddress = Ip.Of("10.0.0.60")));
        Assert.Equal(DhcpMessageType.Ack, reply!.Message.MessageType);
        Assert.Null(reply.Message.LeaseTime);
        Assert.Equal(0u, reply.Message.YourAddress);
        Assert.Equal(Ip.Of("255.255.255.0"), reply.Message.SubnetMask);
        Assert.Equal(new IPEndPoint(IPAddress.Parse("10.0.0.60"), 68), reply.Destination);
        Assert.Null(_store.Find(B));
    }

    [Fact]
    public async Task An_address_in_use_is_skipped_and_marked_as_conflict()
    {
        _probe.InUse.Add("10.0.0.100");
        _probe.InUse.Add("10.0.0.101");
        var offer = (await Handle(Msg(A, DhcpMessageType.Discover)))!.Message;
        Assert.Equal(Ip.Of("10.0.0.102"), offer.YourAddress);
        Assert.True(_store.IsConflict(Ip.Of("10.0.0.100"), _time.GetUtcNow().UtcDateTime));
        Assert.Equal(["10.0.0.100", "10.0.0.101", "10.0.0.102"], _probe.Probed);
    }

    [Fact]
    public async Task One_pending_offer_per_client_without_a_second_probe()
    {
        var first = (await Handle(Msg(A, DhcpMessageType.Discover)))!.Message;
        var second = (await Handle(Msg(A, DhcpMessageType.Discover)))!.Message;
        Assert.Equal(first.YourAddress, second.YourAddress);
        Assert.Single(_probe.Probed);
        Assert.Equal(1, _store.PendingOffers);
    }

    [Fact]
    public async Task Offers_expire_after_60_seconds()
    {
        await Handle(Msg(A, DhcpMessageType.Discover));
        _time.Advance(TimeSpan.FromSeconds(59));
        _store.Sweep(_time.GetUtcNow().UtcDateTime);
        Assert.Equal(1, _store.PendingOffers);
        _time.Advance(TimeSpan.FromSeconds(2));
        _store.Sweep(_time.GetUtcNow().UtcDateTime);
        Assert.Equal(0, _store.PendingOffers);
        Assert.Null(_store.Find(A));
    }

    [Fact]
    public async Task Static_lease_wins_even_outside_the_range_and_is_never_probed()
    {
        _store.SetStatic(B, Ip.Of("10.0.0.48"), "Lobby", null, _time.GetUtcNow().UtcDateTime);
        var offer = (await Handle(Msg(B, DhcpMessageType.Discover, m => m.RequestedAddress = Ip.Of("10.0.0.150"))))!.Message;
        Assert.Equal(Ip.Of("10.0.0.48"), offer.YourAddress);
        Assert.Empty(_probe.Probed);
        var ack = await Handle(Msg(B, DhcpMessageType.Request, m => { m.ServerId = Ip.Of("10.0.0.17"); m.RequestedAddress = Ip.Of("10.0.0.48"); }));
        Assert.Equal(DhcpMessageType.Ack, ack!.Message.MessageType);
        var lease = _store.Find(B)!;
        Assert.True(lease.IsStatic);
        Assert.Equal("Lobby", lease.Name);
        Assert.Equal(LeaseInfo.Active, lease.State);

        // Another device asking for the reserved address gets something else.
        _store.SetStatic(B, Ip.Of("10.0.0.100"), null, B, _time.GetUtcNow().UtcDateTime);
        var other = (await Handle(Msg(A, DhcpMessageType.Discover, m => m.RequestedAddress = Ip.Of("10.0.0.100"))))!.Message;
        Assert.NotEqual(Ip.Of("10.0.0.100"), other.YourAddress);
    }

    [Fact]
    public async Task Server_router_and_dns_addresses_in_the_range_are_never_handed_out()
    {
        using var engine = new DhcpEngine(_store, _probe, new DhcpEngineOptions(), _time) { Scope = DhcpScope.Create(Network, Ip.Of("10.0.0.137"), Ip.Of("10.0.0.139")) };
        var first = (await engine.HandleAsync(Msg(A, DhcpMessageType.Discover), default))!.Message.YourAddress;
        var second = (await engine.HandleAsync(Msg(B, DhcpMessageType.Discover), default))!.Message.YourAddress;
        Assert.Equal([Ip.Of("10.0.0.137"), Ip.Of("10.0.0.139")], new[] { first, second }.Order());
        Assert.Null(await engine.HandleAsync(Msg(Mac.Of("00:40:8C:00:00:09"), DhcpMessageType.Discover), default));
        Assert.DoesNotContain("10.0.0.138", _probe.Probed);
    }

    [Fact]
    public async Task Exhausted_pool_gives_no_offer_reclaims_expired_leases_and_reports_it()
    {
        using var engine = new DhcpEngine(_store, _probe, new DhcpEngineOptions(), _time) { Scope = DhcpScope.Create(Network, Ip.Of("10.0.0.100"), Ip.Of("10.0.0.101")) };
        Assert.NotNull(await AcquireWith(engine, A));
        Assert.NotNull(await AcquireWith(engine, B));
        var c = Mac.Of("00:40:8C:00:00:01");
        Assert.Null(await engine.HandleAsync(Msg(c, DhcpMessageType.Discover), default));
        Assert.True(engine.ExhaustedWithin(TimeSpan.FromMinutes(5)));

        _time.Advance(TimeSpan.FromHours(25));
        _store.Sweep(_time.GetUtcNow().UtcDateTime);
        Assert.Equal(LeaseInfo.Expired, _store.Find(A)!.State);
        Assert.NotNull(await AcquireWith(engine, c)); // the oldest expired lease is reclaimed
    }

    [Fact]
    public async Task Pending_offers_are_capped_at_half_the_pool_so_a_flood_cannot_take_it()
    {
        using var engine = new DhcpEngine(_store, _probe, new DhcpEngineOptions(), _time) { Scope = DhcpScope.Create(Network, Ip.Of("10.0.0.100"), Ip.Of("10.0.0.199")) };
        var known = await AcquireWith(engine, B);
        var offered = 0;
        for (var i = 0; i < 500; i++)
        {
            if (await engine.HandleAsync(Msg(0x0040_8C00_0000UL + (ulong)i, DhcpMessageType.Discover), default) is not null)
            {
                offered++;
            }
        }

        Assert.Equal(50, offered);
        Assert.Equal(50, _store.PendingOffers);

        // A known device still gets its address during the flood; new ones once the flood's offers expired.
        Assert.Equal(known, (await engine.HandleAsync(Msg(B, DhcpMessageType.Discover), default))!.Message.YourAddress);
        Assert.Null(await engine.HandleAsync(Msg(A, DhcpMessageType.Discover), default));
        _time.Advance(TimeSpan.FromSeconds(61));
        _store.Sweep(_time.GetUtcNow().UtcDateTime);
        Assert.NotNull(await AcquireWith(engine, A));
    }

    [Fact]
    public async Task Relayed_messages_and_the_own_probe_mac_are_ignored()
    {
        Assert.Null(await Handle(Msg(A, DhcpMessageType.Discover, m => m.RelayAddress = Ip.Of("10.0.1.1"))));
        _engine.IgnoreMac = mac => mac == B;
        Assert.Null(await Handle(Msg(B, DhcpMessageType.Discover)));
        Assert.Null(await Handle(Msg(Mac.Of("01:00:5E:00:00:01"), DhcpMessageType.Discover)));
    }

    [Fact]
    [Trait("Category", "Timing")] // the requests must overlap the 300 ms probes
    public async Task Probes_are_bounded_and_busy_requests_get_no_answer()
    {
        _probe.Delay = TimeSpan.FromMilliseconds(300);
        using var engine = new DhcpEngine(_store, _probe, new DhcpEngineOptions { MaxConcurrentProbes = 2 }, _time) { Scope = DhcpScope.Create(Network, Ip.Of("10.0.0.100"), Ip.Of("10.0.0.199")) };
        var tasks = Enumerable.Range(0, 5).Select(i => engine.HandleAsync(Msg(0x0040_8C00_0000UL + (ulong)i, DhcpMessageType.Discover), default).AsTask()).ToList();
        var replies = await Task.WhenAll(tasks);
        Assert.Equal(2, replies.Count(r => r is not null));
        Assert.Equal(2, _store.PendingOffers); // the dropped ones hold nothing
    }

    private async Task<uint> Acquire(ulong mac) => (await AcquireWith(_engine, mac))!.Value;

    private static async Task<uint?> AcquireWith(DhcpEngine engine, ulong mac)
    {
        var offer = await engine.HandleAsync(Msg(mac, DhcpMessageType.Discover), default);
        if (offer is null)
        {
            return null;
        }

        var ack = await engine.HandleAsync(Msg(mac, DhcpMessageType.Request, m => { m.ServerId = offer.Message.ServerId; m.RequestedAddress = offer.Message.YourAddress; }), default);
        return ack?.Message.MessageType == DhcpMessageType.Ack ? ack.Message.YourAddress : null;
    }

    private async Task<DhcpReply?> Handle(DhcpMessage message) => await _engine.HandleAsync(message, default);

    private static DhcpMessage Msg(ulong mac, DhcpMessageType type, Action<DhcpMessage>? configure = null)
    {
        var m = new DhcpMessage { Op = DhcpMessage.BootRequest, TransactionId = s_xid++, MessageType = type, Mac = mac };
        configure?.Invoke(m);
        return m;
    }
}
