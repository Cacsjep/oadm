using System.Diagnostics;
using System.Globalization;

using Oadm.Plugins.Network;
using Oadm.Plugins.Network.Client;
using Oadm.Plugins.Network.Model;
using Oadm.Plugins.Network.Vapix;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Network.Tests;

/// <summary>
/// HARD RULE "scale to thousands of devices": the Network settings and Assign IP dialogs with 5,000 selected devices
/// (per-device table, conflicts, payload validation) and CanRun over 5,000 devices stay fast.
/// </summary>
public sealed class ScaleTests
{
    private const int Count = 5000;

    private static readonly CurrentNetworkSettings Current =
        NetworkInfoParser.WithIpv6Parameters(
            NetworkInfoParser.ParseGetNetworkInfo(Fixture.Read(Fixture.GetNetworkInfo), "10.0.0.48"),
            Fixture.Parameters(Fixture.ParamNetwork));

    private static List<IDeviceInfo> Devices(int count) =>
        [.. Enumerable.Range(0, count).Select(i => (IDeviceInfo)new FakeDevice(
            Guid.NewGuid(),
            string.Create(CultureInfo.InvariantCulture, $"10.{i / 65536}.{i / 256 % 256}.{i % 256}"),
            Serial: string.Create(CultureInfo.InvariantCulture, $"ACCC8E{i:X6}")))];

    private static List<AddressStatus> Known(IReadOnlyList<IDeviceInfo> devices) =>
        [.. devices.Select((d, i) => new AddressStatus(string.Create(CultureInfo.InvariantCulture, $"2001:db8::1:{i:x}"), d.Id, "P3265-V"))];

    [Fact]
    [Trait("Category", "Timing")] // time budget
    public void Ipv6_conflicts_of_5000_rows_against_5000_managed_devices_are_linear()
    {
        var devices = Devices(Count);
        var rows = devices.Select((d, i) => new AssignmentRow(
            new AssignmentDevice(d.Id, d.Address),
            string.Create(CultureInfo.InvariantCulture, $"2001:db8::{i % 4000:x}"))).ToList();
        var known = Known(devices).ToDictionary(s => s.Address, StringComparer.Ordinal);

        var watch = Stopwatch.StartNew();
        var problems = AddressConflicts.FindIpv6(rows, known);
        watch.Stop();

        Assert.Equal(Count, problems.Count);
        Assert.Equal("Assigned to more than one device", problems[0]); // 0 and 4000 share 2001:db8::0
        Assert.Null(problems[1500]);
        Assert.True(watch.ElapsedMilliseconds < 1000, $"FindIpv6 took {watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Ipv6_conflict_with_a_managed_device_is_found_in_any_notation()
    {
        var device = Guid.NewGuid();
        var other = Guid.NewGuid();
        var known = new Dictionary<string, AddressStatus>(StringComparer.Ordinal)
        {
            ["2001:DB8:0::10"] = new("2001:DB8:0::10", other, "M3106"),
        };

        var problems = AddressConflicts.FindIpv6([new AssignmentRow(new AssignmentDevice(device, "10.0.0.1"), "2001:db8::10")], known);

        Assert.Equal("Used by M3106", problems[0]);
    }

    [Fact]
    [Trait("Category", "Timing")] // time budget
    public void Ipv4_conflicts_of_5000_rows_are_linear()
    {
        var devices = Devices(Count);
        var rows = devices.Select(d => new AssignmentRow(new AssignmentDevice(d.Id, d.Address), d.Address)).ToList();

        var watch = Stopwatch.StartNew();
        var problems = AddressConflicts.Find(rows, 16, "10.0.255.254");
        watch.Stop();

        Assert.Equal(Count, problems.Count);
        Assert.True(watch.ElapsedMilliseconds < 1000, $"Find took {watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void Network_settings_dialog_with_5000_devices_suggests_and_revalidates_fast()
    {
        var devices = Devices(Count);
        var watch = Stopwatch.StartNew();
        var vm = new NetworkSettingsViewModel(devices);
        vm.SelectedIpv4 = vm.Ipv4Choices.Single(c => c.Value == Ipv4Choice.Static);
        vm.Ipv4Mask = "255.255.0.0";
        vm.Ipv4Gateway = "10.1.0.1";
        vm.Ipv4Address = "10.1.1.1";
        vm.SelectedHostName = vm.HostNameChoices.Single(c => c.Value == SourceChoice.Static);
        vm.HostNameText = "cam-{n}";
        var open = watch.ElapsedMilliseconds;

        watch.Restart();
        vm.Assignment.Rows[10].NewAddress = "10.1.200.10"; // one edit revalidates the whole table
        var edit = watch.ElapsedMilliseconds;

        watch.Restart();
        vm.SelectedIpv6 = vm.Ipv6Choices.Single(c => c.Value == Ipv6Choice.Static);
        vm.Assignment.Rows[0].NewIpv6Address = "2001:db8::60";
        var ipv6 = watch.ElapsedMilliseconds;

        Assert.Equal(Count, vm.Assignment.Rows.Count);
        Assert.Equal("10.1.1.1", vm.Assignment.Rows[0].NewAddress);
        Assert.Equal("10.1.200.10", vm.Assignment.Rows[10].NewAddress);
        Assert.True(open < 2000, $"Opening with a range took {open} ms");
        Assert.True(edit < 500, $"One row edit took {edit} ms");
        Assert.True(ipv6 < 1000, $"Static IPv6 took {ipv6} ms");
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void Assign_ip_dialog_with_5000_devices_suggests_and_builds_the_payload_fast()
    {
        var devices = Devices(Count);
        var watch = Stopwatch.StartNew();
        var vm = new AssignIpViewModel(devices);
        vm.ApplyCurrent(Current);
        vm.SubnetMask = "255.255.0.0";
        vm.DefaultRouter = "10.1.0.1";
        vm.IpRange = "10.1.1.1-10.1.40.254";
        vm.PrimaryCommand.Execute(null); // Next: page 2 suggests an address per device
        var open = watch.ElapsedMilliseconds;
        Assert.True(vm.IsReviewPage);

        watch.Restart();
        vm.Assignment.Rows[42].NewAddress = "10.1.250.42";
        var edit = watch.ElapsedMilliseconds;

        Assert.Equal(Count, vm.Assignment.Rows.Count);
        Assert.Equal("10.1.1.1", vm.Assignment.Rows[0].NewAddress);
        Assert.Equal("10.1.250.42", vm.Assignment.Rows[42].NewAddress);
        Assert.NotNull(vm.Payload);
        Assert.Equal(Count, vm.Payload!.Devices.Count);
        Assert.True(open < 2000, $"Opening with a range took {open} ms");
        Assert.True(edit < 500, $"One row edit took {edit} ms");
    }

    [Fact]
    [Trait("Category", "Perf")] // time budget; a CI runner can take over a second
    public void CanRun_over_5000_devices_is_cheap()
    {
        IReadOnlyList<DeviceApi> apis = [.. Enumerable.Range(0, 120).Select(i => new DeviceApi("api-" + i, "1." + i)), .. Fixture.Modern];
        var devices = Enumerable.Range(0, Count).Select(_ => new FakeDevice(Guid.NewGuid()) { Apis = apis }).ToList();
        var network = new NetworkSettingsTaskPlugin();
        var assign = new AssignIpTaskPlugin();

        var watch = Stopwatch.StartNew();
        var runnable = 0;
        for (var round = 0; round < 5; round++)
        {
            foreach (var device in devices)
            {
                runnable += (network.CanRun(device) ? 1 : 0) + (assign.CanRun(device) ? 1 : 0);
            }
        }

        watch.Stop();
        Assert.Equal(5 * 2 * Count, runnable);
        Assert.True(watch.ElapsedMilliseconds < 1000, $"50,000 CanRun calls took {watch.ElapsedMilliseconds} ms");
    }
}
