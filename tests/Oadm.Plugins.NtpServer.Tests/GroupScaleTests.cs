using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;

using Microsoft.Extensions.Time.Testing;

using Oadm.Plugins.NtpServer.Client;
using Oadm.Plugins.NtpServer.Serving;
using Oadm.Plugins.Shared;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Tests.Shared;

using Xunit.Abstractions;

using Testing = Oadm.Sdk.Testing;

namespace Oadm.Plugins.NtpServer.Tests;

/// <summary>
/// The pages of device groups at the device limit (CLAUDE.md, scale): a publish round never reads the device table; each
/// group's view is built once and again only when the group's devices change, and a group whose pages stopped reading the
/// state is forgotten.
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
    public async Task A_group_view_is_built_again_when_the_group_changes_or_gets_old()
    {
        var time = new FakeTimeProvider();
        var (devices, scopes, keys, _) = Setup();
        using var viewers = new ViewerScopes(scopes, time);
        async Task<NtpGroupView> View() => await viewers.ViewAsync(keys[0], NtpGroupView.From, CancellationToken.None);

        var first = await View();
        Assert.Same(first, await View());
        Assert.Equal(1, scopes.Reads);

        scopes.RaiseChanged(keys[0]); // the group's devices changed
        var second = await View();
        Assert.NotSame(first, second);
        Assert.Equal(2, scopes.Reads);
        scopes.RaiseChanged(keys[1]); // another group: nothing to do
        Assert.Same(second, await View());

        time.Advance(ViewerScopes.ViewLifetime); // an address changed: no group change, read again after a while
        Assert.NotSame(second, await View());
        Assert.Equal(3, scopes.Reads);

        scopes.Delete(keys[0]); // a deleted group sees nothing
        var gone = await View();
        Assert.False(gone.Covers(new RequestEntry(1, DateTime.UtcNow, devices[0].Address, null, RequestEntry.Answered)));
    }

    [Fact]
    public void A_group_whose_pages_stopped_reading_is_forgotten()
    {
        var time = new FakeTimeProvider();
        var (_, scopes, keys, _) = Setup();
        using var viewers = new ViewerScopes(scopes, time, ViewerScopes.PageKeyLifetime);
        var fake = new Testing.FakeCoreContext();
        var a = Testing.FakeCallContext.WithGroup(fake, [], "a", keys[0]);
        var b = Testing.FakeCallContext.WithGroup(fake, [], "b", keys[1]);
        viewers.Seen(a);
        viewers.Seen(b);
        Assert.True(viewers.Renews);

        time.Advance(ViewerScopes.RenewInterval);
        viewers.Seen(a); // a's page read the state again
        time.Advance(ViewerScopes.PageKeyLifetime - ViewerScopes.RenewInterval + TimeSpan.FromSeconds(1));
        Assert.Equal([keys[0]], viewers.Keys());

        scopes.Delete(keys[0]);
        Assert.Empty(viewers.Keys());
        Assert.False(new ViewerScopes(scopes).Renews); // PKI and EST keep their keys until the group is deleted
    }

    [Fact]
    public async Task A_group_reads_its_state_with_renew_and_users_with_all_devices_without()
    {
        var ctx = new Testing.FakeCoreContext();
        var device = ctx.Add(new Testing.FakeDevice { Address = "10.0.0.48" });
        await using var plugin = new NtpServerPlugin(new NtpServerOptions { Time = new FakeTimeProvider() });
        await plugin.StartAsync(ctx, CancellationToken.None);
        var key = ctx.FakeScopes.Add("Group", [device.Id]);
        var group = Testing.FakeCallContext.WithGroup(ctx, [device.Id], "tech", key);

        Assert.True(NtpJson.Deserialize<NtpState>(await ctx.InvokeAsync(plugin, group, NtpServerMethods.GetState, null)).Renew);
        Assert.False(NtpJson.Deserialize<NtpState>(await ctx.InvokeAsync(plugin, ctx.CallAsAllDevices(), NtpServerMethods.GetState, null)).Renew);
        Assert.Equal([key], plugin.Service!.Viewers.Keys());
    }

    [Fact]
    public async Task A_group_page_reads_the_state_again_while_shown()
    {
        var reads = 0;
        var before = NtpServerViewModel.RenewInterval;
        NtpServerViewModel.RenewInterval = TimeSpan.FromMilliseconds(20);
        try
        {
            using var vm = new NtpServerViewModel(new RenewContext(() => Interlocked.Increment(ref reads)));
            vm.Activate();
            await Testing.Eventually.TrueAsync(() => Volatile.Read(ref reads) >= 3, "the state was read again");
            vm.Deactivate();
        }
        finally
        {
            NtpServerViewModel.RenewInterval = before;
        }
    }

    [Fact]
    public void Chosen_sites_none_of_which_is_the_groups_serve_no_site_of_the_group()
    {
        var site = Guid.NewGuid();
        var view = NtpGroupView.From([new Testing.FakeDevice { Address = "10.1.0.5", SiteId = site }]);
        var other = new NtpState { Config = new NtpConfig { ServeAtSites = true, ServeSites = [Guid.NewGuid()] } };
        Assert.False(view.Filter(other).Config.ServeAtSites); // never "every site"
        var all = new NtpState { Config = new NtpConfig { ServeAtSites = true } };
        Assert.True(view.Filter(all).Config.ServeAtSites);
        var mine = new NtpState { Config = new NtpConfig { ServeAtSites = true, ServeSites = [site, Guid.NewGuid()] } };
        Assert.Equal([site], view.Filter(mine).Config.ServeSites);
        Assert.True(view.Filter(mine).Config.ServeAtSites);
    }

    /// <summary>A page of each group, then rounds with requests of every group's devices. Returns the time of the rounds.</summary>
    private static async Task<TimeSpan> PublishRoundsAsync(int rounds)
    {
        var time = new FakeTimeProvider();
        var (devices, scopes, keys, members) = Setup();
        var fake = new Testing.FakeCoreContext();
        await using var service = new NtpServerService(new NtpServerOptions { Time = time }, events: fake, scopes: scopes);
        var pages = keys.Select((key, i) => Testing.FakeCallContext.WithGroup(fake, members[i], "user " + i, key)).ToList();
        pages.ForEach(service.Viewers.Seen);

        var watch = Stopwatch.StartNew();
        for (var round = 0; round < rounds; round++)
        {
            for (var g = 0; g < Groups; g++)
            {
                var device = devices[(round * Groups) + g];
                service.Log.Add(time.GetUtcNow().UtcDateTime, ClientAddress.FromIPAddress(IPAddress.Parse(device.Address)), 1, RequestResult.Answered);
            }

            await service.PublishAsync(CancellationToken.None);
        }

        watch.Stop();
        Assert.Equal(Groups, scopes.Reads); // each group's devices once, never per round
        var seen = fake.EventsSeenBy(pages[0]).Where(e => e.Topic == NtpServerMethods.RequestsTopic)
            .SelectMany(e => NtpJson.Deserialize<RequestsEvent>(e.PayloadJson).Entries).ToList();
        Assert.Equal(rounds, seen.Count);
        var mine = members[0].ToHashSet();
        Assert.All(seen, r => Assert.Contains(devices.First(d => d.Address == r.Client).Id, mine));
        return watch.Elapsed;
    }

    /// <summary>30,000 devices in 5 groups (device i in group i % 5).</summary>
    private static (List<IDeviceInfo> Devices, CountingDeviceScopes Scopes, List<string> Keys, List<List<Guid>> Members) Setup()
    {
        var devices = Enumerable.Range(0, Devices)
            .Select(i => (IDeviceInfo)new Testing.FakeDevice { Address = $"10.{i / 65536}.{i / 256 % 256}.{i % 256}" }).ToList();
        var scopes = new CountingDeviceScopes(devices);
        var members = Enumerable.Range(0, Groups).Select(g => devices.Where((_, i) => i % Groups == g).Select(d => d.Id).ToList()).ToList();
        var keys = members.Select(scopes.Add).ToList();
        return (devices, scopes, keys, members);
    }

    /// <summary>A host whose state asks the page to read again; the live events never end.</summary>
    private sealed class RenewContext(Action onRead) : Oadm.Sdk.Client.ICorePluginClientContext
    {
        public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct)
        {
            onRead();
            return Task.FromResult<string?>(NtpJson.Serialize(new NtpState { Renew = true }));
        }

        public async IAsyncEnumerable<PluginEvent> WatchEventsAsync([EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            yield break;
        }
    }
}
