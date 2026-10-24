using Avalonia.Controls;
using Avalonia.VisualTree;

using Oadm.Plugins.Radius.Api;
using Oadm.Plugins.Radius.Client;

namespace Oadm.Plugins.Radius.Tests.Client;

/// <summary>Review round 2 rendered offscreen: the unusable suites with Save off, no server-wide cards for a group user.</summary>
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
            using var vm = new RadiusViewModel(ctx);
            await vm.Server.LoadAsync(CancellationToken.None); // the server runs: the cards show
            await vm.LoadServerAsync(CancellationToken.None);
            var dialog = vm.NewAdvanced(server.State);
            var window = new AdvancedWindow();
            window.Attach(dialog, _ => { });
            Oadm.Client.App.ApplyCrispText(window);
            window.Show();
            dialog.ResetCipherSuitesCommand.Execute(null);
            foreach (var suite in dialog.CipherSuites.Where(s => s.IsUsable(dialog.MinimumTls.Value)))
            {
                suite.IsChecked = false;
            }

            HeadlessPageTests.Pump();
            HeadlessPageTests.Capture(window, "radius-unusable-suites.png");
            var texts = Texts(window);
            var save = window.GetVisualDescendants().OfType<Button>().Single(b => b.IsEffectivelyVisible && Equals(b.Content, "Save"));
            var result = (texts, saveEnabled: save.IsEffectivelyEnabled, tip: ToolTip.GetTip(save) as string);
            window.Close();
            return result;
        }, CancellationToken.None);

        Assert.Equal(3, checks.texts.Count(t => t == AdvancedViewModel.UnusableSuiteHint)); // the three ECDHE_RSA suites of TLS 1.2
        Assert.Contains(RadiusRules.CheckCipherSuite, checks.texts); // below the list
        Assert.False(checks.saveEnabled);
        Assert.Equal(RadiusRules.CheckCipherSuite, checks.tip);
    }

    [Fact]
    public async Task A_user_the_server_limits_to_a_group_sees_no_server_wide_card()
    {
        var server = new FakeRadiusBackend();
        server.State = server.State with { HasAllDevices = false };
        var ctx = new FakePageContext(server);
        var texts = await HeadlessEntry.Session.Dispatch(async () =>
        {
            var view = new RadiusView();
            using var vm = new RadiusViewModel(ctx);
            var window = HeadlessPageTests.Host(view, 1280, 900);
            window.Show();
            view.DataContext = vm;
            await vm.LoadAllAsync(CancellationToken.None);
            HeadlessPageTests.Pump();
            HeadlessPageTests.Capture(window, "radius-group-server-says.png");
            var result = Texts(window);
            window.Close();
            return result;
        }, CancellationToken.None);

        Assert.DoesNotContain("Policies", texts);
        Assert.DoesNotContain("Trusted CAs", texts);
        Assert.DoesNotContain("Server", texts);
        Assert.Contains("Sessions", texts);
    }

    private static List<string?> Texts(Window window) =>
    [
        .. window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text),
        .. window.GetVisualDescendants().OfType<CheckBox>().Where(c => c.IsEffectivelyVisible).Select(c => c.Content as string),
    ];
}
