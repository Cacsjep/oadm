using System.Text.Json;

using Oadm.Plugins.Provisioning.Model;
using Oadm.Plugins.Provisioning.Nodes;
using Oadm.Sdk.Network;
using Oadm.Sdk.Tasks;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Provisioning.Tests;

/// <summary>Network's address pool (spec node 2, rules 5 and 6): never the same address twice, the DHCP server untouched, the address last.</summary>
public sealed class NetworkNodeTests
{
    private static Task<NodeResult> RunAsync(Harness h, CameraInfo camera, NetworkNodeSettings settings)
    {
        var flow = Harness.Node("net", NodeKind.Network, settings);
        return new NetworkNode().RunAsync(new NodeRun(h.Engine, camera, h.Profile(flow), flow), CancellationToken.None);
    }

    private static async Task<NodeResult> RunEndedAsync(Harness h, CameraInfo camera, NetworkNodeSettings settings)
    {
        var run = RunAsync(h, camera, settings);
        await h.RunUntilAsync(() => run.IsCompleted, "the node ended");
        return await run;
    }

    private static (CameraInfo Camera, FakeDevice Device) Camera(Harness h, int i)
    {
        var (_, device) = h.NewCamera(i, hasPassword: true);
        return (new CameraInfo(device!.Serial, device.Address, device.Model, device.Id), device);
    }

    [Fact]
    public async Task Parallel_cameras_never_get_the_same_address()
    {
        await using var h = new Harness();
        h.Probe.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runs = Enumerable.Range(1, 3).Select(i => RunAsync(h, Camera(h, i).Camera, Harness.Pool())).ToList();

        await Eventually.TrueAsync(() => !h.Probe.Probed.IsEmpty, "the first probe");
        Assert.Single(h.Probe.Probed); // one take at a time: the others wait for the lock

        h.Probe.Gate.SetResult();
        await h.RunUntilAsync(() => runs.All(r => r.IsCompleted), "every camera done");

        Assert.All(runs, r => Assert.Equal(NodeState.Success, r.Result.State));
        Assert.Equal(["192.168.15.10", "192.168.15.11", "192.168.15.12"], h.Engine.Reserved.Values.Order(StringComparer.Ordinal));
        Assert.Equal(["192.168.15.10", "192.168.15.11", "192.168.15.12"], h.Probe.Probed);
        Assert.All(h.Ctx.TaskRuns, run => Assert.Equal(NetworkNode.AssignIpTaskId, run.PluginId)); // nothing touches the DHCP server
    }

    [Fact]
    public async Task The_next_free_address_skips_reservations_oadm_devices_and_answering_addresses_and_says_which()
    {
        await using var h = new Harness();
        var first = await RunEndedAsync(h, Camera(h, 1).Camera, Harness.Pool());
        Assert.Equal(NodeResult.Success("192.168.15.10 assigned."), first);
        h.Devices.Add(new FakeDevice { Serial = "ACCC8E0000FF", Address = "192.168.15.11" });
        h.Probe.Answering["192.168.15.12"] = true;

        var second = await RunEndedAsync(h, Camera(h, 2).Camera, Harness.Pool());

        Assert.Equal(NodeResult.Success("192.168.15.11 and 192.168.15.12 are in use, 192.168.15.13 assigned."), second);
        Assert.DoesNotContain("192.168.15.10", h.Probe.Probed.Skip(1)); // reserved: not probed again
        using var payload = JsonDocument.Parse(h.Ctx.TaskRuns[^1].PayloadJson!);
        Assert.Equal("static", payload.RootElement.GetProperty("ipv4").GetProperty("mode").GetString());
        Assert.Equal(24, payload.RootElement.GetProperty("ipv4").GetProperty("prefixLength").GetInt32());
        Assert.False(payload.RootElement.TryGetProperty("hostName", out _));
    }

    [Fact]
    public async Task A_given_address_stays_reserved_until_it_is_an_oadm_device_and_survives_a_restart()
    {
        await using var h = new Harness();
        h.Outcomes[NetworkNode.AssignIpTaskId] = (TaskEndState.Failed, "The device did not answer at 192.168.15.10.");
        var (camera, device) = Camera(h, 1);
        Assert.Equal(NodeState.Error, (await RunEndedAsync(h, camera, Harness.Pool())).State);
        Assert.Equal("192.168.15.10", h.Engine.Reserved[camera.Serial]);

        await h.RestartAsync();
        h.Outcomes.Clear();
        Assert.Equal(NodeResult.Success("192.168.15.11 assigned."), await RunEndedAsync(h, Camera(h, 2).Camera, Harness.Pool()));
        Assert.Equal(NodeResult.Success("192.168.15.10 assigned."), await RunEndedAsync(h, camera, Harness.Pool())); // its own one again

        h.Devices.Add(device with { Address = "192.168.15.10" }); // OADM followed it: the reservation is done
        await RunEndedAsync(h, Camera(h, 3).Camera, Harness.Pool());
        Assert.False(h.Engine.Reserved.ContainsKey(camera.Serial));
    }

    [Fact]
    public async Task A_used_up_pool_is_an_error_and_the_camera_keeps_its_address()
    {
        await using var h = new Harness();
        h.Probe.Answering["192.168.15.10"] = true;
        h.Probe.Answering["192.168.15.11"] = true;

        var result = await RunEndedAsync(h, Camera(h, 1).Camera, Harness.Pool("192.168.15.10", "192.168.15.11"));

        Assert.Equal(NodeResult.Error("192.168.15.10 and 192.168.15.11 are in use. " + NetworkNode.PoolUsedUp), result);
        Assert.Empty(h.Ctx.TaskRuns);
    }

    [Fact]
    public async Task No_answer_at_the_new_address_is_an_error_with_both_addresses()
    {
        await using var h = new Harness();
        h.Outcomes[NetworkNode.AssignIpTaskId] = (TaskEndState.Failed, "The device did not answer at 192.168.15.10 within 90 seconds.");
        var (camera, _) = Camera(h, 1);

        var result = await RunEndedAsync(h, camera, Harness.Pool());

        Assert.Equal(NodeResult.Error($"The camera did not take 192.168.15.10: The device did not answer at 192.168.15.10 within 90 seconds. OADM keeps it at {camera.Address}."), result);
    }

    [Fact]
    public async Task The_dns_servers_and_search_domain_go_first_and_the_address_last()
    {
        await using var h = new Harness();

        var result = await RunEndedAsync(h, Camera(h, 1).Camera, Harness.Pool() with { DnsServers = ["10.0.0.1"], SearchDomain = "site.local" });

        Assert.Equal(NodeResult.Success("192.168.15.10 assigned. DNS 10.0.0.1, search domain site.local."), result);
        Assert.Equal([NetworkNode.NetworkTaskId, NetworkNode.AssignIpTaskId], h.Ctx.TaskRuns.Select(r => r.PluginId));
        using var address = JsonDocument.Parse(h.Ctx.TaskRuns[1].PayloadJson!);
        Assert.True(address.RootElement.GetProperty("dns").GetProperty("keepDomains").GetBoolean());

        // The DNS write failing: the address is never written.
        h.Outcomes[NetworkNode.NetworkTaskId] = (TaskEndState.Failed, "DNS: the camera refused. Nothing was changed.");
        var count = h.Ctx.TaskRuns.Count;
        Assert.Equal(NodeResult.Error("DNS: the camera refused. Nothing was changed."), await RunEndedAsync(h, Camera(h, 2).Camera, Harness.Pool() with { DnsServers = ["10.0.0.1"] }));
        Assert.Equal(count + 1, h.Ctx.TaskRuns.Count);
    }

    [Fact]
    public async Task A_camera_at_an_address_of_the_pool_keeps_it()
    {
        await using var h = new Harness();
        var device = new FakeDevice { Serial = Harness.Serial(1), Address = "192.168.15.20" };
        h.AutoAdd.Cam(device.Serial, hasPassword: true);
        h.Devices.Add(device);
        h.Probe.Answering["192.168.15.20"] = true; // the camera itself answers there

        var result = await RunEndedAsync(h, new CameraInfo(device.Serial, device.Address, null, device.Id), Harness.Pool());

        Assert.Equal(NodeResult.Success("The camera is at 192.168.15.20 already."), result);
        Assert.Empty(h.Ctx.TaskRuns);
    }

    [Theory]
    [InlineData("192.168.15.10", "192.168.15.50", "255.255.255.0", "192.168.15.1", null)]
    [InlineData("192.168.15.10", "192.168.15.50", "255.255.255.0", "192.168.15.20", "The default router must not be in the pool.")]
    [InlineData("192.168.15.50", "192.168.15.10", "255.255.255.0", "192.168.15.1", "The last address must not be below the first.")]
    [InlineData("192.168.15.", "192.168.15.", "255.255.255.0", null, "Enter the first address of the pool, e.g. 192.168.15.10.")]
    [InlineData("192.168.15.10", "192.168.16.10", "255.255.255.0", "192.168.15.1", "The first and last address must be in the same subnet.")]
    public void The_pool_is_checked(string first, string last, string mask, string? router, string? error) =>
        Assert.Equal(error, new NetworkNode().Check(Harness.Json(new NetworkNodeSettings(IpMode.Pool, first, last, mask, router)), ProfileCheckContext.Empty));

    [Fact]
    public void A_pool_that_overlaps_the_dhcp_range_is_a_warning_never_an_error()
    {
        var dhcp = new DhcpServerSummary(true, "Running", "Ethernet", "192.168.15.1", "255.255.255.0", "192.168.15.100", "192.168.15.200");
        var context = new ProfileCheckContext(null, dhcp);
        var overlapping = Harness.Json(Harness.Pool("192.168.15.150", "192.168.15.160"));

        Assert.Null(new NetworkNode().Check(overlapping, context));
        Assert.Equal("The pool overlaps the DHCP server's range 192.168.15.100 to 192.168.15.200.", new NetworkNode().Warning(overlapping, context));
        Assert.Null(new NetworkNode().Warning(Harness.Json(Harness.Pool()), context));
    }

    [Fact]
    public void A_new_network_node_starts_in_the_dhcp_servers_network()
    {
        var dhcp = new DhcpServerSummary(true, "Running", "Ethernet", "192.168.15.1", "255.255.255.0", "192.168.15.100", "192.168.15.200");

        var settings = NetworkNode.DefaultsFor(dhcp);
        var empty = NetworkNode.DefaultsFor(null);

        Assert.Equal(("192.168.15.", "192.168.15.", "255.255.255.0"), (settings.First, settings.Last, settings.SubnetMask));
        Assert.Equal((null, null, "255.255.255.0"), (empty.First, empty.Last, empty.SubnetMask));
        Assert.Equal(IpMode.Keep, settings.Ip);
    }
}
