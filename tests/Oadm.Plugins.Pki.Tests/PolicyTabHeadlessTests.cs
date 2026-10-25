using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

using Oadm.Plugins.Pki.Client;
using Oadm.Plugins.Pki.Policies;
using Oadm.Sdk.Certificates;

namespace Oadm.Plugins.Pki.Tests;

/// <summary>
/// The Policies tab rendered offscreen at 1,700 and 1,000 px (docs/spec/pki.md "Policies", decision 2026-10-25): the grouped
/// table, the side panel with Create's types, a VLAN policy with an error below its field, the 802.1X server policy and a
/// Block that also revokes; nothing in the panel clipped (OADM_SCREENSHOT_DIR writes pki-policies-{width}-*.png).
/// </summary>
[Collection(HeadlessSessions.Name)]
public sealed class PolicyTabHeadlessTests
{
    [Theory]
    [InlineData(1700)]
    [InlineData(1000)]
    public async Task The_policies_tab_shows_the_grouped_table_and_the_side_panel_without_clipping(int width)
    {
        var outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        await using var pki = await EstPolicyPageTests.StartAsync();
        await pki.Service.EnsureServerCertificatesAsync(null, CancellationToken.None);
        await pki.InvokeAsync<PolicySaveReply>(PolicyMethods.SavePolicy,
            new PkiPolicy { Name = "HTTPS 90 days", Kind = PolicyKinds.HttpsCertificate, CaId = CertificateAuthorityInfo.OadmId, ValidDays = 90, Key = PolicyKeys.Ecc });
        var ctx = new PluginPageContext(pki.Plugin, pki.Hub) { DeviceList = [PolicyPageTests.Camera, PolicyPageTests.Other] };

        var checks = await HeadlessEntry.Session.Dispatch(async () =>
        {
            var view = new PkiView();
            using var vm = new PkiViewModel(ctx, new FakeClientTrust());
            var window = HeadlessPageTests.Host(view, width, 1100);
            window.Show();
            view.DataContext = vm;
            await vm.LoadAsync();
            await vm.Policies.LoadAsync(CancellationToken.None);
            vm.ShowTab(PkiViewModel.PoliciesTabId);
            HeadlessPageTests.Pump();
            HeadlessPageTests.Capture(window, outDir, $"pki-policies-{width}.png");
            var table = Texts(window);
            var panelClosed = view.FindControl<Border>("PolicyPanel")!.Bounds.Width;

            // Create: the types with what each does.
            await vm.Policies.CreateCommand.ExecuteAsync(null);
            Settle();
            HeadlessPageTests.Capture(window, outDir, $"pki-policies-{width}-create.png");
            var create = PanelTexts(view);

            // A VLAN policy with an error below its field.
            vm.Policies.SelectedRow = vm.Policies.Rows.Single(r => r.Name == "Building B");
            vm.Policies.Editor!.Vlan = "0";
            Settle();
            HeadlessPageTests.Capture(window, outDir, $"pki-policies-{width}-vlan.png");
            var vlan = PanelTexts(view);
            var clipped = Clipped(view);
            vm.Policies.Editor.Vlan = "30";

            // The 802.1X server policy: its certificate, no cipher suites.
            vm.Policies.SelectedRow = vm.Policies.Rows.Single(r => r.Name == "802.1X server");
            vm.Policies.Editor!.MinimumTls = PolicyEditorViewModel.TlsVersions[1];
            Settle();
            HeadlessPageTests.Capture(window, outDir, $"pki-policies-{width}-server.png");
            var server = PanelTexts(view);
            clipped.AddRange(Clipped(view));
            vm.Policies.Editor.MinimumTls = PolicyEditorViewModel.TlsVersions[2];

            // Block with Also revoke: devices and sites only.
            vm.Policies.SelectedRow = vm.Policies.Rows.Single(r => r.Name == "Storage room");
            vm.Policies.Editor!.AlsoRevoke = true;
            Settle();
            HeadlessPageTests.Capture(window, outDir, $"pki-policies-{width}-block.png");
            var block = PanelTexts(view);
            var primaries = window.GetVisualDescendants().OfType<Button>().Count(b => b.IsEffectivelyVisible && b.Classes.Contains("primary"));
            var panelOpen = view.FindControl<Border>("PolicyPanel")!.Bounds.Width;
            window.Close();
            return (table, create, vlan, server, block, clipped, primaries, panelClosed, panelOpen);

            void Settle()
            {
                // The panel slides in (0.2 s): pump until its width stops changing.
                for (var i = 0; i < 40; i++)
                {
                    HeadlessPageTests.Pump();
                    Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                }
            }
        }, CancellationToken.None);

        foreach (var text in new[] { "Issuing policies", "Server policies", "802.1X access policies", "EST enrollment policies", "Expiry warnings", "Built-in", "HTTPS 90 days", "Building B", "Storage room", "Create", "Search policies" })
        {
            Assert.Contains(text, checks.table);
        }

        Assert.True(checks.panelClosed < 1, $"closed panel {checks.panelClosed:0} px wide");
        Assert.True(checks.panelOpen > 500, $"open panel {checks.panelOpen:0} px wide");
        Assert.Contains(PoliciesViewModel.CreateTitle, checks.create);
        Assert.Contains("VLAN", checks.create);
        Assert.Contains(PolicyKinds.Description(PolicyKinds.Vlan), checks.create);
        Assert.Contains("Next", checks.create);
        Assert.Contains(PolicyTexts.VlanRange, checks.vlan); // below the VLAN ID
        Assert.Contains("Choose devices", checks.vlan);
        Assert.Contains("Delete", checks.vlan);
        Assert.Contains("Certificate", checks.server);
        Assert.Contains(PolicyEditorViewModel.OldTlsHint, checks.server);
        Assert.DoesNotContain("Cipher suites", checks.server);
        Assert.DoesNotContain("Reset to default", checks.server);
        Assert.Contains(PolicyTexts.AlsoRevoke, checks.block);
        Assert.DoesNotContain(PolicyTexts.AllDevices, checks.block); // also revoking: devices and sites only
        Assert.Equal(1, checks.primaries); // Save of the panel
        Assert.Empty(checks.clipped);
        Assert.All(checks.table.Concat(checks.create), t => Assert.False(t?.EndsWith("...", StringComparison.Ordinal) == true || t?.EndsWith('…') == true, t));
    }

    /// <summary>Inputs and buttons of the panel that end outside it.</summary>
    private static List<string> Clipped(PkiView view)
    {
        var panel = view.FindControl<Border>("PolicyPanel")!;
        var clipped = new List<string>();
        foreach (var control in panel.GetVisualDescendants().OfType<Control>().Where(c => c.IsEffectivelyVisible && c is TextBox or ComboBox or Button))
        {
            var right = control.TranslatePoint(new Point(control.Bounds.Width, 0), panel)!.Value.X;
            if (right > panel.Bounds.Width + 0.5)
            {
                clipped.Add($"{control.GetType().Name} {control.Name} ends at {right:0} of {panel.Bounds.Width:0}");
            }
        }

        return clipped;
    }

    private static List<string?> PanelTexts(PkiView view)
    {
        var panel = view.FindControl<Border>("PolicyPanel")!;
        return
        [
            .. panel.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text),
            .. panel.GetVisualDescendants().OfType<CheckBox>().Where(c => c.IsEffectivelyVisible).Select(c => c.Content as string),
        ];
    }

    private static List<string?> Texts(Window window) =>
    [
        .. window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text),
        .. window.GetVisualDescendants().OfType<CheckBox>().Where(c => c.IsEffectivelyVisible).Select(c => c.Content as string),
    ];
}
