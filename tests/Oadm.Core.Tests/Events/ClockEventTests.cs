using Oadm.Core.Devices;
using Oadm.Core.Events;
using Oadm.Core.Settings;
using Oadm.Core.Sites;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Events;
using Oadm.Sdk.Testing;
using Oadm.Tests.Shared;

namespace Oadm.Core.Tests.Events;

/// <summary>Any device: clock off follows the devices and the allowed clock difference (docs/spec/main-window.md "Clock").</summary>
public sealed class ClockEventTests
{
    [Fact]
    public void TheEventIsBuiltInInTheAnyDeviceGroupWithoutAPerDeviceTwin()
    {
        var type = Assert.Single(BuiltInEvents.Types, t => t.Id == BuiltInEvents.AnyClockOff);
        Assert.Equal("oadm.device.any-clock-off", type.Id);
        Assert.Equal("Any device: clock off", type.DisplayName);
        Assert.Equal(BuiltInEvents.AnyGroup, type.Group);
        Assert.Equal(EventScope.Server, type.Scope);
        Assert.Equal(BuiltInEvents.CountField, Assert.Single(type.DataFields).Name);
        Assert.DoesNotContain(BuiltInEvents.AnyOf, pair => pair.Any == BuiltInEvents.AnyClockOff);
    }

    [Fact]
    public async Task TheCountFollowsTheDevices()
    {
        await using var rig = await EventRulesRig.StartAsync();
        var watch = new AnyClockOffWatch(rig);
        var a = await rig.AddDeviceAsync("10.0.0.1");
        var b = await rig.AddDeviceAsync("10.0.0.2");
        await rig.SettleAsync();

        await rig.Devices.UpdateAsync(a.Id, d => d.ClockOffsetMs = 50_000, CancellationToken.None);
        await watch.WaitForAsync("1");

        await rig.Devices.UpdateAsync(b.Id, d => d.NtpState = NtpState.NotSynced, CancellationToken.None);
        await watch.WaitForAsync("2");

        await rig.Devices.UpdateAsync(a.Id, d => d.Status = DeviceStatus.Unreachable, CancellationToken.None); // not Ok: never off
        await watch.WaitForAsync("1");

        await rig.Devices.RemoveAsync(b.Id, CancellationToken.None);
        await watch.WaitForAsync(null);
        Assert.Equal(["1", "2", "1", null], watch.Counts);
    }

    [Fact]
    public async Task ADeviceKeepsItsPlaceInTheCountWhileItsSiteIsOffline()
    {
        var sites = new FakeSiteTransport();
        var site = sites.AddSite("Building B");
        await using var rig = await EventRulesRig.StartAsync(sites: sites);
        await using var publisher = new SiteStatusPublisher(sites, rig.Devices, rig.Devices.Changes);
        publisher.Start();
        var watch = new AnyClockOffWatch(rig);
        var off = await rig.Devices.AddAsync(new Device { Serial = "ACCC8E400001", Address = "192.168.0.20", SiteId = site.Id, Status = DeviceStatus.Ok, Model = "P3265-V", ClockOffsetMs = 50_000 }, CancellationToken.None);
        await watch.WaitForAsync("1");

        var handled = publisher.Handled;
        sites.SetOnline(site.Id, false);
        await Eventually.TrueAsync(() => publisher.Handled > handled, "the site went offline");
        await rig.SettleAsync();
        Assert.Equal(DeviceStatus.SiteOffline, (await rig.Devices.GetAsync(off.Id, CancellationToken.None))!.Status);

        handled = publisher.Handled;
        sites.SetOnline(site.Id, true);
        await Eventually.TrueAsync(() => publisher.Handled > handled, "the site is back");
        await rig.SettleAsync();

        Assert.Equal(["1"], watch.Counts); // never off and on again: rules on the event fire once
    }

    [Fact]
    public async Task ANewToleranceRepublishesOnlyTheDevicesWhoseStateChanges()
    {
        await using var rig = await EventRulesRig.StartAsync();
        var watch = new AnyClockOffWatch(rig);
        var five = await rig.AddDeviceAsync("10.0.0.1");
        var thirty = await rig.AddDeviceAsync("10.0.0.2");
        var inTime = await rig.AddDeviceAsync("10.0.0.3");
        await rig.Devices.UpdateAsync(five.Id, d => d.ClockOffsetMs = 5_000, CancellationToken.None);
        await rig.Devices.UpdateAsync(thirty.Id, d => d.ClockOffsetMs = -30_000, CancellationToken.None);
        await rig.Devices.UpdateAsync(inTime.Id, d => d.ClockOffsetMs = 300, CancellationToken.None);
        await watch.WaitForAsync("2");

        var seen = new List<Guid>();
        rig.Devices.Changes.Changed += (_, change) =>
        {
            lock (seen)
            {
                seen.Add(change.DeviceId);
            }
        };

        await rig.Settings.SetAsync(SettingKeys.PollingClockToleranceSeconds, 10, CancellationToken.None);
        await rig.Clock.Running;

        Assert.Equal(10, rig.Clock.Seconds);
        lock (seen)
        {
            Assert.Equal([five.Id], seen); // nothing written, one batch with the device that is no longer off
        }

        await watch.WaitForAsync("1");
        Assert.Equal(1, rig.Clock.Published);
    }

    [Fact]
    public async Task TheSameToleranceAgainPublishesNothing()
    {
        await using var rig = await EventRulesRig.StartAsync();
        await rig.Settings.SetServerSettingsAsync(await rig.Settings.GetServerSettingsAsync(CancellationToken.None), CancellationToken.None);
        await rig.Clock.Running;
        Assert.Equal(0, rig.Clock.Published);
    }

    /// <summary>The count of every Any device: clock off event as it arrives (null = off).</summary>
    private sealed class AnyClockOffWatch
    {
        private readonly List<string?> _counts = [];

        public AnyClockOffWatch(EventRulesRig rig)
        {
            Subscription = rig.Engine.SubscriptionsFor("test").Subscribe(e =>
            {
                if (e.TypeId == BuiltInEvents.AnyClockOff)
                {
                    lock (_counts)
                    {
                        _counts.Add(e.Kind == PublishedEventKind.Off ? null : e.Data.GetValueOrDefault(BuiltInEvents.CountField));
                    }
                }
            });
        }

        /// <summary>Ends with the rig's engine.</summary>
        public IDisposable Subscription { get; }

        public IReadOnlyList<string?> Counts
        {
            get
            {
                lock (_counts)
                {
                    return [.. _counts];
                }
            }
        }

        public Task WaitForAsync(string? count) =>
            Eventually.TrueAsync(() => Counts is { Count: > 0 } c && c[^1] == count, "Any device: clock off count " + (count ?? "off"));
    }
}
