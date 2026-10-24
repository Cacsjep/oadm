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

        ctx.RequestTab(PkiPluginInfo.PluginId); // Edit policies: Certificates
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
            await vm.InstallTrustCommand.ExecuteAsync(null); // the OADM CA's row says where it is trusted
            HeadlessPageTests.Pump();
            HeadlessPageTests.Capture(window, outDir, $"pki-tab-certificates-{width}.png");
            var installed = window.GetVisualDescendants().OfType<StatusChip>().Where(c => c.IsEffectivelyVisible).Select(c => c.Text).ToList();

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

            // One ⋯ button per row (no link row); its menu is the row's menu, the same as its right-click menu.
            var rows = window.GetVisualDescendants().OfType<DataGridRow>().Where(r => r.IsEffectivelyVisible && r.DataContext is IssuedCertificateRow).ToList();
            var oneButton = rows.All(r => r.GetVisualDescendants().OfType<Button>().Count(b => b.IsEffectivelyVisible) == 1
                && r.GetVisualDescendants().OfType<Button>().Single(b => b.IsEffectivelyVisible).Classes.Contains("iconOnly"));
            static string Text(IssuedCertificateAction a) => a.IsDanger ? a.Text + " (red)" : a.Text;
            List<string> MenuOf(Func<IssuedCertificateRow, bool> which) => [.. vm.Certificates.MenuOf(vm.Certificates.Rows.First(which)).Select(Text)];
            var links = (
                Oadm: MenuOf(r => r.IsOadmCa),
                Previous: MenuOf(r => r.AuthorityKind == CertificateAuthorityKind.PreviousOadm),
                Axis: MenuOf(r => r.AuthorityKind == CertificateAuthorityKind.AxisDeviceId),
                Device: MenuOf(r => r.Source.Kind == ListedCertificateKind.Device));
            var export = vm.Certificates.MenuOf(vm.Certificates.Rows.First(r => r.IsOadmCa))[1].Items!.Select(a => a.Text).ToList();
            var items = IssuedCertificateList.MenuItems(vm.Certificates.MenuOf(vm.Certificates.Rows.First(r => r.AuthorityKind == CertificateAuthorityKind.PreviousOadm))).ToList();
            var red = items.OfType<MenuItem>().Where(m => (string?)m.Header == "Remove").All(m => m.Classes.Contains("danger")) && items.OfType<Separator>().Count() == 1;
            var enabled = vm.Certificates.MenuOf(vm.Certificates.Rows.First(r => r.IsOadmCa)).Where(a => a.Command is not null).All(a => a.Command!.CanExecute(a.Parameter));
            window.Close();
            return (tabs, menus, links, export, oneButton, enabled, red, installed, vm.GenerateCommand, vm.ImportCommand, vm.ImportAuthorityCommand, vm.Certificates.CreateCommand);
        }, CancellationToken.None);

        Assert.Equal([("Certificates", true), ("802.1X server", false), ("EST server", false)], checks.tabs);
        Assert.Equal(["Certificate", "New OADM CA"], checks.menus.Create.Select(m => m.Item1));
        Assert.Same(checks.CreateCommand, checks.menus.Create[0].Command);
        Assert.Same(checks.GenerateCommand, checks.menus.Create[1].Command);
        Assert.Equal(["Certificate", "CA"], checks.menus.Import);
        Assert.Equal([("Own CA with its key", (System.Windows.Input.ICommand)checks.ImportCommand), ("CA certificate only", checks.ImportAuthorityCommand)], checks.menus.ImportCa);
        Assert.True(checks.oneButton);
        Assert.Equal(["View", "Export", "Install in trusted root store", "Back up"], checks.links.Oadm);
        Assert.Equal(["PEM (.crt)", "DER (.cer)"], checks.export);
        Assert.Equal(["View", "Export", "-", "Remove (red)"], checks.links.Previous);
        Assert.Equal(["View", "Export"], checks.links.Axis);
        Assert.Equal(["View", "Export", "-", "Revoke (red)", "Revoke all of this device (red)"], checks.links.Device);
        Assert.True(checks.enabled);
        Assert.True(checks.red);
        Assert.Contains("Installed on this computer", checks.installed);
    }
}
