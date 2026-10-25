using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;

using Microsoft.Extensions.Time.Testing;

using Oadm.Plugins.DhcpServer.Client;
using Oadm.Plugins.DhcpServer.Protocol;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Tests.Shared;

using Xunit.Abstractions;

using Testing = Oadm.Sdk.Testing;

namespace Oadm.Plugins.DhcpServer.Tests;

/// <summary>
/// The pages of device groups at the device limit (CLAUDE.md, scale): a publish round never reads the device table (each
/// group's view is built once, again only when the group's devices change), and a lease change and a removal in one batch
/// both reach a group's page.
/// </summary>
public sealed class GroupScaleTests(ITestOutputHelper output)
{
    private const int Devices = 30_000;
    private const int Groups = 5;

    [Fact]
    public async Task Publish_rounds_for_group_pages_never_read_the_device_table_again() => await PublishRoundsAsync(rounds: 20);

    [Fact]
    [Trait("Category", "Perf")]
    public async Task Publish_rounds_for_group_pages_at_30000_devices_are_fast()
    {
        var elapsed = await PublishRoundsAsync(rounds: 100);
        output.WriteLine($"100 publish rounds for {Groups} groups at 30,000 devices: {elapsed.TotalMilliseconds:0} ms");
        Assert.True(elapsed < TimeSpan.FromSeconds(10), $"100 rounds took {elapsed.TotalMilliseconds:0} ms");
    }

    [Fact]
    public async Task A_change_and_a_removal_in_one_batch_both_reach_the_group_page()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-25T12:00:00Z", CultureInfo.InvariantCulture));
        var (devices, scopes, keys, members) = Setup();
        var fake = new Testing.FakeCoreContext();
        await using var service = new DhcpServerService(Options.Test(new FakeDhcpNetwork(), time: time), events: fake, scopes: scopes);
        var page = Testing.FakeCallContext.WithGroup(fake, members[0], "guard", keys[0]);
        service.Viewers.Seen(page);
        var now = time.GetUtcNow().UtcDateTime;
        var (a, b) = (Mac(devices[0]), Mac(devices[Groups])); // both in group 0
        service.Store.SetStatic(a, Ip.Of("10.0.0.100"), null, null, now);
        service.Store.SetStatic(b, Ip.Of("10.0.0.101"), null, null, now);
        await service.PublishAsync(CancellationToken.None);

        // One batch: a changes, b goes.
        service.Store.SetStatic(a, Ip.Of("10.0.0.102"), "Lobby", a, now);
        Assert.True(service.Store.DeleteStatic(b));
        await service.PublishAsync(CancellationToken.None);
        var last = fake.EventsSeenBy(page).Where(e => e.Topic == DhcpServerMethods.LeasesTopic).Select(e => DhcpJson.Deserialize<LeasesEvent>(e.PayloadJson)).Last();
        Assert.Equal(["10.0.0.102"], last.Changed.Select(l => l.Address));
        Assert.Equal([MacAddress.Format(b)], last.Removed);

        // The page applies both, also when a large change comes in parts of one version.
        using var vm = new DhcpServerViewModel(new StateContext(new DhcpState()));
        vm.ReplaceLeases([Lease(a, "10.0.0.100"), Lease(b, "10.0.0.101")], last.Version - 1);
        vm.HandleEvent(new PluginEvent(DhcpServerMethods.LeasesTopic, DhcpJson.Serialize(last)));
        Assert.Equal(["10.0.0.102"], vm.Rows.Select(r => r.Address));
        vm.ReplaceLeases([Lease(a, "10.0.0.100"), Lease(b, "10.0.0.101")], last.Version - 1);
        vm.HandleEvent(new PluginEvent(DhcpServerMethods.LeasesTopic, DhcpJson.Serialize(last with { Removed = [] })));
        vm.HandleEvent(new PluginEvent(DhcpServerMethods.LeasesTopic, DhcpJson.Serialize(last with { Changed = [] })));
        Assert.Equal(["10.0.0.102"], vm.Rows.Select(r => r.Address));
        vm.HandleEvent(new PluginEvent(DhcpServerMethods.LeasesTopic, DhcpJson.Serialize(new LeasesEvent(last.Version - 1, [Lease(b, "10.0.0.101")], []))));
        Assert.Equal(["10.0.0.102"], vm.Rows.Select(r => r.Address)); // an older version is already contained
    }

    [Fact]
    public void Large_changes_go_in_parts_of_one_version_and_small_ones_in_one_event()
    {
        var leases = Enumerable.Range(0, DhcpServerService.LeaseChunk + 1).Select(i => new LeaseInfo($"AC:CC:8E:00:{i / 256:X2}:{i % 256:X2}", "10.0.0.1", null, null, true, LeaseInfo.Active, null)).ToList();
        var parts = DhcpServerService.Parts(new LeasesEvent(7, leases, ["AC:CC:8E:FF:FF:FF"])).ToList();
        Assert.Equal(3, parts.Count);
        Assert.All(parts, p => Assert.Equal(7, p.Version));
        Assert.Single(DhcpServerService.Parts(new LeasesEvent(8, [.. leases.Take(10)],["AC:CC:8E:FF:FF:FF"])));
    }

    [Fact]
    public async Task A_group_reads_its_state_with_renew_and_users_with_all_devices_without()
    {
        var ctx = new Testing.FakeCoreContext();
        var device = ctx.Add(new Testing.FakeDevice { Address = "10.0.0.48" });
        await using var plugin = new DhcpServerPlugin(Options.Test(new FakeDhcpNetwork(), time: new FakeTimeProvider()));
        await plugin.StartAsync(ctx, CancellationToken.None);
        var key = ctx.FakeScopes.Add("Lobby", [device.Id]);
        var group = Testing.FakeCallContext.WithGroup(ctx, [device.Id], "guard", key);

        Assert.True(DhcpJson.Deserialize<DhcpState>(await ctx.InvokeAsync(plugin, group, DhcpServerMethods.GetState, null)).Renew);
        Assert.False(DhcpJson.Deserialize<DhcpState>(await ctx.InvokeAsync(plugin, ctx.CallAsAllDevices(), DhcpServerMethods.GetState, null)).Renew);
    }

    [Fact]
    public async Task A_group_page_reads_the_state_again_while_shown()
    {
        var reads = 0;
        var before = DhcpServerViewModel.RenewInterval;
        DhcpServerViewModel.RenewInterval = TimeSpan.FromMilliseconds(20);
        try
        {
            using var vm = new DhcpServerViewModel(new StateContext(new DhcpState { Renew = true }, () => Interlocked.Increment(ref reads)));
            vm.Activate();
            await Testing.Eventually.TrueAsync(() => Volatile.Read(ref reads) >= 3, "the state was read again");
            vm.Deactivate();
        }
        finally
        {
            DhcpServerViewModel.RenewInterval = before;
        }
    }

    /// <summary>A page of each group, then rounds with a lease of each group's devices. Returns the time of the rounds.</summary>
    private static async Task<TimeSpan> PublishRoundsAsync(int rounds)
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-25T12:00:00Z", CultureInfo.InvariantCulture));
        var (devices, scopes, keys, members) = Setup();
        var fake = new Testing.FakeCoreContext();
        await using var service = new DhcpServerService(Options.Test(new FakeDhcpNetwork(), time: time), events: fake, scopes: scopes);
        var pages = keys.Select((key, i) => Testing.FakeCallContext.WithGroup(fake, members[i], "user " + i, key)).ToList();
        pages.ForEach(service.Viewers.Seen);

        var watch = Stopwatch.StartNew();
        for (var round = 0; round < rounds; round++)
        {
            for (var g = 0; g < Groups; g++)
            {
                var index = (round * Groups) + g;
                service.Store.SetStatic(Mac(devices[index]), (uint)(0x0A000000 + 0x100 + index), null, null, time.GetUtcNow().UtcDateTime);
            }

            await service.PublishAsync(CancellationToken.None);
        }

        watch.Stop();
        Assert.Equal(Groups, scopes.Reads); // each group's devices once, never per round
        var seen = fake.EventsSeenBy(pages[0]).Where(e => e.Topic == DhcpServerMethods.LeasesTopic)
            .SelectMany(e => DhcpJson.Deserialize<LeasesEvent>(e.PayloadJson).Changed).ToList();
        Assert.Equal(rounds, seen.Count);
        var mine = members[0].Select(id => MacAddress.Format(Mac(devices.First(d => d.Id == id)))).ToHashSet(StringComparer.Ordinal);
        Assert.All(seen, l => Assert.Contains(l.Mac, mine));
        return watch.Elapsed;
    }

    private static ulong Mac(IDeviceInfo device) => MacAddress.TryParse(device.Serial, out var mac) ? mac : throw new ArgumentException(device.Serial);

    private static LeaseInfo Lease(ulong mac, string address) => new(MacAddress.Format(mac), address, null, null, true, LeaseInfo.Active, null);

    /// <summary>30,000 devices in 5 groups (device i in group i % 5), each with its serial number as MAC address.</summary>
    private static (List<IDeviceInfo> Devices, CountingDeviceScopes Scopes, List<string> Keys, List<List<Guid>> Members) Setup()
    {
        var devices = Enumerable.Range(0, Devices)
            .Select(i => (IDeviceInfo)new Testing.FakeDevice { Address = $"10.1.{i / 256 % 256}.{i % 256}", Serial = $"ACCC8E{i:X6}" }).ToList();
        var scopes = new CountingDeviceScopes(devices);
        var members = Enumerable.Range(0, Groups).Select(g => devices.Where((_, i) => i % Groups == g).Select(d => d.Id).ToList()).ToList();
        var keys = members.Select(scopes.Add).ToList();
        return (devices, scopes, keys, members);
    }

    /// <summary>A host that answers a fixed state; its live events never end.</summary>
    private sealed class StateContext(DhcpState state, Action? onRead = null) : Oadm.Sdk.Client.ICorePluginClientContext
    {
        public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct)
        {
            onRead?.Invoke();
            return Task.FromResult<string?>(DhcpJson.Serialize(state));
        }

        public async IAsyncEnumerable<PluginEvent> WatchEventsAsync([EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            yield break;
        }
    }
}
