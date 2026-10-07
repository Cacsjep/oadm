using Oadm.Plugins.Network.Model;

namespace Oadm.Plugins.Network.Tests;

/// <summary>IP range syntax (all forms ADM / ACS accept), address suggestion and conflict detection.</summary>
public sealed class AddressAssignmentTests
{
    private static List<string> Expand(string text, int prefix = 24, int take = 1000)
    {
        Assert.True(IpRangeExpression.TryParse(text, out var range, out var error), error);
        return range!.Enumerate(prefix).Take(take).Select(Ipv4.Format).ToList();
    }

    [Fact]
    public void Wildcard_in_the_last_octet()
    {
        var all = Expand("192.168.0.*");
        Assert.Equal(256, all.Count);
        Assert.Equal("192.168.0.0", all[0]);
        Assert.Equal("192.168.0.255", all[^1]);
    }

    [Fact]
    public void Wildcards_in_several_octets_enumerate_first_octet_outermost()
    {
        Assert.True(IpRangeExpression.TryParse("10.*.1.*", out var range, out _));
        Assert.Equal(65536, range!.Count);
        Assert.Equal(["10.0.1.0", "10.0.1.1"], Expand("10.*.1.*", take: 2));
        Assert.Equal("10.1.1.0", Expand("10.*.1.*", take: 257)[256]);
    }

    [Theory]
    [InlineData("192.168.0.10-192.168.0.20")]
    [InlineData("192.168.0.10-20")]
    [InlineData(" 192.168.0.10 - 192.168.0.20 ")]
    public void First_and_last_address_also_shortened(string text)
    {
        var all = Expand(text);
        Assert.Equal(11, all.Count);
        Assert.Equal("192.168.0.10", all[0]);
        Assert.Equal("192.168.0.20", all[^1]);
    }

    [Fact]
    public void Full_range_crosses_octets()
    {
        var all = Expand("10.0.0.254-10.0.1.1");
        Assert.Equal(["10.0.0.254", "10.0.0.255", "10.0.1.0", "10.0.1.1"], all);
        Assert.Equal(["10.0.0.10", "10.0.0.11"], Expand("10.0.0.10-0.11")); // right side replaces the last octets
    }

    [Fact]
    public void Range_in_another_octet_and_combined_with_wildcards()
    {
        Assert.Equal(["10.10.1.101", "10.11.1.101", "10.12.1.101"], Expand("10.10-12.1.101"));
        Assert.True(IpRangeExpression.TryParse("10.10-30.1.*", out var range, out _));
        Assert.Equal(21 * 256, range!.Count);
    }

    [Fact]
    public void Commas_separate_several_ranges_in_written_order()
    {
        var all = Expand("192.168.1.10-192.168.1.11,192.168.0.5, 192.168.2.1-2");
        Assert.Equal(["192.168.1.10", "192.168.1.11", "192.168.0.5", "192.168.2.1", "192.168.2.2"], all);
        Assert.Equal(258, Expand("192.168.0.*,192.168.1.10-192.168.1.11").Count);
    }

    [Fact]
    public void A_single_address_is_a_start_address_up_to_the_end_of_the_subnet()
    {
        Assert.True(IpRangeExpression.TryParse("10.0.0.250", out var range, out _));
        Assert.True(range!.IsStartAddress);
        Assert.Equal(["10.0.0.250", "10.0.0.251", "10.0.0.252", "10.0.0.253", "10.0.0.254", "10.0.0.255"], range.Enumerate(24).Select(Ipv4.Format));
        Assert.Equal(["10.0.0.250", "10.0.0.251"], range.Enumerate(30).Select(Ipv4.Format).Take(2)); // 10.0.0.248/30 ends at .251
        Assert.False(IpRangeExpression.TryParse("10.0.0.5,10.0.0.9", out var list, out _) && list!.IsStartAddress);
    }

    [Theory]
    [InlineData("", "Enter an IP range")]
    [InlineData("192.168.0", "four parts")]
    [InlineData("192.168.0.256", "must be a number 0-255")]
    [InlineData("192.168.0.20-10", "must be a number 0-255")]
    [InlineData("192.168.0.20-192.168.0.10", "lower than the first")]
    [InlineData("192.168.0.1,,192.168.0.2", "empty entry")]
    [InlineData("192.168.0.a", "must be a number")]
    [InlineData("*.*.*.*", "at most")]
    [InlineData("192.168.00.1", "must be a number")]
    public void Invalid_ranges_say_why(string text, string message)
    {
        Assert.False(IpRangeExpression.TryParse(text, out _, out var error));
        Assert.Contains(message, error, StringComparison.Ordinal);
    }

    private static List<AssignmentDevice> Devices(int count, int firstOctet = 48) =>
        [.. Enumerable.Range(0, count).Select(i => new AssignmentDevice(Guid.NewGuid(), $"10.0.0.{firstOctet + i}"))];

    private static IpRangeExpression Range(string text)
    {
        Assert.True(IpRangeExpression.TryParse(text, out var range, out var error), error);
        return range!;
    }

    [Fact]
    public void Assignment_skips_network_broadcast_router_and_taken_addresses()
    {
        var result = AddressAssigner.Assign(Range("10.0.0.*"), 24, "10.0.0.1", Devices(4), new HashSet<string> { "10.0.0.3" });

        Assert.Equal(["10.0.0.2", "10.0.0.4", "10.0.0.5", "10.0.0.6"], result.Addresses);
        Assert.Null(result.Error);
    }

    [Fact]
    public void A_device_keeps_its_own_address_when_the_range_reaches_it()
    {
        var devices = Devices(2, firstOctet: 11);
        var taken = new HashSet<string> { "10.0.0.11", "10.0.0.12" }; // both are managed devices: the selected ones
        var result = AddressAssigner.Assign(Range("10.0.0.10-20"), 24, "10.0.0.1", devices, taken);
        Assert.Equal(["10.0.0.10", "10.0.0.12"], result.Addresses); // .11 belongs to the first device, the second keeps .12

        var own = AddressAssigner.Assign(Range("10.0.0.11-20"), 24, "10.0.0.1", devices, taken);
        Assert.Equal(["10.0.0.11", "10.0.0.12"], own.Addresses);
    }

    [Fact]
    public void Too_few_addresses_is_reported()
    {
        var result = AddressAssigner.Assign(Range("10.0.0.253-255"), 24, "10.0.0.1", Devices(3));

        Assert.Equal(["10.0.0.253", "10.0.0.254", null], result.Addresses);
        Assert.Equal(1, result.Missing);
        Assert.Equal("Not enough addresses: the IP range has 2 free addresses for 3 devices. Extend the range.", result.Error);

        var start = AddressAssigner.Assign(Range("10.0.0.254"), 24, "10.0.0.1", Devices(2));
        Assert.Equal(["10.0.0.254", null], start.Addresses);
    }

    [Fact]
    public void Conflicts_per_row()
    {
        var devices = Devices(8);
        var other = Guid.NewGuid();
        var known = new Dictionary<string, AddressStatus>
        {
            ["10.0.0.20"] = new("10.0.0.20", other, "P3265-V ACCC8E0000AA"),
            ["10.0.0.21"] = new("10.0.0.21", InUse: true),
            ["10.0.0.49"] = new("10.0.0.49", devices[1].Id, "itself", InUse: true),
        };
        AssignmentRow[] rows =
        [
            new(devices[0], "10.0.0.10"),
            new(devices[1], "10.0.0.49"),   // keeps its own address: fine although it answers
            new(devices[2], "10.0.0.20"),   // another managed device
            new(devices[3], "10.0.0.21"),   // something answers there
            new(devices[4], "10.0.1.5"),    // other subnet
            new(devices[5], "10.0.0.1"),    // the router
            new(devices[6], "10.0.0.10"),   // twice
            new(devices[7], "10.0.0.255"),  // broadcast
        ];

        var conflicts = AddressConflicts.Find(rows, 24, "10.0.0.1", known);

        Assert.Equal(
            ["Assigned to more than one device", null, "Used by P3265-V ACCC8E0000AA", "In use: another host answers at this address",
             "Outside the subnet of the default router", "Same as the default router", "Assigned to more than one device", "Broadcast address of its subnet"],
            conflicts);
        Assert.Equal(["No address", "Not a valid IPv4 address"], AddressConflicts.Find([new(devices[0], ""), new(devices[1], "10.0.0")], 24, "10.0.0.1"));
    }

    [Fact]
    public void Check_request_and_response_round_trip()
    {
        var request = AddressCheckRequest.Parse(new AddressCheckRequest(["10.0.0.5"], Probe: false).ToJson());
        Assert.Equal(["10.0.0.5"], request.Addresses);
        Assert.False(request.Probe);

        var id = Guid.NewGuid();
        var response = AddressCheckResponse.Parse(new AddressCheckResponse([new("10.0.0.48", id, "P3265-V")], [new("10.0.0.5", InUse: true)]).ToJson());
        Assert.Equal(id, Assert.Single(response.Managed).DeviceId);
        Assert.True(Assert.Single(response.Probed).InUse);
    }
}
