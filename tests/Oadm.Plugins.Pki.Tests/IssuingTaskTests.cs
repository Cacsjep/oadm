using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;

using Oadm.Plugins.Pki.Device;
using Oadm.Plugins.Pki.Policies;
using Oadm.Plugins.Pki.Tasks;
using Oadm.Sdk.Certificates;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Pki.Tests;

/// <summary>
/// Enable HTTPS and Enable IEEE 802.1X issue with the issuing policy of the dialog (docs/spec/pki.md "Issuing policies have
/// no devices"): the policy's values, changed for this run where the payload says so, checked before the first write.
/// </summary>
public sealed class IssuingTaskTests
{
    private static async Task<PkiPolicy> AddAsync(TaskHarness h, PkiPolicy policy)
    {
        var reply = await h.Pki.InvokeAsync<PolicySaveReply>(PolicyMethods.SavePolicy, policy);
        Assert.Null(reply.Error);
        return reply.Saved!;
    }

    private static PkiPolicy Https(string name, int days, string key) =>
        new() { Name = name, Kind = PolicyKinds.HttpsCertificate, CaId = CertificateAuthorityInfo.OadmId, ValidDays = days, Key = key };

    [Fact]
    public async Task Enable_https_issues_with_the_chosen_policy_and_the_values_of_this_run()
    {
        await using var h = await TaskHarness.StartAsync();
        var twoYears = await AddAsync(h, Https("HTTPS 2 years", 730, PolicyKeys.Rsa));

        // The policy's own values.
        Assert.Null(await h.RunAsync(PkiTaskIds.HttpsEnable, PkiJson.Serialize(new IssuePayload { PolicyId = twoYears.Id })));
        using (var served = h.Served(h.Camera.WebServerAlias))
        {
            Assert.Equal(2048, served.GetRSAPublicKey()!.KeySize);
            Assert.InRange((served.NotAfter.ToUniversalTime() - h.Time.GetUtcNow().UtcDateTime).TotalDays, 729, 731);
        }

        Assert.Contains("HTTPS 2 years: ", h.Detail(PkiSteps.CheckCompatibility), StringComparison.Ordinal);
        Assert.Contains(h.Context.LogEntries, e => e.Message.Contains("with the policy HTTPS 2 years", StringComparison.Ordinal));

        // The dialog changed the values for this run: the policy stays as it is.
        Assert.Null(await h.RunAsync(PkiTaskIds.HttpsEnable, PkiJson.Serialize(new IssuePayload { PolicyId = twoYears.Id, ValidDays = 100, Key = PolicyKeys.Ecc })));
        using (var served = h.Served(h.Camera.WebServerAlias))
        {
            Assert.NotNull(served.GetECDsaPublicKey());
            Assert.InRange((served.NotAfter.ToUniversalTime() - h.Time.GetUtcNow().UtcDateTime).TotalDays, 99, 101);
        }

        Assert.Equal(["RSA-2048", "EC-P256"], h.Camera.KeyTypesCreated);
        Assert.Equal(730, h.Pki.Service.Policies.Find(twoYears.Id)!.ValidDays);
    }

    [Fact]
    public async Task A_run_without_a_dialog_uses_the_first_policy_of_the_kind()
    {
        await using var h = await TaskHarness.StartAsync();
        var first = h.Pki.Service.Policies.FirstOf(PolicyKinds.HttpsCertificate)!;
        Assert.Equal(PolicyLimits.HttpsName, first.Name);
        await AddAsync(h, Https("HTTPS 2 years", 730, PolicyKeys.Rsa));

        Assert.Null(await h.RunAsync(PkiTaskIds.HttpsEnable, "{}"));

        Assert.Equal(["EC-P256"], h.Camera.KeyTypesCreated);
        Assert.Contains("HTTPS: ", h.Detail(PkiSteps.CheckCompatibility), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, null, "Use 1 to 3,650 days.")]
    [InlineData(3651, null, "Use 1 to 3,650 days.")]
    [InlineData(null, "dsa", "Choose a key.")]
    public async Task Values_that_a_policy_would_not_allow_change_nothing(int? days, string? key, string message)
    {
        await using var h = await TaskHarness.StartAsync();

        var ex = await h.RunAsync(PkiTaskIds.HttpsEnable, PkiJson.Serialize(new IssuePayload { ValidDays = days, Key = key }));

        Assert.Contains(message, ex!.Message, StringComparison.Ordinal);
        Assert.EndsWith("Nothing was changed.", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(h.Camera.Requests, r => r.IsWrite);
    }

    [Fact]
    public async Task A_policy_deleted_after_the_dialog_changes_nothing()
    {
        await using var h = await TaskHarness.StartAsync();
        var added = await AddAsync(h, Https("HTTPS 2 years", 730, PolicyKeys.Rsa));
        Assert.Null((await h.Pki.InvokeAsync<PolicySaveReply>(PolicyMethods.DeletePolicy, new PolicyRef(added.Id))).Error);

        var ex = await h.RunAsync(PkiTaskIds.HttpsEnable, PkiJson.Serialize(new IssuePayload { PolicyId = added.Id }));

        Assert.Contains("The chosen policy no longer exists", ex!.Message, StringComparison.Ordinal);
        Assert.Empty(h.Camera.Requests);
    }

    [Fact]
    public async Task A_device_that_cannot_make_the_key_changes_nothing()
    {
        await using var h = await TaskHarness.StartAsync();
        h.Camera.KeyTypes = ["RSA-2048", "RSA-4096"];

        var ex = await h.RunAsync(PkiTaskIds.HttpsEnable, "{}");

        Assert.IsType<DeviceNotCompatibleException>(ex);
        Assert.Equal("The device cannot create an ECC key. Choose a policy with RSA. Nothing was changed.", ex.Message);
        Assert.DoesNotContain(h.Camera.Requests, r => r.IsWrite);

        // RSA works on it.
        Assert.Null(await h.RunAsync(PkiTaskIds.HttpsEnable, PkiJson.Serialize(new IssuePayload { Key = PolicyKeys.Rsa })));
        Assert.Equal(["RSA-2048"], h.Camera.KeyTypesCreated);
    }

    [Fact]
    public async Task A_device_that_does_not_list_its_key_types_gets_rsa_only()
    {
        await using var h = await TaskHarness.StartAsync();
        h.Camera.KeyTypes = null;

        Assert.IsType<DeviceNotCompatibleException>(await h.RunAsync(PkiTaskIds.HttpsEnable, "{}"));
        Assert.DoesNotContain(h.Camera.Requests, r => r.IsWrite);
        Assert.Null(await h.RunAsync(PkiTaskIds.HttpsEnable, PkiJson.Serialize(new IssuePayload { Key = PolicyKeys.Rsa })));
    }

    [Fact]
    public async Task Enable_8021x_takes_the_8021x_settings_of_the_policy_and_of_the_run()
    {
        await using var h = await TaskHarness.StartAsync();
        var policy = h.Pki.Service.Policies.FirstOf(PolicyKinds.Dot1xCertificate)!;
        Assert.Equal(PolicyLimits.Dot1xName, policy.Name);

        Assert.Null(await h.RunAsync(PkiTaskIds.Dot1xEnable, PkiJson.Serialize(new IssuePayload
        {
            PolicyId = policy.Id, EapolVersion = 2, Identity = Dot1xIdentity.Custom, CustomIdentity = "x-{serial}", Key = PolicyKeys.Rsa,
        })));

        Assert.Equal("x-B8A44F631339", h.Camera.EapTlsParams["identity"]!.GetValue<string>());
        Assert.Equal("EAPoLv2", h.Camera.EapTlsParams["eapolVersion"]!.GetValue<string>());
        Assert.Equal(["RSA-2048"], h.Camera.KeyTypesCreated);
        Assert.Equal(1, h.Pki.Service.Policies.Find(policy.Id)!.EapolVersion); // the policy keeps its values
    }

    [Fact]
    public async Task An_imported_radius_ca_chosen_without_one_in_the_policy_changes_nothing()
    {
        await using var h = await TaskHarness.StartAsync();

        var ex = await h.RunAsync(PkiTaskIds.Dot1xEnable, PkiJson.Serialize(new IssuePayload { RadiusCa = RadiusCaSource.Imported }));

        Assert.Contains("Import the CA certificate of the RADIUS server.", ex!.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(h.Camera.Requests, r => r.IsWrite);
    }

    [Fact]
    public async Task The_dialogs_read_the_policies_of_their_kind()
    {
        await using var h = await TaskHarness.StartAsync();
        await AddAsync(h, Https("HTTPS 2 years", 730, PolicyKeys.Rsa));
        var context = new PkiTaskContext(h.Camera);

        var https = PkiJson.Deserialize<IssuingPolicyList>(await h.Task<HttpsEnableTask>(PkiTaskIds.HttpsEnable)
            .QueryAsync(context, h.Device, PkiQueries.ListIssuingPolicies, null, CancellationToken.None));
        var dot1x = PkiJson.Deserialize<IssuingPolicyList>(await h.Task<Dot1xEnableTask>(PkiTaskIds.Dot1xEnable)
            .QueryAsync(context, h.Device, PkiQueries.ListIssuingPolicies, null, CancellationToken.None));

        Assert.Equal(["HTTPS", "HTTPS 2 years"], https.Policies.Select(p => p.Name));
        Assert.Equal(["IEEE 802.1X"], dot1x.Policies.Select(p => p.Name));
        Assert.Equal(Ca.CaCertificates.CommonName(h.Pki.Service.Ca!.Certificate), https.CaName);
        Assert.Empty(h.Camera.Requests); // read only, and nothing of the device
    }

    [Fact]
    public async Task Renew_issues_with_the_first_policy_of_each_kind()
    {
        await using var h = await TaskHarness.StartAsync();
        Assert.Null(await h.RunAsync(PkiTaskIds.HttpsEnable, PkiJson.Serialize(new IssuePayload { Key = PolicyKeys.Rsa })));
        var first = h.Pki.Service.Policies.FirstOf(PolicyKinds.HttpsCertificate)!;
        Assert.Null((await h.Pki.InvokeAsync<PolicySaveReply>(PolicyMethods.SavePolicy, first with { ValidDays = 30 })).Error);

        Assert.Null(await h.RunAsync(PkiTaskIds.Renew));

        using var served = h.Served(h.Camera.WebServerAlias);
        Assert.NotNull(served.GetECDsaPublicKey());
        Assert.InRange((served.NotAfter.ToUniversalTime() - h.Time.GetUtcNow().UtcDateTime).TotalDays, 29, 31);
    }

    [Fact]
    public void The_key_types_come_from_the_default_keystore_of_the_device()
    {
        var root = JsonNode.Parse(PkiFixture.Read("rest-cert-v1-get.json"))!["data"];
        var types = CertApi.ParseKeyTypes(root)!;
        Assert.Contains("EC-P256", types);
        Assert.Contains("RSA-1024", types); // SE0, the default keystore

        var withoutDefault = JsonNode.Parse("""{"keystores":[{"id":"A","key_types":["RSA-2048","EC-P256"]},{"id":"B","key_types":["RSA-2048"]}]}""");
        Assert.Equal(["RSA-2048"], CertApi.ParseKeyTypes(withoutDefault)!); // the types every keystore offers
        Assert.Null(CertApi.ParseKeyTypes(JsonNode.Parse("{}")));
        Assert.Equal("EC-P256", CertApi.KeyTypeOf(PolicyKeys.Ecc));
        Assert.Equal("RSA-2048", CertApi.KeyTypeOf(PolicyKeys.Rsa));
    }
}
