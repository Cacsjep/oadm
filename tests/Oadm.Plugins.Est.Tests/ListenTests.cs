using System.Net;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Time.Testing;

using Oadm.Plugins.Est.Serving;
using Oadm.Plugins.Est.Tasks;
using Oadm.Sdk.Network;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Est.Tests;

/// <summary>"Listen on": every address, or one interface of the server (like the NTP and DHCP server pages).</summary>
public sealed class ListenTests : IAsyncLifetime
{
    private static readonly ServerNetworkInterface Lan = new(
        "lan", "Ethernet", "Intel I219", [IPAddress.Parse("10.0.0.17"), IPAddress.Parse("fe80::1"), IPAddress.Parse("2001:db8::17")], true, false);

    private static readonly ServerNetworkInterface Cameras = new(
        "cams", "Ethernet 2", "Ethernet 2", [IPAddress.Parse("192.168.50.1")], true, false);

    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private readonly FakeAuthority _ca = new();
    private readonly FakeSettings _settings = new();
    private readonly FakePluginNetwork _network = new();
    private readonly Interfaces _interfaces = new() { List = [Lan, Cameras] };
    private EstService _est = null!;

    public async Task InitializeAsync() => _est = await StartAsync();

    public async Task DisposeAsync()
    {
        await _est.DisposeAsync();
        _ca.Dispose();
    }

    [Fact]
    public void By_default_it_listens_on_every_address_and_offers_the_interfaces()
    {
        Assert.Empty(_network.LastTcpAddresses);
        Assert.Equal([InterfaceOptions.AllId, "lan", "cams"], _est.GetState().Interfaces.Select(i => i.Id));
        Assert.Equal("Running on port 9454", _est.GetState().StatusText);
    }

    [Fact]
    public async Task One_interface_listens_on_its_addresses_only_without_link_local()
    {
        var reply = await _est.SaveAsync(new SaveRequest(_est.Store.Config with { InterfaceId = "lan" }), CancellationToken.None);

        Assert.Null(reply.Error);
        Assert.Equal(["10.0.0.17", "2001:db8::17"], _network.LastTcpAddresses.Select(a => a.ToString()));
        Assert.Equal("Ethernet", _est.Store.Config.InterfaceName);
        Assert.Equal(2, _network.Listeners.Count); // the old listener was released, a new one opened
        Assert.Equal("Running on port 9454", reply.State!.StatusText);
    }

    [Fact]
    public async Task An_interface_the_server_does_not_have_is_refused_below_the_field()
    {
        var reply = await _est.SaveAsync(new SaveRequest(_est.Store.Config with { InterfaceId = "wifi" }), CancellationToken.None);

        Assert.Equal(("interface", "This interface is not available on the server."), (reply.Field, reply.Error));
        Assert.Equal(InterfaceOptions.AllId, _est.Store.Config.InterfaceId);
    }

    [Fact]
    public async Task A_chosen_interface_that_is_gone_says_so_and_listens_again_when_it_is_back()
    {
        await _est.SaveAsync(new SaveRequest(_est.Store.Config with { InterfaceId = "cams" }), CancellationToken.None);
        await _est.DisposeAsync();
        _interfaces.List = [Lan];

        _est = await StartAsync();
        Assert.Null(_est.Listener);
        Assert.Equal("Interface Ethernet 2 is not available", _est.GetState().StatusText);

        _interfaces.List = [Lan, Cameras];
        _time.Advance(TimeSpan.FromSeconds(30));
        await Eventually.TrueAsync(() => _est.Listener is not null, "the listener opened again");
        Assert.Equal(["192.168.50.1"], _network.LastTcpAddresses.Select(a => a.ToString()));
    }

    [Fact]
    public async Task A_new_address_of_the_chosen_interface_is_listened_on_within_30_seconds()
    {
        await _est.SaveAsync(new SaveRequest(_est.Store.Config with { InterfaceId = "cams" }), CancellationToken.None);
        Assert.Equal(["192.168.50.1"], _network.LastTcpAddresses.Select(a => a.ToString()));

        _interfaces.List = [Lan, Cameras with { Addresses = [IPAddress.Parse("192.168.50.7")] }]; // a new DHCP lease
        _time.Advance(TimeSpan.FromSeconds(30));

        await Eventually.TrueAsync(() => _network.LastTcpAddresses.Select(a => a.ToString()).SequenceEqual(["192.168.50.7"]), "listening on the new address");
        Assert.NotNull(_est.Listener);
    }

    [Fact]
    public async Task A_chosen_interface_that_goes_away_while_listening_stops_and_says_so()
    {
        await _est.SaveAsync(new SaveRequest(_est.Store.Config with { InterfaceId = "cams" }), CancellationToken.None);
        Assert.NotNull(_est.Listener);

        _interfaces.List = [Lan]; // unplugged
        _time.Advance(TimeSpan.FromSeconds(30));

        await Eventually.TrueAsync(() => _est.Listener is null, "the listener closed");
        Assert.Equal("Interface Ethernet 2 is not available", _est.GetState().StatusText);
    }

    [Fact]
    public async Task Set_up_gives_the_camera_the_address_of_the_chosen_interface()
    {
        await _est.SaveAsync(new SaveRequest(_est.Store.Config with { InterfaceId = "cams", Profiles = [new EstProfile("https", ProfileUse.Https) { PolicyId = _est.Store.Config.Profiles[0].PolicyId }] }), CancellationToken.None);
        var device = new FakeDevice { Serial = "B8A44F631339", Address = "192.168.50.20", FirmwareVersion = "12.11.77" };
        var camera = new TaskTests.EstCamera { Server = _est, Device = device };
        var ctx = new FakeTaskContext(camera) { Network = new FakeDeviceNetwork { LocalAddress = IPAddress.Parse("10.0.0.17") } };
        _ca.NameAlso("192.168.50.1"); // the interface's address is in the EST server's certificate

        await new EstSetupTask(() => _est).ExecuteAsync(ctx, device, "{}", CancellationToken.None);

        Assert.Equal("192.168.50.1:9454/.well-known/est/https", Assert.Single(camera.Profiles.Values)["server"]!.GetValue<string>());
    }

    [Fact]
    public async Task Set_up_refuses_when_the_chosen_interface_has_no_address_of_the_cameras_family()
    {
        await _est.SaveAsync(new SaveRequest(_est.Store.Config with { InterfaceId = "cams" }), CancellationToken.None);
        var device = new FakeDevice { Serial = "B8A44F631339", Address = "2001:db8::48", FirmwareVersion = "12.11.77" };
        var camera = new TaskTests.EstCamera();
        var ctx = new FakeTaskContext(camera) { Network = new FakeDeviceNetwork { LocalAddress = IPAddress.Parse("2001:db8::17") } };

        var error = await Assert.ThrowsAsync<Sdk.Vapix.DeviceNotCompatibleException>(() => new EstSetupTask(() => _est).ExecuteAsync(ctx, device, "{}", CancellationToken.None));

        Assert.Equal(EstTaskIds.NoInterfaceAddress("Ethernet 2", System.Net.Sockets.AddressFamily.InterNetworkV6), error.Message);
        Assert.Empty(camera.Profiles);
    }

    private async Task<EstService> StartAsync()
    {
        // PKI's policies of a new server; the profiles name their issuing policies, as after the EST core plugin's start.
        var policies = await TestPolicies.EstStoreAsync();
        await EstPolicies.ConvertAsync(_settings, policies, CancellationToken.None);
        var est = new EstService(new EstHost(_settings, new ServerTests.Devices(), _network, () => _ca, () => true)
        {
            Time = _time,
            Interfaces = _interfaces,
            Policies = () => policies,
        });
        await est.StartAsync(CancellationToken.None);
        return est;
    }

    private sealed class Interfaces : IServerNetworkInterfaces
    {
        public IReadOnlyList<ServerNetworkInterface> List { get; set; } = [];

        IReadOnlyList<ServerNetworkInterface> IServerNetworkInterfaces.List() => List;
    }
}
