using Oadm.Plugins.Pki.Policies;
using Oadm.Plugins.Pki.Tasks;

namespace Oadm.Plugins.Pki.Tests;

/// <summary>
/// Renew certificates and Install certificates (IEEE 802.1X) take the IEEE 802.1X certificate policy of their dialog
/// (decision 2026-10-25): its identity and EAPOL version; a payload without one (older clients) the first policy; a chosen
/// policy deleted after the dialog fails before the first write.
/// </summary>
public sealed class Dot1xPolicyChoiceTaskTests
{
    private static async Task<PkiPolicy> SiteBAsync(TaskHarness h)
    {
        var builtIn = h.Pki.Service.Policies.FirstOf(PolicyKinds.Dot1xCertificate)!;
        var reply = await h.Pki.InvokeAsync<PolicySaveReply>(PolicyMethods.SavePolicy,
            builtIn with { Id = Guid.Empty, Name = "Site B", Identity = Dot1xIdentity.Custom, CustomIdentity = "b-{serial}", EapolVersion = 2 });
        Assert.Null(reply.Error);
        return reply.Saved!;
    }

    private static string Identity(TaskHarness h) => h.Camera.EapTlsParams["identity"]!.GetValue<string>();

    private static string Renew(Guid? policyId, bool clockConfirmed = false) =>
        PkiJson.Serialize(new RenewPayload { Dot1xPolicyId = policyId, ClockConfirmed = clockConfirmed });

    [Fact]
    public async Task Renew_sets_the_8021x_settings_of_the_chosen_policy_and_an_old_payload_takes_the_first()
    {
        await using var h = await TaskHarness.StartAsync();
        Assert.Null(await h.RunAsync(PkiTaskIds.Dot1xEnable));
        var firstIdentity = Identity(h);
        var siteB = await SiteBAsync(h);
        Assert.True(h.Task<RenewTask>(PkiTaskIds.Renew).RequiresDialog);

        Assert.Null(await h.RunAsync(PkiTaskIds.Renew, Renew(siteB.Id)));

        Assert.Contains("IEEE 802.1X: Verify 802.1X settings: Done", h.Lines);
        Assert.Equal("b-B8A44F631339", Identity(h));
        Assert.Equal("EAPoLv2", h.Camera.EapTlsParams["eapolVersion"]!.GetValue<string>());

        // A payload of an older client (or none): the first IEEE 802.1X policy, as before.
        Assert.Null(await h.RunAsync(PkiTaskIds.Renew));
        Assert.Equal(firstIdentity, Identity(h));
        Assert.Null(await h.RunAsync(PkiTaskIds.Renew, Renew(siteB.Id)));
        Assert.Null(await h.RunAsync(PkiTaskIds.Renew, "{}"));
        Assert.Equal(firstIdentity, Identity(h));
    }

    [Fact]
    public async Task Renew_with_a_deleted_policy_fails_before_the_first_write_also_for_https()
    {
        await using var h = await TaskHarness.StartAsync();
        Assert.Null(await h.RunAsync(PkiTaskIds.HttpsEnable));
        Assert.Null(await h.RunAsync(PkiTaskIds.Dot1xEnable));
        var siteB = await SiteBAsync(h);
        Assert.Null((await h.Pki.InvokeAsync<PolicySaveReply>(PolicyMethods.DeletePolicy, new PolicyRef(siteB.Id))).Error);
        var writes = h.Camera.Writes.Count;

        var ex = await h.RunAsync(PkiTaskIds.Renew, Renew(siteB.Id));

        Assert.Equal(IssuingChoice.PolicyGone, ex!.Message);
        Assert.Equal("The chosen policy no longer exists. Start the task again. Nothing was changed.", IssuingChoice.PolicyGone);
        Assert.Contains("Check compatibility: Failed", h.Lines);
        Assert.Equal(writes, h.Camera.Writes.Count);

        // An HTTPS policy's id is no IEEE 802.1X policy either.
        var https = h.Pki.Service.Policies.FirstOf(PolicyKinds.HttpsCertificate)!;
        Assert.Equal(IssuingChoice.PolicyGone, (await h.RunAsync(PkiTaskIds.Renew, Renew(https.Id)))!.Message);
        Assert.Equal(writes, h.Camera.Writes.Count);
    }

    [Fact]
    public async Task Renew_refuses_a_camera_behind_unless_the_user_continued_in_the_dialog()
    {
        await using var h = await TaskHarness.StartAsync();
        Assert.Null(await h.RunAsync(PkiTaskIds.Dot1xEnable));
        h.Camera.ClockOffset = -TimeSpan.FromMinutes(12.5);
        var writes = h.Camera.Writes.Count;

        var ex = await h.RunAsync(PkiTaskIds.Renew, Renew(null));
        Assert.EndsWith(" Set the device to use NTP. Nothing was changed.", ex!.Message, StringComparison.Ordinal);
        Assert.Equal(writes, h.Camera.Writes.Count);

        Assert.Null(await h.RunAsync(PkiTaskIds.Renew, Renew(null, clockConfirmed: true)));
        Assert.Contains("IEEE 802.1X: Verify 802.1X settings: Done", h.Lines);
    }

    [Fact]
    public async Task Renew_and_install_list_the_8021x_policies_read_only()
    {
        await using var h = await TaskHarness.StartAsync();
        await SiteBAsync(h);
        var context = new PkiTaskContext(h.Camera);

        foreach (var reply in new[]
        {
            await h.Task<RenewTask>(PkiTaskIds.Renew).QueryAsync(context, h.Device, PkiQueries.ListIssuingPolicies, null, CancellationToken.None),
            await h.Task<InstallCertificatesTask>(PkiTaskIds.Install).QueryAsync(context, h.Device, PkiQueries.ListIssuingPolicies, null, CancellationToken.None),
        })
        {
            var list = PkiJson.Deserialize<IssuingPolicyList>(reply);
            Assert.Equal(["IEEE 802.1X", "Site B"], list.Policies.Select(p => p.Name));
            Assert.Equal(2, list.Summaries.Count);
        }

        Assert.Empty(h.Camera.Requests);
    }

    [Fact]
    public async Task Install_for_8021x_sets_the_chosen_policy_refuses_a_deleted_one_and_takes_the_first_without_one()
    {
        await using var h = await TaskHarness.StartAsync();
        var siteB = await SiteBAsync(h);
        using var ca = TestCa.Root("Customer CA");
        using var leaf = RenewDeleteInstallTests.LeafWithKey(ca, "10.0.0.48");
        var pfx = TestCa.Pfx("secret", leaf);
        string Payload(Guid? policyId) => PkiJson.Serialize(new InstallPayload
        {
            Purpose = InstallPurpose.Dot1x,
            Password = "secret",
            Files = [new InstallFile(h.Device.Id, "f1", "cam48.pfx")],
            PolicyId = policyId,
        });

        Assert.Null(await RenewDeleteInstallTests.RunWithFileAsync(h, Payload(siteB.Id), pfx));
        Assert.Equal("b-B8A44F631339", Identity(h));
        Assert.Equal("EAPoLv2", h.Camera.EapTlsParams["eapolVersion"]!.GetValue<string>());

        Assert.Null(await RenewDeleteInstallTests.RunWithFileAsync(h, Payload(null), pfx));
        Assert.NotEqual("b-B8A44F631339", Identity(h)); // the first policy

        var writes = h.Camera.Writes.Count;
        var ex = await RenewDeleteInstallTests.RunWithFileAsync(h, Payload(Guid.NewGuid()), pfx);
        Assert.Equal(IssuingChoice.PolicyGone, ex!.Message);
        Assert.Equal(writes, h.Camera.Writes.Count);
    }
}
