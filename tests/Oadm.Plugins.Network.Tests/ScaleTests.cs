using System.Diagnostics;
using System.Globalization;

using Oadm.Plugins.Network;
using Oadm.Plugins.Network.Client;
using Oadm.Plugins.Network.Model;
using Oadm.Plugins.Network.Vapix;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Vapix;

using Xunit.Abstractions;

namespace Oadm.Plugins.Network.Tests;

/// <summary>
/// HARD RULE "scale to thousands of devices": the Network settings and Assign IP dialogs with the most selected devices
/// (<see cref="DeviceLimits.MaxDevices"/>: per-device table, conflicts, payload validation) and CanRun over as many stay fast.
/// The addresses are in 10.0.0.0/12 (a /16 holds fewer than 100,000 devices).
/// </summary>
public sealed class ScaleTests(ITestOutputHelper output)
{
    private const int Count = DeviceLimits.MaxDevices;

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
        [.. devices.Select((d, i) => new AddressStatus(Ipv6("2001:db8:1::", i), d.Id, "P3265-V"))];

    /// <summary>An IPv6 address of a number: prefix, then the number in the last two groups (one group holds only 65,536).</summary>
    private static string Ipv6(string prefix, int n) => string.Create(CultureInfo.InvariantCulture, $"{prefix}{n / 65536:x}:{n % 65536:x}");

    [Fact]
    public void Ipv6_conflicts_of_100000_rows_against_100000_managed_devices_are_found() => FindIpv6Conflicts();

    [Fact]
    [Trait("Category", "Perf")]
    public void Ipv6_conflicts_of_100000_rows_against_100000_managed_devices_are_linear()
    {
        var elapsed = FindIpv6Conflicts();
        Assert.True(elapsed < TimeSpan.FromSeconds(5), $"FindIpv6 took {elapsed.TotalMilliseconds:0} ms");
    }

    private static TimeSpan FindIpv6Conflicts()
    {
        var devices = Devices(Count);
        var rows = devices.Select((d, i) => new AssignmentRow(
            new AssignmentDevice(d.Id, d.Address),
            Ipv6("2001:db8::", i % (Count - 1000)))).ToList();
        var known = Known(devices).ToDictionary(s => s.Address, StringComparer.Ordinal);

        var watch = Stopwatch.StartNew();
        var problems = AddressConflicts.FindIpv6(rows, known);
        watch.Stop();

        Assert.Equal(Count, problems.Count);
        Assert.Equal("Assigned to more than one device", problems[0]); // 0 and Count - 1000 share 2001:db8::0:0
        Assert.Null(problems[1500]);
        return watch.Elapsed;
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
    public void Ipv4_conflicts_of_100000_rows_are_found() => FindIpv4Conflicts();

    [Fact]
    [Trait("Category", "Perf")]
    public void Ipv4_conflicts_of_100000_rows_are_linear()
    {
        var elapsed = FindIpv4Conflicts();
        Assert.True(elapsed < TimeSpan.FromSeconds(5), $"Find took {elapsed.TotalMilliseconds:0} ms");
    }

    private static TimeSpan FindIpv4Conflicts()
    {
        var devices = Devices(Count);
        var rows = devices.Select(d => new AssignmentRow(new AssignmentDevice(d.Id, d.Address), d.Address)).ToList();

        var watch = Stopwatch.StartNew();
        var problems = AddressConflicts.Find(rows, 12, "10.15.255.254");
        watch.Stop();

        Assert.Equal(Count, problems.Count);
        return watch.Elapsed;
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void Network_settings_dialog_with_100000_devices_suggests_and_revalidates_fast()
    {
        var devices = Devices(Count);
        var watch = Stopwatch.StartNew();
        var vm = new NetworkSettingsViewModel(devices);
        vm.SelectedIpv4 = vm.Ipv4Choices.Single(c => c.Value == Ipv4Choice.Static);
        vm.Ipv4Mask = "255.240.0.0";
        vm.Ipv4Gateway = "10.0.0.1";
        vm.Ipv4Address = "10.2.0.1";
        vm.SelectedHostName = vm.HostNameChoices.Single(c => c.Value == SourceChoice.Static);
        vm.HostNameText = "cam-{n}";
        var open = watch.ElapsedMilliseconds;

        watch.Restart();
        vm.Assignment.Rows[10].NewAddress = "10.4.0.10"; // one edit revalidates the whole table
        var edit = watch.ElapsedMilliseconds;

        watch.Restart();
        vm.SelectedIpv6 = vm.Ipv6Choices.Single(c => c.Value == Ipv6Choice.Static);
        vm.Assignment.Rows[0].NewIpv6Address = "2001:db8::60";
        var ipv6 = watch.ElapsedMilliseconds;

        output.WriteLine($"Network settings, {Count:N0} devices: open {open} ms, one edit {edit} ms, static IPv6 {ipv6} ms");
        Assert.Equal(Count, vm.Assignment.Rows.Count);
        Assert.Equal("10.2.0.1", vm.Assignment.Rows[0].NewAddress);
        Assert.Equal("10.4.0.10", vm.Assignment.Rows[10].NewAddress);
        // Linear (30,000: 582 / 176 / 411 ms); every edit revalidates the whole table and payload. Budgets about 10x.
        Assert.True(open < 20_000, $"Opening with a range took {open} ms (measured about 2.1 s at 100,000)");
        Assert.True(edit < 6_000, $"One row edit took {edit} ms (measured about 0.6 s at 100,000)");
        Assert.True(ipv6 < 12_000, $"Static IPv6 took {ipv6} ms (measured about 1.1 s at 100,000)");
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void Assign_ip_dialog_with_100000_devices_suggests_and_builds_the_payload_fast()
    {
        var devices = Devices(Count);
        var watch = Stopwatch.StartNew();
        var vm = new AssignIpViewModel(devices);
        vm.ApplyCurrent(Current);
        vm.SubnetMask = "255.240.0.0";
        vm.DefaultRouter = "10.0.0.1";
        vm.IpRange = "10.2.0.1-10.3.255.254";
        vm.PrimaryCommand.Execute(null); // Next: page 2 suggests an address per device
        var open = watch.ElapsedMilliseconds;
        Assert.True(vm.IsReviewPage);

        watch.Restart();
        vm.Assignment.Rows[42].NewAddress = "10.4.0.42";
        var edit = watch.ElapsedMilliseconds;

        output.WriteLine($"Assign IP, {Count:N0} devices: open {open} ms, one edit {edit} ms");
        Assert.Equal(Count, vm.Assignment.Rows.Count);
        Assert.Equal("10.2.0.1", vm.Assignment.Rows[0].NewAddress);
        Assert.Equal("10.4.0.42", vm.Assignment.Rows[42].NewAddress);
        Assert.NotNull(vm.Payload);
        Assert.Equal(Count, vm.Payload!.Devices.Count);
        Assert.True(open < 2000, $"Opening with a range took {open} ms");
        Assert.True(edit < 500, $"One row edit took {edit} ms");
    }

    [Fact]
    [Trait("Category", "Perf")] // time budget; a CI runner can take over a second
    public void CanRun_over_100000_devices_is_cheap()
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
        Assert.True(watch.ElapsedMilliseconds < 5000, $"{10 * Count:N0} CanRun calls took {watch.ElapsedMilliseconds} ms");
    }
}
