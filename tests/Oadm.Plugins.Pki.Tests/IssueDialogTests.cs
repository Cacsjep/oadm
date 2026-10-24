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
/// The dialogs of Enable HTTPS and Enable IEEE 802.1X pick an issuing policy (the first preselected) whose values the user
/// may change for this run; the payload carries the policy and the values. The policy editor's issuing kinds. Headless
/// screenshots of both dialogs and the issuing editors.
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
    public async Task Enable_https_preselects_the_first_policy_and_sends_it_with_the_values_of_this_run()
    {
        await using var pki = await PkiHarness.StartAsync();
        var twoYears = await AddAsync(pki, new PkiPolicy { Name = "HTTPS 2 years", Kind = PolicyKinds.HttpsCertificate, CaId = CertificateAuthorityInfo.OadmId, ValidDays = 730, Key = PolicyKeys.Rsa });
        var ctx = new IssuingDialogContext(pki.Service, PolicyKinds.HttpsCertificate);
        var vm = new IssueCertificateViewModel(ctx, Devices, PolicyKinds.HttpsCertificate);

        await vm.LoadAsync(CancellationToken.None);

        Assert.Equal([PkiQueries.ListIssuingPolicies], ctx.Methods); // one read, with the first device
        Assert.Equal(["HTTPS", "HTTPS 2 years"], vm.Policies.Select(p => p.Label));
        Assert.Equal("HTTPS", vm.Policy!.Label);
        Assert.Equal(("365", PolicyKeys.Ecc), (vm.ValidDays, vm.Key.Value));
        Assert.Equal("OADM Root CA SERVER01", vm.CaName);
        Assert.Null(vm.Warning);
        Assert.True(vm.EnableCommand.CanExecute(null));

        vm.Policy = vm.Policies[1];
        Assert.Equal(("730", PolicyKeys.Rsa), (vm.ValidDays, vm.Key.Value)); // the policy fills the form

        vm.ValidDays = "0";
        Assert.Equal(PolicyTexts.ValidDaysRange, vm.ErrorOf(nameof(vm.ValidDays)));
        Assert.False(vm.EnableCommand.CanExecute(null));
        vm.ValidDays = "100";
        vm.Key = PolicyEditorViewModel.KeyChoices.Single(k => k.Value == PolicyKeys.Ecc);
        vm.EnableCommand.Execute(null);

        Assert.True(vm.Completed);
        Assert.Equal(new IssuePayload { PolicyId = twoYears.Id, ValidDays = 100, Key = PolicyKeys.Ecc }, vm.Payload);
        Assert.Equal(730, pki.Policies.Find(twoYears.Id)!.ValidDays); // this run only
    }

    [Fact]
    public async Task Enable_8021x_shows_the_warning_and_the_8021x_settings_of_the_policy()
    {
        await using var pki = await PkiHarness.StartAsync();
        using var radius = TestCa.Root("Corp RADIUS CA");
        var policy = pki.Policies.FirstOf(PolicyKinds.Dot1xCertificate)!;
        await AddAsync(pki, policy with { Identity = Dot1xIdentity.Custom, CustomIdentity = "cam-{serial}", RadiusCa = RadiusCaSource.Imported, RadiusCaPem = Ca.CaCertificates.ToPem(radius) });
        var vm = new IssueCertificateViewModel(new IssuingDialogContext(pki.Service, PolicyKinds.Dot1xCertificate), Devices, PolicyKinds.Dot1xCertificate);

        await vm.LoadAsync(CancellationToken.None);

        Assert.Equal(PkiTaskIds.Dot1xWarning, vm.Warning);
        Assert.Equal(("IEEE 802.1X", Dot1xIdentity.Custom, "cam-{serial}", RadiusCaSource.Imported), (vm.Policy!.Label, vm.Identity.Value, vm.CustomIdentity, vm.RadiusCa.Value));
        Assert.StartsWith("Corp RADIUS CA, valid until ", vm.RadiusCaLine, StringComparison.Ordinal);
        Assert.Equal(["This OADM CA", "Imported CA"], vm.RadiusChoices.Select(c => c.Label));

        vm.CustomIdentity = "cam-{mac}";
        Assert.Equal("Unknown placeholder {mac}. Use {serial} or {hostName}.", vm.ErrorOf(nameof(vm.CustomIdentity)));
        vm.CustomIdentity = "x-{serial}";
        vm.Eapol = PolicyEditorViewModel.EapolChoices[1];
        vm.RadiusCa = vm.RadiusChoices[0];
        vm.EnableCommand.Execute(null);

        Assert.Equal(new IssuePayload
        {
            PolicyId = policy.Id, ValidDays = 365, Key = PolicyKeys.Ecc, EapolVersion = 2, Identity = Dot1xIdentity.Custom, CustomIdentity = "x-{serial}", RadiusCa = RadiusCaSource.Oadm,
        }, vm.Payload);
    }

    [Fact]
    public void Without_a_policy_of_the_kind_the_dialog_says_so_and_enables_nothing()
    {
        var vm = new IssueCertificateViewModel(new IssuingDialogContext(null!, PolicyKinds.HttpsCertificate), Devices, PolicyKinds.HttpsCertificate);

        vm.Apply(new IssuingPolicyList());

        Assert.Equal("There is no HTTPS certificate policy. Add one on the PKI page.", vm.ErrorOf(nameof(vm.Policy)));
        Assert.False(vm.EnableCommand.CanExecute(null));
    }

    [Fact]
    public async Task The_editor_of_an_issuing_policy_checks_its_fields_and_imports_the_radius_ca()
    {
        await using var pki = await PolicyPageTests.StartAsync();
        var card = new PoliciesViewModel(new PluginPageContext(pki.Plugin, pki.Hub));
        await card.LoadAsync(CancellationToken.None);
        Assert.DoesNotContain(card.NewEditor(null).KindChoices, k => k.Value == PolicyKinds.ServerCertificate); // both servers have theirs

        var https = card.NewEditor(null);
        https.Kind = https.KindChoices.Single(k => k.Value == PolicyKinds.HttpsCertificate);
        https.Name = "HTTPS 2 years";
        Assert.Equal(("365", PolicyKeys.Ecc, CertificateAuthorityInfo.OadmId), (https.ValidDays, https.Key.Value, https.IssuingCa!.Value));
        Assert.False(https.ShowsPicker); // no devices
        https.ValidDays = "3651";
        Assert.Equal(PolicyTexts.ValidDaysRange, https.ErrorOf(nameof(https.ValidDays)));
        https.ValidDays = "730";
        https.Key = PolicyEditorViewModel.KeyChoices.Single(k => k.Value == PolicyKeys.Rsa);
        Assert.True(await https.SaveAsync());
        Assert.Equal("Enable HTTPS issues certificates from OADM Root CA SERVER01, valid 730 days, with an RSA key.", card.Rows.Single(r => r.Name == "HTTPS 2 years").Summary);

        var dot1x = card.NewEditor(card.Rows.Single(r => r.Name == "IEEE 802.1X").Source);
        dot1x.Identity = PolicyEditorViewModel.IdentityChoices.Single(c => c.Value == Dot1xIdentity.Custom);
        dot1x.CustomIdentity = "cam-{mac}";
        Assert.Equal("Unknown placeholder {mac}. Use {serial} or {hostName}.", dot1x.ErrorOf(nameof(dot1x.CustomIdentity)));
        dot1x.CustomIdentity = "cam-{serial}";
        dot1x.RadiusCa = PolicyEditorViewModel.RadiusChoices.Single(c => c.Value == RadiusCaSource.Imported);
        Assert.Equal("Import the CA certificate of the RADIUS server.", dot1x.ErrorOf(nameof(dot1x.RadiusCa)));
        await dot1x.ApplyRadiusFileAsync(new PickedFile("x.crt", [1, 2, 3]));
        Assert.Equal("The file is not a certificate (.crt or .cer, PEM or DER).", dot1x.ErrorOf(nameof(dot1x.RadiusCa)));
        using var radius = TestCa.Root("Corp RADIUS CA");
        await dot1x.ApplyRadiusFileAsync(new PickedFile("radius.cer", radius.RawData));
        Assert.Null(dot1x.ErrorOf(nameof(dot1x.RadiusCa)));
        Assert.StartsWith("Corp RADIUS CA, valid until ", dot1x.RadiusCaText, StringComparison.Ordinal);
        dot1x.Eapol = PolicyEditorViewModel.EapolChoices[1];
        Assert.True(await dot1x.SaveAsync());
        var saved = pki.Policies.FirstOf(PolicyKinds.Dot1xCertificate)!;
        Assert.Equal((2, "cam-{serial}", RadiusCaSource.Imported), (saved.EapolVersion!.Value, saved.CustomIdentity, saved.RadiusCa));
        Assert.NotNull(saved.RadiusCaPem);

        var server = card.NewEditor(card.Rows.Single(r => r.Name == "802.1X server" && r.Source.Kind == PolicyKinds.ServerCertificate).Source);
        server.Key = PolicyEditorViewModel.KeyChoices.Single(k => k.Value == PolicyKeys.Rsa);
        Assert.False(await server.SaveAsync());
        Assert.Equal(PolicyTexts.Dot1xServerNeedsEcc, server.ErrorOf(nameof(server.Key)));

        // The issuing policies lead the table in age order; the system policy a task needs is never deleted (the reason
        // below the table), the user's own one is.
        Assert.Equal(["HTTPS", "IEEE 802.1X", "802.1X server", "EST server", "HTTPS 2 years"], card.Rows.Where(r => r.Group == PolicyGroups.Issuing).Select(r => r.Name));
        Assert.Equal(card.Rows.Take(5).Select(r => r.Name), card.Rows.Where(r => r.Group == PolicyGroups.Issuing).Select(r => r.Name));
        await card.DeleteRowCommand.ExecuteAsync(card.Rows.Single(r => r.Name == "IEEE 802.1X"));
        Assert.Equal(PolicyTexts.SystemPolicy, card.Error);
        await card.DeleteRowCommand.ExecuteAsync(card.Rows.Single(r => r.Name == "HTTPS 2 years"));
        Assert.Null(card.Error);
        Assert.DoesNotContain(card.Rows, r => r.Name == "HTTPS 2 years");
    }

    [Fact]
    public async Task The_dialogs_and_the_issuing_editors_render()
    {
        var outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        await using var pki = await PolicyPageTests.StartAsync();
        using var radius = TestCa.Root("Corp RADIUS CA");
        await AddAsync(pki, new PkiPolicy { Name = "HTTPS 2 years", Kind = PolicyKinds.HttpsCertificate, CaId = CertificateAuthorityInfo.OadmId, ValidDays = 730, Key = PolicyKeys.Rsa });
        var dot1xPolicy = pki.Policies.FirstOf(PolicyKinds.Dot1xCertificate)!;
        await AddAsync(pki, dot1xPolicy with { Identity = Dot1xIdentity.Custom, CustomIdentity = "axis-{serial}", RadiusCa = RadiusCaSource.Imported, RadiusCaPem = Ca.CaCertificates.ToPem(radius) });
        var page = new PluginPageContext(pki.Plugin, pki.Hub);

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

            var card = new PoliciesViewModel(page);
            await card.LoadAsync(CancellationToken.None);
            foreach (var (row, name) in new[] { ("HTTPS 2 years", "pki-policy-https.png"), ("IEEE 802.1X", "pki-policy-dot1x-certificate.png"), ("EST server", "pki-policy-server-certificate.png") })
            {
                var editor = card.NewEditor(card.Rows.Single(r => r.Name == row && PolicyKinds.IsIssuing(r.Source.Kind)).Source);
                var window = PolicyFormView.CreateWindow(editor);
                Oadm.Client.App.ApplyCrispText(window);
                window.Show();
                HeadlessPageTests.Pump();
                HeadlessPageTests.Capture(window, outDir, name);
                texts[row] = Texts(window);
                window.Close();
            }

            return texts;
        }, CancellationToken.None);

        var https = checks[PolicyKinds.HttpsCertificate];
        Assert.Contains("Policy", https);
        Assert.Contains("Valid (days)", https);
        Assert.Contains("OADM Root CA SERVER01", https);
        Assert.DoesNotContain(PkiTaskIds.Dot1xWarning, https);
        Assert.DoesNotContain("EAP identity", https);
        var dot1x = checks[PolicyKinds.Dot1xCertificate];
        Assert.Contains(PkiTaskIds.Dot1xWarning, dot1x);
        Assert.Contains("EAP identity", dot1x);
        Assert.Contains("Custom identity", dot1x);
        Assert.Contains(dot1x, t => t?.StartsWith("Corp RADIUS CA, valid until ", StringComparison.Ordinal) == true);
        Assert.Contains("Valid (days)", checks["HTTPS 2 years"]);
        Assert.DoesNotContain("Sites", checks["HTTPS 2 years"]); // no devices
        Assert.Contains("RADIUS server CA", checks["IEEE 802.1X"]);
        Assert.Contains("For", checks["EST server"]);
        Assert.All(checks.Values.SelectMany(t => t), t => Assert.False(t?.EndsWith("...", StringComparison.Ordinal) == true || t?.EndsWith('…') == true, t));
    }

    private static List<string?> Texts(Window window) =>
    [
        .. window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text),
    ];
}
