using Oadm.Core.Devices;
using Oadm.Core.Tests.Persistence;
using Oadm.Core.Vapix;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Testing;

namespace Oadm.Core.Tests.Vapix;

/// <summary>
/// The days of "expires soon" PKI hands the host (docs/spec/pki.md "Policies", Expiry warning): only PKI's count, a failing
/// lookup counts as 30, and a change sends every device row again (the device grid's Certificate expires chip).
/// </summary>
public sealed class ExpiryWarningTests
{
    [Fact]
    public void OnlyPkisDaysCountAndWithoutThemEveryDeviceHas30()
    {
        var registry = new TrustAnchorRegistry();
        var device = new FakeDevice { Serial = "ACCC8E000001", Tags = ["PTZ"] };
        Assert.Equal((30, 30), (registry.ExpiryWarningDays(device), registry.ExpiryDefaultDays));

        registry.For("oadm.other").SetExpiryWarning(_ => 90, 90);
        Assert.Equal(30, registry.ExpiryWarningDays(device)); // another plugin changes nothing

        registry.For(TrustAnchorRegistry.RenewalOwner).SetExpiryWarning(d => d.Tags.Contains("PTZ") ? 60 : 45, 60);
        Assert.Equal((60, 60), (registry.ExpiryWarningDays(device), registry.ExpiryDefaultDays));
        Assert.Equal(45, registry.ExpiryWarningDays(new FakeDevice()));

        registry.For(TrustAnchorRegistry.RenewalOwner).SetExpiryWarning(_ => throw new InvalidOperationException("broken"), 60);
        Assert.Equal(30, registry.ExpiryWarningDays(device)); // never breaks the device list

        registry.Remove(TrustAnchorRegistry.RenewalOwner); // PKI stopped
        Assert.Equal((30, 30), (registry.ExpiryWarningDays(device), registry.ExpiryDefaultDays));
    }

    [Fact]
    public async Task NewDaysPublishEveryDeviceOnceAsUpdatedWithoutWritingAnything()
    {
        await using var db = await TestDatabase.CreateAsync();
        var devices = db.Get<DeviceRepository>();
        var first = await devices.AddAsync(new Device { Serial = "ACCC8E000001", Address = "10.0.0.48" }, CancellationToken.None);
        var second = await devices.AddAsync(new Device { Serial = "ACCC8E000002", Address = "10.0.0.49" }, CancellationToken.None);
        var feed = db.Get<IDeviceChangeFeed>();
        var registry = new TrustAnchorRegistry();
        await using var publisher = new ExpiryWarningPublisher(registry, devices, feed);
        publisher.Start();
        using var subscription = feed.Subscribe();

        registry.For(TrustAnchorRegistry.RenewalOwner).SetExpiryWarning(_ => 60, 60);
        await Eventually.TrueAsync(() => publisher.Published == 1, "one pass");

        var changes = new List<DeviceChange>();
        while (subscription.Reader.TryRead(out var change))
        {
            changes.Add(change);
        }

        Assert.Equal(new HashSet<Guid> { first.Id, second.Id }, changes.Select(c => c.DeviceId).ToHashSet());
        Assert.Equal(2, changes.Count);
        Assert.All(changes, c => Assert.Equal(DeviceChangeKind.Updated, c.Kind));
    }
}
