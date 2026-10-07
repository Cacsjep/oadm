using Oadm.Plugins.Network.Model;
using Oadm.Plugins.Network.Vapix;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Network.Tests;

/// <summary>Exact requests per API version and the write order.</summary>
public sealed class RequestBuildingTests
{
    private static readonly Guid Id = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static readonly CurrentNetworkSettings Current =
        NetworkInfoParser.WithIpv6Parameters(
            NetworkInfoParser.ParseGetNetworkInfo(Fixture.Read(Fixture.GetNetworkInfo), "10.0.0.48"),
            Fixture.Parameters(Fixture.ParamNetwork));

    private static readonly CurrentNetworkSettings LegacyCurrent = NetworkInfoParser.ParseParameters(Fixture.Parameters(Fixture.ParamNetwork));

    private static NetworkPayload Everything(Ipv6Change? ipv6 = null) => new()
    {
        Ipv4 = new Ipv4Change(Ipv4Mode.Static, 24, "10.0.0.138"),
        Ipv6 = ipv6 ?? new Ipv6Change(Ipv6Mode.Static, "2001:db8::48", 64, "fe80::1"),
        Dns = new DnsChange(false, ["10.0.0.2", "1.1.1.1"], "example.com", ["example.com", "lab.example.com"]),
        HostName = new HostNameChange(false),
        Devices = new Dictionary<Guid, DeviceAssignment> { [Id] = new("10.0.0.60", "cam-01") },
    };

    private static string[] Bodies(NetworkPlan plan) => plan.Steps.SelectMany(s => s.Requests).Select(r => r.Body).ToArray();

    [Fact]
    public void Network_settings_1_37_uses_json_methods_with_the_listed_version()
    {
        var plan = NetworkPlanner.Build(Everything(), Id, Fixture.Modern, Current, "10.0.0.48");

        Assert.Equal([StepKind.HostName, StepKind.Dns, StepKind.Ipv6, StepKind.Ipv4], plan.Steps.Select(s => s.Kind));
        Assert.Equal(
        [
            """{"apiVersion":"1.37","context":"oadm","method":"setHostnameConfiguration","params":{"useDhcpHostname":false,"staticHostname":"cam-01"}}""",
            """{"apiVersion":"1.37","context":"oadm","method":"setResolverConfiguration","params":{"useDhcpResolverInfo":false,"staticNameServers":["10.0.0.2","1.1.1.1"],"staticDomainName":"example.com","staticSearchDomains":["example.com","lab.example.com"]}}""",
            "action=update&Network.IPv6.AcceptRA=no&Network.IPv6.DHCPv6=off&Network.IPv6.IPAddress=2001%3Adb8%3A%3A48%2F64&Network.IPv6.DefaultRouter=fe80%3A%3A1",
            """{"apiVersion":"1.37","context":"oadm","method":"setIPv6AddressConfiguration","params":{"deviceName":"eth0","enabled":true}}""",
            """{"apiVersion":"1.37","context":"oadm","method":"setIPv4AddressConfiguration","params":{"deviceName":"eth0","configurationMode":"static","staticDefaultRouter":"10.0.0.138","staticAddressConfigurations":[{"address":"10.0.0.60","prefixLength":24}]}}""",
        ],
        Bodies(plan));
        Assert.All(plan.Steps.SelectMany(s => s.Requests).OfType<JsonMethodRequest>(), r => Assert.Equal("axis-cgi/network_settings.cgi", r.Path));
        Assert.All(plan.Steps.SelectMany(s => s.Requests).OfType<ParamUpdateRequest>(), r => Assert.Equal("application/x-www-form-urlencoded", r.ContentType));
    }

    [Fact]
    public void Network_settings_below_1_6_toggles_ipv6_with_param_cgi()
    {
        var plan = NetworkPlanner.Build(Everything(new Ipv6Change(Ipv6Mode.Auto)), Id, Fixture.NetworkSettings15, Current, "10.0.0.48");
        var ipv6 = plan.Steps.Single(s => s.Kind == StepKind.Ipv6);
        Assert.Equal(["action=update&Network.IPv6.AcceptRA=yes&Network.IPv6.DHCPv6=auto&Network.IPv6.Enabled=yes"], ipv6.Requests.Select(r => r.Body));
        Assert.StartsWith("""{"apiVersion":"1.5",""", plan.Steps.Single(s => s.Kind == StepKind.Ipv4).Requests[0].Body, StringComparison.Ordinal);
    }

    [Fact]
    public void Disabling_ipv6_uses_the_json_toggle_from_1_6()
    {
        var payload = new NetworkPayload { Ipv6 = new Ipv6Change(Ipv6Mode.Disabled), Devices = new Dictionary<Guid, DeviceAssignment> { [Id] = new() } };
        var plan = NetworkPlanner.Build(payload, Id, Fixture.Modern, Current, "10.0.0.48");
        Assert.Equal(["""{"apiVersion":"1.37","context":"oadm","method":"setIPv6AddressConfiguration","params":{"deviceName":"eth0","enabled":false}}"""], Bodies(plan));

        var legacy = NetworkPlanner.Build(payload, Id, Fixture.NetworkSettings15, Current, "10.0.0.48");
        Assert.Equal(["action=update&Network.IPv6.Enabled=no"], Bodies(legacy));
    }

    [Fact]
    public void Dhcp_sections_only_switch_the_source()
    {
        var payload = new NetworkPayload
        {
            Ipv4 = new Ipv4Change(Ipv4Mode.Dhcp),
            Dns = new DnsChange(true),
            HostName = new HostNameChange(true),
            Devices = new Dictionary<Guid, DeviceAssignment> { [Id] = new() },
        };
        var plan = NetworkPlanner.Build(payload, Id, Fixture.Modern, Current, "10.0.0.48");
        Assert.Equal(
        [
            """{"apiVersion":"1.37","context":"oadm","method":"setHostnameConfiguration","params":{"useDhcpHostname":true}}""",
            """{"apiVersion":"1.37","context":"oadm","method":"setResolverConfiguration","params":{"useDhcpResolverInfo":true}}""",
            """{"apiVersion":"1.37","context":"oadm","method":"setIPv4AddressConfiguration","params":{"deviceName":"eth0","configurationMode":"dhcp"}}""",
        ],
        Bodies(plan));
    }

    [Fact]
    public void Legacy_device_gets_param_cgi_updates_with_address_change_last()
    {
        var payload = Everything(new Ipv6Change(Ipv6Mode.Dhcp)) with { Dns = new DnsChange(false, ["10.0.0.2"], "example.com") };
        var plan = NetworkPlanner.Build(payload, Id, Fixture.LegacyOnly, LegacyCurrent, "10.0.0.48");
        Assert.Equal(
        [
            "action=update&Network.HostName=cam-01&Network.VolatileHostName.ObtainFromDHCP=no",
            "action=update&Network.Resolver.ObtainFromDHCP=no&Network.DNSServer1=10.0.0.2&Network.DNSServer2=0.0.0.0&Network.DomainName=example.com",
            "action=update&Network.IPv6.AcceptRA=yes&Network.IPv6.DHCPv6=stateful&Network.IPv6.Enabled=yes",
            "action=update&Network.IPAddress=10.0.0.60&Network.SubnetMask=255.255.255.0&Network.Broadcast=10.0.0.255&Network.DefaultRouter=10.0.0.138&Network.BootProto=none",
        ],
        Bodies(plan));
        Assert.All(plan.Steps.SelectMany(s => s.Requests), r => Assert.Equal("axis-cgi/param.cgi", r.Path));
    }

    [Fact]
    public void Legacy_device_cannot_set_search_domains()
    {
        var ex = Assert.Throws<DeviceNotCompatibleException>(() => NetworkPlanner.Build(Everything(), Id, Fixture.LegacyOnly, LegacyCurrent, "10.0.0.48"));
        Assert.Contains("Search domains", ex.Message, StringComparison.Ordinal);
        Assert.EndsWith("Nothing was changed.", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Ipv6_address_mode_requires_param_cgi()
    {
        var ex = Assert.Throws<DeviceNotCompatibleException>(() => NetworkPlanner.Build(Everything(), Id, Fixture.JsonOnly, Current, "10.0.0.48"));
        Assert.Equal("param-cgi", ex.ApiId);
    }

    [Fact]
    public void Device_without_any_network_api_is_rejected()
    {
        var ex = Assert.Throws<DeviceNotCompatibleException>(() => NetworkPlanner.Build(Everything(), Id, [new DeviceApi("network-settings", "2.0")], Current, "10.0.0.48"));
        Assert.Equal("param-cgi", ex.ApiId);
    }

    [Fact]
    public void Device_limits_are_checked_before_writing()
    {
        var limited = Current with { Dns = Current.Dns with { MaxStaticServers = 1 } };
        Assert.Throws<NetworkValidationException>(() => NetworkPlanner.Build(Everything(), Id, Fixture.Modern, limited, "10.0.0.48"));
    }

    [Fact]
    public void Ipv6_connection_writes_ipv4_first_and_ipv6_last()
    {
        var plan = NetworkPlanner.Build(Everything(), Id, Fixture.Modern, Current, "2001:db8::47");
        Assert.Equal([StepKind.HostName, StepKind.Dns, StepKind.Ipv4, StepKind.Ipv6], plan.Steps.Select(s => s.Kind));
        Assert.True(plan.Impact.Readdressed);
        Assert.Equal("2001:db8::48", plan.Impact.NewAddress);
    }

    [Theory]
    [InlineData("10.0.0.60", true)]
    [InlineData("10.0.0.48", false)]
    public void Static_ipv4_reports_readdressing(string newAddress, bool readdressed)
    {
        var payload = new NetworkPayload
        {
            Ipv4 = new Ipv4Change(Ipv4Mode.Static, 24, "10.0.0.138"),
            Devices = new Dictionary<Guid, DeviceAssignment> { [Id] = new(newAddress) },
        };
        var plan = NetworkPlanner.Build(payload, Id, Fixture.Modern, Current, "10.0.0.48");
        Assert.True(plan.Impact.Affected);
        Assert.Equal(readdressed, plan.Impact.Readdressed);
        Assert.Equal("10.0.0.48", plan.Impact.OldAddress);
    }

    [Fact]
    public void Dhcp_on_a_dhcp_device_is_not_a_readdress()
    {
        var payload = new NetworkPayload { Ipv4 = new Ipv4Change(Ipv4Mode.Dhcp), Devices = new Dictionary<Guid, DeviceAssignment> { [Id] = new() } };
        var plan = NetworkPlanner.Build(payload, Id, Fixture.Modern, Current, "10.0.0.48");
        Assert.False(plan.Impact.Readdressed);

        var wasStatic = Current with { Ipv4 = Current.Ipv4 with { Mode = "static" } };
        var plan2 = NetworkPlanner.Build(payload, Id, Fixture.Modern, wasStatic, "10.0.0.48");
        Assert.True(plan2.Impact.Readdressed);
        Assert.Null(plan2.Impact.NewAddress);
    }

    [Fact]
    public void Missing_device_entry_is_rejected()
    {
        Assert.Throws<NetworkValidationException>(() => NetworkPlanner.Build(Everything(), Guid.NewGuid(), Fixture.Modern, Current, "10.0.0.48"));
    }
}
