using Microsoft.EntityFrameworkCore;

using Oadm.Core.Devices;
using Oadm.Core.Events;
using Oadm.Core.Settings;
using Oadm.Core.Tests.Events;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Events;
using Oadm.Sdk.Testing;

using Xunit.Abstractions;

namespace Oadm.Core.Tests.Perf;

/// <summary>
/// Clock health at 30,000 devices (docs/spec/main-window.md "Clock"): the count of Any device: clock off follows one batch of
/// every device, and a new allowed difference republishes only the devices whose state changes, in one batch.
/// </summary>
[Trait(PerfScale.Category, PerfScale.Perf)]
[Collection(PerfScale.OneAtATime)]
public sealed class ClockScaleTests(ITestOutputHelper output)
{
    [Fact]
    public async Task TheCountAndANewToleranceStayLinearForThirtyThousandDevices()
    {
        await using var rig = await EventRulesRig.StartAsync();
        var counts = new List<string?>();
        using var subscription = rig.Engine.SubscriptionsFor("test").Subscribe(e =>
        {
            if (e.TypeId == BuiltInEvents.AnyClockOff)
            {
                lock (counts)
                {
                    counts.Add(e.Kind == PublishedEventKind.Off ? null : e.Data.GetValueOrDefault(BuiltInEvents.CountField));
                }
            }
        });

        // Every 3rd device 5 s ahead, every 10th (not a 3rd) NTP not synced, the rest in time.
        var ids = new List<Guid>(PerfScale.Devices);
        await using (var db = await rig.DbFactory.CreateDbContextAsync())
        {
            for (var i = 0; i < PerfScale.Devices; i++)
            {
                var device = new Device
                {
                    Serial = $"B8A44F{i:X6}",
                    Address = $"10.1.{i / 250}.{i % 250}",
                    Model = "P3265-V",
                    Status = DeviceStatus.Ok,
                    ClockOffsetMs = i % 3 == 0 ? 5_000 : 200,
                    NtpState = i % 3 != 0 && i % 10 == 0 ? NtpState.NotSynced : NtpState.Synced,
                };
                ids.Add(device.Id);
                db.Devices.Add(device);
            }

            await db.SaveChangesAsync();
        }

        var ahead = (PerfScale.Devices + 2) / 3;
        var notSynced = Enumerable.Range(0, PerfScale.Devices).Count(i => i % 3 != 0 && i % 10 == 0);
        string? Last()
        {
            lock (counts)
            {
                return counts.Count == 0 ? null : counts[^1];
            }
        }

        await PerfScale.MeasureAsync(output, "Any device: clock off over 30,000 published devices", TimeSpan.FromSeconds(10), async () =>
        {
            await rig.Devices.PublishStoredAsync(ids);
            await rig.SettleAsync();
            await Eventually.TrueAsync(() => Last() == (ahead + notSynced).ToString(System.Globalization.CultureInfo.InvariantCulture), "the count of every device");
        });

        var seen = 0;
        rig.Devices.Changes.Changed += (_, _) => Interlocked.Increment(ref seen);
        var version = ((DeviceChangeFeed)rig.Devices.Changes).Version;
        await PerfScale.MeasureAsync(output, "Allowed clock difference 2 -> 10 s at 30,000 devices", TimeSpan.FromSeconds(5), async () =>
        {
            await rig.Settings.SetAsync(SettingKeys.PollingClockToleranceSeconds, 10, CancellationToken.None);
            await rig.Clock.Running;
            await rig.SettleAsync();
            await Eventually.TrueAsync(() => Last() == notSynced.ToString(System.Globalization.CultureInfo.InvariantCulture), "the count after the new tolerance");
        });

        var batches = (int)(((DeviceChangeFeed)rig.Devices.Changes).Version - version);
        Assert.Equal(ahead, Volatile.Read(ref seen)); // only the devices 5 s ahead changed state
        Assert.Equal(1, batches);
    }
}
