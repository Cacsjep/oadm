using Avalonia.Controls;
using Avalonia.VisualTree;

using Oadm.Plugins.Radius.Api;
using Oadm.Plugins.Radius.Client;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Radius.Tests.Client;

/// <summary>Review round 2 rendered offscreen: the unusable suites with Save off, Revoke's progress row, no VLANs for a group user.</summary>
[Collection(HeadlessSessions.Name)]
public sealed class ReviewRound2HeadlessTests
{
    [Fact]
    public async Task Unusable_suites_carry_their_hint_and_save_stays_off_with_its_tooltip()
    {
        var server = new FakeRadiusBackend();
        server.State = ReviewRound2PageTests.WithOnlyRsaSuitesChecked(server.State);
        var ctx = new FakePageContext(server);
        var checks = await HeadlessEntry.Session.Dispatch(async () =>
        {
            var view = new RadiusView();
            using var vm = new RadiusViewModel(ctx);
            var window = HeadlessPageTests.Host(view, 1280, 1100);
            window.Show();
            view.DataContext = vm;
            await vm.LoadServerAsync(CancellationToken.None);
            vm.Settings.ShowAdvanced = true;
            vm.Settings.ShowAllErrors();
            HeadlessPageTests.Pump();
            HeadlessPageTests.Capture(window, "radius-unusable-suites.png");
            var texts = Texts(window);
            var save = window.GetVisualDescendants().OfType<Button>().Single(b => b.IsEffectivelyVisible && Equals(b.Content, "Save"));
            var result = (texts, saveEnabled: save.IsEffectivelyEnabled, tip: ToolTip.GetTip(save) as string);
            window.Close();
            return result;
        }, CancellationToken.None);

        Assert.Equal(3, checks.texts.Count(t => t == SettingsViewModel.UnusableSuiteHint)); // the three ECDHE_RSA suites of TLS 1.2
        Assert.Contains(RadiusRules.CheckCipherSuite, checks.texts); // below the list
        Assert.False(checks.saveEnabled);
        Assert.Equal(RadiusRules.CheckCipherSuite, checks.tip);
    }

    [Fact]
    public async Task Revoke_shows_the_progress_row_while_it_runs()
    {
        var server = new FakeRadiusBackend();
        var ctx = new FakePageContext(server);
        var hold = server.Hold(RadiusMethods.Revoke);
        var checks = await HeadlessEntry.Session.Dispatch(async () =>
        {
            var view = new RadiusView();
            using var vm = new RadiusViewModel(ctx);
            var window = HeadlessPageTests.Host(view, 1280, 720);
            window.Show();
            view.DataContext = vm;
            await vm.ShowRevokedTabCommand.ExecuteAsync(null);
            vm.Revoked.Dialogs = new ReviewRound2PageTests.RevokeDialogs(); // the view's own dialogs would open windows
            var revoking = vm.Revoked.RevokeCommand.ExecuteAsync(null);
            HeadlessPageTests.Pump();
            HeadlessPageTests.Capture(window, "radius-revoking.png");
            var during = Texts(window);
            var revoke = window.GetVisualDescendants().OfType<Button>().Single(b => b.IsEffectivelyVisible && b.Classes.Contains("danger"));
            var enabledDuring = revoke.IsEffectivelyEnabled;
            hold.SetResult();
            await revoking;
            HeadlessPageTests.Pump();
            var after = Texts(window);
            var enabledAfter = revoke.IsEffectivelyEnabled;
            window.Close();
            return (during, enabledDuring, after, enabledAfter);
        }, CancellationToken.None);

        Assert.Contains(RevokedViewModel.RevokingText, checks.during);
        Assert.False(checks.enabledDuring);
        Assert.DoesNotContain(RevokedViewModel.RevokingText, checks.after);
        Assert.True(checks.enabledAfter);
        Assert.Contains("3 certificates revoked", checks.after);
    }

    [Fact]
    public async Task A_user_the_server_limits_to_a_group_sees_no_vlan_card()
    {
        var server = new FakeRadiusBackend();
        server.State = server.State with { HasAllDevices = false };
        server.Vlans = new VlanSettings();
        var ctx = new FakePageContext(server);
        var texts = await HeadlessEntry.Session.Dispatch(async () =>
        {
            var view = new RadiusView();
            using var vm = new RadiusViewModel(ctx);
            var window = HeadlessPageTests.Host(view, 1280, 900);
            window.Show();
            view.DataContext = vm;
            await vm.LoadServerAsync(CancellationToken.None);
            HeadlessPageTests.Pump();
            HeadlessPageTests.Capture(window, "radius-group-server-tab.png");
            var result = Texts(window);
            window.Close();
            return result;
        }, CancellationToken.None);

        Assert.DoesNotContain("VLANs", texts);
        Assert.DoesNotContain("Assign VLANs", texts);
        Assert.Contains("General", texts);
    }

    private static List<string?> Texts(Window window) =>
    [
        .. window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text),
        .. window.GetVisualDescendants().OfType<CheckBox>().Where(c => c.IsEffectivelyVisible).Select(c => c.Content as string),
    ];
}
