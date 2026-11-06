using Oadm.Plugins.Network.Model;
using Oadm.Sdk.Plugins;
using StepRun = Oadm.Sdk.Testing.StepRun;

namespace Oadm.Plugins.Network.Tests;

/// <summary>OADM follows a re-addressed device: step sequences for static, DHCP, unreachable and foreign devices.</summary>
public sealed class FollowAddressTests
{
    private static readonly ReachabilityOptions Fast =
        new(TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(60), TimeSpan.FromSeconds(30)) // one probe request: generous
        {
            NewAddressTimeout = TimeSpan.FromMilliseconds(60),
        };

    private const string Head = "Check compatibility: Done|Read current settings: Done|Read IPv6 address mode: Done|Validate settings: Done|Check address is free: Done|Set host name: Skipped|Set DNS: Done|Set IPv6: Skipped|Set IPv4: Done|Wait for the settings to apply: Done";

    private static Task RunAsync(RecordingContext ctx, FakeDevice device, string payload) =>
        StepRun.RunAsync(ctx.Steps, () => new NetworkSettingsTaskPlugin(Fast, TimeProvider.System, new FakeAddressProbe()).ExecuteAsync(ctx, device, payload, CancellationToken.None));

    private static string StaticPayload(Guid id, string address) => new NetworkPayload
    {
        Ipv4 = new Ipv4Change(Ipv4Mode.Static, 24, "10.0.0.138"),
        Dns = new DnsChange(false, ["10.0.0.2"]),
        Devices = new Dictionary<Guid, DeviceAssignment> { [id] = new(address) },
    }.ToJson();

    /// <summary>Every case here succeeds, so the engine appends "Completed".</summary>
    private static string[] Expect(params string[] tail) => [.. Head.Split('|'), .. tail, "Completed: Done"];

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
        Assert.Contains("No answer at 10.0.0.60", warning, StringComparison.Ordinal);
        Assert.Contains("Check the subnet mask and default router", warning, StringComparison.Ordinal);
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

        Assert.Equal("Update OADM device address: Warning", StepRun.Lines(ctx.Steps)[^2]);
        Assert.Contains("record is unchanged", Assert.Single(ctx.Warnings), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Host_name_is_kept()
    {
        var device = new FakeDevice(Guid.NewGuid(), Address: "axis-accc8e000001.example.com");
        var ctx = new RecordingContext(new FakeNetworkVapix { Answers = _ => false });
        ctx.Others["10.0.0.60"] = new FakeNetworkVapix();

        await RunAsync(ctx, device, StaticPayload(device.Id, "10.0.0.60"));

        Assert.Equal("Update OADM device address: Skipped", StepRun.Lines(ctx.Steps)[^2]);
        Assert.Equal("OADM reaches the device by host name axis-accc8e000001.example.com; the host name is kept.", StepRun.Detail(ctx.Steps, "Update OADM device address"));
        Assert.Equal("Verify device identity: Done", StepRun.Lines(ctx.Steps)[^3]);
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

    /// <summary>The device never answers the write that moves it: a timeout as the server's VAPIX client reports it, or a lost connection.</summary>
    private static FakeNetworkVapix LosesTheAddressWrite(bool timeout, Func<int, bool> answersAfterwards) => new()
    {
        Answers = answersAfterwards,
        WriteResponse = r => !r.Body.Contains("setIPv4AddressConfiguration", StringComparison.Ordinal)
            ? null
            : timeout
                ? throw new TaskCanceledException("The device did not answer within 15 seconds.", new TimeoutException())
                : throw new HttpRequestException("Connection reset by peer"),
    };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Lost_answer_of_the_address_write_is_followed_to_the_new_address(bool timeout)
    {
        // The device applied the address before it could answer: it is found at the new address and the record moves.
        var device = new FakeDevice(Guid.NewGuid());
        var ctx = new RecordingContext(LosesTheAddressWrite(timeout, _ => false));
        ctx.Others["10.0.0.60"] = new FakeNetworkVapix { BaseAddress = new Uri("http://10.0.0.60/") };

        await RunAsync(ctx, device, StaticPayload(device.Id, "10.0.0.60"));

        Assert.Equal(
            Expect("Check reachability: Skipped", "Wait for the device at the new address: Done", "Verify device identity: Done", "Update OADM device address: Done"),
            StepRun.Lines(ctx.Steps));
        Assert.Equal("No answer; the device is checked at 10.0.0.60", StepRun.Detail(ctx.Steps, "Set IPv4"));
        Assert.Equal("10.0.0.48 -> 10.0.0.60", StepRun.Detail(ctx.Steps, "Update OADM device address"));
        Assert.Equal(["10.0.0.60"], ctx.AddressUpdates);
        Assert.Empty(ctx.Warnings);
        Assert.Contains(ctx.LogEntries, l => l.Level == TaskLogLevel.Warning && l.Message.EndsWith("the device did not answer. Looking for it at 10.0.0.60.", StringComparison.Ordinal));
        Assert.Contains(ctx.LogEntries, l => l.Level == TaskLogLevel.Info && l.Message.EndsWith("applied, the device answers at 10.0.0.60.", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Lost_answer_and_the_device_still_at_the_old_address_fails_without_moving_the_record(bool timeout)
    {
        var device = new FakeDevice(Guid.NewGuid());
        var ctx = new RecordingContext(LosesTheAddressWrite(timeout, _ => true)); // nobody at 10.0.0.60

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(ctx, device, StaticPayload(device.Id, "10.0.0.60")));

        Assert.Equal(
            "IPv4 static 10.0.0.60/24, default router 10.0.0.138: the device did not answer. It still answers at 10.0.0.48: the address was not changed. Already applied: DNS 10.0.0.2.",
            ex.Message);
        Assert.Equal(
            [.. Head.Split('|'), "Check reachability: Skipped", "Wait for the device at the new address: Failed", "Verify device identity: Skipped", "Update OADM device address: Skipped"],
            StepRun.Lines(ctx.Steps));
        Assert.Equal(ex.Message, StepRun.Detail(ctx.Steps, "Wait for the device at the new address"));
        Assert.Equal(["10.0.0.60"], ctx.ClientsCreated);
        Assert.Empty(ctx.AddressUpdates);
        Assert.Contains(ctx.LogEntries, l => l.Level == TaskLogLevel.Error && l.Message == ex.Message);
    }

    [Fact]
    public async Task Lost_answer_and_no_device_at_either_address_says_where_to_look()
    {
        var device = new FakeDevice(Guid.NewGuid());
        var ctx = new RecordingContext(LosesTheAddressWrite(timeout: true, _ => false)); // nobody at 10.0.0.60 either

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(ctx, device, StaticPayload(device.Id, "10.0.0.60")));

        Assert.Contains("the device did not answer. It answers at neither 10.0.0.48 nor 10.0.0.60", ex.Message, StringComparison.Ordinal);
        Assert.Contains("The change may have been applied. Look for the device at 10.0.0.60; OADM keeps 10.0.0.48.", ex.Message, StringComparison.Ordinal);
        Assert.EndsWith("Already applied: DNS 10.0.0.2.", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Nothing was changed", ex.Message, StringComparison.Ordinal);
        Assert.Equal("Wait for the device at the new address: Failed", StepRun.Lines(ctx.Steps)[^3]);
        Assert.Empty(ctx.AddressUpdates);
    }

    [Fact]
    public async Task Lost_answer_and_another_device_at_the_new_address_never_moves_the_record()
    {
        var device = new FakeDevice(Guid.NewGuid());
        var ctx = new RecordingContext(LosesTheAddressWrite(timeout: false, _ => false));
        ctx.Others["10.0.0.60"] = new FakeNetworkVapix { Serial = "ACCC8E0000FF" };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(ctx, device, StaticPayload(device.Id, "10.0.0.60")));

        Assert.Contains("Another device answers at 10.0.0.60 (serial number ACCC8E0000FF, expected ACCC8E000001). The change may have been applied. OADM keeps 10.0.0.48.", ex.Message, StringComparison.Ordinal);
        Assert.Equal("Verify device identity: Failed", StepRun.Lines(ctx.Steps)[^2]);
        Assert.Empty(ctx.AddressUpdates);
    }

    [Fact]
    public async Task Lost_answer_of_a_write_that_does_not_move_the_device_claims_nothing()
    {
        // DNS is the first write: no answer proves nothing, so neither "Nothing was changed." nor a follow.
        var device = new FakeDevice(Guid.NewGuid());
        var ctx = new RecordingContext(new FakeNetworkVapix
        {
            WriteResponse = r => r.Body.Contains("setResolverConfiguration", StringComparison.Ordinal)
                ? throw new TaskCanceledException("The device did not answer within 15 seconds.", new TimeoutException())
                : null,
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(ctx, device, StaticPayload(device.Id, "10.0.0.60")));

        Assert.Equal("DNS 10.0.0.2 failed: The device did not answer within 15 seconds. The change may have been applied.", ex.Message);
        Assert.Equal("Set DNS: Failed", StepRun.Lines(ctx.Steps)[6]);
        Assert.Equal("Set IPv4: Skipped", StepRun.Lines(ctx.Steps)[8]);
        Assert.Empty(ctx.ClientsCreated);
    }

    [Fact]
    public async Task A_refusal_of_the_address_write_is_not_followed()
    {
        var device = new FakeDevice(Guid.NewGuid());
        var vapix = new FakeNetworkVapix
        {
            WriteResponse = r => r.Body.Contains("setIPv4AddressConfiguration", StringComparison.Ordinal)
                ? """{"apiVersion":"1.37","context":"oadm","method":"setIPv4AddressConfiguration","error":{"code":4004,"message":"Invalid parameter(s)","details":{"subCode":107}}}"""
                : null,
        };
        var ctx = new RecordingContext(vapix);
        var payload = new NetworkPayload
        {
            Ipv4 = new Ipv4Change(Ipv4Mode.Static, 24, "10.0.0.138"),
            Devices = new Dictionary<Guid, DeviceAssignment> { [device.Id] = new("10.0.0.60") },
        }.ToJson();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(ctx, device, payload));

        Assert.Contains("(4004/107)", ex.Message, StringComparison.Ordinal);
        Assert.EndsWith("Nothing was changed.", ex.Message, StringComparison.Ordinal); // the only write, and the device refused it
        Assert.Equal("Set IPv4: Failed", StepRun.Lines(ctx.Steps)[8]);
        Assert.Equal("Wait for the device at the new address: Skipped", StepRun.Lines(ctx.Steps)[^3]);
        Assert.Empty(ctx.ClientsCreated);
        Assert.Equal(0, vapix.Pings);
    }

    [Fact]
    public async Task Cancel_during_the_address_write_stays_a_cancel()
    {
        var device = new FakeDevice(Guid.NewGuid());
        using var cts = new CancellationTokenSource();
        var ctx = new RecordingContext(new FakeNetworkVapix
        {
            WriteResponse = r =>
            {
                if (r.Body.Contains("setIPv4AddressConfiguration", StringComparison.Ordinal))
                {
                    cts.Cancel();
                    throw new OperationCanceledException(cts.Token);
                }

                return null;
            },
        });
        var plugin = new NetworkSettingsTaskPlugin(Fast, TimeProvider.System, new FakeAddressProbe());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            StepRun.RunAsync(ctx.Steps, () => plugin.ExecuteAsync(ctx, device, StaticPayload(device.Id, "10.0.0.60"), cts.Token)));

        Assert.Equal("Set IPv4: Failed", StepRun.Lines(ctx.Steps)[8]);
        Assert.Equal("Cancelled.", StepRun.Detail(ctx.Steps, "Set IPv4"));
        Assert.Empty(ctx.ClientsCreated);
        Assert.Empty(ctx.AddressUpdates);
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
            ["Check compatibility: Done", "Read current settings: Done", "Read IPv6 address mode: Done", "Validate settings: Done", "Check address is free: Skipped", "Set host name: Skipped", "Set DNS: Skipped",
             "Set IPv6: Skipped", "Set IPv4: Done", "Wait for the settings to apply: Done", "Check reachability: Warning",
             "Wait for the device at the new address: Skipped", "Verify device identity: Skipped", "Update OADM device address: Skipped", "Completed: Done"],
            StepRun.Lines(ctx.Steps));
        Assert.Equal(
            "OADM finds the device again at its new address.",
            StepRun.Detail(ctx.Steps, "Update OADM device address"));
        Assert.Contains("OADM finds the device again", Assert.Single(ctx.Warnings), StringComparison.Ordinal);
        Assert.Empty(ctx.ClientsCreated);
        Assert.Empty(ctx.AddressUpdates);
    }
}
