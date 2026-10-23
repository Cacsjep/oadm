using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Microsoft.Extensions.Time.Testing;

using Oadm.Plugins.Pki.Ca;
using Oadm.Plugins.Pki.Client;
using Oadm.Sdk.Certificates;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Pki.Tests;

/// <summary>The window and the confirmation the Certificates tab asks for, answered by the test.</summary>
internal sealed class FakeCertificateDialogs : IIssuedCertificatesDialogs
{
    public List<CertificateDetails> Shown { get; } = [];

    public List<(string Title, string Message)> Asked { get; } = [];

    /// <summary>The reason the user picks; null = Cancel.</summary>
    public RevocationReason? Answer { get; set; } = RevocationReason.KeyCompromised;

    public Task ShowCertificateAsync(CertificateDetails certificate)
    {
        Shown.Add(certificate);
        return Task.CompletedTask;
    }

    public Task<RevocationReason?> ConfirmRevokeAsync(string title, string message)
    {
        Asked.Add((title, message));
        return Task.FromResult(Answer);
    }
}

/// <summary>The Certificates tab of the PKI page (view model against the running plugin).</summary>
public sealed class CertificatesTabTests
{
    private static async Task<(PkiHarness Pki, Guid Device, string Https, string Dot1x)> StartWithCertificatesAsync()
    {
        var pki = await PkiHarness.StartAsync();
        IDeviceCertificateAuthority authority = new PkiCertificateAuthority(pki.Service);
        var device = Guid.NewGuid();
        var https = (await RevocationTests.SignAsync(authority, device, source: null)).SerialNumber;
        var dot1x = (await RevocationTests.SignAsync(authority, device, use: DeviceCertificateUse.Client)).SerialNumber;
        return (pki, device, https, dot1x);
    }

    [Fact]
    public async Task The_tab_reads_the_first_page_when_opened_and_filters_on_the_server()
    {
        var (pki, _, https, _) = await StartWithCertificatesAsync();
        await using var _ = pki;
        var calls = new List<string>();
        var ctx = new PluginPageContext(pki.Plugin, pki.Hub);
        ctx.Intercept = (method, payload) =>
        {
            lock (calls)
            {
                calls.Add(method);
            }

            return pki.Plugin.InvokeAsync(method, payload, CancellationToken.None);
        };
        using var page = new PkiViewModel(ctx, new FakeClientTrust());
        var tab = page.Certificates;
        Assert.DoesNotContain(PkiMethods.GetCertificates, calls); // nothing read before the tab is opened

        await page.ShowCertificatesTabCommand.ExecuteAsync(null);

        Assert.True(page.IsCertificatesTab);
        Assert.Equal(2, tab.Total);
        Assert.Equal("1-2 of 2", tab.PageText);
        Assert.False(tab.HasPages);
        var row = tab.Rows.Single(r => r.Source.SerialNumber == CertificateSerials.Normalize(https));
        Assert.Equal(("B8A44F631339 (removed)", "HTTPS", "Task", "Valid"), (row.Device, row.Use, row.IssuedBy, row.State)); // the harness manages no devices
        Assert.True(row.IsOk);

        // A filter reads again with it (one call).
        tab.UseFilter = tab.UseChoices.Single(c => c.Value == DeviceCertificatePurposes.Dot1x);
        await Eventually.TrueAsync(() => tab.Rows.Count == 1 && !tab.IsReloading, "the filtered page");
        Assert.Equal("IEEE 802.1X", tab.Rows[0].Use);
        tab.UseFilter = tab.UseChoices[0];
        tab.SourceFilter = tab.SourceChoices.Single(c => c.Value == IssuedCertificateSources.Est);
        await Eventually.TrueAsync(() => tab.Rows.Count == 1 && tab.Rows[0].IssuedBy == "EST", "the source filter");
        tab.SourceFilter = tab.SourceChoices[0];
        tab.StateFilter = tab.StateChoices.Single(c => c.Value == nameof(IssuedCertificateState.Revoked));
        await Eventually.TrueAsync(() => tab.Total == 0 && !tab.IsReloading, "no revoked one");
        Assert.Equal("No certificate matches.", tab.EmptyText);
    }

    [Fact]
    public async Task The_search_waits_for_the_typing_to_stop()
    {
        var (pki, _, _, _) = await StartWithCertificatesAsync();
        await using var _ = pki;
        var time = new FakeTimeProvider();
        var reads = 0;
        var ctx = new PluginPageContext(pki.Plugin, pki.Hub);
        ctx.Intercept = (m, p) =>
        {
            Interlocked.Increment(ref reads);
            return pki.Plugin.InvokeAsync(m, p, CancellationToken.None);
        };
        var tab = new IssuedCertificatesViewModel(ctx, time);
        await tab.LoadAsync();
        Assert.Equal(1, reads);

        tab.Search = "1";
        tab.Search = "10";
        tab.Search = "10.0.0.99";
        Assert.Equal(1, reads);
        time.Advance(TimeSpan.FromMilliseconds(300));
        await Eventually.TrueAsync(() => tab.Total == 0 && Volatile.Read(ref reads) == 2, "one read for the last text");
        Assert.Equal("No certificate matches.", tab.EmptyText);
    }

    [Fact]
    public async Task Revoke_asks_with_the_reason_revokes_the_chosen_rows_and_reads_the_page_again()
    {
        var (pki, device, https, dot1x) = await StartWithCertificatesAsync();
        await using var _ = pki;
        var ctx = new PluginPageContext(pki.Plugin, pki.Hub);
        var dialogs = new FakeCertificateDialogs { Answer = null };
        var tab = new IssuedCertificatesViewModel(ctx) { Dialogs = dialogs };
        await tab.LoadAsync();
        Assert.False(tab.RevokeCommand.CanExecute(null)); // nothing chosen
        tab.SetSelection(tab.Rows.Where(r => r.Source.SerialNumber == CertificateSerials.Normalize(https)));
        Assert.True(tab.MayRevoke);

        await tab.RevokeCommand.ExecuteAsync(null); // Cancel
        var authority = new PkiCertificateAuthority(pki.Service);
        Assert.False(authority.IsRevoked(https));
        Assert.Equal(("Revoke the certificate of " + tab.Rows[0].Device + "?", IssuedCertificatesViewModel.ConfirmMessageOne), dialogs.Asked[0]);

        dialogs.Answer = RevocationReason.DeviceReplaced;
        await tab.RevokeCommand.ExecuteAsync(null);
        Assert.True(authority.IsRevoked(https));
        Assert.Equal(RevocationReason.DeviceReplaced, authority.FindRevocation(https)!.Reason);
        var revokedRow = tab.Rows.Single(r => r.Source.SerialNumber == CertificateSerials.Normalize(https));
        Assert.Equal(("Revoked", true), (revokedRow.State, revokedRow.IsError));
        Assert.StartsWith("Certificate revoked on ", revokedRow.StateTip, StringComparison.Ordinal);
        Assert.Single(tab.Selected); // the choice is kept over the new page
        Assert.False(tab.RevokeCommand.CanExecute(null)); // already revoked

        // View certificate shows the revocation in the window.
        await tab.ViewCertificateCommand.ExecuteAsync(null);
        var shown = Assert.Single(dialogs.Shown);
        Assert.True(shown.IsRevoked);
        Assert.Equal("Certificate revoked on " + DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + " (Device replaced)", shown.ValidityText);

        // Revoke all of this device: the IEEE 802.1X one too.
        await tab.RevokeDeviceCommand.ExecuteAsync(tab.Rows.Single(r => r.Source.SerialNumber == CertificateSerials.Normalize(dot1x)));
        Assert.True(authority.IsRevoked(dot1x));
        Assert.StartsWith("Revoke all certificates of ", dialogs.Asked[^1].Title, StringComparison.Ordinal);
        Assert.All(tab.Rows, r => Assert.Equal("Revoked", r.State));
        Assert.Empty(ctx.Messages);
        Assert.All(tab.Rows, r => Assert.Equal(device, r.Source.DeviceId));

        // Again: nothing left, the user is told.
        await tab.RevokeDeviceCommand.ExecuteAsync(tab.Rows[0]);
        Assert.Equal("Nothing was revoked: these certificates are already revoked or expired.", Assert.Single(ctx.Messages).Message);
    }

    [Fact]
    public async Task A_group_user_can_view_but_not_revoke()
    {
        var (pki, _, _, _) = await StartWithCertificatesAsync();
        await using var _ = pki;
        var ctx = new PluginPageContext(pki.Plugin, pki.Hub) { HasAllDevices = false };
        var tab = new IssuedCertificatesViewModel(ctx) { Dialogs = new FakeCertificateDialogs() };
        await tab.LoadAsync();
        tab.SetSelection(tab.Rows.Take(1));

        Assert.False(tab.MayRevoke);
        Assert.False(tab.RevokeCommand.CanExecute(null));
        Assert.False(tab.RevokeDeviceCommand.CanExecute(tab.Rows[0]));
        Assert.True(tab.ViewCertificateCommand.CanExecute(null));
    }

    [Fact]
    public async Task Pages_of_500_and_an_event_reads_the_shown_page_again()
    {
        await using var pki = await PkiHarness.StartAsync();
        var now = DateTime.UtcNow;
        await pki.SetIssuedAsync(Enumerable.Range(0, 1100).Select(i => new IssuedCertificate
        {
            SerialNumber = "D" + i.ToString("X5", System.Globalization.CultureInfo.InvariantCulture),
            DeviceId = Guid.NewGuid(),
            IssuedUtc = now.AddMinutes(-i),
            NotAfterUtc = now.AddDays(10 + (i % 60)),
        }));
        var ctx = new PluginPageContext(pki.Plugin, pki.Hub);
        using var page = new PkiViewModel(ctx, new FakeClientTrust());
        var tab = page.Certificates;
        await page.ShowCertificatesTabCommand.ExecuteAsync(null);
        Assert.Equal((500, "1-500 of 1,100", true), (tab.Rows.Count, tab.PageText, tab.HasPages));
        Assert.False(tab.PreviousPageCommand.CanExecute(null));
        Assert.Contains(tab.Rows, r => r.IsWarning); // within the 30 warning days

        await tab.NextPageCommand.ExecuteAsync(null);
        await tab.NextPageCommand.ExecuteAsync(null);
        Assert.Equal((100, "1,001-1,100 of 1,100"), (tab.Rows.Count, tab.PageText));
        Assert.False(tab.NextPageCommand.CanExecute(null));
        await tab.PreviousPageCommand.ExecuteAsync(null);
        Assert.Equal("501-1,000 of 1,100", tab.PageText);

        // A new certificate on the server: the shown page is read again.
        await pki.SetIssuedAsync([new IssuedCertificate { SerialNumber = "E00001", DeviceId = Guid.NewGuid(), IssuedUtc = now, NotAfterUtc = now.AddDays(300) }]);
        await page.HandleEventAsync(new Oadm.Sdk.Plugins.PluginEvent(PkiMethods.CertificatesTopic, "{}"));
        Assert.Equal("501-1,000 of 1,101", tab.PageText);

        // The other tab only notes it; opening the tab again reads.
        page.ShowCaTabCommand.Execute(null);
        await pki.SetIssuedAsync([new IssuedCertificate { SerialNumber = "E00002", DeviceId = Guid.NewGuid(), IssuedUtc = now, NotAfterUtc = now.AddDays(300) }]);
        await page.HandleEventAsync(new Oadm.Sdk.Plugins.PluginEvent(PkiMethods.CertificatesTopic, "{}"));
        Assert.Equal("501-1,000 of 1,101", tab.PageText);
        await page.ShowCertificatesTabCommand.ExecuteAsync(null);
        Assert.Equal("501-1,000 of 1,102", tab.PageText);
    }
}

/// <summary>The Certificates tab, the revoke confirmation and the certificate window offscreen (OADM_SCREENSHOT_DIR writes PNGs).</summary>
[Collection(HeadlessSessions.Name)]
public sealed class CertificatesTabHeadlessTests
{
    [Fact]
    public async Task The_tab_the_revoke_confirmation_and_the_certificate_window_render()
    {
        var outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        var devices = Enumerable.Range(0, 12).Select(i => new PkiFakeDevice(Guid.NewGuid(), "10.0.0." + (40 + i).ToString(System.Globalization.CultureInfo.InvariantCulture), "ACCC8E" + (0x123400 + i).ToString("X6", System.Globalization.CultureInfo.InvariantCulture))).ToList();
        await using var pki = await PkiHarness.StartAsync(devices: devices);
        IDeviceCertificateAuthority authority = new PkiCertificateAuthority(pki.Service);
        var serials = new List<string>();
        for (var i = 0; i < 12; i++)
        {
            var device = devices[i].Id;
            var serial = devices[i].Serial;
            serials.Add((await RevocationTests.SignAsync(authority, device, serial, source: i % 3 == 0 ? "EST" : null)).SerialNumber);
            if (i % 4 == 0)
            {
                serials.Add((await RevocationTests.SignAsync(authority, device, serial, DeviceCertificateUse.Client)).SerialNumber);
            }
        }

        await authority.RevokeAsync([serials[1]], RevocationReason.KeyCompromised, "admin", CancellationToken.None);
        var ctx = new PluginPageContext(pki.Plugin, pki.Hub);
        var session = HeadlessEntry.Session;
        var checks = await session.Dispatch(async () =>
        {
            var view = new PkiView();
            using var vm = new PkiViewModel(ctx, new FakeClientTrust());
            var window = HeadlessPageTests.Host(view);
            window.Show();
            view.DataContext = vm;
            await vm.LoadAsync();
            await vm.ShowCertificatesTabCommand.ExecuteAsync(null);
            vm.Certificates.SetSelection(vm.Certificates.Rows.Take(2));
            HeadlessPageTests.Pump();
            HeadlessPageTests.Capture(window, outDir, "pki-certificates.png");
            var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
            window.Close();

            // The confirmation with the reason (the shared message window).
            var confirm = new MessageWindow
            {
                Heading = "Revoke 2 certificates?",
                Message = IssuedCertificatesViewModel.ConfirmMessage,
                ConfirmText = "Revoke",
                ChoiceLabel = "Reason",
                Choices = [.. IssuedCertificatesViewModel.Reasons.Select(CertificateRevocation.ReasonText)],
                IsDanger = true,
            };
            Oadm.Client.App.ApplyCrispText(confirm);
            confirm.Show();
            HeadlessPageTests.Pump();
            HeadlessPageTests.Capture(confirm, outDir, "pki-revoke-confirm.png");
            var reasons = confirm.ChoiceBox.ItemCount;
            var danger = confirm.ConfirmButton.Classes.Contains("danger");
            confirm.Close();

            // The shared certificate window of a revoked certificate.
            var details = await ctx.InvokeAsync(PkiMethods.GetCertificate, PkiJson.Serialize(new CertificateDetailsRequest(serials[1])), CancellationToken.None);
            var reply = PkiJson.Deserialize<CertificateDetailsReply>(details);
            var certificate = new CertificateWindow { DataContext = new CertificateDetails(reply.Certificate!, "Certificate · 10.0.0.48 · HTTPS", DateTime.UtcNow, reply.Revocation) };
            Oadm.Client.App.ApplyCrispText(certificate);
            certificate.Show();
            HeadlessPageTests.Pump();
            HeadlessPageTests.Capture(certificate, outDir, "pki-certificate-window.png");
            var windowTexts = certificate.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
            certificate.Close();
            return (texts, reasons, danger, windowTexts);
        }, CancellationToken.None);
        GC.KeepAlive(session);

        Assert.Contains("Certificates", checks.texts);
        Assert.Contains("Revoked", checks.texts);
        Assert.Contains("Serial number", checks.texts);
        Assert.Equal(4, checks.reasons);
        Assert.True(checks.danger);
        Assert.Contains(checks.windowTexts, t => t?.StartsWith("Certificate revoked on ", StringComparison.Ordinal) == true);
        Assert.Contains("Issued by", checks.windowTexts);
        _ = Dispatcher.UIThread;
    }
}
