using System.Collections.Concurrent;

using Microsoft.Extensions.Time.Testing;

using Oadm.Plugins.Pki.Policies;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Events;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Pki.Tests;

/// <summary>
/// "Certificate expires soon" and "Certificate expired" with their Any device twins are PKI's events (docs/spec/pki.md
/// "Policies", Expiry warning): the ids of OADM 4.1's built-in events, group "Certificates", the days of each device's
/// Expiry warning policy, the full report at the start, again after a change of the policies and every minute.
/// </summary>
public sealed class ExpiryEventTests
{
    /// <summary>What PKI published: the last full report of each type and every single state.</summary>
    private sealed class RecordingPublisher : IEventPublisher
    {
        public ConcurrentDictionary<string, IReadOnlyCollection<Guid>> Replaced { get; } = new();

        public ConcurrentQueue<(string Type, bool On, string? Count)> States { get; } = new();

        public void SetState(string typeId, Guid? deviceId, bool isOn, IReadOnlyDictionary<string, string>? data = null) =>
            States.Enqueue((typeId, isOn, data?.GetValueOrDefault(ExpiryEvents.CountField)));

        public void Fire(string typeId, Guid? deviceId, IReadOnlyDictionary<string, string>? data = null)
        {
        }

        public void ReplaceStates(string typeId, IReadOnlyCollection<Guid> onDevices) => Replaced[typeId] = [.. onDevices];

        public bool IsWatched(string typeId) => true;
    }

    [Fact]
    public void TheTypesKeepTheIdsOfTheBuiltInEventsOfOadm41InPkisGroupCertificates()
    {
        Assert.Equal(
            ["oadm.device.certificate-expires-soon", "oadm.device.certificate-expired", "oadm.device.any-certificate-expires-soon", "oadm.device.any-certificate-expired"],
            ExpiryEvents.Types.Select(t => t.Id));
        Assert.Equal([Oadm.Core.Events.BuiltInEvents.CertificateExpiresSoon, Oadm.Core.Events.BuiltInEvents.CertificateExpired], ExpiryEvents.Types.Take(2).Select(t => t.Id));
        Assert.All(ExpiryEvents.Types, t => Assert.Equal(("Certificates", EventKind.Stateful), (t.Group, t.Kind)));
        Assert.Equal([EventScope.Device, EventScope.Device, EventScope.Server, EventScope.Server], ExpiryEvents.Types.Select(t => t.Scope));
        Assert.Same(ExpiryEvents.Types, new PkiPlugin().EventTypes);
    }

    [Fact]
    public async Task TheDaysComeFromEachDevicesPolicyAndAChangeOfThePoliciesReportsAgainAtOnce()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero));
        var now = time.GetUtcNow().UtcDateTime;
        var sites = new FakeSites();
        var site = sites.Add("Building B");
        var inForty = new FakeDevice { Serial = "ACCC8E000001", CertNotAfterUtc = now.AddDays(40) };
        var atSiteInForty = new FakeDevice { Serial = "ACCC8E000002", SiteId = site.Id, CertNotAfterUtc = now.AddDays(40) };
        var inTen = new FakeDevice { Serial = "ACCC8E000003", CertNotAfterUtc = now.AddMinutes(30) };
        var ended = new FakeDevice { Serial = "ACCC8E000004", CertNotAfterUtc = now.AddDays(-1) };
        var http = new FakeDevice { Serial = "ACCC8E000005" };
        var publisher = new RecordingPublisher();
        await using var pki = await PkiHarness.StartAsync(time: time, devices: [inForty, atSiteInForty, inTen, ended, http], publisher: publisher, sites: sites);

        // The full report at the start ("All devices", 30 days): every type, so the event rules count PKI as ready.
        Assert.Equal([inTen.Id], publisher.Replaced[ExpiryEvents.ExpiresSoon]);
        Assert.Equal([ended.Id], publisher.Replaced[ExpiryEvents.Expired]);
        Assert.Equal([Guid.Empty], publisher.Replaced[ExpiryEvents.AnyExpiresSoon]);
        Assert.Equal([Guid.Empty], publisher.Replaced[ExpiryEvents.AnyExpired]);
        Assert.Contains((ExpiryEvents.AnyExpiresSoon, true, "1"), publisher.States);

        // Building B warns 60 days before the end (out of "All devices" first: a device is in one policy of a kind): its device
        // expires soon at once.
        var all = pki.Policies.PoliciesOf(PolicyKinds.ExpiryWarning)[0];
        Assert.Equal(new PolicyFieldError(PolicyFields.Devices, "192.0.2.10 is in Building B and All devices."),
            (await pki.InvokeAsync<PolicySaveReply>(PolicyMethods.SavePolicy, new PkiPolicy { Name = "Building B", Kind = PolicyKinds.ExpiryWarning, Days = 60, Devices = new PolicyDevices { SiteIds = [site.Id] } })).Error);
        Assert.Null((await pki.InvokeAsync<PolicySaveReply>(PolicyMethods.SavePolicy, all with { Devices = all.Devices! with { SiteIds = [] } })).Error);
        var reply = await pki.InvokeAsync<PolicySaveReply>(PolicyMethods.SavePolicy, new PkiPolicy
        {
            Name = "Building B", Kind = PolicyKinds.ExpiryWarning, Days = 60, Devices = new PolicyDevices { SiteIds = [site.Id] },
        });
        Assert.Null(reply.Error);
        await Eventually.TrueAsync(() => publisher.Replaced[ExpiryEvents.ExpiresSoon].Count == 2, "the new days applied");
        Assert.Equal(new HashSet<Guid> { atSiteInForty.Id, inTen.Id }, publisher.Replaced[ExpiryEvents.ExpiresSoon].ToHashSet());
        await Eventually.TrueAsync(() => publisher.States.Contains((ExpiryEvents.AnyExpiresSoon, true, "2")), "the count of the Any device twin");

        // Time passes: the next minute's round finds the device whose certificate ended meanwhile.
        time.Advance(TimeSpan.FromMinutes(31));
        await Eventually.TrueAsync(() => publisher.Replaced[ExpiryEvents.Expired].Contains(inTen.Id), "the certificate that ended");
    }

    [Fact]
    public async Task AHostWithoutEventRulesStartsPkiWithoutTheEvents()
    {
        await using var pki = await PkiHarness.StartAsync();
        Assert.Null(pki.Plugin.Expiry);
    }
}
