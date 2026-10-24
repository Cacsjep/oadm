using Avalonia.Controls;
using Avalonia.VisualTree;

using Oadm.Plugins.Pki.Ca;
using Oadm.Plugins.Pki.Certificates;
using Oadm.Plugins.Pki.Client;
using Oadm.Plugins.Pki.Policies;
using Oadm.Sdk.Certificates;
using Oadm.Sdk.Client.Controls;

namespace Oadm.Plugins.Pki.Tests;

/// <summary>The Certificates card with every kind of row and its dialogs offscreen (OADM_SCREENSHOT_DIR writes PNGs).</summary>
[Collection(HeadlessSessions.Name)]
public sealed class CertificateCardHeadlessTests
{
    [Fact]
    public async Task The_card_with_every_kind_of_row_and_its_dialogs_render()
    {
        var outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        var devices = Enumerable.Range(0, 6).Select(i => new PkiFakeDevice(Guid.NewGuid(), "10.0.0." + (40 + i).ToString(System.Globalization.CultureInfo.InvariantCulture),
            "ACCC8E" + (0x123400 + i).ToString("X6", System.Globalization.CultureInfo.InvariantCulture))).ToList();
        await using var pki = await PkiHarness.StartAsync(devices: devices);
        var authority = new PkiCertificateAuthority(pki.Service);
        foreach (var device in devices)
        {
            await RevocationTests.SignAsync(authority, device.Id, device.Serial);
        }

        await authority.SignAsync(new DeviceCertificateRequest(RevocationTests.Csr(), "oadm-server", ["10.0.0.5"], ["oadm-server"], DeviceCertificateUse.Server)
        {
            Source = ServerCertificateSources.Dot1x,
        }, CancellationToken.None);
        Assert.True((await CertificateListTests.CreateAsync(pki)).Ok);
        using (var root = TestCa.Root("Contoso Root"))
        using (var leaf = TestCa.Leaf(root, "10.0.0.30"))
        {
            Assert.True((await pki.InvokeAsync<CertificateActionReply>(CertificateMethods.Import, new ImportCertificateRequest(Convert.ToBase64String(TestCa.Pem(leaf)), "leaf.crt", null))).Ok);
        }

        var ctx = new PluginPageContext(pki.Plugin, pki.Hub);
        var policies = pki.Policies.All;
        var session = HeadlessEntry.Session;
        var checks = await session.Dispatch(async () =>
        {
            var view = new PkiView();
            using var vm = new PkiViewModel(ctx, new FakeClientTrust());
            var window = HeadlessPageTests.Host(view, 1700, 1300);
            window.Show();
            view.DataContext = vm;
            await vm.LoadAsync();
            await vm.Certificates.LoadAsync();
            HeadlessPageTests.Pump();
            var list = view.GetVisualDescendants().OfType<IssuedCertificateList>().Single();
            list.BringIntoView();
            HeadlessPageTests.Pump();
            HeadlessPageTests.Capture(window, outDir, "pki-certificates-card.png");
            var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
            var buttons = vm.Certificates.Rows.SelectMany(r => vm.Certificates.MenuOf(r)).Select(a => a.Text).ToList(); // the rows' menus

            // The rows further down (the grid shows only what fits): the servers' and the created ones.
            list.Table.ScrollIntoView(vm.Certificates.Rows[^1], null);
            HeadlessPageTests.Pump();
            HeadlessPageTests.Capture(window, outDir, "pki-certificates-card-end.png");
            texts.AddRange(window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
            window.Close();

            // The dialogs, each with an error shown where the user would see it.
            var create = new CreateCertificateViewModel((_, _, _) => Task.FromResult(true), policies) { Name = "nvr.example.com", Addresses = "nvr.example.com, 10.0.0.20" };
            create.Password = "short";
            Show(new CreateCertificateWindow(), create, "pki-create-certificate.png");
            var sign = new SignRequestViewModel((_, _) => Task.FromResult(true), policies);
            sign.SetFile(new PickedFile("nvr.csr", RevocationTests.Csr()));
            Show(new SignRequestWindow(), sign, "pki-sign-request.png");
            var import = new ImportCertificateViewModel((_, _) => Task.FromResult(true));
            import.SetFile(new PickedFile("nvr.p12", [0x30, 0x82, 0x01]));
            Show(new ImportCertificateWindow(), import, "pki-import-certificate.png");
            Show(new ExportKeyWindow(), new ExportKeyViewModel("nvr.example.com", (_, _) => Task.FromResult(true)), "pki-export-key.png");
            return (texts, buttons, import.NeedsPassword);
        }, CancellationToken.None);
        GC.KeepAlive(session);

        Assert.DoesNotContain("Used by", checks.texts); // no Used by column (decision 2026-10-25)
        Assert.Contains("802.1X server", checks.texts);
        Assert.Contains("nvr.example.com", checks.texts);
        Assert.Contains("OADM Root CA SERVER01", checks.texts);
        Assert.Contains("Create", checks.texts);
        Assert.Contains("Sign request", checks.texts);
        Assert.Contains("Delete", checks.buttons);
        Assert.True(checks.NeedsPassword);

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
