using Avalonia.Controls;
using Avalonia.VisualTree;

using Oadm.Plugins.Pki.Client;
using Oadm.Sdk.Certificates;
using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Controls;

namespace Oadm.Plugins.Pki.Tests;

/// <summary>The tabs of the PKI page (docs/spec/pki.md "The PKI page"): Certificates, then the server pages the host hands over.</summary>
[Collection(HeadlessSessions.Name)]
public sealed class PkiTabsTests
{
    private static Task<T> OnUi<T>(Func<T> work) => HeadlessEntry.Session.Dispatch(() => Task.FromResult(work()), CancellationToken.None);

    [Fact]
    public async Task Without_server_tabs_there_is_no_tab_row_and_the_certificates_show()
    {
        await using var pki = await PkiHarness.StartAsync();
        var ctx = new PluginPageContext(pki.Plugin, pki.Hub);
        using var vm = new PkiViewModel(ctx, new FakeClientTrust());

        Assert.False(vm.HasTabs);
        Assert.Equal(["Certificates"], vm.Tabs.Select(t => t.Title).ToArray());
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
        Assert.Equal(["Certificates", "802.1X server", "EST server"], vm.Tabs.Select(t => t.Title).ToArray());
        Assert.True(vm.Tabs[0].IsActive);

        ctx.RequestTab("oadm.est"); // OpenAsync("plugin:oadm.est")
        Assert.False(vm.IsCertificatesTab);
        Assert.Same(est, vm.ActiveTabView);
        Assert.Equal([false, false, true], vm.Tabs.Select(t => t.IsActive).ToArray());

        vm.Tabs[1].ActivateCommand.Execute(null); // a click on the tab
        Assert.Same(radius, vm.ActiveTabView);

        ctx.RequestTab("made.up"); // unknown: nothing changes
        Assert.Same(radius, vm.ActiveTabView);

        ctx.RequestTab(PkiPluginInfo.PluginId); // Edit in PKI: Certificates
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
        Assert.Equal(["Certificates", "EST server"], vm.Tabs.Select(t => t.Title).ToArray());

        ctx.Tabs = []; // and of the EST server
        Assert.True(vm.IsCertificatesTab);
        Assert.False(vm.HasTabs);
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
            await RevocationTests.SignAsync(authority, Guid.NewGuid(), "ACCC8E00020" + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
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
            await vm.Certificates.LoadAsync();
            HeadlessPageTests.Pump();
            HeadlessPageTests.Capture(window, outDir, $"pki-tab-certificates-{width}.png");

            var tabs = window.GetVisualDescendants().OfType<PageTab>().Where(t => t.IsEffectivelyVisible).Select(t => (t.Title, t.IsActive)).ToList();
            Button Labeled(string text) => window.GetVisualDescendants().OfType<Button>()
                .Single(b => b.IsEffectivelyVisible && b.GetVisualDescendants().OfType<IconLabel>().Any(l => l.Text == text));
            // The menus get their commands once they open, as for the user.
            var createButton = Labeled("Create");
            var createFlyout = (MenuFlyout)createButton.Flyout!;
            createFlyout.ShowAt(createButton);
            HeadlessPageTests.Pump();
            var create = createFlyout.Items.OfType<MenuItem>().ToList();
            var createCommands = create.Select(m => ((string?)m.Header, m.Command)).ToList();
            createFlyout.Hide();
            var importButton = Labeled("Import");
            var importFlyout = (MenuFlyout)importButton.Flyout!;
            var import = importFlyout.Items.OfType<MenuItem>().ToList();
            importFlyout.ShowAt(importButton);
            HeadlessPageTests.Pump();
            var caItem = import.Single(m => (string?)m.Header == "CA");
            caItem.Open();
            HeadlessPageTests.Pump();
            var importCa = caItem.Items.OfType<MenuItem>().ToList();
            var menus = (
                Create: createCommands,
                Import: import.Select(m => (string?)m.Header).ToList(),
                ImportCa: importCa.Select(m => ((string?)m.Header, m.Command)).ToList());
            importFlyout.Hide();

            // The row buttons of each kind of row (the grid shows the CAs first).
            var rows = window.GetVisualDescendants().OfType<DataGridRow>().Where(r => r.IsEffectivelyVisible).ToList();
            List<string> LinksOf(Func<IssuedCertificateRow, bool> which) => [.. rows.Where(r => r.DataContext is IssuedCertificateRow row && which(row))
                .SelectMany(r => r.GetVisualDescendants().OfType<Button>()).Where(b => b.IsEffectivelyVisible && b.Classes.Contains("link")).Select(b => b.Content as string ?? string.Empty)];
            var links = (
                Oadm: LinksOf(r => r.IsOadmCa),
                Previous: LinksOf(r => r.AuthorityKind == CertificateAuthorityKind.PreviousOadm),
                Axis: LinksOf(r => r.AuthorityKind == CertificateAuthorityKind.AxisDeviceId).Distinct().ToList());
            var enabled = rows.Where(r => r.DataContext is IssuedCertificateRow { IsOadmCa: true }).SelectMany(r => r.GetVisualDescendants().OfType<Button>())
                .Where(b => b.IsEffectivelyVisible && b.Classes.Contains("link")).All(b => b.IsEffectivelyEnabled);
            var red = rows.SelectMany(r => r.GetVisualDescendants().OfType<Button>()).Where(b => b.IsEffectivelyVisible && b.Content as string is "Remove" or "Revoke")
                .All(b => b.Classes.Contains("danger"));
            window.Close();
            return (tabs, menus, links, enabled, red, vm.GenerateCommand, vm.ImportCommand, vm.ImportAuthorityCommand, vm.Certificates.CreateCommand);
        }, CancellationToken.None);

        Assert.Equal([("Certificates", true), ("802.1X server", false), ("EST server", false)], checks.tabs);
        Assert.Equal(["Certificate", "New OADM CA"], checks.menus.Create.Select(m => m.Item1));
        Assert.Same(checks.CreateCommand, checks.menus.Create[0].Command);
        Assert.Same(checks.GenerateCommand, checks.menus.Create[1].Command);
        Assert.Equal(["Certificate", "CA"], checks.menus.Import);
        Assert.Equal([("Own CA with its key", (System.Windows.Input.ICommand)checks.ImportCommand), ("CA certificate only", checks.ImportAuthorityCommand)], checks.menus.ImportCa);
        Assert.Equal(["View", "Export", "Install", "Back up"], checks.links.Oadm);
        Assert.Equal(["View", "Export", "Remove"], checks.links.Previous);
        Assert.Equal(["View", "Export"], checks.links.Axis);
        Assert.True(checks.enabled);
        Assert.True(checks.red);
    }
}
