using System.Globalization;

using Microsoft.Extensions.Time.Testing;

using Oadm.Core.Plugins;
using Oadm.Plugins.Pki.Ca;

namespace Oadm.Plugins.Pki.Tests;

public sealed class IssuedRegistryTests
{
    [Fact]
    public async Task Registry_changes_are_batched_in_memory_and_written_on_stop()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var settings = new InMemoryPluginSettingsProvider();
        var store = new PkiStore(settings.GetSettings(PkiPluginInfo.PluginId));
        var registry = new IssuedRegistry(store, time);
        var device = Guid.NewGuid();
        for (var i = 0; i < 100; i++)
        {
            await registry.AddAsync(new IssuedCertificate { SerialNumber = i.ToString("X2", CultureInfo.InvariantCulture), DeviceId = device, NotAfterUtc = DateTime.UtcNow.AddDays(10), Alias = "a" + i.ToString(CultureInfo.InvariantCulture) }, CancellationToken.None);
        }

        Assert.Empty(await store.LoadIssuedAsync(CancellationToken.None)); // not written per task
        Assert.Equal(100, (await registry.ForDeviceAsync(device, CancellationToken.None)).Count);
        await registry.RemoveAsync(device, ["00", "01"], CancellationToken.None);
        await registry.AddAsync(new IssuedCertificate { SerialNumber = "OLD", DeviceId = device, NotAfterUtc = DateTime.UtcNow.AddDays(-31) }, CancellationToken.None); // long expired: dropped

        await registry.DisposeAsync();

        var stored = await store.LoadIssuedAsync(CancellationToken.None);
        Assert.Equal(98, stored.Count);
        Assert.DoesNotContain(stored, e => e.SerialNumber is "00" or "OLD");
        Assert.Equal("a2", stored[0].Alias);
    }

    [Fact]
    public async Task Registry_writes_after_the_flush_delay()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var store = new PkiStore(new InMemoryPluginSettingsProvider().GetSettings(PkiPluginInfo.PluginId));
        await using var registry = new IssuedRegistry(store, time);
        await registry.AddAsync(new IssuedCertificate { SerialNumber = "01", DeviceId = Guid.NewGuid(), NotAfterUtc = DateTime.UtcNow.AddDays(10) }, CancellationToken.None);
        Assert.Empty(await store.LoadIssuedAsync(CancellationToken.None));

        // The flush loop may start its delay after this line: advance until the write happened (no race on a busy machine).
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while ((await store.LoadIssuedAsync(CancellationToken.None)).Count == 0 && DateTime.UtcNow < deadline)
        {
            time.Advance(TimeSpan.FromSeconds(3));
            await Task.Delay(20);
        }

        Assert.Single(await store.LoadIssuedAsync(CancellationToken.None));
    }
}
