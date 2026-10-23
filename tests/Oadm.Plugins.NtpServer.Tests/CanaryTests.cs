using System.Net;

using Microsoft.Extensions.Time.Testing;

using Oadm.Plugins.NtpServer.Serving;
using Oadm.Sdk.Plugins;

using PluginCanary = Oadm.Sdk.Testing.PluginCanary;
using Testing = Oadm.Sdk.Testing;

namespace Oadm.Plugins.NtpServer.Tests;

/// <summary>Device groups (docs/spec/device-scope.md): a server-wide service, saved by users with all devices, read by groups with their rows.</summary>
public sealed class CanaryTests
{
    [Fact]
    public async Task Save_is_for_users_with_all_devices_and_reading_shows_no_device_outside_the_group()
    {
        var ctx = new Testing.FakeCoreContext();
        var inside = ctx.Add(new Testing.FakeDevice { Address = "10.0.0.48" });
        var outside = ctx.Add(new Testing.FakeDevice { Address = "10.0.0.49" });
        await using var plugin = new NtpServerPlugin(new NtpServerOptions { Time = new FakeTimeProvider() });
        await plugin.StartAsync(ctx, CancellationToken.None);
        await new PluginCanary(ctx, plugin, inside, outside)
            .WithoutDevices(NtpServerMethods.GetState, "server-wide state; the requests and sites of the user's devices only")
            .AllDevicesOnly(NtpServerMethods.Save, """{"enabled":false}""")
            .RunAsync();
    }

    [Fact]
    public async Task A_group_page_gets_the_requests_and_sites_of_its_devices_only()
    {
        var time = new FakeTimeProvider();
        var ctx = new Testing.FakeCoreContext();
        var siteA = ctx.FakeSites.Add("Building A").Id;
        ctx.FakeSites.Add("Building B");
        var local = ctx.Add(new Testing.FakeDevice { Address = "10.0.0.48" });
        var atSite = ctx.Add(new Testing.FakeDevice { Address = "10.1.0.5", SiteId = siteA, SiteName = "Building A" });
        ctx.Add(new Testing.FakeDevice { Address = "10.0.0.49" });
        await using var plugin = new NtpServerPlugin(new NtpServerOptions { Time = time });
        await plugin.StartAsync(ctx, CancellationToken.None);
        var key = ctx.FakeScopes.Add("Group", [local.Id, atSite.Id]);
        var group = Testing.FakeCallContext.WithGroup(ctx, [local.Id, atSite.Id], "tech2", key);
        var admin = ctx.CallAsAllDevices();
        var log = plugin.Service!.Log;
        void Request(string address) => log.Add(time.GetUtcNow().UtcDateTime, ClientAddress.FromIPAddress(IPAddress.Parse(address)), 1, RequestResult.Answered);

        Request("10.0.0.49");
        var state = NtpJson.Deserialize<NtpState>(await ctx.InvokeAsync(plugin, group, NtpServerMethods.GetState, null));
        Assert.Empty(state.Requests);
        Assert.Equal([siteA], state.SiteChoices.Select(s => s.Id));
        var full = NtpJson.Deserialize<NtpState>(await ctx.InvokeAsync(plugin, admin, NtpServerMethods.GetState, null));
        Assert.Equal((1, 2), (full.Requests.Count, full.SiteChoices.Count));
        Assert.Empty(ctx.RefusedAccesses);

        // Live: the group's page gets only the requests of its devices; the administrator's page gets every one.
        Request("10.0.0.48");
        Request("10.0.0.49");
        await Testing.Eventually.TrueAsync(() =>
        {
            time.Advance(TimeSpan.FromMilliseconds(500));
            return ctx.EventsSeenBy(group).Any(e => e.Topic == NtpServerMethods.RequestsTopic);
        }, "the requests reached the group's page");
        var mine = ctx.EventsSeenBy(group).Where(e => e.Topic == NtpServerMethods.RequestsTopic)
            .SelectMany(e => NtpJson.Deserialize<RequestsEvent>(e.PayloadJson).Entries).ToList();
        Assert.Equal(["10.0.0.48"], mine.Select(r => r.Client));
        Assert.DoesNotContain(ctx.EventsSeenBy(group), e => e.PayloadJson?.Contains("Building B", StringComparison.Ordinal) == true);
        Assert.Contains(ctx.EventsSeenBy(admin), e => e.Topic == NtpServerMethods.RequestsTopic && e.PayloadJson!.Contains("10.0.0.49", StringComparison.Ordinal));
    }
}
