using System.Net;
using System.Net.Sockets;

using Oadm.Core.Plugins;
using Oadm.Plugins.DhcpServer.Protocol;
using Oadm.Plugins.DhcpServer.Serving;
using Oadm.Plugins.DhcpServer.Status;
using Oadm.Sdk.Network;

namespace Oadm.Plugins.DhcpServer.Tests;

/// <summary>The whole server (settings, listener, leases, events) on the in-memory network: never a real DHCP server.</summary>
public sealed class ServerTests : IAsyncLifetime
{
    private readonly FakeDhcpNetwork _network = new();
    private readonly FakeProbe _probe = new();
    private readonly InMemoryPluginSettingsProvider _settingsProvider = new();
    private readonly PluginEventHub _hub = new();
    private DhcpServerPlugin _plugin = null!;

    private DhcpServerService Service => _plugin.Service!;

    public async Task InitializeAsync() => _plugin = await StartAsync();

    public async Task DisposeAsync() => await _plugin.DisposeAsync();

    [Fact]
    public async Task Starts_disabled_and_lists_only_IPv4_interfaces_that_are_up()
    {
        var state = DhcpJson.Deserialize<DhcpState>(await _plugin.InvokeAsync(DhcpServerMethods.GetState, null, default));
        Assert.False(state.Config.Enabled);
        Assert.Equal(ServiceStatus.Neutral, state.Status.Kind);
        Assert.Equal("Stopped", state.Status.Text);
        Assert.Equal(["eth-id", "wifi-id"], state.Interfaces.Select(i => i.Id));
        Assert.Equal("Ethernet - 10.0.0.17/24 (Intel(R) Ethernet Connection I219-LM)", state.Interfaces[0].Label);
        var eth = state.Networks[0];
        Assert.Equal("10.0.0.0/24", eth.Subnet);
        Assert.Equal("Clients get mask 255.255.255.0, router 10.0.0.138, DNS 10.0.0.138, domain example.local, lease 24 h", eth.ClientsGet);
        Assert.Equal("Clients get mask 255.255.255.0, no router, no DNS, lease 24 h", state.Networks[1].ClientsGet);
        Assert.Empty(state.Leases!);
        Assert.Equal(0, _network.OpenSockets(67));
    }

    [Fact]
    public async Task Save_validates_the_range_with_errors_per_field()
    {
        var reply = await Save(new DhcpSaveRequest(true, "eth-id", "10.0.1.100", "10.0.0.50"));
        Assert.False(reply.Saved);
        Assert.Equal("Must be inside the subnet 10.0.0.0/24.", reply.FieldErrors!["RangeStart"]);
        Assert.False(reply.FieldErrors.ContainsKey("RangeEnd"));

        reply = await Save(new DhcpSaveRequest(true, "eth-id", "10.0.0.100", "10.0.0.50"));
        Assert.Equal("Must be after the start address.", reply.FieldErrors!["RangeEnd"]);

        reply = await Save(new DhcpSaveRequest(true, "eth-id", "10.0.0.100", "10.0.0.100"));
        Assert.Equal("Must be after the start address.", reply.FieldErrors!["RangeEnd"]);

        reply = await Save(new DhcpSaveRequest(true, "eth-id", "10.0.0.0", "10.0.0.255"));
        Assert.Equal("Must not be the network address of the subnet.", reply.FieldErrors!["RangeStart"]);
        Assert.Equal("Must not be the broadcast address of the subnet.", reply.FieldErrors["RangeEnd"]);

        reply = await Save(new DhcpSaveRequest(true, null, "", "x"));
        Assert.Equal("Select the interface the devices are connected to.", reply.FieldErrors!["Interface"]);
        Assert.Equal("Enter an IPv4 address, e.g. 10.0.0.100.", reply.FieldErrors["RangeEnd"]);
        Assert.Equal("Enter the first address clients can get.", reply.FieldErrors["RangeStart"]);

        reply = await Save(new DhcpSaveRequest(false, "eth-id", "", ""));
        Assert.True(reply.Saved); // disabling never needs a range
    }

    [Fact]
    public async Task Enabled_server_answers_clients_and_publishes_the_leases()
    {
        var reply = await Save(Options.Enable());
        Assert.True(reply.Saved);
        Assert.Equal("Running on Ethernet (10.0.0.17/24)", reply.State.Status.Text);
        Assert.Equal(ServiceStatus.Ok, reply.State.Status.Kind);

        using var watch = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var events = new List<Oadm.Sdk.Plugins.PluginEvent>();
        var watching = Task.Run(async () =>
        {
            await foreach (var e in _hub.WatchAsync(DhcpServerPluginInfo.PluginId, watch.Token))
            {
                lock (events)
                {
                    events.Add(e);
                }
            }
        });
        await Wait.UntilAsync(() => _hub.WatcherCount(DhcpServerPluginInfo.PluginId) == 1);

        await using var client = new TestClient(_network, Mac.Of("AC:CC:8E:5F:60:71"));
        var address = await client.AcquireAsync("axis-accc8e5f6071");
        Assert.Equal(Ip.Of("10.0.0.100"), address);

        // Renew by unicast to the server: the answer comes back by unicast.
        await client.UseAddressAsync(address!.Value);
        var renew = client.New(DhcpMessageType.Request);
        renew.ClientAddress = address.Value;
        var ack = await client.SendAsync(renew, new IPEndPoint(IPAddress.Parse("10.0.0.17"), 67));
        Assert.Equal(DhcpMessageType.Ack, ack!.MessageType);
        Assert.Contains(_network.Sent, s => s.To.Equals(new IPEndPoint(IPAddress.Parse("10.0.0.100"), 68)));

        await Wait.UntilAsync(() =>
        {
            lock (events)
            {
                return events.Any(e => e.Topic == DhcpServerMethods.LeasesTopic && e.PayloadJson!.Contains("axis-accc8e5f6071", StringComparison.Ordinal));
            }
        });
        await watch.CancelAsync();
        try
        {
            await watching;
        }
        catch (OperationCanceledException)
        {
        }
    }

    [Fact]
    public async Task Settings_and_leases_survive_a_restart()
    {
        await Save(Options.Enable());
        await using (var client = new TestClient(_network, Mac.Of("AC:CC:8E:5F:60:71")))
        {
            Assert.Equal(Ip.Of("10.0.0.100"), await client.AcquireAsync("cam"));
        }

        Assert.True(DhcpJson.Deserialize<StaticLeaseReply>(await _plugin.InvokeAsync(DhcpServerMethods.SaveStatic,
            DhcpJson.Serialize(new StaticLeaseRequest("B8:A4:4F:63:13:39", "10.0.0.48", "Lobby")), default)).Saved);

        await _plugin.DisposeAsync();
        Assert.Equal(0, _network.OpenSockets(67));
        _plugin = await StartAsync();

        Assert.True(Service.IsRunning);
        Assert.True(Service.Config.Enabled);
        Assert.Equal("10.0.0.100", Service.Config.RangeStart);
        var leases = Service.GetState(full: true).Leases!;
        Assert.Equal(2, leases.Count);
        Assert.Contains(leases, l => l is { Mac: "AC:CC:8E:5F:60:71", Address: "10.0.0.100", HostName: "cam", State: LeaseInfo.Active });
        Assert.Contains(leases, l => l is { Mac: "B8:A4:4F:63:13:39", Address: "10.0.0.48", Name: "Lobby", IsStatic: true });

        // The same device gets the same address after the restart.
        await using var again = new TestClient(_network, Mac.Of("AC:CC:8E:5F:60:71"));
        Assert.Equal(Ip.Of("10.0.0.100"), await again.AcquireAsync());
    }

    [Fact]
    public async Task Lease_actions_from_the_page()
    {
        await Save(Options.Enable());
        await using (var client = new TestClient(_network, Mac.Of("AC:CC:8E:5F:60:71")))
        {
            await client.AcquireAsync("cam");
        }

        var make = DhcpJson.Deserialize<StaticLeaseReply>(await _plugin.InvokeAsync(DhcpServerMethods.MakeStatic, DhcpJson.Serialize(new LeaseRequest("AC:CC:8E:5F:60:71")), default));
        Assert.True(make.Saved);
        Assert.True(Service.Leases.Find(Mac.Of("AC:CC:8E:5F:60:71"))!.IsStatic);

        var collide = DhcpJson.Deserialize<StaticLeaseReply>(await _plugin.InvokeAsync(DhcpServerMethods.SaveStatic,
            DhcpJson.Serialize(new StaticLeaseRequest("B8:A4:4F:63:13:39", "10.0.0.100", null)), default));
        Assert.False(collide.Saved);
        Assert.Equal("Already reserved for AC:CC:8E:5F:60:71.", collide.FieldErrors!["Address"]);

        var invalid = DhcpJson.Deserialize<StaticLeaseReply>(await _plugin.InvokeAsync(DhcpServerMethods.SaveStatic,
            DhcpJson.Serialize(new StaticLeaseRequest("B8:A4", "192.168.1.5", new string('x', 64))), default));
        Assert.Equal("Enter a MAC address, e.g. B8:A4:4F:63:13:39.", invalid.FieldErrors!["Mac"]);
        Assert.Equal("Must be inside the subnet 10.0.0.0/24.", invalid.FieldErrors["Address"]);
        Assert.Equal("At most 63 characters.", invalid.FieldErrors["Name"]);

        await _plugin.InvokeAsync(DhcpServerMethods.DeleteStatic, DhcpJson.Serialize(new LeaseRequest("AC:CC:8E:5F:60:71")), default);
        Assert.Null(Service.Leases.Find(Mac.Of("AC:CC:8E:5F:60:71")));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => _plugin.InvokeAsync(DhcpServerMethods.Release, DhcpJson.Serialize(new LeaseRequest("AC:CC:8E:5F:60:71")), default));
        await Assert.ThrowsAsync<ArgumentException>(() => _plugin.InvokeAsync(DhcpServerMethods.Release, DhcpJson.Serialize(new LeaseRequest("nope")), default));
        await Assert.ThrowsAsync<ArgumentException>(() => _plugin.InvokeAsync("nope", null, default));
    }

    [Fact]
    public async Task Another_DHCP_server_needs_a_confirmation_then_the_status_says_running()
    {
        await using var router = new FakeOtherServer(_network);
        var reply = await Save(Options.Enable());
        Assert.False(reply.Saved);
        Assert.Equal(["10.0.0.1"], reply.OtherServers);
        Assert.False(Service.IsRunning);
        Assert.False(Service.Config.Enabled);
        Assert.True(router.Discovers >= 1);

        // "Enable anyway": the confirmed server no longer turns the status into a warning.
        reply = await Save(Options.Enable(confirmed: ["10.0.0.1"]));
        Assert.True(reply.Saved);
        Assert.True(Service.IsRunning);
        Assert.Equal(ServiceStatus.Ok, reply.State.Status.Kind);
        Assert.StartsWith("Running on ", reply.State.Status.Text, StringComparison.Ordinal);
        Assert.Equal(["10.0.0.1"], Service.Config.AcceptedOtherServers);

        // Disable and enable again on the same interface: no second question for the accepted server.
        Assert.True((await Save(Options.Enable() with { Enabled = false })).Saved);
        reply = await Save(Options.Enable());
        Assert.True(reply.Saved);
        Assert.Equal(ServiceStatus.Ok, reply.State.Status.Kind);
    }

    [Fact]
    public async Task Without_another_server_enabling_needs_no_confirmation_and_the_own_server_is_not_counted()
    {
        Assert.True((await Save(Options.Enable())).Saved);
        var check = new OtherServerCheck(_network) { Wait = TimeSpan.FromMilliseconds(300) };
        var result = await check.RunAsync(new DhcpBinding("Ethernet", 7, IPAddress.Parse("10.0.0.17")), default);
        Assert.Empty(result.Servers);
        Assert.Null(result.Error);
        Assert.Empty(Service.Leases.Snapshot(out _)); // the own server ignored the probe (no offer held for it)
    }

    [Fact]
    public async Task Port_in_use_is_shown_and_retried()
    {
        _network.FailPorts[67] = SocketError.AddressAlreadyInUse;
        var reply = await Save(Options.Enable());
        Assert.True(reply.Saved);
        Assert.Equal(ServiceStatus.Error, reply.State.Status.Kind);
        Assert.Equal("Port 67 is in use by another program", reply.State.Status.Text);

        _network.FailPorts.Clear();
        await Wait.UntilAsync(() => Service.IsRunning);
        Assert.Equal("Running on Ethernet (10.0.0.17/24)", Service.GetState(false).Status.Text);
    }

    [Fact]
    public async Task On_Windows_the_service_holding_port_67_is_named()
    {
        var services = new FakeServices();
        services.Running.Add("SharedAccess");
        var network = new FakeDhcpNetwork();
        network.FailPorts[67] = SocketError.AccessDenied;
        await using var plugin = new DhcpServerPlugin(Options.Test(network) with { Os = HostOs.Windows, WindowsServices = services });
        await plugin.StartAsync(new TestCoreContext(new InMemoryPluginSettingsProvider().GetSettings(DhcpServerPluginInfo.PluginId), null), default);
        var reply = DhcpJson.Deserialize<DhcpSaveReply>(await plugin.InvokeAsync(DhcpServerMethods.Save, DhcpJson.Serialize(Options.Enable()), default));
        Assert.Equal("Port 67 is in use by another program", reply.State.Status.Text);
        Assert.StartsWith("The Internet Connection Sharing service is running", reply.State.Status.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_changed_interface_address_stops_serving_with_the_range_error()
    {
        var interfaces = new FakeInterfaces();
        await _plugin.DisposeAsync();
        _plugin = await StartAsync(interfaces);
        await Save(Options.Enable());
        Assert.True(Service.IsRunning);

        interfaces.Items[0] = FakeInterfaces.Ethernet("192.0.2.17");
        // The listener stops first and the range error is set right after it: wait for the error, not for the stop.
        await Wait.UntilAsync(() => !Service.IsRunning && Service.GetState(false).Status.Text == "Range is not inside the interface subnet");
        var status = Service.GetState(false).Status;
        Assert.Equal("Range is not inside the interface subnet", status.Text);
        Assert.Equal(ServiceStatus.Error, status.Kind);

        interfaces.Items.RemoveAt(0);
        await Wait.UntilAsync(() => Service.GetState(false).Status.Text == "Interface Ethernet is not available");
    }

    [Fact]
    public async Task Pool_exhausted_is_a_warning()
    {
        await Save(Options.Enable("10.0.0.100", "10.0.0.101"));
        for (var i = 0; i < 2; i++)
        {
            await using var client = new TestClient(_network, 0x0040_8C00_0000UL + (ulong)i);
            Assert.NotNull(await client.AcquireAsync());
        }

        await using var late = new TestClient(_network, Mac.Of("00:40:8C:00:00:99"));
        Assert.Null(await late.DiscoverAsync());
        var status = Service.GetState(false).Status;
        Assert.Equal("Address pool exhausted", status.Text);
        Assert.Equal(ServiceStatus.Warning, status.Kind);
    }

    [Fact]
    public async Task A_flood_from_one_device_is_rate_limited()
    {
        await Save(Options.Enable());
        await using var client = new TestClient(_network, Mac.Of("AC:CC:8E:5F:60:71"));
        var answered = 0;
        for (var i = 0; i < 30; i++)
        {
            var inform = client.New(DhcpMessageType.Inform);
            inform.ClientAddress = Ip.Of("10.0.0.60");
            if (await client.SendAsync(inform, wait: TimeSpan.FromMilliseconds(100)) is not null)
            {
                answered++;
            }
        }

        Assert.Equal(10, answered); // burst of 10, then one every 3 s
    }

    [Fact]
    public async Task Malformed_datagrams_never_stop_the_server()
    {
        await Save(Options.Enable());
        await using var junk = _network.Add(IPAddress.Any, 68);
        for (var i = 0; i < 100; i++)
        {
            await junk.SendAsync(new byte[i * 3], new IPEndPoint(IPAddress.Broadcast, 67), default);
        }

        await using var client = new TestClient(_network, Mac.Of("AC:CC:8E:5F:60:71"));
        Assert.NotNull(await client.AcquireAsync());
    }

    private async Task<DhcpSaveReply> Save(DhcpSaveRequest request) =>
        DhcpJson.Deserialize<DhcpSaveReply>(await _plugin.InvokeAsync(DhcpServerMethods.Save, DhcpJson.Serialize(request), default));

    private async Task<DhcpServerPlugin> StartAsync(FakeInterfaces? interfaces = null)
    {
        var plugin = new DhcpServerPlugin(Options.Test(_network, _probe, interfaces));
        await plugin.StartAsync(new TestCoreContext(_settingsProvider.GetSettings(DhcpServerPluginInfo.PluginId), _hub.For(DhcpServerPluginInfo.PluginId)), default);
        return plugin;
    }
}

public sealed class RateLimitTests
{
    [Fact]
    public void Per_mac_burst_of_10_then_one_every_3_seconds_with_one_log_line_per_minute()
    {
        var time = Clock.New();
        var limiter = new KeyedRateLimiter<ulong>(new DhcpRateLimits().PerMac, time);
        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(RateLimitDecision.Allow, limiter.Check(1).Decision);
        }

        var first = limiter.Check(1);
        Assert.Equal(RateLimitDecision.Drop, first.Decision);
        Assert.True(first.IsDue(0));
        Assert.False(limiter.Check(1).IsDue(0));
        Assert.Equal(RateLimitDecision.Allow, limiter.Check(2).Decision);
        time.Advance(TimeSpan.FromSeconds(3.1));
        Assert.Equal(RateLimitDecision.Allow, limiter.Check(1).Decision);
    }

    [Fact]
    public void Global_cap_and_LRU_table()
    {
        var time = Clock.New();
        var limiter = new KeyedRateLimiter<ulong>(new DhcpRateLimits().PerMac with { MaxClients = 100 }, time);
        var allowed = 0;
        for (ulong i = 0; i < 1_000; i++)
        {
            if (limiter.Check(i).Decision == RateLimitDecision.Allow)
            {
                allowed++;
            }
        }

        Assert.Equal(500, allowed);
        Assert.True(limiter.GloballyLimitedWithin(TimeSpan.FromSeconds(1)));
        Assert.Equal(100, limiter.ClientCount);
    }
}

public sealed class StatusTests
{
    [Fact]
    public void Port_in_use_on_Windows_names_the_DHCP_Server_role_or_ICS()
    {
        var known = DhcpStatusTexts.ForBindError(SocketError.AddressAlreadyInUse, 67, "Ethernet", HostOs.Windows, "Windows DHCP Server");
        Assert.Equal("Port 67 is in use by another program", known.Text);
        Assert.Contains("Windows DHCP Server service is running", known.Detail, StringComparison.Ordinal);

        var unknown = DhcpStatusTexts.ForBindError(SocketError.AccessDenied, 67, "Ethernet", HostOs.Windows);
        Assert.Equal("Port 67 is in use by another program", unknown.Text); // no privileged ports on Windows
        Assert.Contains("Internet Connection Sharing", unknown.Detail, StringComparison.Ordinal);
        Assert.Contains("netstat -abno -p UDP", unknown.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HostOs.Linux, "dnsmasq")]
    [InlineData(HostOs.MacOs, "bootpd")]
    public void Port_in_use_on_Linux_and_macOS_names_the_usual_programs(HostOs os, string hint)
    {
        var status = DhcpStatusTexts.ForBindError(SocketError.AddressAlreadyInUse, 67, "eth0", os);
        Assert.Equal("Port 67 is in use by another program", status.Text);
        Assert.Contains(hint, status.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Permission_on_Linux_and_macOS()
    {
        var linux = DhcpStatusTexts.ForBindError(SocketError.AccessDenied, 67, "eth0", HostOs.Linux);
        Assert.Equal("Insufficient permission to use port 67", linux.Text);
        Assert.Contains("cap_net_bind_service,cap_net_raw=+ep", linux.Detail, StringComparison.Ordinal);
        var mac = DhcpStatusTexts.ForBindError(SocketError.AccessDenied, 67, "en0", HostOs.MacOs);
        Assert.Equal("Insufficient permission to use port 67", mac.Text);
        Assert.Contains("sudo", mac.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HostOs.Windows)]
    [InlineData(HostOs.Linux)]
    [InlineData(HostOs.MacOs)]
    public void Address_not_available_means_the_interface_is_gone(HostOs os) =>
        Assert.Equal("Interface Ethernet is not available", DhcpStatusTexts.ForBindError(SocketError.AddressNotAvailable, 67, "Ethernet", os).Text);

    [Fact]
    public void Plain_texts()
    {
        Assert.Equal("Running on Ethernet (10.0.0.17/24)", DhcpStatusTexts.Running("Ethernet", "10.0.0.17", 24).Text);
        Assert.Equal(new ServiceStatus(ServiceStatus.Neutral, "Stopped"), DhcpStatusTexts.Stopped);
        Assert.Equal("Another DHCP server answers on this network (10.0.0.1)", DhcpStatusTexts.OtherServer(["10.0.0.1"]).Text);
        Assert.Equal("Address pool exhausted", DhcpStatusTexts.PoolExhausted.Text);
        Assert.Equal("Range is not inside the interface subnet", DhcpStatusTexts.RangeOutsideSubnet("10.0.0.0/24").Text);
        Assert.Equal(ServiceStatus.Warning, DhcpStatusTexts.OtherServer(["10.0.0.1"]).Kind);
    }
}

/// <summary>The real UDP socket on the loopback address and random ports: replies never leave the machine.</summary>
public sealed class LoopbackSocketTests
{
    [Fact]
    public async Task Real_socket_answers_inform_and_discover_on_loopback()
    {
        var serverPort = FreeUdpPort();
        var clientPort = FreeUdpPort();
        var network = new DhcpNetworkInfo("lo-id", "Loopback", "127.0.0.1", 8, null, [], null);
        var store = new Leases.LeaseStore();
        using var engine = new DhcpEngine(store, new FakeProbe(), new DhcpEngineOptions { ClientPort = clientPort })
        {
            Scope = DhcpScope.Create(network, Ip.Of("127.0.0.100"), Ip.Of("127.0.0.110")),
        };
        var socket = UdpDhcpSocketFactory.Instance.Open(new DhcpBinding("Loopback", -1, IPAddress.Loopback), serverPort, reusePort: false);
        await using var listener = new DhcpListener(socket, engine, new KeyedRateLimiter<ulong>(new DhcpRateLimits().PerMac), 8);
        listener.Start();

        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        client.Bind(new IPEndPoint(IPAddress.Loopback, clientPort));
        var bytes = new byte[DhcpMessage.MaxLength];

        var inform = new DhcpMessage { TransactionId = 42, MessageType = DhcpMessageType.Inform, ClientAddress = Ip.Of("127.0.0.1"), Mac = Mac.Of("AC:CC:8E:5F:60:71") };
        await client.SendToAsync(bytes.AsMemory(0, inform.Write(bytes)), SocketFlags.None, new IPEndPoint(IPAddress.Loopback, serverPort));
        var ack = await ReceiveAsync(client);
        Assert.Equal(DhcpMessageType.Ack, ack.MessageType);
        Assert.Equal(42u, ack.TransactionId);
        Assert.Equal(Ip.Of("255.0.0.0"), ack.SubnetMask);

        // The broadcast reply of a discover stays on loopback (rewritten to 127.0.0.1).
        var discover = new DhcpMessage { TransactionId = 43, MessageType = DhcpMessageType.Discover, Mac = Mac.Of("AC:CC:8E:5F:60:71") };
        await client.SendToAsync(bytes.AsMemory(0, discover.Write(bytes)), SocketFlags.None, new IPEndPoint(IPAddress.Loopback, serverPort));
        var offer = await ReceiveAsync(client);
        Assert.Equal(DhcpMessageType.Offer, offer.MessageType);
        Assert.Equal(Ip.Of("127.0.0.100"), offer.YourAddress);
    }

    [Fact]
    public void Binding_a_port_twice_fails_with_address_in_use()
    {
        var port = FreeUdpPort();
        using var holder = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ExclusiveAddressUse = OperatingSystem.IsWindows() };
        holder.Bind(new IPEndPoint(IPAddress.Loopback, port));
        var ex = Assert.Throws<SocketException>(() => UdpDhcpSocketFactory.Instance.Open(new DhcpBinding("Loopback", -1, IPAddress.Loopback), port, reusePort: false));
        Assert.Equal(BindErrorKind.InUse, PortBindErrors.Classify(ex.SocketErrorCode, HostOsInfo.Current));
    }

    private static async Task<DhcpMessage> ReceiveAsync(Socket socket)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var buffer = new byte[DhcpMessage.MaxLength];
        var received = await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), cts.Token);
        Assert.True(DhcpMessage.TryRead(buffer.AsSpan(0, received.ReceivedBytes), out var message, out var error), error);
        return message!;
    }

    private static int FreeUdpPort()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }
}
