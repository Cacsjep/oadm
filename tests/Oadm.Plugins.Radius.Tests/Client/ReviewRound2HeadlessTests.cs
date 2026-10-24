using Avalonia.Controls;
using Avalonia.VisualTree;

using Oadm.Plugins.Radius.Api;
using Oadm.Plugins.Radius.Client;

namespace Oadm.Plugins.Radius.Tests.Client;

/// <summary>Review round 2 rendered offscreen: no server-wide cards for a group user.</summary>
[Collection(HeadlessSessions.Name)]
public sealed class ReviewRound2HeadlessTests
{
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

        Assert.DoesNotContain("Switches", texts);
        Assert.DoesNotContain("Server", texts);
        Assert.Contains("Sessions", texts);
    }

    private static List<string?> Texts(Window window) =>
    [
        .. window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text),
        .. window.GetVisualDescendants().OfType<CheckBox>().Where(c => c.IsEffectivelyVisible).Select(c => c.Content as string),
    ];
}
