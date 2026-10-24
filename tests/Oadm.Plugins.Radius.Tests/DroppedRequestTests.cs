using System.Net;

using Oadm.Plugins.Radius.Api;
using Oadm.Plugins.Radius.Server;
using Oadm.Plugins.Radius.Tests.Support;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Radius.Tests;

/// <summary>
/// Requests dropped before a session starts become log rows (docs/spec/radius-server.md "Log"): a sender that is no switch
/// and a wrong shared secret, at most one row per sender and reason per minute, only for users with all devices.
/// </summary>
public sealed class DroppedRequestTests
{
    private static async Task SendAsync(PluginSwitch sender, int count, Func<bool> counted, string what)
    {
        for (var i = 0; i < count; i++)
        {
            Assert.Null((await sender.SendAsync([], null, TimeSpan.FromMilliseconds(1))).Answer);
        }

        await Eventually.TrueAsync(counted, what);
    }

    private static Task<LogPage> LogAsync(PluginHarness harness, string? search = null, ICallContext? call = null) =>
        harness.CallAsync<LogPage>(RadiusMethods.ListLog, new LogQuery(search), call);

    [Fact]
    public async Task ARequestFromASwitchThatIsNotAddedIsLoggedOncePerMinuteWithTheCount()
    {
        await using var harness = await PluginHarness.CreateAsync();
        var stranger = harness.Switch("10.0.0.9");

        await SendAsync(stranger, 1, () => harness.Service.GetState(true).UnknownRequests.Count == 1, "the first request counted");
        harness.Tick();

        var row = Assert.Single((await LogAsync(harness)).Rows);
        Assert.Equal("10.0.0.9 (not added)", row.SwitchName);
        Assert.Equal(LogResults.Failed, row.Result);
        Assert.Equal(DroppedRequestTexts.NotAdded, row.Reason);
        Assert.Equal(1, row.Requests);
        Assert.Equal("10.0.0.9", row.Sender);
        Assert.Null(row.SenderSiteId);
        Assert.Null(row.DeviceId);
        Assert.Equal(string.Empty, row.DeviceName);
        Assert.False(row.HasCertificate);
        Assert.Null(row.CertificateSerial);
        var published = Assert.Single(harness.Ctx.PublishedEvents, e => e.Topic == RadiusTopics.Log);
        Assert.Equal(PluginEventAudienceKind.DeviceRows, published.Audience.Kind);
        Assert.Equal([Guid.Empty], published.Audience.DeviceIds); // no device: users with all devices only
        Assert.Empty(harness.Publisher.Fired); // no device tried: no "Authentication failed"

        // More requests within the minute: counted, no row yet.
        await SendAsync(stranger, 11, () => harness.Service.GetState(true).UnknownRequests.Count == 12, "the next requests counted");
        harness.Tick(10);
        Assert.Equal(1, (await LogAsync(harness)).Total);

        // The minute ended: one row for the eleven.
        harness.Tick(110);
        var page = await LogAsync(harness);
        Assert.Equal(2, page.Total);
        Assert.Equal(11, page.Rows[0].Requests);
        Assert.Equal(DroppedRequestTexts.NotAdded + " 11 requests in the last minute.", page.Rows[0].Reason);

        // Quiet for a minute: no row, the sender is forgotten; the search finds the rows.
        harness.Tick(130);
        Assert.Equal(2, (await LogAsync(harness)).Total);
        Assert.Equal(0, harness.Service.Dropped.Followed);
        Assert.Equal(2, (await LogAsync(harness, "NOT ADDED")).Total);
        Assert.Equal(1, (await LogAsync(harness, "11 requests")).Total);
        Assert.Equal(2, (await LogAsync(harness, "10.0.0.9")).Total);
    }

    [Fact]
    public async Task AWrongSharedSecretIsLoggedWithTheSwitchName()
    {
        await using var harness = await PluginHarness.CreateAsync();
        await harness.AddSwitchAsync("Core switch", "10.0.0.2");
        var wrong = harness.Switch(secret: "another-secret-of-24-chars");

        await SendAsync(wrong, 3, () => harness.Service.ListSwitches().Switches[0].DroppedCount == 3, "the dropped requests counted");
        harness.Tick();

        var row = Assert.Single((await LogAsync(harness)).Rows);
        Assert.Equal("Core switch", row.SwitchName);
        Assert.Equal(DroppedRequestTexts.WrongSecret + " 3 requests in the last minute.", row.Reason);
        Assert.Equal(3, row.Requests);
        Assert.Null(row.Sender); // an added switch: nothing to add
        Assert.Null(row.DeviceId);
        Assert.Equal(1, (await LogAsync(harness, "wrong shared secret")).Total);
    }

    [Fact]
    public async Task UsersWithADeviceGroupNeverSeeTheRows()
    {
        await using var harness = await PluginHarness.CreateAsync();
        var device = harness.AddDevice("ACCC8E123456");
        await harness.AddSwitchAsync("Core switch", "10.0.0.2");
        await SendAsync(harness.Switch("10.0.0.9"), 1, () => harness.Service.GetState(true).UnknownRequests.Count == 1, "the unknown sender counted");
        await SendAsync(harness.Switch(secret: "another-secret-of-24-chars"), 1, () => harness.Service.ListSwitches().Switches[0].DroppedCount == 1, "the wrong secret counted");
        harness.Tick();

        Assert.Equal(2, (await LogAsync(harness)).Total);
        var group = harness.Ctx.CallAsGroup([device.Id]);
        Assert.Equal(0, (await LogAsync(harness, null, group)).Total);
        Assert.Equal(0, (await LogAsync(harness, "not added", group)).Total);
    }

    [Fact]
    public void SendersOfEachReasonAreThrottledApartAndSendersThatAreNoSwitchAreCapped()
    {
        var dropped = new DroppedRequests();
        var start = new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);
        var address = IPAddress.Parse("10.0.0.2");
        dropped.Count(DroppedRequests.Kind.NotAdded, null, address, "10.0.0.2 (not added)", start);
        dropped.Count(DroppedRequests.Kind.WrongSecret, null, address, "Core switch", start);
        dropped.Count(DroppedRequests.Kind.NotAdded, Guid.NewGuid(), address, "10.0.0.2 at Building B (not added)", start); // another site
        Assert.Equal(3, dropped.Drain(start).Count);
        dropped.Count(DroppedRequests.Kind.NotAdded, null, address, "10.0.0.2 (not added)", start.AddSeconds(30));
        Assert.Empty(dropped.Drain(start.AddSeconds(59)));
        Assert.Equal(1, Assert.Single(dropped.Drain(start.AddSeconds(60))).Requests);

        var flood = new DroppedRequests();
        for (var i = 0; i < 1000; i++)
        {
            flood.Count(DroppedRequests.Kind.NotAdded, null, new IPAddress([10, 1, (byte)(i >> 8), (byte)i]), "x", start);
        }

        flood.Count(DroppedRequests.Kind.WrongSecret, null, address, "Core switch", start); // added switches are never capped
        Assert.Equal(DroppedRequests.MaxUnknownSenders + 1, flood.Drain(start).Count);
    }
}
