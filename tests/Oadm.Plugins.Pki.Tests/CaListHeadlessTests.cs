using Avalonia.Controls;
using Avalonia.VisualTree;

using Oadm.Plugins.Pki.Ca;
using Oadm.Plugins.Pki.Client;
using Oadm.Sdk.Certificates;
using Oadm.Sdk.Client.Controls;

namespace Oadm.Plugins.Pki.Tests;

/// <summary>
/// The Certificates tab after the CA rework of 2026-10-26 offscreen (OADM_SCREENSHOT_DIR writes PNGs): the CA list with the
/// first-start root CA, a created intermediate and a CA certificate without key, Create CA (root and intermediate), the
/// delete confirmation of a CA, and the certificate list.
/// </summary>
[Collection(HeadlessSessions.Name)]
public sealed class CaListHeadlessTests
{
    [Fact]
    public async Task The_ca_list_shows_only_root_or_intermediate_with_delete_and_create_ca_renders()
    {
        var outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        var devices = Enumerable.Range(0, 4).Select(i => new PkiFakeDevice(Guid.NewGuid(), "10.0.0." + (40 + i).ToString(System.Globalization.CultureInfo.InvariantCulture),
            "ACCC8E" + (0x123400 + i).ToString("X6", System.Globalization.CultureInfo.InvariantCulture))).ToList();
        await using var pki = await PkiHarness.StartAsync(devices: devices);
        var created = await pki.InvokeAsync<PkiReply>(PkiMethods.CreateAuthority,
            new CreateCaRequest("OADM Issuing CA", Intermediate: true, CertificateAuthorityInfo.OadmId, CaKeys.Ecc, 5));
        Assert.True(created.Ok, created.Error ?? string.Join(", ", created.Errors?.Values ?? []));
        using (var contoso = TestCa.Root("Contoso Root"))
        {
            var imported = await pki.InvokeAsync<PkiReply>(PkiMethods.ImportAuthority, new ImportAuthorityRequest(Convert.ToBase64String(contoso.RawData), "contoso.cer"));
            Assert.True(imported.Ok, imported.Error ?? string.Join(", ", imported.Errors?.Values ?? []));
        }

        var authority = new PkiCertificateAuthority(pki.Service);
        foreach (var device in devices)
        {
            await RevocationTests.SignAsync(authority, device.Id, device.Serial);
        }

        var ctx = new PluginPageContext(pki.Plugin, pki.Hub);
        var session = HeadlessEntry.Session;
        var checks = await session.Dispatch(async () =>
        {
            var view = new PkiView();
            using var vm = new PkiViewModel(ctx, new FakeClientTrust());
            var window = HeadlessPageTests.Host(view, 1700, 1300);
            window.Show();
            view.DataContext = vm;
            await vm.LoadAsync();
            await vm.Policies.LoadAsync(CancellationToken.None);
            await vm.Authorities.LoadAsync();
            await vm.Certificates.LoadAsync();
            var root = vm.Authorities.Rows.First(r => r.Source.AuthorityId == CertificateAuthorityInfo.OadmId);
            await vm.InstallTrustCommand.ExecuteAsync(root); // the root CA's row says where it is trusted
            HeadlessPageTests.Pump();
            HeadlessPageTests.Capture(window, outDir, "pki-a-certificates-tab.png");
            var texts = window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();

            // The end of the CA list: the created intermediate and the CA certificate without its key.
            var authoritiesList = view.FindControl<IssuedCertificateList>("AuthoritiesList")!;
            authoritiesList.Table.ScrollIntoView(vm.Authorities.Rows[^1], null);
            HeadlessPageTests.Pump();
            HeadlessPageTests.Capture(window, outDir, "pki-a-ca-list-end.png");
            var badges = vm.Authorities.Rows.Select(r => (r.Name, string.Join("+", r.Badges), r.KeyType)).ToList();
            static string Text(IssuedCertificateAction a) => a.IsDanger ? a.Text + " (red)" : a.Text;
            var menus = vm.Authorities.Rows.ToDictionary(r => r.Name, r => vm.Authorities.MenuOf(r).Select(Text).ToList());
            var device = vm.Certificates.MenuOf(vm.Certificates.Rows.First(r => r.Source.Kind == ListedCertificateKind.Device)).Select(Text).ToList();
            var toolbar = window.GetVisualDescendants().OfType<ToolbarButton>().Where(b => b.IsEffectivelyVisible).Select(b => b.Text).ToList();
            window.Close();

            // Create CA: a root CA, then an intermediate one with its Signed by.
            var issuers = (vm.State?.Authorities ?? []).Where(a => a.Issues).Select(a => new Choice(a.Id, a.Name)).ToList();
            var rootDialog = new CreateCaViewModel((_, _) => Task.FromResult(true), issuers) { CommonName = "Contoso Root CA" };
            Show(new CreateCaWindow(), rootDialog, "pki-a-create-ca-root.png");
            var intermediate = new CreateCaViewModel((_, _) => Task.FromResult(true), issuers) { CommonName = "Building B CA" };
            intermediate.Type = CreateCaViewModel.TypeChoices[1];
            intermediate.Key = CreateCaViewModel.KeyChoices[0];
            Show(new CreateCaWindow(), intermediate, "pki-a-create-ca-intermediate.png");

            // The delete confirmation of a CA (the page's ConfirmAsync shows the shared message window).
            var confirm = new MessageWindow { Heading = PkiViewModel.DeleteCaTitle("Contoso Root"), Message = PkiViewModel.DeleteCaMessage, ConfirmText = "Delete", CancelText = "Cancel" };
            Oadm.Client.App.ApplyCrispText(confirm);
            confirm.Show();
            HeadlessPageTests.Pump();
            HeadlessPageTests.Capture(confirm, outDir, "pki-a-delete-ca.png");
            var red = confirm.IsDanger; // red because the confirm button says Delete
            confirm.Close();
            return (texts, badges, menus, device, toolbar, red, intermediate.IsIntermediate, issuers.Count);
        }, CancellationToken.None);
        GC.KeepAlive(session);

        // Item 4: only Root CA or Intermediate CA; item 5: "No key" for a certificate only.
        Assert.Contains(("OADM Root CA SERVER01", "Root CA", "RSA 2048"), checks.badges);
        Assert.Contains(("OADM Issuing CA", "Intermediate CA", "ECDSA P-256"), checks.badges);
        Assert.Contains(("Contoso Root", "Root CA", CaKeyTexts.None), checks.badges);
        Assert.DoesNotContain(checks.badges, b => b.Item2.Contains("OADM CA", StringComparison.Ordinal) || b.Item2.Contains("Imported", StringComparison.Ordinal));

        // Items 1 and 6: Delete for every CA, Install in trusted root store on every root CA, Back up for a CA with its key.
        Assert.Equal(["View", "Export", "Install in trusted root store", "Back up", "-", "Delete (red)"], checks.menus["OADM Root CA SERVER01"]);
        Assert.Equal(["View", "Export", "Back up", "-", "Delete (red)"], checks.menus["OADM Issuing CA"]);
        Assert.Equal(["View", "Export", "Install in trusted root store", "-", "Delete (red)"], checks.menus["Contoso Root"]);

        // Item 7: no revoke anywhere.
        Assert.Equal(["View", "Export", "-", "Delete (red)"], checks.device);
        Assert.Contains("Create CA", checks.toolbar);
        Assert.DoesNotContain("New OADM CA", checks.toolbar);
        Assert.Contains("OS root store: client", checks.texts);
        Assert.True(checks.red);
        Assert.True(checks.Item7);
        Assert.Equal(2, checks.Item8); // the first-start root CA and the created intermediate can sign

        void Show(Window dialog, PkiDialogViewModel model, string name)
        {
            PkiDialogs.Attach(dialog, model);
            Oadm.Client.App.ApplyCrispText(dialog);
            dialog.Show();
            HeadlessPageTests.Pump();
            HeadlessPageTests.Capture(dialog, outDir, name);
            dialog.Close();
        }
    }
}
