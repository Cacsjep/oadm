using Avalonia.Controls;
using Avalonia.VisualTree;

using Oadm.Plugins.Pki.Ca;
using Oadm.Plugins.Pki.Client;
using Oadm.Sdk.Certificates;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Pki.Tests;

/// <summary>
/// Review round 2: Revoke and Revoke all of this device (up to 20 s when the device is read first) show the progress row
/// above the list and keep both commands off until the server answered.
/// </summary>
public sealed class RevokeProgressTests
{
    internal static async Task<(PkiHarness Pki, Guid Device)> StartAsync()
    {
        var pki = await PkiHarness.StartAsync();
        IDeviceCertificateAuthority authority = new PkiCertificateAuthority(pki.Service);
        var device = Guid.NewGuid();
        pki.Manage(device, "B8A44F631339");
        await RevocationTests.SignAsync(authority, device, source: null);
        await RevocationTests.SignAsync(authority, device, use: DeviceCertificateUse.Client);
        return (pki, device);
    }

    /// <summary>The device certificates of the list (the CAs come first).</summary>
    internal static List<IssuedCertificateRow> DeviceRows(IssuedCertificateListViewModel list) =>
        [.. list.Rows.Where(r => r.Source.Kind == ListedCertificateKind.Device)];

    /// <summary>Holds the answers of <paramref name="method"/> until the returned source is completed.</summary>
    internal static TaskCompletionSource Hold(PluginPageContext ctx, PkiHarness pki, string method)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.Intercept = async (m, payload) =>
        {
            if (m == method)
            {
                await gate.Task;
            }

            return await pki.Plugin.InvokeAsync(m, payload, CancellationToken.None);
        };
        return gate;
    }

    [Fact]
    public async Task Revoke_all_of_this_device_shows_its_progress_and_keeps_both_commands_off()
    {
        var (pki, device) = await StartAsync();
        await using var _ = pki;
        var ctx = new PluginPageContext(pki.Plugin, pki.Hub);
        var tab = new IssuedCertificatesViewModel(ctx) { Dialogs = new FakeCertificateDialogs() };
        await tab.LoadAsync();
        tab.SetSelection(DeviceRows(tab).Take(1));
        Assert.Equal((false, IssuedCertificateTexts.Reading), (tab.ShowsProgress, tab.ProgressText));
        Assert.True(tab.RevokeCommand.CanExecute(null));

        var gate = Hold(ctx, pki, PkiMethods.RevokeDevice);
        var revoking = tab.RevokeDeviceCommand.ExecuteAsync(DeviceRows(tab)[0]);
        await Eventually.TrueAsync(() => tab.IsWorking, "Revoke all of this device is running");

        Assert.True(tab.ShowsProgress);
        Assert.Equal(IssuedCertificateTexts.Revoking, tab.ProgressText);
        Assert.False(tab.RevokeCommand.CanExecute(null));
        Assert.False(tab.RevokeDeviceCommand.CanExecute(DeviceRows(tab)[0]));

        gate.SetResult();
        await revoking;

        Assert.False(tab.IsWorking);
        Assert.False(tab.ShowsProgress);
        Assert.Null(tab.WorkText);
        Assert.Empty(DeviceRows(tab)); // the page was read again: revoked certificates leave the list
        Assert.Equal(2, pki.Service.Issued.Revocations.Count(r => r.DeviceId == device));
        Assert.Empty(ctx.Messages);
    }

    [Fact]
    public async Task A_failed_revoke_ends_the_progress_before_its_message()
    {
        var (pki, _) = await StartAsync();
        await using var _ = pki;
        var ctx = new PluginPageContext(pki.Plugin, pki.Hub);
        var tab = new IssuedCertificatesViewModel(ctx) { Dialogs = new FakeCertificateDialogs() };
        await tab.LoadAsync();
        tab.SetSelection(DeviceRows(tab).Take(1));
        ctx.Intercept = (m, payload) => m == PkiMethods.Revoke
            ? throw new InvalidOperationException("The PKI has no certificate authority.")
            : pki.Plugin.InvokeAsync(m, payload, CancellationToken.None);
        bool? messageBeforeTheEnd = null;
        tab.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IssuedCertificatesViewModel.IsWorking) && !tab.IsWorking)
            {
                messageBeforeTheEnd = ctx.Messages.Count > 0;
            }
        };

        await tab.RevokeCommand.ExecuteAsync(null);

        Assert.False(messageBeforeTheEnd);
        Assert.Equal("Revoke", Assert.Single(ctx.Messages).Title);
        Assert.False(tab.IsWorking);
        Assert.True(tab.RevokeCommand.CanExecute(null));
    }
}

/// <summary>The progress row of Revoke rendered offscreen (OADM_SCREENSHOT_DIR writes pki-revoking.png).</summary>
[Collection(HeadlessSessions.Name)]
public sealed class RevokeProgressHeadlessTests
{
    [Fact]
    public async Task The_list_shows_the_revoke_in_its_progress_row()
    {
        var outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        var (pki, _) = await RevokeProgressTests.StartAsync();
        await using var _ = pki;
        var ctx = new PluginPageContext(pki.Plugin, pki.Hub);
        var checks = await HeadlessEntry.Session.Dispatch(async () =>
        {
            var view = new PkiView();
            using var vm = new PkiViewModel(ctx, new FakeClientTrust());
            var window = HeadlessPageTests.Host(view);
            window.Show();
            view.DataContext = vm;
            await vm.LoadAsync();
            await vm.Certificates.LoadAsync();
            vm.Certificates.Dialogs = new FakeCertificateDialogs(); // no real window
            vm.Certificates.SetSelection(RevokeProgressTests.DeviceRows(vm.Certificates).Take(1));
            var gate = RevokeProgressTests.Hold(ctx, pki, PkiMethods.RevokeDevice);
            var revoking = vm.Certificates.RevokeDeviceCommand.ExecuteAsync(RevokeProgressTests.DeviceRows(vm.Certificates)[0]);
            HeadlessPageTests.Pump();
            HeadlessPageTests.Capture(window, outDir, "pki-revoking.png");
            var during = Visible(window);
            // The row's Revoke (its actions menu) is off while the write runs.
            var row = RevokeProgressTests.DeviceRows(vm.Certificates)[1];
            var revoke = vm.Certificates.MenuOf(row).First(a => a.Text == "Revoke");
            var enabledDuring = revoke.Command!.CanExecute(revoke.Parameter);
            gate.SetResult();
            await revoking;
            HeadlessPageTests.Pump();
            var after = Visible(window);
            window.Close();
            return (during, enabledDuring, after);
        }, CancellationToken.None);

        Assert.Contains(IssuedCertificateTexts.Revoking, checks.during);
        Assert.False(checks.enabledDuring);
        Assert.DoesNotContain(IssuedCertificateTexts.Revoking, checks.after);
    }

    private static List<string?> Visible(Window window) =>
        [.. window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text)];
}
