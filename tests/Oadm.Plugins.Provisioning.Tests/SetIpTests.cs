using System.Text.Json;

using Oadm.Plugins.Provisioning.Engine;
using Oadm.Plugins.Provisioning.Model;
using Oadm.Plugins.Provisioning.Nodes;
using Oadm.Sdk.Network;
using Oadm.Sdk.Tasks;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Provisioning.Tests;

/// <summary>Set IP (spec node 15): its own pool, never the same address twice, the DHCP server untouched.</summary>
public sealed class SetIpTests
{
    private static Profile WithSetIp(Harness h, SetIpSettings? pool = null) => h.Profile(Harness.Node("ip", NodeKind.SetIp, pool ?? Harness.Pool()));

    [Fact]
    public async Task Parallel_cameras_never_get_the_same_address()
    {
        await using var h = new Harness();
        h.Probe.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await h.StartAsync(WithSetIp(h));
        for (var i = 1; i <= 3; i++)
        {
            h.Ack(i);
        }

        await h.RunUntilAsync(() => Enumerable.Range(1, 3).All(i => h.Row(i).NodeName == "Set IP"), "three cameras at Set IP");
        await Eventually.TrueAsync(() => !h.Probe.Probed.IsEmpty, "the first probe");
        Assert.Single(h.Probe.Probed); // one take at a time: the others wait for the lock

        h.Probe.Gate.SetResult();
        await h.RunUntilAsync(() => h.Engine.Counts().Done == 3, "every camera done");

        var addresses = Enumerable.Range(1, 3).Select(i => h.Row(i).Address).ToList();
        Assert.Equal(["192.168.15.10", "192.168.15.11", "192.168.15.12"], addresses.Order(StringComparer.Ordinal));
        Assert.Equal(3, h.Engine.Session!.Reserved.Values.Distinct().Count());
        Assert.Equal(["192.168.15.10", "192.168.15.11", "192.168.15.12"], h.Probe.Probed);
        Assert.All(h.Ctx.TaskRuns, run => Assert.Equal(SetIpNode.TaskPluginId, run.PluginId)); // nothing touches the DHCP server
    }

    [Fact]
    public async Task The_next_free_address_skips_reservations_oadm_devices_and_answering_addresses_and_says_which()
    {
        await using var h = new Harness();
        await h.StartAsync(WithSetIp(h));
        h.Ack(1);
        await h.RunUntilAsync(() => h.Row(1).State == CameraState.Done, "the first camera has .10");
        h.Devices.Add(new FakeDevice { Serial = "ACCC8E0000FF", Address = "192.168.15.11" });
        h.Probe.Answering["192.168.15.12"] = true;

        h.Ack(2);
        await h.RunUntilAsync(() => h.Row(2).State == CameraState.Done, "the second camera done");

        Assert.Equal("192.168.15.11 and 192.168.15.12 are in use, 192.168.15.13 assigned.", h.Row(2).Results[2]!.Message);
        Assert.Equal("192.168.15.13", h.Row(2).Address);
        Assert.DoesNotContain("192.168.15.10", h.Probe.Probed.Skip(1)); // reserved in this session: not probed again
    }

    [Fact]
    public async Task The_payload_writes_the_fixed_address_mask_router_and_dns_of_the_pool()
    {
        await using var h = new Harness();
        await h.StartAsync(WithSetIp(h));
        h.Ack(1);
        await h.RunUntilAsync(() => h.Row(1).State == CameraState.Done, "done");

        var run = Assert.Single(h.Ctx.TaskRuns);
        Assert.Equal([h.Row(1).DeviceId!.Value], run.DeviceIds);
        using var json = JsonDocument.Parse(run.PayloadJson!);
        var ipv4 = json.RootElement.GetProperty("ipv4");
        Assert.Equal("static", ipv4.GetProperty("mode").GetString());
        Assert.Equal(24, ipv4.GetProperty("prefixLength").GetInt32());
        Assert.Equal("192.168.15.1", ipv4.GetProperty("gateway").GetString());
        Assert.Equal("192.168.15.10", json.RootElement.GetProperty("devices").GetProperty(h.Row(1).DeviceId!.Value.ToString()).GetProperty("ipv4Address").GetString());
        Assert.Equal(["192.168.15.1"], json.RootElement.GetProperty("dns").GetProperty("servers").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Reservations_survive_a_restart_until_stop_and_a_new_session_starts_at_the_first_address()
    {
        await using var h = new Harness();
        h.Outcomes[SetIpNode.TaskPluginId] = (TaskEndState.Failed, "The device did not answer at 192.168.15.10 within 90 seconds.");
        await h.StartAsync(WithSetIp(h));
        h.Ack(1);
        await h.RunUntilAsync(() => h.Row(1).State == CameraState.Failed, "Set IP failed");
        await h.Engine.FlushAsync(CancellationToken.None);

        await h.RestartAsync();
        Assert.Equal("192.168.15.10", h.Engine.Session!.Reserved[Harness.Mac(1)]);

        // The run again keeps its address (no second address for the same camera).
        h.Outcomes[SetIpNode.TaskPluginId] = (TaskEndState.Done, null);
        h.Engine.RunFailedAgain([Harness.Mac(1)]);
        await h.RunUntilAsync(() => h.Row(1).State == CameraState.Done, "Set IP ran again");
        Assert.Equal("192.168.15.10 assigned.", h.Row(1).Results[2]!.Message);

        await h.Engine.StopAsync(CancellationToken.None);
        await h.StartAsync(WithSetIp(h));
        Assert.Empty(h.Engine.Session!.Reserved);
        h.Devices.Add(new FakeDevice { Serial = "ACCC8E0000EE", Address = "192.168.15.10" }); // the camera of the last session
        h.Ack(2);
        await h.RunUntilAsync(() => h.Row(2).State == CameraState.Done, "the next session's camera");
        Assert.Equal("192.168.15.10 is in use, 192.168.15.11 assigned.", h.Row(2).Results[2]!.Message);
    }

    [Fact]
    public async Task No_answer_at_the_new_address_is_an_error_with_both_addresses_and_the_camera_stays_where_it_answers()
    {
        await using var h = new Harness();
        h.Outcomes[SetIpNode.TaskPluginId] = (TaskEndState.Failed, "The device did not answer at 192.168.15.10 within 90 seconds.");
        await h.StartAsync(WithSetIp(h));
        h.Ack(1);

        await h.RunUntilAsync(() => h.Row(1).State == CameraState.Failed, "Set IP failed");

        Assert.Equal(
            "The camera did not take 192.168.15.10: The device did not answer at 192.168.15.10 within 90 seconds. OADM keeps it at 192.168.15.101.",
            h.Row(1).Results[2]!.Message);
        Assert.Equal("192.168.15.101", h.Row(1).Address);
    }

    [Fact]
    public async Task A_used_up_pool_is_an_error()
    {
        await using var h = new Harness();
        h.Probe.Answering["192.168.15.10"] = true;
        h.Probe.Answering["192.168.15.11"] = true;
        await h.StartAsync(WithSetIp(h, Harness.Pool("192.168.15.10", "192.168.15.11")));
        h.Ack(1);

        await h.RunUntilAsync(() => h.Row(1).State == CameraState.Failed, "Set IP failed");

        Assert.Equal("192.168.15.10 and 192.168.15.11 are in use. No free address left in the pool.", h.Row(1).Results[2]!.Message);
        Assert.Empty(h.Ctx.TaskRuns);
    }

    [Fact]
    public void An_overlap_with_the_dhcp_range_is_a_warning_never_an_error()
    {
        var dhcp = new DhcpServerSummary(true, "Running", "Ethernet", "192.168.15.1", "255.255.255.0", "192.168.15.40", "192.168.15.200");
        var profile = new Harness().Profile(Harness.Node("ip", NodeKind.SetIp, Harness.Pool()));
        var context = new ProfileCheckContext(null, dhcp);

        var check = ProfileCheck.Check(profile, NodeRegistry.Default, context);

        Assert.True(check.IsValid);
        Assert.Equal("The pool overlaps the DHCP server's range 192.168.15.40 to 192.168.15.200.", check.NodeWarnings["ip"]);
        Assert.Empty(ProfileCheck.Check(profile, NodeRegistry.Default, new ProfileCheckContext(null, dhcp with { RangeStart = "192.168.15.100" })).NodeWarnings);
    }

    [Theory]
    [InlineData("192.168.15.10", "192.168.15.50", "255.255.255.0", "192.168.15.1", null)]
    [InlineData("", "192.168.15.50", "255.255.255.0", "192.168.15.1", "Enter the first address of the pool, e.g. 192.168.15.10.")]
    [InlineData("192.168.15.50", "192.168.15.10", "255.255.255.0", "192.168.15.1", "The last address must not be below the first.")]
    [InlineData("192.168.15.10", "192.168.16.10", "255.255.255.0", "192.168.15.1", "The first and last address must be in the same subnet.")]
    [InlineData("192.168.15.10", "192.168.15.50", "255.0.255.0", "192.168.15.1", "Enter a subnet mask, e.g. 255.255.255.0.")]
    [InlineData("192.168.15.10", "192.168.15.50", "255.255.255.0", "10.0.0.1", "The default router must be in the subnet of the pool.")]
    [InlineData("192.168.15.10", "192.168.15.50", "255.255.255.0", "192.168.15.20", "The default router must not be in the pool.")]
    [InlineData("192.168.15.0", "192.168.15.50", "255.255.255.0", "192.168.15.1", "192.168.15.0 is the network address of its subnet.")]
    public void The_pool_is_checked_with_one_error(string first, string last, string mask, string gateway, string? error) =>
        Assert.Equal(error, new SetIpNode().Check(Harness.Json(new SetIpSettings(first, last, mask, gateway, [])), ProfileCheckContext.Empty));

    [Fact]
    public async Task The_free_address_check_reads_the_devices_once_and_skips_them_without_probing()
    {
        await using var h = new Harness();
        h.Devices.AddRange(Enumerable.Range(0, 100).Select(i => new FakeDevice { Serial = $"ACCC8E{i:X6}", Address = $"10.1.{i / 250}.{i % 250 + 1}" }));
        h.Devices.AddRange(Enumerable.Range(10, 40).Select(i => new FakeDevice { Serial = $"B8A44F{i:X6}", Address = $"192.168.15.{i}" }));
        await h.StartAsync(WithSetIp(h));

        var take = await h.Engine.TakeAddressAsync(Harness.Mac(1), Harness.Pool(), CancellationToken.None);

        Assert.Equal("192.168.15.50", take.Address);
        Assert.Equal(40, take.InUse.Count);
        Assert.Equal(["192.168.15.50"], h.Probe.Probed);
        Assert.Equal("40 addresses are in use", SetIpNode.InUseText(take.InUse));
    }
}
