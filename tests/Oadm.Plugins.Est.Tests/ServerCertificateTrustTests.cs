using Microsoft.Extensions.Time.Testing;

using Oadm.Plugins.Est.Serving;
using Oadm.Plugins.Pki.Policies;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Est.Tests;

/// <summary>
/// The EST server's certificate (docs/spec/est-server.md "Server certificate"): a revoked one is never shown again, and the
/// status warns when a site's node address or one of this server's addresses is not in it.
/// </summary>
public sealed class ServerCertificateTrustTests
{
    [Fact]
    public async Task A_revoked_server_certificate_is_not_shown_again()
    {
        using var ca = new FakeAuthority();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var certificates = new ServerCertificates(() => ca) { ChosenId = () => "test-server", Time = time };
        var shown = await certificates.GetAsync(CancellationToken.None);
        Assert.NotNull(shown);
        Assert.Same(shown, await certificates.GetAsync(CancellationToken.None)); // read once, then kept

        ca.Revoke(ca.ServerSerial());

        Assert.Null(await certificates.GetAsync(CancellationToken.None));
        Assert.Equal("The EST server cannot use its certificate: it is no longer accepted.", certificates.Problem);
    }

    [Fact]
    public async Task The_status_warns_for_a_site_and_for_an_own_address_that_are_not_in_the_certificate()
    {
        using var ca = new FakeAuthority();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var sites = new FakeSites();
        var site = sites.Add("Building B");
        var addresses = new List<string> { "10.0.0.17" };
        var policies = await TestPolicies.EstStoreAsync();
        await using var est = new EstService(new EstHost(new FakeSettings(), new NoDevices(), new FakePluginNetwork(sites), () => ca, () => true)
        {
            Sites = sites,
            Policies = () => policies,
            Time = time,
            LocalAddresses = () => addresses,
        });
        await est.StartAsync(CancellationToken.None);
        Assert.Null(est.GetState(null).CertificateWarning); // 10.0.0.17 is in the certificate

        addresses.Add("10.0.0.250"); // a new address of this server
        time.Advance(TimeSpan.FromMinutes(1)); // the status reads the certificate at most every 30 s
        Assert.Equal(PolicyTexts.AddressNotInCertificate("10.0.0.250"), est.GetState(null).CertificateWarning);

        est.Store.AddSiteAddress(site.Id, "192.168.5.2");
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(PolicyTexts.SiteNotInCertificate("Building B"), est.GetState(null).CertificateWarning);
    }

    private sealed class NoDevices : IDeviceRepository
    {
        public Task<IReadOnlyList<IDeviceInfo>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<IDeviceInfo>>([]);

        public Task<IDeviceInfo?> FindAsync(Guid id, CancellationToken ct) => Task.FromResult<IDeviceInfo?>(null);
    }
}
