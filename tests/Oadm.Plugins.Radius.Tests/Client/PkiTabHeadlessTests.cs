using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

using Oadm.Plugins.Pki;
using Oadm.Plugins.Pki.Client;
using Oadm.Plugins.Radius.Client;
using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Controls;

namespace Oadm.Plugins.Radius.Tests.Client;

/// <summary>
/// The 802.1X server as the tab of the PKI page (docs/spec/pki.md "The PKI page"), as the user sees it: the PKI page with its
/// tabs and the 802.1X server's own view on its tab (OADM_SCREENSHOT_DIR writes pki-tab-802-1x-*.png).
/// </summary>
[Collection(HeadlessSessions.Name)]
public sealed class PkiTabHeadlessTests
{
    /// <summary>The PKI page's context: it hands over the server tabs; PKI's own calls answer nothing (the tab is not Certificates).</summary>
    private sealed class PkiContext(IReadOnlyList<CorePluginTab> tabs) : ICorePluginClientContext
    {
        public IReadOnlyList<CorePluginTab> Tabs => tabs;

        public event EventHandler<string>? OpenTabRequested;

        public void RequestTab(string id) => OpenTabRequested?.Invoke(this, id);

        public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) => Task.FromResult<string?>(null);
    }

    [Theory]
    [InlineData(1700)]
    [InlineData(1000)]
    public async Task The_802_1x_server_is_a_tab_of_the_pki_page(int width)
    {
        var outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        var server = new FakeRadiusBackend();
        var ctx = new FakePageContext(server);
        var checks = await HeadlessEntry.Session.Dispatch(async () =>
        {
            var radius = new RadiusView();
            using var radiusVm = new RadiusViewModel(ctx);
            var est = new TextBlock { Text = "EST server" };
            var pkiCtx = new PkiContext([new CorePluginTab(RadiusPluginInfo.PluginId, RadiusPluginInfo.DisplayName, radius), new CorePluginTab("oadm.est", "EST server", est)]);
            var page = new PkiView();
            using var pkiVm = new PkiViewModel(pkiCtx, new NoTrust());
            pkiCtx.RequestTab(RadiusPluginInfo.PluginId); // OpenAsync("plugin:oadm.radius")
            var window = PkiHost(page, width, 1300);
            window.Show();
            page.DataContext = pkiVm;
            HeadlessPageTests.Pump();
            radius.DataContext = radiusVm; // after attaching: no live watch, deterministic content
            await radiusVm.LoadAllAsync(CancellationToken.None);
            HeadlessPageTests.Pump();
            HeadlessPageTests.Capture(window, $"pki-tab-802-1x-{width}.png");

            var tabs = window.GetVisualDescendants().OfType<PageTab>().Where(t => t.IsEffectivelyVisible).Select(t => (t.Title, t.IsActive)).ToList();
            var texts = window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();

            // PKI's own page without a request: the Certificates tab.
            pkiCtx.RequestTab(PkiPluginInfo.PluginId);
            HeadlessPageTests.Pump();
            var radiusShown = radius.IsEffectivelyVisible && TopLevel.GetTopLevel(radius) is not null;
            window.Close();
            return (tabs, texts, radiusShown, pkiVm.IsCertificatesTab);
        }, CancellationToken.None);

        Assert.Equal([("Certificates", false), ("Policies", false), ("802.1X server", true), ("EST server", false)], checks.tabs);
        foreach (var title in new[] { "PKI", "Server", "Switches", "Sessions" })
        {
            Assert.Contains(title, checks.texts);
        }

        Assert.True(checks.IsCertificatesTab);
        Assert.False(checks.radiusShown); // only the shown tab's view is in the window
    }

    /// <summary>The real host page of PKI (title, subtitle, own cards).</summary>
    private static Window PkiHost(Control view, int width, int height)
    {
        var page = new Oadm.Client.Shell.CorePluginPageView
        {
            DataContext = new Oadm.Client.Shell.CorePluginPageViewModel(PkiPluginInfo.PluginId, PkiPluginInfo.DisplayName, view, hasOwnCards: true),
        };
        var window = new Window { Width = width, Height = height, Content = new Border { Padding = new Thickness(16), Child = page } };
        Oadm.Client.App.ApplyCrispText(window);
        return window;
    }

    private sealed class NoTrust : Oadm.Plugins.Pki.TrustStore.ITrustStoreInstaller
    {
        public Task<bool> IsInstalledAsync(System.Security.Cryptography.X509Certificates.X509Certificate2 ca, CancellationToken ct) => Task.FromResult(false);

        public Task<Oadm.Plugins.Pki.TrustStore.TrustStoreResult> InstallAsync(System.Security.Cryptography.X509Certificates.X509Certificate2 ca, CancellationToken ct) =>
            Task.FromResult(new Oadm.Plugins.Pki.TrustStore.TrustStoreResult(false));

        public Task<Oadm.Plugins.Pki.TrustStore.TrustStoreResult> RemoveAsync(System.Security.Cryptography.X509Certificates.X509Certificate2 ca, CancellationToken ct) =>
            Task.FromResult(new Oadm.Plugins.Pki.TrustStore.TrustStoreResult(false));
    }
}
