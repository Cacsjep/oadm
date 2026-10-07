using System.Text.Json;

using Oadm.Plugins.Network.Model;
using Oadm.Plugins.Network.Vapix;

namespace Oadm.Plugins.Network.Tests;

/// <summary>Parsing of read-only responses recorded on an AXIS P3265-V, AXIS OS 12.11.77 (network-settings 1.37).</summary>
public sealed class ParserTests
{
    [Fact]
    public void Parses_recorded_getNetworkInfo()
    {
        var s = NetworkInfoParser.ParseGetNetworkInfo(Fixture.Read(Fixture.GetNetworkInfo), "10.0.0.48");

        Assert.Equal("network-settings 1.38", s.Source);
        Assert.Equal("eth0", s.InterfaceName);
        Assert.Equal("dhcp", s.Ipv4.Mode);
        Assert.Equal("10.0.0.48", s.Ipv4.Address);
        Assert.Equal(24, s.Ipv4.PrefixLength);
        Assert.Equal("10.0.0.138", s.Ipv4.Gateway);
        Assert.Equal("192.168.0.90", s.Ipv4.StaticAddress);
        Assert.Equal(24, s.Ipv4.StaticPrefixLength);
        Assert.Equal("192.168.0.1", s.Ipv4.StaticGateway);
        Assert.Equal(1, s.Ipv4.MaxStaticAddresses);

        Assert.True(s.Ipv6.Supported);
        Assert.False(s.Ipv6.Enabled);
        Assert.Empty(s.Ipv6.Addresses);

        Assert.False(s.Dns.UseDhcp);
        Assert.Equal(["1.1.1.1"], s.Dns.Servers);
        Assert.Equal(["1.1.1.1"], s.Dns.StaticServers);
        Assert.Equal(3, s.Dns.MaxStaticServers);
        Assert.Equal(6, s.Dns.MaxStaticSearchDomains);
        Assert.Null(s.Dns.DomainName);

        Assert.True(s.HostName.UseDhcp);
        Assert.Equal("axis-accc8e000001", s.HostName.HostName);
        Assert.Equal("axis-accc8e000001", s.HostName.StaticHostName);
    }

    [Fact]
    public void Adds_ipv6_mode_from_recorded_parameters()
    {
        var s = NetworkInfoParser.ParseGetNetworkInfo(Fixture.Read(Fixture.GetNetworkInfo), "10.0.0.48");
        var merged = NetworkInfoParser.WithIpv6Parameters(s, Fixture.Parameters(Fixture.ParamNetwork));

        Assert.Equal("auto", merged.Ipv6.Mode); // AcceptRA=yes, DHCPv6=auto
        Assert.Empty(merged.Ipv6.StaticAddresses);
        Assert.Null(merged.Ipv6.Gateway);
    }

    [Fact]
    public void Parses_recorded_param_cgi_network_group()
    {
        var s = NetworkInfoParser.ParseParameters(Fixture.Parameters(Fixture.ParamNetwork));

        Assert.Equal("param.cgi", s.Source);
        Assert.Null(s.InterfaceName);
        Assert.Equal("dhcp", s.Ipv4.Mode);
        Assert.Equal("10.0.0.48", s.Ipv4.Address);
        Assert.Equal(24, s.Ipv4.PrefixLength);
        Assert.Equal("10.0.0.138", s.Ipv4.Gateway);
        Assert.Equal("192.168.0.90", s.Ipv4.StaticAddress);
        Assert.Equal("192.168.0.1", s.Ipv4.StaticGateway);
        Assert.False(s.Ipv6.Enabled);
        Assert.Equal("auto", s.Ipv6.Mode);
        Assert.False(s.Dns.UseDhcp);
        Assert.Equal(["1.1.1.1"], s.Dns.Servers);
        Assert.Equal(["1.1.1.1"], s.Dns.StaticServers); // DNSServer2=0.0.0.0 means "not set"
        Assert.True(s.HostName.UseDhcp);
        Assert.Equal("axis-accc8e000001", s.HostName.HostName);
    }

    [Theory]
    [InlineData("yes", "auto", "auto")]
    [InlineData("yes", "stateful", "dhcp")]
    [InlineData("no", "off", "static")]
    [InlineData("yes", "off", "auto")]
    public void Derives_ipv6_mode(string acceptRa, string dhcpv6, string expected)
    {
        var p = new Dictionary<string, string> { ["Network.IPv6.AcceptRA"] = acceptRa, ["Network.IPv6.DHCPv6"] = dhcpv6 };
        Assert.Equal(expected, NetworkInfoParser.Ipv6ModeFromParameters(p));
    }

    [Fact]
    public void Static_legacy_device_parses()
    {
        var p = new Dictionary<string, string>
        {
            ["Network.BootProto"] = "none",
            ["Network.IPAddress"] = "192.168.1.20",
            ["Network.SubnetMask"] = "255.255.255.0",
            ["Network.DefaultRouter"] = "192.168.1.1",
            ["Network.eth0.IPAddress"] = "192.168.1.20",
            ["Network.eth0.SubnetMask"] = "255.255.255.0",
            ["Network.IPv6.Enabled"] = "yes",
            ["Network.IPv6.AcceptRA"] = "no",
            ["Network.IPv6.DHCPv6"] = "off",
            ["Network.IPv6.IPAddress"] = "2001:db8::20/64",
            ["Network.IPv6.DefaultRouter"] = "fe80::1",
        };
        var s = NetworkInfoParser.ParseParameters(p);
        Assert.Equal("static", s.Ipv4.Mode);
        Assert.Equal(24, s.Ipv4.StaticPrefixLength);
        Assert.True(s.Ipv6.Enabled);
        Assert.Equal("static", s.Ipv6.Mode);
        Assert.Equal(["2001:db8::20/64"], s.Ipv6.StaticAddresses);
        Assert.Equal("fe80::1", s.Ipv6.Gateway);
    }

    [Fact]
    public void Recorded_error_response_becomes_api_exception()
    {
        using var doc = JsonDocument.Parse(Fixture.Read("network-settings-error-4001-P3265-V-12.11.json"));
        var ex = Assert.Throws<NetworkApiException>(() => NetworkSettingsClient.ThrowIfError(doc.RootElement, "getNetworkInfo"));
        Assert.Equal(4001, ex.Code);
        Assert.Contains("The specified version is not supported", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Recorded_supported_versions_differ_from_api_discovery()
    {
        // apidiscovery lists network-settings 1.37, while the CGI reports 1.38 (field "apiVersions", not the
        // documented "supportedVersions"). The plugin therefore sends the version from the API list.
        using var doc = JsonDocument.Parse(Fixture.Read("network-settings-getSupportedVersions-P3265-V-12.11.json"));
        Assert.Equal("1.38", doc.RootElement.GetProperty("data").GetProperty("apiVersions")[0].GetString());
    }

    [Fact]
    public void Current_settings_round_trip_as_query_json()
    {
        var s = NetworkInfoParser.ParseGetNetworkInfo(Fixture.Read(Fixture.GetNetworkInfo), null);
        var back = CurrentNetworkSettings.FromJson(s.ToJson());
        Assert.Equal(s.Ipv4, back.Ipv4);
        Assert.Equal(s.Dns.Servers, back.Dns.Servers);
        Assert.Equal("eth0", back.InterfaceName);
    }
}
