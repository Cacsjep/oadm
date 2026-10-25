using Avalonia.Controls;
using Avalonia.VisualTree;

using Oadm.Plugins.Pki.Client;
using Oadm.Sdk.Certificates;
using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Controls;

namespace Oadm.Plugins.Pki.Tests;

/// <summary>The tabs of the PKI page (docs/spec/pki.md "The PKI page"): Certificates, Policies, then the server pages the host hands over.</summary>
[Collection(HeadlessSessions.Name)]
public sealed class PkiTabsTests
{
    private static Task<T> OnUi<T>(Func<T> work) => HeadlessEntry.Session.Dispatch(() => Task.FromResult(work()), CancellationToken.None);

    [Fact]
    public async Task Without_server_tabs_certificates_and_policies_show()
    {
        await using var pki = await PkiHarness.StartAsync();
        var ctx = new PluginPageContext(pki.Plugin, pki.Hub);
        using var vm = new PkiViewModel(ctx, new FakeClientTrust());

        Assert.True(vm.HasTabs);
        Assert.Equal(["Certificates", "Policies"], vm.Tabs.Select(t => t.Title).ToArray());
        Assert.True(vm.IsCertificatesTab);
        Assert.Null(vm.ActiveTabView);
    }

    [Fact]
    public async Task The_server_tabs_follow_certificates_in_a_fixed_order_and_open_on_request()
    {
        await using var pki = await PkiHarness.StartAsync();
        var ctx = new PluginPageContext(pki.Plugin, pki.Hub);
        var (est, radius) = await OnUi(() => ((Control)new TextBlock { Text = "EST" }, (Control)new TextBlock { Text = "802.1X" }));
        ctx.Tabs = [new CorePluginTab("oadm.est", "EST server", est), new CorePluginTab("oadm.radius", "802.1X server", radius)];
        using var vm = new PkiViewModel(ctx, new FakeClientTrust());

        Assert.True(vm.HasTabs);
        Assert.Equal(["Certificates", "Policies", "802.1X server", "EST server"], vm.Tabs.Select(t => t.Title).ToArray());
        Assert.True(vm.Tabs[0].IsActive);

        ctx.RequestTab("oadm.est"); // OpenAsync("plugin:oadm.est")
        Assert.False(vm.IsCertificatesTab);
        Assert.Same(est, vm.ActiveTabView);
        Assert.Equal([false, false, false, true], vm.Tabs.Select(t => t.IsActive).ToArray());

        vm.Tabs[2].ActivateCommand.Execute(null); // a click on the tab
        Assert.Same(radius, vm.ActiveTabView);

        ctx.RequestTab("made.up"); // unknown: nothing changes
        Assert.Same(radius, vm.ActiveTabView);

        PkiTabRequest.Set(PkiViewModel.PoliciesTabId); // Edit policies of a server tab
        ctx.RequestTab(PkiPluginInfo.PluginId);
        Assert.True(vm.IsPoliciesTab);
        Assert.Null(vm.ActiveTabView);

        ctx.RequestTab(PkiPluginInfo.PluginId); // PKI's page without a request: Certificates
        Assert.True(vm.IsCertificatesTab);
        Assert.Null(vm.ActiveTabView);
    }

    [Fact]
    public async Task A_tab_that_leaves_falls_back_to_certificates_and_the_others_keep_the_shown_one()
    {
        await using var pki = await PkiHarness.StartAsync();
        var ctx = new PluginPageContext(pki.Plugin, pki.Hub);
        var (est, radius) = await OnUi(() => ((Control)new TextBlock(), (Control)new TextBlock()));
        ctx.Tabs = [new CorePluginTab("oadm.radius", "802.1X server", radius), new CorePluginTab("oadm.est", "EST server", est)];
        using var vm = new PkiViewModel(ctx, new FakeClientTrust());
        ctx.RequestTab("oadm.est");

        ctx.Tabs = [new CorePluginTab("oadm.est", "EST server", est)]; // the role lost "show" of the 802.1X server
        Assert.Same(est, vm.ActiveTabView);
        Assert.Equal(["Certificates", "Policies", "EST server"], vm.Tabs.Select(t => t.Title).ToArray());

        ctx.Tabs = []; // and of the EST server
        Assert.True(vm.IsCertificatesTab);
        Assert.Equal(["Certificates", "Policies"], vm.Tabs.Select(t => t.Title).ToArray());
    }

    [Theory]
    [InlineData(1700)]
    [InlineData(1000)]
    public async Task The_certificates_tab_reaches_every_ca_action_from_the_toolbar_and_the_rows(int width)
    {
        var outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        await using var pki = await PkiHarness.StartAsync();
        await pki.InvokeAsync<PkiReply>(PkiMethods.Generate, new GenerateRequest("OADM Root CA SERVER01", null, 10, true)); // a previous CA
        IDeviceCertificateAuthority authority = new Ca.PkiCertificateAuthority(pki.Service);
        for (var i = 0; i < 4; i++)
        {
            var serial = "ACCC8E00020" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var device = pki.Manage(Guid.NewGuid(), serial); // the list shows the cameras OADM manages
            await RevocationTests.SignAsync(authority, device.Id, serial);
        }

        var ctx = new PluginPageContext(pki.Plugin, pki.Hub);
        var checks = await HeadlessEntry.Session.Dispatch(async () =>
        {
            // The server tabs the host hands over (stand-ins here; their own tests render them on the tab).
            ctx.Tabs = [new CorePluginTab("oadm.radius", "802.1X server", new TextBlock()), new CorePluginTab("oadm.est", "EST server", new TextBlock())];
            var view = new PkiView();
            using var vm = new PkiViewModel(ctx, new FakeClientTrust());
            var window = HeadlessPageTests.Host(view, width, 1500);
            window.Show();
            view.DataContext = vm;
            await vm.LoadAsync();
            await vm.Policies.LoadAsync(CancellationToken.None);
            await vm.Authorities.LoadAsync();
            await vm.Certificates.LoadAsync();
            await vm.InstallTrustCommand.ExecuteAsync(null); // the OADM CA's row says where it is trusted
            HeadlessPageTests.Pump();
            HeadlessPageTests.Capture(window, outDir, $"pki-tab-certificates-{width}.png");
            var texts = window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();

            var tabs = window.GetVisualDescendants().OfType<PageTab>().Where(t => t.IsEffectivelyVisible).Select(t => (t.Title, t.IsActive)).ToList();
            var authoritiesList = view.FindControl<IssuedCertificateList>("AuthoritiesList")!;
            var certificatesList = view.FindControl<IssuedCertificateList>("CertificatesList")!;
            var splitter = view.FindControl<GridSplitter>("CertificatesSplitter")!.IsEffectivelyVisible;
            static ToolbarButton Labeled(Control list, string text) => list.GetVisualDescendants().OfType<ToolbarButton>()
                .Single(b => b.IsEffectivelyVisible && b.Text == text);

            // The CA toolbar: New OADM CA and Import with its menu (the menus get their commands once they open, as for the user).
            var newCa = Labeled(authoritiesList, "New OADM CA");
            var importCaButton = Labeled(authoritiesList, "Import");
            var importFlyout = (MenuFlyout)importCaButton.Flyout!;
            importFlyout.ShowAt(importCaButton);
            HeadlessPageTests.Pump();
            var importCa = importFlyout.Items.OfType<MenuItem>().Select(m => ((string?)m.Header, m.Command)).ToList();
            importFlyout.Hide();

            // The certificates' toolbar: Create (the tab's accent button), Sign request and Import, each without a menu.
            var create = Labeled(certificatesList, "Create");
            var sign = Labeled(certificatesList, "Sign request");
            var import = Labeled(certificatesList, "Import");
            var toolbar = (
                NewCa: newCa.Command, ImportCa: importCa, NewCaPrimary: newCa.IsPrimary,
                Create: create.Command, CreatePrimary: create.IsPrimary, Sign: sign.Command, Import: import.Command,
                NoMenus: create.Flyout is null && sign.Flyout is null && import.Flyout is null);

            // One ⋯ button per row (no link row); its menu is the row's menu, the same as its right-click menu.
            var rows = window.GetVisualDescendants().OfType<DataGridRow>().Where(r => r.IsEffectivelyVisible && r.DataContext is IssuedCertificateRow).ToList();
            var oneButton = rows.Count > 0 && rows.All(r => r.GetVisualDescendants().OfType<Button>().Count(b => b.IsEffectivelyVisible) == 1
                && r.GetVisualDescendants().OfType<Button>().Single(b => b.IsEffectivelyVisible).Classes.Contains("iconOnly"));
            static string Text(IssuedCertificateAction a) => a.IsDanger ? a.Text + " (red)" : a.Text;
            List<string> CaMenuOf(Func<IssuedCertificateRow, bool> which) => [.. vm.Authorities.MenuOf(vm.Authorities.Rows.First(which)).Select(Text)];
            var links = (
                Oadm: CaMenuOf(r => r.IsOadmCa),
                Previous: CaMenuOf(r => r.AuthorityKind == CertificateAuthorityKind.PreviousOadm),
                Axis: CaMenuOf(r => r.AuthorityKind == CertificateAuthorityKind.AxisDeviceId),
                Device: vm.Certificates.MenuOf(vm.Certificates.Rows.First(r => r.Source.Kind == ListedCertificateKind.Device)).Select(Text).ToList());
            var export = vm.Authorities.MenuOf(vm.Authorities.Rows.First(r => r.IsOadmCa))[1].Items!.Select(a => a.Text).ToList();
            var items = IssuedCertificateList.MenuItems(vm.Authorities.MenuOf(vm.Authorities.Rows.First(r => r.AuthorityKind == CertificateAuthorityKind.PreviousOadm))).ToList();
            var red = items.OfType<MenuItem>().Where(m => (string?)m.Header == "Remove").All(m => m.Classes.Contains("danger")) && items.OfType<Separator>().Count() == 1;
            var enabled = vm.Authorities.MenuOf(vm.Authorities.Rows.First(r => r.IsOadmCa)).Where(a => a.Command is not null).All(a => a.Command!.CanExecute(a.Parameter));
            var lists = (CaRowsOnly: vm.Authorities.Rows.All(r => r.Source.Kind == ListedCertificateKind.Authority),
                NoCaRows: vm.Certificates.Rows.All(r => r.Source.Kind != ListedCertificateKind.Authority));
            window.Close();
            return (tabs, toolbar, links, export, oneButton, enabled, red, texts, splitter, lists,
                Generate: vm.GenerateCommand, ImportOwn: vm.ImportCommand, ImportAuthority: vm.ImportAuthorityCommand, Create: vm.Certificates.CreateCommand,
                Sign: vm.Certificates.SignRequestCommand, ImportCertificate: vm.Certificates.ImportCommand);
        }, CancellationToken.None);

        Assert.Equal([("Certificates", true), ("Policies", false), ("802.1X server", false), ("EST server", false)], checks.tabs);
        Assert.Contains("Certificate authorities", checks.texts);
        Assert.Contains("Certificates", checks.texts);
        Assert.True(checks.splitter);
        Assert.True(checks.lists.CaRowsOnly);
        Assert.True(checks.lists.NoCaRows);
        Assert.Same(checks.Generate, checks.toolbar.NewCa);
        Assert.False(checks.toolbar.NewCaPrimary);
        Assert.Equal([("CA certificate", (System.Windows.Input.ICommand)checks.ImportAuthority), ("CA with its key", checks.ImportOwn)], checks.toolbar.ImportCa);
        Assert.Same(checks.Create, checks.toolbar.Create);
        Assert.False(checks.toolbar.CreatePrimary); // every button of the card is flat (decision 2026-10-25)
        Assert.Same(checks.Sign, checks.toolbar.Sign);
        Assert.Same(checks.ImportCertificate, checks.toolbar.Import);
        Assert.True(checks.toolbar.NoMenus);
        Assert.True(checks.oneButton);
        Assert.Equal(["View", "Export", "Install in trusted root store", "Back up"], checks.links.Oadm);
        Assert.Equal(["PEM (.crt)", "DER (.cer)"], checks.export);
        Assert.Equal(["View", "Export", "-", "Remove (red)"], checks.links.Previous);
        Assert.Equal(["View", "Export"], checks.links.Axis);
        Assert.Equal(["View", "Export", "-", "Revoke (red)", "Revoke all of this device (red)"], checks.links.Device);
        Assert.True(checks.enabled);
        Assert.True(checks.red);
        Assert.Contains("OS root store: client", checks.texts); // the OADM CA's trust as a badge next to its type
    }
}
