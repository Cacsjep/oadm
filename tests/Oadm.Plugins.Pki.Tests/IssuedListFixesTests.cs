using Microsoft.Extensions.Time.Testing;

using Oadm.Core.Plugins;
using Oadm.Plugins.Pki.Ca;
using Oadm.Sdk.Certificates;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Pki.Tests;

/// <summary>
/// The list of issued certificates around the certificates it did not hold: revoking unknown serials, Revoke all of this
/// device with the device's own list, the copy of an entry of OADM 4.1, deleted certificates, and the page's counts.
/// </summary>
public sealed class IssuedListFixesTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    private static IssuedCertificate Entry(string serial, Guid device, DateTime notAfter, string purpose = CertificatePurpose.Https, DateTime? issued = null, string caId = "CA") => new()
    {
        SerialNumber = serial,
        DeviceId = device,
        Purpose = purpose,
        CaId = caId,
        NotAfterUtc = notAfter,
        IssuedUtc = issued ?? Start.UtcDateTime,
    };

    private static IssuedRegistry Registry(FakeTimeProvider time, MemoryPluginRecords? records = null, PkiStore? store = null) =>
        new(store ?? new PkiStore(new InMemoryPluginSettingsProvider().GetSettings(PkiPluginInfo.PluginId)), time, records ?? new MemoryPluginRecords());

    [Fact]
    public async Task A_serial_the_list_does_not_hold_is_revoked_with_time_reason_and_user_until_the_ca_could_have_ended()
    {
        var time = new FakeTimeProvider(Start);
        var records = new MemoryPluginRecords();
        await using var registry = Registry(time, records);
        var caEnd = Start.UtcDateTime.AddDays(400);
        registry.UnknownNotAfter = () => caEnd;
        await registry.LoadAsync(CancellationToken.None);

        var result = await registry.RevokeAsync(["00:AB:CD:EF", "not a serial", new string('A', 41)], RevocationReason.DeviceRemoved, "installer", CancellationToken.None);

        Assert.Equal(new RevokeResult(1, 0, 2), result);
        var revocation = registry.FindRevocation("ABCDEF")!;
        Assert.Equal((Start.UtcDateTime, RevocationReason.DeviceRemoved, "installer", (Guid?)null, caEnd), (revocation.RevokedUtc, revocation.Reason, revocation.UserName, revocation.DeviceId, revocation.NotAfterUtc));
        Assert.Single(await records.ListAsync(IssuedRegistry.RevokedCollection, CancellationToken.None)); // written before it returned
        Assert.Null(registry.Find("ABCDEF")); // not listed as an issued certificate

        // Kept while a certificate of the CA could still be valid.
        time.Advance(TimeSpan.FromDays(399));
        await registry.PruneAsync(CancellationToken.None);
        Assert.True(registry.IsRevoked("ABCDEF"));
        time.Advance(TimeSpan.FromDays(2));
        await registry.PruneAsync(CancellationToken.None);
        Assert.False(registry.IsRevoked("ABCDEF"));
    }

    [Fact]
    public async Task Without_a_ca_end_an_unknown_serial_stays_on_the_list()
    {
        var time = new FakeTimeProvider(Start);
        await using var registry = Registry(time);
        await registry.LoadAsync(CancellationToken.None);
        await registry.RevokeAsync(["0BAD"], RevocationReason.Other, "admin", CancellationToken.None);
        time.Advance(TimeSpan.FromDays(3650));
        await registry.PruneAsync(CancellationToken.None);
        Assert.True(registry.IsRevoked("BAD"));
    }

    [Fact]
    public async Task Pki_keeps_an_unknown_serial_until_the_end_of_its_latest_ca()
    {
        await using var pki = await PkiHarness.StartAsync();
        var reply = await pki.InvokeAsync<RevokeReply>(PkiMethods.Revoke, new RevokeRequest(["0BAD"], RevocationReason.KeyCompromised));
        Assert.Equal((1, 0, 0), (reply.Revoked, reply.AlreadyRevoked, reply.NotFound));
        var revocation = new PkiCertificateAuthority(pki.Service).FindRevocation("0BAD")!;
        Assert.Equal(pki.Service.Ca!.Certificate.NotAfter.ToUniversalTime(), revocation.NotAfterUtc);
        Assert.Equal(PkiPlugin.InProcessUser, revocation.UserName);
    }

    [Fact]
    public async Task Revoke_all_of_this_device_reads_the_device_and_revokes_oadm_certificates_the_list_did_not_hold()
    {
        var device = new PkiFakeDevice(Guid.NewGuid(), "10.0.0.48", "B8A44F631339");
        await using var pki = await PkiHarness.StartAsync(devices: [device]);
        using var onDevice = RevocationTests.Issue(pki.Service.Ca!, "B8A44F631339", client: true); // from before the list
        var asked = new List<Guid>();
        pki.Service.DeviceCertificateReader = (d, _) =>
        {
            asked.Add(d.Id);
            return Task.FromResult<IReadOnlyList<(string Alias, string Pem)>>([("OADM 802.1X 20240101", CaCertificates.ToPem(onDevice))]);
        };
        Assert.Empty(pki.Service.Issued.ForDevice(device.Id));

        var reply = await pki.InvokeAsync<RevokeReply>(PkiMethods.RevokeDevice, new RevokeDeviceRequest(device.Id, RevocationReason.KeyCompromised));

        Assert.Equal([device.Id], asked);
        Assert.Equal(1, reply.Revoked);
        Assert.True(pki.Service.Issued.IsRevoked(onDevice.SerialNumber));
        Assert.Equal(device.Id, pki.Service.Issued.FindRevocation(onDevice.SerialNumber)!.DeviceId);
    }

    [Fact]
    public async Task Revoke_all_of_this_device_revokes_the_listed_ones_when_the_device_does_not_answer()
    {
        var device = new PkiFakeDevice(Guid.NewGuid(), "10.0.0.48", "B8A44F631339");
        await using var pki = await PkiHarness.StartAsync(devices: [device]);
        IDeviceCertificateAuthority authority = new PkiCertificateAuthority(pki.Service);
        var listed = (await RevocationTests.SignAsync(authority, device.Id)).SerialNumber;
        pki.Service.DeviceCertificateReader = (_, _) => throw new HttpRequestException("unreachable");

        var reply = await pki.InvokeAsync<RevokeReply>(PkiMethods.RevokeDevice, new RevokeDeviceRequest(device.Id, RevocationReason.DeviceReplaced));

        Assert.Equal(1, reply.Revoked);
        Assert.True(authority.IsRevoked(listed));
    }

    [Fact]
    public async Task An_entry_of_oadm_4_1_gets_its_certificate_copy_when_a_read_of_the_device_finds_it()
    {
        await using var pki = await PkiHarness.StartAsync();
        using var onDevice = RevocationTests.Issue(pki.Service.Ca!, "B8A44F631339", client: false);
        var device = new PkiFakeDevice(Guid.NewGuid(), "10.0.0.48", "B8A44F631339");
        var settings = new InMemoryPluginSettingsProvider().GetSettings(PkiPluginInfo.PluginId);
        var legacy = Entry(onDevice.SerialNumber, device.Id, onDevice.NotAfter.ToUniversalTime(), caId: pki.Service.Ca!.Id) with { Alias = "OADM HTTPS 20240101" };
        await settings.SetAsync(PkiStore.IssuedKey, PkiJson.Serialize(new[] { legacy }), CancellationToken.None);
        var records = new MemoryPluginRecords();
        var time = new FakeTimeProvider(Start);
        await using var registry = Registry(time, records, new PkiStore(settings));
        await registry.LoadAsync(CancellationToken.None);
        Assert.True(registry.Find(onDevice.SerialNumber)!.NoCopy);
        Assert.Null(await registry.GetDerAsync(onDevice.SerialNumber, CancellationToken.None));

        var added = await registry.ImportFoundAsync(device, [("OADM HTTPS 20240101", CaCertificates.ToPem(onDevice))], pki.Service.KnownCaCertificates(), CancellationToken.None);

        Assert.Equal(0, added); // listed already
        Assert.Equal(onDevice.RawData, await registry.GetDerAsync(onDevice.SerialNumber, CancellationToken.None));
        var entry = registry.Find(onDevice.SerialNumber)!;
        Assert.False(entry.NoCopy);
        Assert.Equal(("B8A44F631339", onDevice.NotBefore.ToUniversalTime()), (entry.DeviceSerial, entry.NotBeforeUtc));
        await registry.FlushAsync(CancellationToken.None);
        Assert.NotNull(await records.GetAsync(IssuedRegistry.DerCollection, CertificateSerials.Normalize(onDevice.SerialNumber), CancellationToken.None));
        Assert.Contains("\"noCopy\":false", (await records.GetAsync(IssuedRegistry.IssuedCollection, CertificateSerials.Normalize(onDevice.SerialNumber), CancellationToken.None))!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_certificate_deleted_from_the_device_stays_listed_as_replaced_and_leaves_the_counts()
    {
        var time = new FakeTimeProvider(Start);
        await using var registry = Registry(time);
        await registry.LoadAsync(CancellationToken.None);
        var device = Guid.NewGuid();
        await registry.AddAsync(Entry("0A", device, Start.UtcDateTime.AddDays(300)), CancellationToken.None);
        Assert.Equal(IssuedCertificateState.Valid, registry.StateOf(registry.Find("0A")!, Start.UtcDateTime, 30));

        registry.MarkRemovedFromDevice("0a");

        Assert.Equal(IssuedCertificateState.Replaced, registry.StateOf(registry.Find("0A")!, Start.UtcDateTime, 30));
        Assert.Equal(Start.UtcDateTime, registry.Find("0A")!.RemovedFromDeviceUtc);
        Assert.Equal(["A"], registry.RevocableOf(device)); // a copy could be in use: it can still be revoked
        Assert.Equal(0, IssuedCounts.From(registry.Snapshot(), "CA", Start.UtcDateTime, 30).Devices);
    }

    [Fact]
    public void The_counts_leave_out_ended_and_revoked_certificates()
    {
        var now = Start.UtcDateTime;
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid(), d = Guid.NewGuid();
        IssuedCertificate[] issued =
        [
            Entry("01", a, now.AddDays(300)),
            Entry("02", b, now.AddDays(10)), // expires soon
            Entry("03", c, now.AddDays(-200)), // ended long ago, still listed for a year
            Entry("04", d, now.AddDays(300)), // revoked
            Entry("05", a, now.AddDays(-1), CertificatePurpose.Dot1x, caId: "OLD"), // ended: not "from a previous CA"
        ];

        var counts = IssuedCounts.From(issued, "CA", now, 30, serial => serial == "04");

        Assert.Equal((2, 2, 0, 1), (counts.Devices, counts.WithCurrentCa, counts.WithPreviousCa, counts.ExpiringSoon));
        Assert.False(counts.DevicesPerCa.ContainsKey("OLD"));
    }
}
