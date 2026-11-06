using Oadm.Plugins.Pki.Policies;
using Oadm.Plugins.Radius.Tests.Support;
using Oadm.Sdk.Certificates;

namespace Oadm.Plugins.Radius.Tests;

/// <summary>
/// A new server gets the default policies of the 802.1X kinds once, at the first start (docs/spec/pki.md "Converting
/// existing settings", owner <c>oadm.radius</c>); a second start changes nothing.
/// </summary>
public sealed class ConversionTests
{
    [Fact]
    public async Task ANewServerGetsTheDefaultPoliciesOfThe8021XKinds()
    {
        await using var harness = await PluginHarness.CreateAsync(start: false);

        await harness.StartAsync();

        var server = Assert.Single(harness.Policies.PoliciesOf(PolicyKinds.Dot1xServer));
        Assert.Equal((PolicyRules.DefaultMinimumTls, PolicyRules.DefaultEapPacket, (int?)null), (server.MinimumTls, server.LargestEapPacket!.Value, server.ReauthMinutes));
        Assert.Equal([(CertificateAuthorityInfo.OadmId, true), (CertificateAuthorityInfo.AxisDeviceIdId, true)],
            harness.Policies.PoliciesOf(PolicyKinds.AllowCa).Select(p => (p.CaId!, p.OnlyManaged!.Value)));
        Assert.Empty(harness.Policies.PoliciesOf(PolicyKinds.Vlan));
        Assert.Empty(harness.Policies.PoliciesOf(PolicyKinds.Block));
    }

    [Fact]
    public async Task TheDefaultsAreAddedOnceAndASecondStartChangesNothing()
    {
        await using var harness = await PluginHarness.CreateAsync(start: false);
        await harness.StartAsync();
        var first = harness.Policies.All.Select(p => p.Id).ToList();
        await harness.Policies.DeleteAsync(harness.Policies.PoliciesOf(PolicyKinds.AllowCa)[1].Id, "admin", CancellationToken.None);

        await harness.Plugin.StopAsync(CancellationToken.None);
        await harness.StartAsync();

        Assert.Equal(first.Count - 1, harness.Policies.All.Count); // the deleted one stays deleted, nothing doubled
        Assert.Equal(CertificateAuthorityInfo.OadmId, Assert.Single(harness.Policies.PoliciesOf(PolicyKinds.AllowCa)).CaId);
        Assert.Contains(RadiusPlugin.PluginId, harness.Policies.Converted);

        // PKI's store read again from its settings (a restart of PKI) keeps the same list.
        using var again = new PolicyStore(harness.PkiSettings, _ => Task.FromResult<IReadOnlyList<Sdk.Devices.IDeviceInfo>>([]), () => [], () => [], null);
        await again.LoadAsync(CancellationToken.None);
        Assert.Equal(harness.Policies.All.Select(p => p.Id), again.All.Select(p => p.Id));
        Assert.False(await again.ConvertOnceAsync(RadiusPlugin.PluginId, _ => throw new InvalidOperationException("added twice"), CancellationToken.None));
    }
}
