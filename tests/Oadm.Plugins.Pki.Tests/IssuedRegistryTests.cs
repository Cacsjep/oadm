using System.Globalization;

using Microsoft.Extensions.Time.Testing;

using Oadm.Core.Plugins;
using Oadm.Plugins.Pki.Ca;
using Oadm.Sdk.Certificates;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Pki.Tests;

/// <summary>
/// The list of issued certificates and the revocation list (docs/spec/radius-server.md "Certificates and revocation"):
/// plugin records, batched writes of only the changed rows, the move from the settings of OADM 4.1, how long entries stay,
/// revocation that is written at once and answered by a hash lookup.
/// </summary>
public sealed class IssuedRegistryTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    private static (IssuedRegistry Registry, MemoryPluginRecords Records, PkiStore Store, FakeTimeProvider Time) Create(FakeTimeProvider? time = null, MemoryPluginRecords? records = null, PkiStore? store = null)
    {
        time ??= new FakeTimeProvider(Start);
        records ??= new MemoryPluginRecords();
        store ??= new PkiStore(new InMemoryPluginSettingsProvider().GetSettings(PkiPluginInfo.PluginId));
        return (new IssuedRegistry(store, time, records), records, store, time);
    }

    private static IssuedCertificate Entry(string serial, Guid device, DateTime notAfter, string purpose = CertificatePurpose.Https, DateTime? issued = null, string? source = null) => new()
    {
        SerialNumber = serial,
        DeviceId = device,
        Purpose = purpose,
        NotAfterUtc = notAfter,
        IssuedUtc = issued ?? Start.UtcDateTime,
        Source = source,
    };

    [Fact]
    public async Task New_certificates_are_written_after_the_flush_delay_and_only_the_changed_rows()
    {
        var (registry, records, _, time) = Create();
        await registry.LoadAsync(CancellationToken.None);
        var device = Guid.NewGuid();
        for (var i = 0; i < 100; i++)
        {
            await registry.AddAsync(Entry(i.ToString("X2", CultureInfo.InvariantCulture) + "AB", device, Start.UtcDateTime.AddDays(10)), [1, 2, (byte)i], CancellationToken.None);
        }

        Assert.Empty(await records.ListAsync(IssuedRegistry.IssuedCollection, CancellationToken.None)); // not written per task
        Assert.Equal(100, registry.ForDevice(device).Count);
        Assert.Equal([1, 2, 7], await registry.GetDerAsync("07AB", CancellationToken.None)); // read while waiting

        // The flush may start its delay after this line: advance until the write happened.
        await Eventually.TrueAsync(async () =>
        {
            time.Advance(TimeSpan.FromSeconds(3));
            return (await records.ListAsync(IssuedRegistry.IssuedCollection, CancellationToken.None)).Count == 100;
        }, "the batched write");
        Assert.Equal(100, (await records.ListAsync(IssuedRegistry.DerCollection, CancellationToken.None)).Count);
        var written = records.WrittenRecords;

        await registry.AddAsync(Entry("FFAB", device, Start.UtcDateTime.AddDays(10)), [9], CancellationToken.None);
        await registry.FlushAsync(CancellationToken.None);
        Assert.Equal(written + 2, records.WrittenRecords); // the new row and its DER, nothing else
        Assert.Equal([9], await registry.GetDerAsync("FFAB", CancellationToken.None)); // from the records now
        await registry.DisposeAsync();
    }

    [Fact]
    public async Task The_list_of_oadm_4_1_moves_from_the_settings_into_the_records()
    {
        var settings = new InMemoryPluginSettingsProvider().GetSettings(PkiPluginInfo.PluginId);
        var store = new PkiStore(settings);
        var device = Guid.NewGuid();
        await settings.SetAsync(PkiStore.IssuedKey, PkiJson.Serialize(new[] { Entry("0A", device, Start.UtcDateTime.AddDays(100)), Entry("0B", device, Start.UtcDateTime.AddDays(200)) }), CancellationToken.None);
        var (registry, records, _, _) = Create(store: store);

        await registry.LoadAsync(CancellationToken.None);

        Assert.Equal(2, registry.Count);
        Assert.Null(await settings.GetAsync(PkiStore.IssuedKey, CancellationToken.None));
        Assert.Equal(2, (await records.ListAsync(IssuedRegistry.IssuedCollection, CancellationToken.None)).Count);
        Assert.Null(await registry.GetDerAsync("0A", CancellationToken.None)); // OADM 4.1 kept no copy
        await registry.DisposeAsync();

        // The next start reads the records.
        var (again, _, _, _) = Create(records: records, store: store);
        await again.LoadAsync(CancellationToken.None);
        Assert.Equal(2, again.Count);
        await again.DisposeAsync();
    }

    [Fact]
    public async Task A_revocation_is_written_at_once_counts_expired_ones_and_text_that_is_no_serial_and_is_never_changed()
    {
        var (registry, records, _, time) = Create();
        await registry.LoadAsync(CancellationToken.None);
        var device = Guid.NewGuid();
        await registry.AddAsync(Entry("007DFD05", device, Start.UtcDateTime.AddDays(300)), CancellationToken.None);
        await registry.AddAsync(Entry("1234", device, Start.UtcDateTime.AddDays(-1)), CancellationToken.None); // expired
        var changes = 0;
        registry.RevocationChanged += (_, _) => changes++;

        var result = await registry.RevokeAsync(["7d:fd:05", "1234", "XYZ"], RevocationReason.KeyCompromised, "admin", CancellationToken.None);

        Assert.Equal(new RevokeResult(1, 0, 2), result);
        Assert.Equal(1, changes);
        Assert.True(registry.IsRevoked("007DFD05"));
        Assert.True(registry.IsRevoked("7DFD05"));
        Assert.True(registry.IsRevoked("00 7d fd 05"));
        Assert.False(registry.IsRevoked("1234"));
        Assert.Single(await records.ListAsync(IssuedRegistry.RevokedCollection, CancellationToken.None)); // no waiting
        var revocation = registry.FindRevocation("7DFD05")!;
        Assert.Equal((RevocationReason.KeyCompromised, "admin", device), (revocation.Reason, revocation.UserName, revocation.DeviceId));
        Assert.Equal("Certificate revoked on 2026-10-24 (Key compromised)", revocation.Describe());

        time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(new RevokeResult(0, 1, 0), await registry.RevokeAsync(["7DFD05"], RevocationReason.Other, "other", CancellationToken.None));
        Assert.Equal((RevocationReason.KeyCompromised, "admin"), (registry.FindRevocation("7DFD05")!.Reason, registry.FindRevocation("7DFD05")!.UserName));
        Assert.Equal(1, changes);
        Assert.Equal(IssuedCertificateState.Revoked, registry.StateOf(registry.Find("7DFD05")!, time.GetUtcNow().UtcDateTime, 30));
        await registry.DisposeAsync();
    }

    [Fact]
    public async Task A_certificate_stays_a_year_after_it_ended_and_a_revocation_while_it_could_be_valid()
    {
        var time = new FakeTimeProvider(Start);
        var (registry, records, _, _) = Create(time);
        await registry.LoadAsync(CancellationToken.None);
        var device = Guid.NewGuid();
        await registry.AddAsync(Entry("01", device, Start.UtcDateTime.AddDays(10)), [1], CancellationToken.None);
        await registry.AddAsync(Entry("02", device, Start.UtcDateTime.AddDays(500)), CancellationToken.None);
        await registry.RevokeAsync(["01"], RevocationReason.DeviceRemoved, "admin", CancellationToken.None);
        var revocationChanges = 0;
        registry.RevocationChanged += (_, _) => revocationChanges++;

        time.Advance(TimeSpan.FromDays(11)); // 01 ended: not valid any more, so it leaves the revocation list
        await registry.PruneAsync(CancellationToken.None);
        Assert.False(registry.IsRevoked("01"));
        Assert.Equal(1, revocationChanges);
        Assert.NotNull(registry.Find("01")); // listed as Expired for a year
        Assert.Empty(await records.ListAsync(IssuedRegistry.RevokedCollection, CancellationToken.None));

        time.Advance(TimeSpan.FromDays(366));
        await registry.PruneAsync(CancellationToken.None);
        Assert.Null(registry.Find("01"));
        Assert.NotNull(registry.Find("02"));
        Assert.False((await records.ListAsync(IssuedRegistry.IssuedCollection, CancellationToken.None)).ContainsKey("1"));
        Assert.Empty(await records.ListAsync(IssuedRegistry.DerCollection, CancellationToken.None));
        await registry.DisposeAsync();
    }

    [Fact]
    public async Task States_replaced_by_a_newer_one_of_the_same_device_and_use_expires_soon_and_expired()
    {
        var (registry, _, _, time) = Create();
        await registry.LoadAsync(CancellationToken.None);
        var device = Guid.NewGuid();
        var now = Start.UtcDateTime;
        await registry.AddAsync(Entry("01", device, now.AddDays(200), issued: now.AddDays(-30)), CancellationToken.None);
        await registry.AddAsync(Entry("02", device, now.AddDays(365), issued: now), CancellationToken.None);
        await registry.AddAsync(Entry("03", device, now.AddDays(20), CertificatePurpose.Dot1x, now), CancellationToken.None);
        await registry.AddAsync(Entry("04", device, now.AddDays(-2), CertificatePurpose.Mqtt, now.AddDays(-400)), CancellationToken.None);

        IssuedCertificateState State(string serial) => registry.StateOf(registry.Find(serial)!, now, 30);
        Assert.Equal(IssuedCertificateState.Replaced, State("01"));
        Assert.Equal(IssuedCertificateState.Valid, State("02"));
        Assert.Equal(IssuedCertificateState.ExpiresSoon, State("03"));
        Assert.Equal(IssuedCertificateState.Expired, State("04"));
        Assert.Equal(["1", "2", "3"], registry.RevocableOf(device).Order(StringComparer.Ordinal)); // 01 (replaced, still valid), 02, 03; not the expired 04
        _ = time;
        await registry.DisposeAsync();
    }

    [Fact]
    public async Task A_page_filters_searches_and_counts_in_one_pass_newest_first()
    {
        var (registry, _, _, _) = Create();
        await registry.LoadAsync(CancellationToken.None);
        var now = Start.UtcDateTime;
        var a = new PkiFakeDevice(Guid.NewGuid(), "10.0.0.48", "B8A44F631339");
        var b = new PkiFakeDevice(Guid.NewGuid(), "10.0.0.49", "ACCC8E000001");
        for (var i = 0; i < 30; i++)
        {
            var device = i % 2 == 0 ? a : b;
            await registry.AddAsync(Entry("A" + i.ToString("X3", CultureInfo.InvariantCulture), device.Id, now.AddDays(100 + i), i % 3 == 0 ? CertificatePurpose.Dot1x : CertificatePurpose.Https, now.AddMinutes(i), i % 5 == 0 ? IssuedCertificateSources.Est : null), CancellationToken.None);
        }

        await registry.AddAsync(Entry("FF01", Guid.NewGuid(), now.AddDays(100)), CancellationToken.None); // a device OADM no longer manages
        var devices = new Dictionary<Guid, Oadm.Sdk.Devices.IDeviceInfo> { [a.Id] = a, [b.Id] = b };

        var all = registry.Query(new IssuedCertificateQuery { Limit = 10 }, devices, onlyListedDevices: false, 30);
        Assert.Equal(31, all.Total);
        Assert.Equal(10, all.Rows.Count);
        Assert.Equal("A01D", all.Rows[0].SerialNumber); // newest first
        var removed = Assert.Single(registry.Query(new IssuedCertificateQuery { Limit = 500 }, devices, onlyListedDevices: false, 30).Rows, r => !r.DeviceManaged);
        Assert.Equal(string.Empty, removed.Device);

        var group = registry.Query(new IssuedCertificateQuery { Limit = 500 }, new Dictionary<Guid, Oadm.Sdk.Devices.IDeviceInfo> { [a.Id] = a }, onlyListedDevices: true, 30);
        Assert.Equal(15, group.Total);
        Assert.All(group.Rows, r => Assert.Equal(a.Id, r.DeviceId));

        Assert.Equal(10, registry.Query(new IssuedCertificateQuery { Purpose = CertificatePurpose.Dot1x }, devices, false, 30).Total);
        Assert.Equal(6, registry.Query(new IssuedCertificateQuery { Source = IssuedCertificateSources.Est }, devices, false, 30).Total);
        Assert.Equal(15, registry.Query(new IssuedCertificateQuery { Search = "10.0.0.49" }, devices, false, 30).Total);
        Assert.Equal(15, registry.Query(new IssuedCertificateQuery { Search = "b8a44f" }, devices, false, 30).Total);
        Assert.Equal(1, registry.Query(new IssuedCertificateQuery { Search = "a0:0a" }, devices, false, 30).Total);
        Assert.Equal(15, registry.Query(new IssuedCertificateQuery { DeviceIds = new HashSet<Guid> { b.Id } }, devices, false, 30).Total);

        await registry.RevokeAsync(["A000", "A001"], RevocationReason.Other, "admin", CancellationToken.None);
        var revoked = registry.Query(new IssuedCertificateQuery { State = IssuedCertificateState.Revoked }, devices, false, 30);
        Assert.Equal(2, revoked.Total);
        Assert.All(revoked.Rows, r => Assert.NotNull(r.Revocation));

        var page2 = registry.Query(new IssuedCertificateQuery { Offset = 10, Limit = 10 }, devices, false, 30);
        Assert.Equal(31, page2.Total);
        Assert.Empty(page2.Rows.Select(r => r.SerialNumber).Intersect(all.Rows.Select(r => r.SerialNumber)));
        await registry.DisposeAsync();
    }
}
