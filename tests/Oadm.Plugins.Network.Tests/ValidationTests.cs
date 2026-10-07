using Oadm.Plugins.Network.Model;

namespace Oadm.Plugins.Network.Tests;

public sealed class Ipv4Tests
{
    [Theory]
    [InlineData("24", 24)]
    [InlineData("/16", 16)]
    [InlineData("255.255.255.0", 24)]
    [InlineData("255.255.252.0", 22)]
    [InlineData("255.255.255.252", 30)]
    [InlineData("0.0.0.0", 0)]
    public void Parses_prefix_or_mask(string text, int expected)
    {
        Assert.True(Ipv4.TryParsePrefix(text, out var prefix));
        Assert.Equal(expected, prefix);
    }

    [Theory]
    [InlineData("255.0.255.0")]
    [InlineData("33")]
    [InlineData("abc")]
    [InlineData("")]
    public void Rejects_bad_masks(string text) => Assert.False(Ipv4.TryParsePrefix(text, out _));

    [Theory]
    [InlineData("10.5")]
    [InlineData("10.0.0.256")]
    [InlineData("10.0.0.01")]
    [InlineData("10.0.0")]
    [InlineData(" ")]
    public void Rejects_short_or_invalid_addresses(string text) => Assert.False(Ipv4.TryParse(text, out _));

    [Theory]
    [InlineData("10.0.0.0", 24, "network address")]
    [InlineData("10.0.0.255", 24, "broadcast address")]
    [InlineData("127.0.0.1", 8, "loopback")]
    [InlineData("224.0.0.5", 24, "multicast")]
    [InlineData("169.254.3.4", 16, "link-local")]
    [InlineData("0.0.0.0", 24, "not a usable")]
    public void Flags_unusable_host_addresses(string address, int prefix, string expected)
    {
        Assert.True(Ipv4.TryParse(address, out var value));
        Assert.Contains(expected, Ipv4.HostAddressProblem(value, prefix), StringComparison.Ordinal);
    }

    [Fact]
    public void Assigns_consecutive_addresses()
    {
        Assert.True(Ipv4.TryAssignRange("10.0.0.250", 24, 4, out var addresses, out var error));
        Assert.Null(error);
        Assert.Equal(["10.0.0.250", "10.0.0.251", "10.0.0.252", "10.0.0.253"], addresses);
    }

    [Fact]
    public void Range_crosses_octets_inside_a_larger_subnet()
    {
        Assert.True(Ipv4.TryAssignRange("172.16.0.254", 16, 3, out var addresses, out _));
        Assert.Equal(["172.16.0.254", "172.16.0.255", "172.16.1.0"], addresses);
    }

    [Fact]
    public void Range_must_not_reach_the_broadcast_address()
    {
        Assert.False(Ipv4.TryAssignRange("10.0.0.253", 24, 3, out _, out var error));
        Assert.Contains("room for 2 of 3", error, StringComparison.Ordinal);
    }
}

public sealed class PayloadValidatorTests
{
    private static readonly Guid A = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid B = Guid.Parse("00000000-0000-0000-0000-00000000000b");

    private static NetworkPayload StaticV4(string gateway, int prefix, params string?[] addresses) => new()
    {
        Ipv4 = new Ipv4Change(Ipv4Mode.Static, prefix, gateway),
        Devices = addresses.Select((a, i) => (Id: i == 0 ? A : B, a)).ToDictionary(x => x.Id, x => new DeviceAssignment(x.a)),
    };

    [Fact]
    public void Valid_static_batch_passes()
    {
        Assert.Empty(PayloadValidator.Validate(StaticV4("10.0.0.1", 24, "10.0.0.50", "10.0.0.51")));
    }

    [Fact]
    public void Nothing_selected_is_an_error()
    {
        var errors = PayloadValidator.Validate(new NetworkPayload { Devices = new Dictionary<Guid, DeviceAssignment> { [A] = new() } });
        Assert.Contains(errors, e => e.Contains("Nothing to change", StringComparison.Ordinal));
    }

    [Fact]
    public void Gateway_outside_the_subnet_is_rejected()
    {
        var errors = PayloadValidator.Validate(StaticV4("10.0.1.1", 24, "10.0.0.50"));
        Assert.Contains(errors, e => e.Contains("not in the same /24 subnet", StringComparison.Ordinal));
    }

    [Fact]
    public void Network_and_broadcast_addresses_are_rejected()
    {
        var errors = PayloadValidator.Validate(StaticV4("10.0.0.1", 24, "10.0.0.0", "10.0.0.255"));
        Assert.Contains(errors, e => e.Contains("network address", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("broadcast address", StringComparison.Ordinal));
    }

    [Fact]
    public void Duplicate_addresses_in_the_batch_are_rejected()
    {
        var errors = PayloadValidator.Validate(StaticV4("10.0.0.1", 24, "10.0.0.50", "10.0.0.50"));
        Assert.Contains(errors, e => e.Contains("10.0.0.50 is assigned to 2 devices", StringComparison.Ordinal));
    }

    [Fact]
    public void Device_address_equal_to_gateway_is_rejected()
    {
        var errors = PayloadValidator.Validate(StaticV4("10.0.0.1", 24, "10.0.0.1"));
        Assert.Contains(errors, e => e.Contains("is the gateway address", StringComparison.Ordinal));
    }

    [Fact]
    public void Missing_address_mask_or_gateway_are_rejected()
    {
        Assert.Contains(PayloadValidator.Validate(StaticV4("10.0.0.1", 24, "10.0.0.5", null)), e => e.Contains("one device has no address", StringComparison.Ordinal));
        Assert.Contains(PayloadValidator.Validate(StaticV4("10.0.0.1", 31, "10.0.0.5")), e => e.Contains("subnet mask", StringComparison.Ordinal));
        Assert.Contains(PayloadValidator.Validate(StaticV4("", 24, "10.0.0.5")), e => e.Contains("default gateway", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("2001:db8::10", 64, null, true)]
    [InlineData("2001:db8::10", 64, "fe80::1", true)]
    [InlineData("2001:db8::zz", 64, null, false)]
    [InlineData("2001:db8::10/64", 64, null, false)]
    [InlineData("::1", 64, null, false)]
    [InlineData("ff02::1", 64, null, false)]
    [InlineData("2001:db8::10", 129, null, false)]
    [InlineData("2001:db8::10", 64, "10.0.0.1", false)]
    public void Validates_static_ipv6(string address, int prefix, string? gateway, bool valid)
    {
        var payload = new NetworkPayload
        {
            Ipv6 = new Ipv6Change(Ipv6Mode.Static, address, prefix, gateway),
            Devices = new Dictionary<Guid, DeviceAssignment> { [A] = new() },
        };
        Assert.Equal(valid, PayloadValidator.Validate(payload).Count == 0);
    }

    [Fact]
    public void Static_ipv6_for_several_devices_needs_one_distinct_address_per_device()
    {
        var shared = new NetworkPayload
        {
            Ipv6 = new Ipv6Change(Ipv6Mode.Static, "2001:db8::10", 64),
            Devices = new Dictionary<Guid, DeviceAssignment> { [A] = new(), [B] = new() },
        };
        Assert.Contains(PayloadValidator.Validate(shared), e => e.Contains("assigned to more than one device", StringComparison.Ordinal));

        var missing = new NetworkPayload
        {
            Ipv6 = new Ipv6Change(Ipv6Mode.Static, null, 64),
            Devices = new Dictionary<Guid, DeviceAssignment> { [A] = new(Ipv6Address: "2001:db8::10"), [B] = new() },
        };
        Assert.Contains(PayloadValidator.Validate(missing), e => e.Contains("one device has no IPv6 address", StringComparison.Ordinal));

        var ok = missing with { Devices = new Dictionary<Guid, DeviceAssignment> { [A] = new(Ipv6Address: "2001:db8::10"), [B] = new(Ipv6Address: "2001:db8::11") } };
        Assert.Empty(PayloadValidator.Validate(ok));
        Assert.Equal("2001:db8::11", ok.Ipv6!.AddressFor(ok.Devices[B]));
    }

    [Theory]
    [InlineData(new[] { "1.1.1.1", "2606:4700:4700::1111" }, "example.com", true)]
    [InlineData(new string[0], null, false)]
    [InlineData(new[] { "1.1.1" }, null, false)]
    [InlineData(new[] { "1.1.1.1", "1.1.1.1" }, null, false)]
    [InlineData(new[] { "1.1.1.1", "8.8.8.8", "9.9.9.9" }, null, false)]
    [InlineData(new[] { "1.1.1.1" }, "bad_domain", false)]
    public void Validates_static_dns(string[] servers, string? domain, bool valid)
    {
        var payload = new NetworkPayload
        {
            Dns = new DnsChange(false, servers, domain, ["example.com"]),
            Devices = new Dictionary<Guid, DeviceAssignment> { [A] = new() },
        };
        Assert.Equal(valid, PayloadValidator.Validate(payload).Count == 0);
    }

    [Fact]
    public void Dns_from_dhcp_needs_no_servers()
    {
        var payload = new NetworkPayload { Dns = new DnsChange(true), Devices = new Dictionary<Guid, DeviceAssignment> { [A] = new() } };
        Assert.Empty(PayloadValidator.Validate(payload));
    }

    [Theory]
    [InlineData("cam-01", true)]
    [InlineData("-cam", false)]
    [InlineData("cam_01", false)]
    [InlineData("12345", false)]
    [InlineData("a234567890123456789012345678901234567890123456789012345678901234", false)]
    public void Validates_host_names(string name, bool valid) => Assert.Equal(valid, PayloadValidator.IsValidHostName(name));

    [Fact]
    public void Duplicate_host_names_are_rejected()
    {
        var payload = new NetworkPayload
        {
            HostName = new HostNameChange(false),
            Devices = new Dictionary<Guid, DeviceAssignment> { [A] = new(HostName: "cam"), [B] = new(HostName: "CAM") },
        };
        Assert.Contains(PayloadValidator.Validate(payload), e => e.Contains("assigned to 2 devices", StringComparison.Ordinal));
    }

    [Fact]
    public void Host_name_template_expands_position_and_serial()
    {
        Assert.Equal("cam-3", PayloadValidator.ExpandHostName("cam-{n}", 3, "ACCC8E000001"));
        Assert.Equal("axis-accc8e000001", PayloadValidator.ExpandHostName("axis-{serial}", 1, "ACCC8E000001"));
    }

    [Fact]
    public void Payload_round_trips_as_json()
    {
        var payload = StaticV4("10.0.0.1", 24, "10.0.0.50") with { Dns = new DnsChange(true), HostName = new HostNameChange(true) };
        var json = payload.ToJson();
        Assert.Contains("\"mode\":\"static\"", json, StringComparison.Ordinal);
        var back = NetworkPayload.Parse(json);
        Assert.Equal(payload.Ipv4, back.Ipv4);
        Assert.Equal("10.0.0.50", back.Devices[A].Ipv4Address);
        Assert.True(back.Dns!.UseDhcp);
    }

    [Fact]
    public void Malformed_payload_says_nothing_was_changed()
    {
        var ex = Assert.Throws<NetworkValidationException>(() => NetworkPayload.Parse("{not json"));
        Assert.EndsWith("Nothing was changed.", ex.Message, StringComparison.Ordinal);
        Assert.Throws<NetworkValidationException>(() => NetworkPayload.Parse(null));
    }
}
