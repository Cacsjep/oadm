using Avalonia.Controls;
using Avalonia.VisualTree;

using Oadm.Plugins.Pki.Client;
using Oadm.Plugins.Pki.Policies;
using Oadm.Plugins.Pki.Tasks;
using Oadm.Sdk.Certificates;
using Oadm.Sdk.Client;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Pki.Tests;

/// <summary>The dialog context of Enable HTTPS and Enable IEEE 802.1X: the task's query of the policies, read from PKI.</summary>
internal sealed class IssuingDialogContext(PkiService service, string kind) : ITaskDialogContext
{
    public List<string> Methods { get; } = [];

    public Task<string?> QueryAsync(Guid deviceId, string method, string? payloadJson, CancellationToken ct)
    {
        Methods.Add(method);
        Assert.Equal(PkiQueries.ListIssuingPolicies, method);
        return Task.FromResult<string?>(PkiJson.Serialize(IssuingChoice.List(service, kind)));
    }

    public Task<UploadedFile> UploadAsync(string localPath, IProgress<double>? progress, CancellationToken ct) => throw new NotSupportedException();
}

/// <summary>
/// The dialogs of Enable HTTPS and Enable IEEE 802.1X pick an issuing policy (the first preselected) and show what it
/// issues in one sentence; the payload carries the policy only (decision 2026-10-25: the policy decides every value). The
/// issuing policies' forms. Headless screenshots of both dialogs and the forms in the side panel.
/// </summary>
public sealed class IssueDialogTests
{
    private static readonly IReadOnlyList<IDeviceInfo> Devices = [new FakeDevice { Serial = "ACCC8E000001", Address = "10.0.0.48" }, new FakeDevice { Serial = "ACCC8E000002", Address = "10.0.0.49" }];

    private static async Task<PkiPolicy> AddAsync(PkiHarness pki, PkiPolicy policy)
    {
        var reply = await pki.InvokeAsync<PolicySaveReply>(PolicyMethods.SavePolicy, policy);
        Assert.Null(reply.Error);
        return reply.Saved!;
    }

    [Fact]
    public async Task Enable_https_preselects_the_first_policy_shows_its_sentence_and_sends_the_policy_only()
    {
        await using var pki = await PkiHarness.StartAsync();
        var twoYears = await AddAsync(pki, new PkiPolicy { Name = "HTTPS 2 years", Kind = PolicyKinds.HttpsCertificate, CaId = CertificateAuthorityInfo.OadmId, ValidDays = 730, Key = PolicyKeys.Rsa });
        var ctx = new IssuingDialogContext(pki.Service, PolicyKinds.HttpsCertificate);
        var vm = new IssueCertificateViewModel(ctx, Devices, PolicyKinds.HttpsCertificate);

        await vm.LoadAsync(CancellationToken.None);

        Assert.Equal([PkiQueries.ListIssuingPolicies], ctx.Methods); // one read, with the first device
        Assert.Equal(["HTTPS", "HTTPS 2 years"], vm.Policies.Select(p => p.Label));
        Assert.Equal("HTTPS", vm.Policy!.Label);
        Assert.Equal("Enable HTTPS issues certificates from OADM Root CA SERVER01, valid 365 days, with an ECC key.", vm.Summary);
        Assert.Null(vm.Warning);
        Assert.True(vm.EnableCommand.CanExecute(null));

        vm.Policy = vm.Policies[1];
        Assert.Equal("Enable HTTPS issues certificates from OADM Root CA SERVER01, valid 730 days, with an RSA key.", vm.Summary);
        vm.EnableCommand.Execute(null);

        Assert.True(vm.Completed);
        Assert.Equal(new IssuePayload { PolicyId = twoYears.Id }, vm.Payload);
    }

    [Fact]
    public async Task Enable_8021x_shows_the_warning_and_the_sentence_of_the_policy()
    {
        await using var pki = await PkiHarness.StartAsync();
        var policy = pki.Policies.FirstOf(PolicyKinds.Dot1xCertificate)!;
        await AddAsync(pki, policy with { Identity = Dot1xIdentity.Custom, CustomIdentity = "cam-{serial}" });
        var vm = new IssueCertificateViewModel(new IssuingDialogContext(pki.Service, PolicyKinds.Dot1xCertificate), Devices, PolicyKinds.Dot1xCertificate);

        await vm.LoadAsync(CancellationToken.None);

        Assert.Equal(PkiTaskIds.Dot1xWarning, vm.Warning);
        Assert.Equal("IEEE 802.1X", vm.Policy!.Label);
        Assert.Contains("Cameras sign in as cam-{serial}.", vm.Summary, StringComparison.Ordinal);
        vm.EnableCommand.Execute(null);
        Assert.Equal(new IssuePayload { PolicyId = policy.Id }, vm.Payload);
    }

    [Fact]
    public void Without_a_policy_of_the_kind_the_dialog_says_so_and_enables_nothing()
    {
        var vm = new IssueCertificateViewModel(new IssuingDialogContext(null!, PolicyKinds.HttpsCertificate), Devices, PolicyKinds.HttpsCertificate);

        vm.Apply(new IssuingPolicyList());

        Assert.Equal("There is no HTTPS certificate policy. Add one on the PKI page.", vm.ErrorOf(nameof(vm.Policy)));
        Assert.Null(vm.Summary);
        Assert.False(vm.EnableCommand.CanExecute(null));
    }

    [Fact]
    public async Task The_form_of_an_issuing_policy_checks_its_fields_and_picks_the_radius_server_ca_from_the_list()
    {
        await using var pki = await PolicyPageTests.StartAsync();
        var page = new PoliciesViewModel(PolicyPageTests.Page(pki));
        await page.LoadAsync(CancellationToken.None);
        // One 802.1X server and one EST server policy, both there: Create offers neither.
        Assert.DoesNotContain(page.KindChoices, k => k.Kind is PolicyKinds.Dot1xServer or PolicyKinds.EstServer or PolicyKinds.LegacyServerCertificate);

        var https = page.NewEditor(PolicyKinds.HttpsCertificate);
        https.Name = "HTTPS 2 years";
        Assert.Equal(("365", PolicyKeys.Ecc, CertificateAuthorityInfo.OadmId), (https.ValidDays, https.Key.Value, https.IssuingCa!.Value));
        Assert.False(https.ShowsDevicesField); // no devices
        https.ValidDays = "3651";
        Assert.Equal(PolicyTexts.ValidDaysRange, https.ErrorOf(nameof(https.ValidDays)));
        https.ValidDays = "730";
        https.Key = PolicyEditorViewModel.KeyChoices.Single(k => k.Value == PolicyKeys.Rsa);
        Assert.True(await https.SaveAsync());
        Assert.Equal("Enable HTTPS issues certificates from OADM Root CA SERVER01, valid 730 days, with an RSA key.", page.Rows.Single(r => r.Name == "HTTPS 2 years").Summary);

        // The built-in IEEE 802.1X policy: the identity and the RADIUS server CA, a CA of the list (decision 2026-10-25).
        using var radius = TestCa.Root("Corp RADIUS CA");
        Assert.True((await pki.InvokeAsync<PkiReply>(PkiMethods.ImportAuthority, new ImportAuthorityRequest(Convert.ToBase64String(radius.RawData), "radius.crt"))).Ok);
        await page.LoadAsync(CancellationToken.None);
        var dot1x = PolicyPageTests.Open(page, "IEEE 802.1X");
        Assert.True(dot1x.IsInPlace);
        dot1x.Identity = PolicyEditorViewModel.IdentityChoices.Single(c => c.Value == Dot1xIdentity.Custom);
        dot1x.CustomIdentity = "cam-{mac}";
        Assert.Equal("Unknown placeholder {mac}. Use {serial} or {hostName}.", dot1x.ErrorOf(nameof(dot1x.CustomIdentity)));
        dot1x.CustomIdentity = "cam-{serial}";
        Assert.Contains(dot1x.RadiusCaChoices, c => c.Label == "Corp RADIUS CA");
        dot1x.RadiusCa = dot1x.RadiusCaChoices.Single(c => c.Label == "Corp RADIUS CA");
        dot1x.Eapol = PolicyEditorViewModel.EapolChoices[1];
        Assert.Equal(PolicyEditorViewModel.NotSavedText, dot1x.SaveStatus);
        Assert.True(await dot1x.SaveAsync());
        var saved = pki.Policies.FirstOf(PolicyKinds.Dot1xCertificate)!;
        Assert.Equal((2, "cam-{serial}", "imported:" + Ca.CaCertificates.Fingerprint(radius)), (saved.EapolVersion!.Value, saved.CustomIdentity, saved.RadiusCaId));

        // The 802.1X server policy picks a certificate; no Issued by, valid days or key of its own.
        var server = PolicyPageTests.Open(page, "802.1X server");
        Assert.True(server.IsServerKind);
        Assert.False(server.IsIssuing);

        // The built-in policy a task needs is never deleted (the server refuses it), the user's own one is.
        var refused = await pki.InvokeAsync<PolicySaveReply>(PolicyMethods.DeletePolicy, new PolicyRef(saved.Id));
        Assert.Equal(PolicyTexts.SystemPolicy, refused.Error!.Message);
        PolicyPageTests.Open(page, "HTTPS 2 years");
        await page.DeleteOpenCommand.ExecuteAsync(null);
        Assert.Null(page.Error);
        Assert.DoesNotContain(page.Rows, r => r.Name == "HTTPS 2 years");
    }
}

/// <summary>Both dialogs and the issuing forms in the side panel, rendered headless (screenshots into OADM_SCREENSHOT_DIR).</summary>
[Collection(HeadlessSessions.Name)]
public sealed class IssueDialogHeadlessTests
{
    private static readonly IReadOnlyList<IDeviceInfo> Devices = [new FakeDevice { Serial = "ACCC8E000001", Address = "10.0.0.48" }];

    [Fact]
    public async Task The_dialogs_and_the_issuing_forms_render()
    {
        var outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        await using var pki = await PolicyPageTests.StartAsync();
        await pki.Service.EnsureServerCertificatesAsync(null, CancellationToken.None);
        Assert.Null((await pki.InvokeAsync<PolicySaveReply>(PolicyMethods.SavePolicy,
            new PkiPolicy { Name = "HTTPS 2 years", Kind = PolicyKinds.HttpsCertificate, CaId = CertificateAuthorityInfo.OadmId, ValidDays = 730, Key = PolicyKeys.Rsa })).Error);
        var ctx = PolicyPageTests.Page(pki);

        var checks = await HeadlessEntry.Session.Dispatch(async () =>
        {
            var texts = new Dictionary<string, List<string?>>();
            foreach (var (kind, name) in new[] { (PolicyKinds.HttpsCertificate, "pki-enable-https.png"), (PolicyKinds.Dot1xCertificate, "pki-enable-dot1x.png") })
            {
                var vm = new IssueCertificateViewModel(new IssuingDialogContext(pki.Service, kind), Devices, kind);
                var window = new IssueCertificateWindow();
                PkiDialogs.Attach(window, vm);
                Oadm.Client.App.ApplyCrispText(window);
                window.Show();
                await vm.LoadAsync(CancellationToken.None);
                HeadlessPageTests.Pump();
                HeadlessPageTests.Capture(window, outDir, name);
                texts[kind] = Texts(window);
                window.Close();
            }

            var view = new PkiView();
            using var page = new PkiViewModel(ctx, new FakeClientTrust());
            var host = HeadlessPageTests.Host(view, 1700, 1100);
            host.Show();
            view.DataContext = page;
            await page.LoadAsync();
            await page.Policies.LoadAsync(CancellationToken.None);
            page.ShowTab(PkiViewModel.PoliciesTabId);
            foreach (var (row, name) in new[] { ("HTTPS 2 years", "pki-policy-https.png"), ("IEEE 802.1X", "pki-policy-dot1x-certificate.png"), ("EST server", "pki-policy-est-server.png"), ("802.1X server", "pki-policy-dot1x-server.png") })
            {
                page.Policies.SelectedRow = page.Policies.Rows.Single(r => r.Name == row);
                HeadlessPageTests.Pump();
                HeadlessPageTests.Capture(host, outDir, name);
                texts[row] = Texts(host);
            }

            host.Close();
            return texts;
        }, CancellationToken.None);

        var https = checks[PolicyKinds.HttpsCertificate];
        Assert.Contains("Policy", https);
        Assert.Contains("Enable HTTPS issues certificates from OADM Root CA SERVER01, valid 365 days, with an ECC key.", https);
        Assert.DoesNotContain("Valid (days)", https); // the policy decides
        Assert.DoesNotContain(PkiTaskIds.Dot1xWarning, https);
        var dot1x = checks[PolicyKinds.Dot1xCertificate];
        Assert.Contains(PkiTaskIds.Dot1xWarning, dot1x);
        Assert.DoesNotContain("EAP identity", dot1x);
        Assert.Contains("Valid (days)", checks["HTTPS 2 years"]);
        Assert.DoesNotContain("Choose devices", checks["HTTPS 2 years"]); // no devices
        Assert.Contains("RADIUS server CA", checks["IEEE 802.1X"]);
        Assert.Contains("EAP identity", checks["IEEE 802.1X"]);
        Assert.Contains("Certificate", checks["EST server"]);
        Assert.DoesNotContain("Issued by", checks["EST server"]); // the server picks a certificate
        Assert.Contains("Certificate", checks["802.1X server"]);
        Assert.Contains("Minimum TLS version", checks["802.1X server"]);
        Assert.All(checks.Values.SelectMany(t => t), t => Assert.False(t?.EndsWith("...", StringComparison.Ordinal) == true || t?.EndsWith('…') == true, t));
    }

    private static List<string?> Texts(Window window) =>
    [
        .. window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text),
    ];
}
