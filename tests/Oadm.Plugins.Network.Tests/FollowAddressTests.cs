using Oadm.Plugins.Network.Model;
using Oadm.Sdk.Plugins;
using Oadm.Tests.Shared;

namespace Oadm.Plugins.Network.Tests;

/// <summary>OADM follows a re-addressed device: step sequences for static, DHCP, unreachable and foreign devices.</summary>
public sealed class FollowAddressTests
{
    private static readonly ReachabilityOptions Fast =
        new(TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(60), TimeSpan.FromSeconds(1))
        {
            NewAddressTimeout = TimeSpan.FromMilliseconds(60),
        };

    private const string Head = "Check compatibility: Done|Read current settings: Done|Read IPv6 address mode: Done|Validate settings: Done|Set host name: Skipped|Set DNS: Done|Set IPv6: Skipped|Set IPv4: Done|Wait for the settings to apply: Done";

    private static Task RunAsync(RecordingContext ctx, FakeDevice device, string payload) =>
        StepRun.RunAsync(ctx.Steps, () => new NetworkSettingsTaskPlugin(Fast, TimeProvider.System).ExecuteAsync(ctx, device, payload, CancellationToken.None));

    private static string StaticPayload(Guid id, string address) => new NetworkPayload
    {
        Ipv4 = new Ipv4Change(Ipv4Mode.Static, 24, "10.0.0.138"),
        Dns = new DnsChange(false, ["10.0.0.2"]),
        Devices = new Dictionary<Guid, DeviceAssignment> { [id] = new(address) },
    }.ToJson();

    private static string[] Expect(params string[] tail) => [.. Head.Split('|'), .. tail];

    [Fact]
    public async Task Static_address_moves_the_oadm_device_record()
    {
        var device = new FakeDevice(Guid.NewGuid());
        var ctx = new RecordingContext(new FakeNetworkVapix { Answers = _ => false });
        var moved = new FakeNetworkVapix { BaseAddress = new Uri("http://10.0.0.60/") };
        ctx.Others["10.0.0.60"] = moved;

        await RunAsync(ctx, device, StaticPayload(device.Id, "10.0.0.60"));

        Assert.Equal(
            Expect("Check reachability: Skipped", "Wait for the device at the new address: Done", "Verify device identity: Done", "Update OADM device address: Done"),
            StepRun.Lines(ctx.Steps));
        Assert.Equal("The device moves to 10.0.0.60.", StepRun.Detail(ctx.Steps, "Check reachability"));
        Assert.Equal("The device answers at 10.0.0.60", StepRun.Detail(ctx.Steps, "Wait for the device at the new address"));
        Assert.Equal("Serial number ACCC8E000001", StepRun.Detail(ctx.Steps, "Verify device identity"));
        Assert.Equal("10.0.0.48 -> 10.0.0.60", StepRun.Detail(ctx.Steps, "Update OADM device address"));
        Assert.Equal(["10.0.0.60"], ctx.AddressUpdates);
        Assert.Empty(ctx.Warnings);
        Assert.True(moved.Pings >= 1);
    }

    [Fact]
    public async Task Not_answering_at_the_new_address_keeps_the_record()
    {
        var device = new FakeDevice(Guid.NewGuid());
        var ctx = new RecordingContext(new FakeNetworkVapix { Answers = _ => false }); // nobody at 10.0.0.60 either

        await RunAsync(ctx, device, StaticPayload(device.Id, "10.0.0.60"));

        Assert.Equal(
            Expect("Check reachability: Skipped", "Wait for the device at the new address: Warning", "Verify device identity: Skipped", "Update OADM device address: Skipped"),
            StepRun.Lines(ctx.Steps));
        var warning = Assert.Single(ctx.Warnings);
        Assert.Contains("does not answer at 10.0.0.60", warning, StringComparison.Ordinal);
        Assert.Contains("nor at 10.0.0.48", warning, StringComparison.Ordinal);
        Assert.Contains("keeps 10.0.0.48", warning, StringComparison.Ordinal);
        Assert.Equal("The device was not found at the new address.", StepRun.Detail(ctx.Steps, "Update OADM device address"));
        Assert.Empty(ctx.AddressUpdates);
    }

    [Fact]
    public async Task Still_answering_at_the_old_address_means_the_change_did_not_apply()
    {
        var device = new FakeDevice(Guid.NewGuid());
        var ctx = new RecordingContext(new FakeNetworkVapix { Answers = _ => true });

        await RunAsync(ctx, device, StaticPayload(device.Id, "10.0.0.60"));

        Assert.Contains("still answers at 10.0.0.48", Assert.Single(ctx.Warnings), StringComparison.Ordinal);
        Assert.Empty(ctx.AddressUpdates);
    }

    [Fact]
    public async Task Another_device_at_the_new_address_is_not_taken_for_this_one()
    {
        var device = new FakeDevice(Guid.NewGuid());
        var ctx = new RecordingContext(new FakeNetworkVapix { Answers = _ => false });
        ctx.Others["10.0.0.60"] = new FakeNetworkVapix { Serial = "ACCC8E0000FF" };

        await RunAsync(ctx, device, StaticPayload(device.Id, "10.0.0.60"));

        Assert.Equal(
            Expect("Check reachability: Skipped", "Wait for the device at the new address: Done", "Verify device identity: Warning", "Update OADM device address: Skipped"),
            StepRun.Lines(ctx.Steps));
        Assert.Contains("serial number ACCC8E0000FF, expected ACCC8E000001", Assert.Single(ctx.Warnings), StringComparison.Ordinal);
        Assert.Equal("The device at the new address is not this device.", StepRun.Detail(ctx.Steps, "Update OADM device address"));
        Assert.Empty(ctx.AddressUpdates);
    }

    [Fact]
    public async Task Server_refusing_the_move_is_a_warning()
    {
        var device = new FakeDevice(Guid.NewGuid());
        var ctx = new RecordingContext(new FakeNetworkVapix { Answers = _ => false })
        {
            UpdateAddress = a => throw new DeviceIdentityException($"The device at {a} has serial number X, not ACCC8E000001. The OADM device record is unchanged."),
        };
        ctx.Others["10.0.0.60"] = new FakeNetworkVapix();

        await RunAsync(ctx, device, StaticPayload(device.Id, "10.0.0.60"));

        Assert.Equal("Update OADM device address: Warning", StepRun.Lines(ctx.Steps)[^1]);
        Assert.Contains("record is unchanged", Assert.Single(ctx.Warnings), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Host_name_is_kept()
    {
        var device = new FakeDevice(Guid.NewGuid(), Address: "axis-accc8e000001.example.com");
        var ctx = new RecordingContext(new FakeNetworkVapix { Answers = _ => false });
        ctx.Others["10.0.0.60"] = new FakeNetworkVapix();

        await RunAsync(ctx, device, StaticPayload(device.Id, "10.0.0.60"));

        Assert.Equal("Update OADM device address: Skipped", StepRun.Lines(ctx.Steps)[^1]);
        Assert.Equal("OADM reaches the device by host name axis-accc8e000001.example.com; the host name is kept.", StepRun.Detail(ctx.Steps, "Update OADM device address"));
        Assert.Equal("Verify device identity: Done", StepRun.Lines(ctx.Steps)[^2]);
        Assert.Empty(ctx.AddressUpdates);
    }

    [Fact]
    public async Task Host_without_address_support_says_what_to_do()
    {
        var device = new FakeDevice(Guid.NewGuid());
        var ctx = new RecordingContext(new FakeNetworkVapix { Answers = _ => false }) { SupportsAddresses = false };

        await RunAsync(ctx, device, StaticPayload(device.Id, "10.0.0.60"));

        Assert.Equal(
            Expect("Check reachability: Skipped", "Wait for the device at the new address: Warning", "Verify device identity: Skipped", "Update OADM device address: Skipped"),
            StepRun.Lines(ctx.Steps));
        Assert.Contains("Remove the device and add it again", Assert.Single(ctx.Warnings), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dhcp_keeps_the_record_and_leaves_it_to_the_next_scan()
    {
        var device = new FakeDevice(Guid.NewGuid());
        var vapix = new FakeNetworkVapix
        {
            Answers = _ => false,
            NetworkInfoJson = Fixture.Read(Fixture.GetNetworkInfo).Replace("\"configurationMode\": \"dhcp\"", "\"configurationMode\": \"static\"", StringComparison.Ordinal),
        };
        var ctx = new RecordingContext(vapix);
        var payload = new NetworkPayload
        {
            Ipv4 = new Ipv4Change(Ipv4Mode.Dhcp),
            Devices = new Dictionary<Guid, DeviceAssignment> { [device.Id] = new() },
        }.ToJson();

        await RunAsync(ctx, device, payload);

        Assert.Equal(
            ["Check compatibility: Done", "Read current settings: Done", "Read IPv6 address mode: Done", "Validate settings: Done", "Set host name: Skipped", "Set DNS: Skipped",
             "Set IPv6: Skipped", "Set IPv4: Done", "Wait for the settings to apply: Done", "Check reachability: Warning",
             "Wait for the device at the new address: Skipped", "Verify device identity: Skipped", "Update OADM device address: Skipped"],
            StepRun.Lines(ctx.Steps));
        Assert.Equal(
            "DHCP: address assigned by the network, the device will be found again by the next scan",
            StepRun.Detail(ctx.Steps, "Update OADM device address"));
        Assert.Contains("next mDNS scan", Assert.Single(ctx.Warnings), StringComparison.Ordinal);
        Assert.Empty(ctx.ClientsCreated);
        Assert.Empty(ctx.AddressUpdates);
    }
}
