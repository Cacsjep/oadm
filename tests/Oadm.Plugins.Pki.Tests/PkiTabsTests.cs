using Avalonia.Controls;

using Oadm.Plugins.Pki.Client;
using Oadm.Sdk.Client;

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
}
