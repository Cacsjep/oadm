using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

using Oadm.Plugins.Pki.Client;
using Oadm.Plugins.Pki.Policies;
using Oadm.Sdk.Certificates;

namespace Oadm.Plugins.Pki.Tests;

/// <summary>
/// The Policies tab rendered offscreen at 1,700 and 1,000 px (docs/spec/pki.md "Policies", decision 2026-10-25): the cards
/// by area, the system policies in place, the rule lists, a checked device; nothing clipped, nothing scrolls sideways
/// (OADM_SCREENSHOT_DIR writes pki-policies-{width}.png and pki-policies-{width}-end.png).
/// </summary>
[Collection(HeadlessSessions.Name)]
public sealed class PolicyTabHeadlessTests
{
    private static readonly string[] Cards = ["CheckCard", "CameraCertificatesCard", "NetworkAccessCard", "EstCard"];

    [Theory]
    [InlineData(1700)]
    [InlineData(1000)]
    public async Task The_policies_tab_shows_its_cards_by_area_without_clipping_or_sideways_scroll(int width)
    {
        var outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        await using var pki = await EstPolicyPageTests.StartAsync();
        await pki.InvokeAsync<PolicySaveReply>(PolicyMethods.SavePolicy,
            new PkiPolicy { Name = "HTTPS 90 days", Kind = PolicyKinds.HttpsCertificate, CaId = CertificateAuthorityInfo.OadmId, ValidDays = 90, Key = PolicyKeys.Ecc });
        var ctx = new PluginPageContext(pki.Plugin, pki.Hub) { DeviceList = [PolicyPageTests.Camera, PolicyPageTests.Other] };

        var checks = await HeadlessEntry.Session.Dispatch(async () =>
        {
            var view = new PkiView();
            using var vm = new PkiViewModel(ctx, new FakeClientTrust());
            var window = HeadlessPageTests.Host(view, width, 1500);
            window.Show();
            view.DataContext = vm;
            await vm.LoadAsync();
            await vm.Policies.LoadAsync(CancellationToken.None);
            vm.ShowTab(PkiViewModel.PoliciesTabId);
            vm.Policies.CheckedDevice = PolicyPageTests.Camera;
            await vm.Policies.CheckAsync(CancellationToken.None);
            vm.Policies.Expiry!.Days = "0"; // the error below its field
            HeadlessPageTests.Pump();
            HeadlessPageTests.Capture(window, outDir, $"pki-policies-{width}.png");

            var scroll = view.FindControl<ScrollViewer>("PoliciesScroll")!;
            var sideways = scroll.Extent.Width - scroll.Viewport.Width;

            // Every input, button and table lies whole inside its card.
            var clipped = new List<string>();
            foreach (var name in Cards)
            {
                var card = view.FindControl<Border>(name)!;
                foreach (var control in card.GetVisualDescendants().OfType<Control>().Where(c => c.IsEffectivelyVisible && c is TextBox or ComboBox or Button or DataGrid))
                {
                    var right = control.TranslatePoint(new Point(control.Bounds.Width, 0), card)!.Value.X;
                    if (right > card.Bounds.Width + 0.5)
                    {
                        clipped.Add($"{name}: {control.GetType().Name} {control.Name} ends at {right:0} of {card.Bounds.Width:0}");
                    }
                }
            }

            var texts = window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
            var primaries = window.GetVisualDescendants().OfType<Button>().Count(b => b.IsEffectivelyVisible && b.Classes.Contains("primary"));
            var columns = view.GetVisualDescendants().OfType<PolicyRulesView>()
                .Select(r => (((PolicySectionViewModel)r.DataContext!).Title, r.FindControl<DataGrid>("RulesGrid")!.Columns[1].IsVisible)).ToList();

            scroll.Offset = new Vector(0, scroll.Extent.Height);
            HeadlessPageTests.Pump();
            HeadlessPageTests.Capture(window, outDir, $"pki-policies-{width}-end.png");
            window.Close();
            return (sideways, clipped, texts, primaries, columns);
        }, CancellationToken.None);

        Assert.True(checks.sideways <= 0.5, $"{checks.sideways:0} px sideways");
        Assert.Empty(checks.clipped);
        foreach (var text in new[]
                 {
                     "Check a device", "Certificates for cameras", "802.1X network access", "EST", // the cards in their order
                     "HTTPS", "IEEE 802.1X", "Expiry warning", "802.1X server", "EST server", // the system policies in place
                     "Other issuing", "Expiry warnings for some devices", "Trusted CAs", "VLANs", "Blocked", "Enrollment", "Sign-in CAs", // the rules
                     "HTTPS 90 days", "Building B", "Storage room", "System", "Days before the end", "Minimum TLS version", "Issued by",
                     PolicyTexts.DaysRange, "It gets VLAN 30.", "By Building B", "10.0.0.48 · P3265-V",
                 })
        {
            Assert.Contains(text, checks.texts);
        }

        Assert.Equal(1, checks.primaries); // the device of Check a device; the Saves and Adds are secondary
        Assert.DoesNotContain("Add policy", checks.texts);
        Assert.Equal(
            [("Other issuing", false), ("Expiry warnings for some devices", true), ("Trusted CAs", false), ("VLANs", true), ("Blocked", true), ("Enrollment", true), ("Sign-in CAs", false)],
            checks.columns);
        Assert.All(checks.texts, t => Assert.False(t?.EndsWith("...", StringComparison.Ordinal) == true || t?.EndsWith('…') == true, t));
    }
}
