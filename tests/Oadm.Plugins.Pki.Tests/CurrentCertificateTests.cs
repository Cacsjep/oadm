using Microsoft.Extensions.Time.Testing;

using Oadm.Core.Plugins;
using Oadm.Plugins.Pki.Ca;
using Oadm.Sdk.Certificates;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Pki.Tests;

/// <summary>
/// The store of current and pending certificates (docs/spec/pki.md "Current and pending", decisions 2026-10-25): the list
/// shows each managed camera's current certificate per use, found by the camera's serial; confirming a pending one revokes
/// the previous one (Superseded, by "OADM"); an unused pending one is revoked; a revoked one leaves the store and never
/// comes back from a camera.
/// </summary>
public sealed class CurrentCertificateTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    private static IssuedRegistry Registry(FakeTimeProvider time) =>
        new(new PkiStore(new InMemoryPluginSettingsProvider().GetSettings(PkiPluginInfo.PluginId)), time, new MemoryPluginRecords());

    private static IssuedCertificate Entry(string serial, Guid device, string camera = "B8A44F631339", string purpose = CertificatePurpose.Https, int issuedMinutes = 0,
        string? source = null, bool pending = false) => new()
    {
        SerialNumber = serial,
        DeviceId = device,
        DeviceSerial = camera,
        Purpose = purpose,
        NotAfterUtc = Start.UtcDateTime.AddDays(300),
        IssuedUtc = Start.UtcDateTime.AddMinutes(issuedMinutes),
        Source = source,
        Pending = pending,
    };

    private static Dictionary<Guid, IDeviceInfo> Managed(params IDeviceInfo[] devices) => devices.ToDictionary(d => d.Id);

    private static List<string> Listed(IssuedRegistry registry, Dictionary<Guid, IDeviceInfo> devices) =>
        [.. registry.Query(new IssuedCertificateQuery { Limit = 500 }, devices, false, 30).Rows.Select(r => r.SerialNumber)];

    [Fact]
    public async Task The_list_shows_only_the_current_certificate_of_each_managed_camera_and_use()
    {
        await using var registry = Registry(new FakeTimeProvider(Start));
        await registry.LoadAsync(CancellationToken.None);
        var camera = new PkiFakeDevice(Guid.NewGuid());
        var gone = new PkiFakeDevice(Guid.NewGuid(), "10.0.0.49", "ACCC8E000001");

        // Entries of OADM before 4.5 (no conversion): the newest of a camera and use counts as current.
        await registry.AddAsync(Entry("01", camera.Id, issuedMinutes: -60), CancellationToken.None);
        await registry.AddAsync(Entry("02", camera.Id), CancellationToken.None);
        await registry.AddAsync(Entry("03", camera.Id, purpose: CertificatePurpose.Dot1x), CancellationToken.None);
        await registry.AddAsync(Entry("04", gone.Id, gone.Serial), CancellationToken.None);
        await registry.AddPendingAsync(Entry("05", camera.Id, issuedMinutes: 5), null, CancellationToken.None);
        var devices = Managed(camera);

        Assert.Equal(["3", "2"], Listed(registry, devices)); // not the older 01, not the pending 05, not the camera OADM no longer manages
        var row = registry.Query(new IssuedCertificateQuery(), devices, false, 30).Rows.Single(r => r.SerialNumber == "2");
        Assert.Equal((camera.Id, camera.Address, true, camera.Address), (row.DeviceId, row.Device, row.DeviceManaged, row.UsedBy));
        Assert.DoesNotContain(registry.Query(new IssuedCertificateQuery(), devices, false, 30).Rows, r => r.State == IssuedCertificateState.Replaced);

        // The camera removed from OADM stays valid and hidden; added again (a new device id), its certificate is listed again.
        Assert.False(registry.IsRevoked("04"));
        var again = new PkiFakeDevice(Guid.NewGuid(), "10.0.0.77", gone.Serial);
        Assert.Equal(["4", "3", "2"], Listed(registry, Managed(camera, again))); // newest first (same time: by serial)
        var back = registry.Query(new IssuedCertificateQuery { Search = "10.0.0.77" }, Managed(camera, again), false, 30).Rows.Single();
        Assert.Equal((again.Id, "10.0.0.77"), (back.DeviceId, back.Device));
        Assert.Equal(1, registry.Query(new IssuedCertificateQuery { DeviceIds = new HashSet<Guid> { again.Id } }, Managed(camera, again), false, 30).Total);

        // Every caller that keys by device finds it under the new id too: Revoke all of this device, the tasks' reads.
        Assert.Equal(["4"], registry.RevocableOf(again));
        Assert.Empty(registry.RevocableOf(again.Id));
        Assert.Single(registry.ForDevice(again));
    }

    [Fact]
    public async Task Confirming_a_pending_certificate_revokes_the_previous_one_by_oadm_and_removes_it()
    {
        await using var registry = Registry(new FakeTimeProvider(Start));
        await registry.LoadAsync(CancellationToken.None);
        var camera = new PkiFakeDevice(Guid.NewGuid());
        await registry.AddAsync(Entry("0A", camera.Id), CancellationToken.None);
        await registry.AddPendingAsync(Entry("0B", camera.Id, issuedMinutes: 1), [1], CancellationToken.None);
        Assert.Equal(["A"], Listed(registry, Managed(camera))); // pending: the previous one stays current
        Assert.False(registry.IsRevoked("0A"));

        Assert.True(await registry.ConfirmAsync("0B", CancellationToken.None));

        Assert.Equal(["B"], Listed(registry, Managed(camera)));
        Assert.Null(registry.Find("0A"));
        var revocation = registry.FindRevocation("0A")!;
        Assert.Equal((RevocationReason.Superseded, IssuedRegistry.OadmUser, camera.Id), (revocation.Reason, revocation.UserName, revocation.DeviceId));
        Assert.Equal("Certificate revoked on 2026-10-25 (Replaced by a newer certificate)", revocation.Describe());
        Assert.False(await registry.ConfirmAsync("0B", CancellationToken.None)); // not pending any more
    }

    [Fact]
    public async Task A_newer_pending_certificate_replaces_an_older_one_but_keeps_one_a_task_delivered()
    {
        await using var registry = Registry(new FakeTimeProvider(Start));
        await registry.LoadAsync(CancellationToken.None);
        var camera = Guid.NewGuid();
        await registry.AddPendingAsync(Entry("11", camera, pending: true, source: IssuedCertificateSources.Est), null, CancellationToken.None);
        await registry.AddPendingAsync(Entry("12", camera, issuedMinutes: 1), null, CancellationToken.None);
        Assert.Null(registry.Find("11")); // the camera never used it: revoked, out of the store
        Assert.Equal(RevocationReason.Superseded, registry.FindRevocation("11")!.Reason);

        // A task ran "Set 802.1X configuration" with it: it may be in use, so it is never revoked as unused.
        await registry.AddPendingAsync(Entry("21", camera, purpose: CertificatePurpose.Dot1x), null, CancellationToken.None);
        registry.MarkDelivered("21");
        await registry.AddPendingAsync(Entry("22", camera, purpose: CertificatePurpose.Dot1x, issuedMinutes: 1), null, CancellationToken.None);
        var kept = registry.Find("21")!;
        Assert.Equal((false, true), (kept.Pending, kept.NotCurrent));
        Assert.False(registry.IsRevoked("21"));
        Assert.True(registry.Find("22")!.Pending);
    }

    [Fact]
    public async Task An_unused_pending_certificate_is_revoked_but_one_a_task_delivered_stays_unless_the_camera_uses_another()
    {
        await using var registry = Registry(new FakeTimeProvider(Start));
        await registry.LoadAsync(CancellationToken.None);
        var camera = Guid.NewGuid();
        await registry.AddAsync(Entry("30", camera, purpose: CertificatePurpose.Dot1x, issuedMinutes: -1), CancellationToken.None);
        await registry.AddPendingAsync(Entry("31", camera, purpose: CertificatePurpose.Dot1x), null, CancellationToken.None);
        Assert.True(await registry.DiscardAsync("31", evenDelivered: false, CancellationToken.None));
        Assert.Equal((RevocationReason.Other, IssuedRegistry.OadmUser), (registry.FindRevocation("31")!.Reason, registry.FindRevocation("31")!.UserName));
        Assert.True(registry.IsCurrent("30")); // the previous one stays current and valid
        Assert.False(registry.IsRevoked("30"));

        await registry.AddPendingAsync(Entry("32", camera, purpose: CertificatePurpose.Dot1x, issuedMinutes: 1), null, CancellationToken.None);
        registry.MarkDelivered("32");
        Assert.False(await registry.DiscardAsync("32", evenDelivered: false, CancellationToken.None)); // device safety: may be in use
        Assert.False(registry.IsRevoked("32"));
        Assert.True(await registry.DiscardAsync("32", evenDelivered: true, CancellationToken.None)); // the camera reports another one
        Assert.True(registry.IsRevoked("32"));
        Assert.True(registry.IsCurrent("30"));
    }

    [Fact]
    public async Task Est_https_is_current_once_its_answer_is_written_and_ieee_8021x_only_after_the_camera_signs_in()
    {
        await using var pki = await PkiHarness.StartAsync();
        var camera = pki.Manage(Guid.NewGuid(), "B8A44F631339");
        var authority = new PkiCertificateAuthority(pki.Service);
        var oldHttps = (await RevocationTests.SignAsync(authority, camera.Id, camera.Serial)).SerialNumber;
        var oldDot1x = (await RevocationTests.SignAsync(authority, camera.Id, camera.Serial, DeviceCertificateUse.Client)).SerialNumber;

        // An EST renewal: pending until the answer is written; then the old one is revoked as replaced.
        var https = (await RevocationTests.SignAsync(authority, camera.Id, camera.Serial, confirm: false)).SerialNumber;
        Assert.True(pki.Service.Issued.Find(https)!.Pending);
        await authority.DeliveredAsync(https, CancellationToken.None);
        Assert.True(pki.Service.Issued.IsCurrent(https));
        Assert.Equal(RevocationReason.Superseded, authority.FindRevocation(oldHttps)!.Reason);

        // IEEE 802.1X: the written answer is not enough (the camera may still use the old one on the network).
        var dot1x = (await RevocationTests.SignAsync(authority, camera.Id, camera.Serial, DeviceCertificateUse.Client, confirm: false)).SerialNumber;
        await authority.DeliveredAsync(dot1x, CancellationToken.None);
        Assert.True(pki.Service.Issued.Find(dot1x)!.Pending);
        Assert.False(authority.IsRevoked(oldDot1x));
        await authority.SignedInAsync(oldDot1x, CancellationToken.None); // the old one signing in confirms nothing
        Assert.True(pki.Service.Issued.Find(dot1x)!.Pending);
        await authority.SignedInAsync(dot1x, CancellationToken.None);
        Assert.True(pki.Service.Issued.IsCurrent(dot1x));
        Assert.Equal(RevocationReason.Superseded, authority.FindRevocation(oldDot1x)!.Reason);

        // An answer that could not be written: the camera never got it.
        var lost = (await RevocationTests.SignAsync(authority, camera.Id, camera.Serial, confirm: false)).SerialNumber;
        await authority.NotDeliveredAsync(lost, CancellationToken.None);
        Assert.Equal(RevocationReason.Other, authority.FindRevocation(lost)!.Reason);
        Assert.True(pki.Service.Issued.IsCurrent(https));
    }

    [Fact]
    public async Task An_est_certificate_whose_answer_was_never_written_is_revoked_after_the_timeout()
    {
        var time = new FakeTimeProvider(Start);
        await using var registry = Registry(time);
        await registry.LoadAsync(CancellationToken.None);
        var camera = Guid.NewGuid();
        await registry.AddPendingAsync(Entry("41", camera, source: IssuedCertificateSources.Est), null, CancellationToken.None);
        await registry.AddPendingAsync(Entry("42", camera, purpose: CertificatePurpose.Mqtt), null, CancellationToken.None); // a task's: waits for its task

        time.Advance(registry.PendingTimeout - TimeSpan.FromMinutes(1));
        await registry.PruneAsync(CancellationToken.None);
        Assert.False(registry.IsRevoked("41"));

        time.Advance(TimeSpan.FromMinutes(2));
        await registry.PruneAsync(CancellationToken.None);
        Assert.True(registry.IsRevoked("41"));
        Assert.Null(registry.Find("41"));
        Assert.NotNull(registry.Find("42"));
    }

    [Fact]
    public async Task A_revoked_certificate_found_on_the_camera_never_comes_back_into_the_store()
    {
        await using var pki = await PkiHarness.StartAsync();
        var camera = (PkiFakeDevice)pki.Manage(Guid.NewGuid(), "B8A44F631339");
        using var issued = RevocationTests.Issue(pki.Service.Ca!, camera.Serial, client: false);
        var found = new[] { ("OADM HTTPS 20250101-120000", Ca.CaCertificates.ToPem(issued)) };
        Assert.Equal(1, await pki.Service.ImportFoundAsync(camera, found, CancellationToken.None)); // relearned (from before the list)

        await pki.Service.Issued.RevokeAsync([issued.SerialNumber], RevocationReason.KeyCompromised, "admin", CancellationToken.None);
        Assert.Null(pki.Service.Issued.Find(issued.SerialNumber));
        Assert.Equal(0, await pki.Service.ImportFoundAsync(camera, found, CancellationToken.None));
        Assert.Null(pki.Service.Issued.Find(issued.SerialNumber));
    }

    [Fact]
    public void The_state_filter_offers_no_replaced_and_no_revoked_and_superseded_has_its_text()
    {
        var list = new IssuedCertificateListViewModel(new NoSource());
        Assert.Equal(["All states", "Valid", "Expires soon", "Expired"], list.StateChoices.Select(c => c.Label)); // decision 2026-10-26: no revoke
        Assert.Equal("Replaced by a newer certificate", CertificateRevocation.ReasonText(RevocationReason.Superseded));
    }

    private sealed class NoSource : IIssuedCertificateSource
    {
        public Task<IssuedCertificateListPage> ReadAsync(IssuedCertificateQuery query, CancellationToken ct) => Task.FromResult(new IssuedCertificateListPage([], 0, 0));

        public Task<IssuedCertificateDetails> GetCertificateAsync(string serialNumber, CancellationToken ct) => Task.FromResult(new IssuedCertificateDetails(null, null, null));
    }
}
