using Avalonia.Controls;
using Avalonia.VisualTree;

using Oadm.Plugins.Radius.Api;
using Oadm.Plugins.Radius.Client;

namespace Oadm.Plugins.Radius.Tests.Client;

/// <summary>
/// The cards in four, two and one column by the page width, Save in the Server card, and the log rows of dropped requests
/// (headless, screenshots with OADM_SCREENSHOT_DIR; checks are content and rules).
/// </summary>
[Collection(HeadlessSessions.Name)]
public sealed class ServerLayoutTests
{
    [Theory]
    [InlineData(1900, 4)]
    [InlineData(1300, 2)]
    [InlineData(900, 1)]
    public async Task The_cards_use_four_two_or_one_column_by_width_and_save_sits_in_the_server_card(int width, int columns)
    {
        var ctx = new FakePageContext(new FakeRadiusBackend());
        var checks = await HeadlessEntry.Session.Dispatch(async () =>
        {
            var view = new RadiusView();
            using var vm = new RadiusViewModel(ctx);
            var window = HeadlessPageTests.Host(view, width, 1500);
            window.Show();
            view.DataContext = vm;
            await vm.LoadServerAsync(CancellationToken.None);
            HeadlessPageTests.Pump();
            HeadlessPageTests.Capture(window, $"radius-page-{columns}-columns.png");
            var cards = window.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("card") && b.IsEffectivelyVisible).ToList();
            var save = window.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Save" && b.Classes.Contains("primary"));
            var saveInCard = save.GetVisualAncestors().OfType<Border>().Any(b => b.Name == "ServerCard");
            var status = window.GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsEffectivelyVisible && t.Text == "Running on port 1812");
            var count = view.CardColumnCount;
            // Every column of the switches is at its width: never squeezed by a pass at a width the card does not fit.
            var switches = window.GetVisualDescendants().OfType<DataGrid>().First(g => g.Name == "SwitchesGrid");
            var squeezed = switches.Columns.Skip(1).Any(c => c.ActualWidth < c.Width.Value);
            window.Close();
            return (count, cards.Count, saveInCard, status, squeezed);
        }, CancellationToken.None);

        Assert.Equal(columns, checks.count);
        Assert.Equal(6, checks.Item2); // Server, Trusted CAs, Switches, Policies, Sessions, Log
        Assert.True(checks.saveInCard);
        Assert.True(checks.status);
        Assert.False(checks.squeezed);
    }

    [Fact]
    public async Task Log_rows_of_dropped_requests_render_without_add_switch()
    {
        var server = new FakeRadiusBackend();
        var now = DateTime.UtcNow;
        server.Log.Insert(0, FakeRadiusBackend.DroppedEntry(now.AddSeconds(-2), "10.0.0.9 (not added)", DroppedRequestTexts.NotAdded, 12, "10.0.0.9"));
        server.Log.Insert(1, FakeRadiusBackend.DroppedEntry(now.AddSeconds(-4), "Core switch", DroppedRequestTexts.WrongSecret, 1, null));
        var ctx = new FakePageContext(server);
        var checks = await HeadlessEntry.Session.Dispatch(async () =>
        {
            var view = new RadiusView();
            using var vm = new RadiusViewModel(ctx);
            var window = HeadlessPageTests.Host(view, 1600, 900);
            window.Show();
            view.DataContext = vm;
            await vm.Log.LoadAsync(CancellationToken.None);
            HeadlessPageTests.Pump();
            vm.Log.Selected = vm.Log.Rows[0];
            HeadlessPageTests.Pump();
            HeadlessPageTests.Capture(window, "radius-log-dropped.png");
            var texts = window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
            vm.Log.Selected = vm.Log.Rows[2];
            var view1 = vm.Log.ViewCertificateCommand.CanExecute(null);
            vm.Log.Selected = vm.Log.Rows[0];
            var viewDropped = vm.Log.ViewCertificateCommand.CanExecute(null);
            window.Close();
            return (texts, view1, viewDropped);
        }, CancellationToken.None);

        Assert.Contains("10.0.0.9 (not added)", checks.texts);
        Assert.Contains(checks.texts, t => t?.Contains(DroppedRequestTexts.NotAdded + " 12 requests in the last minute.", StringComparison.Ordinal) == true);
        Assert.Contains(checks.texts, t => t?.Contains(DroppedRequestTexts.WrongSecret, StringComparison.Ordinal) == true);
        Assert.True(checks.view1);
        Assert.False(checks.viewDropped); // no certificate
    }

    [Fact]
    public void The_last_request_of_a_switch_shows_the_time_today_and_the_day_before()
    {
        var now = new DateTime(2026, 10, 24, 15, 0, 0, DateTimeKind.Local);
        Assert.Equal("14:03:12", SwitchRowViewModel.ShortTime(new DateTime(2026, 10, 24, 14, 3, 12, DateTimeKind.Local).ToUniversalTime(), now));
        Assert.Equal("2026-10-23", SwitchRowViewModel.ShortTime(new DateTime(2026, 10, 23, 23, 59, 0, DateTimeKind.Local).ToUniversalTime(), now));
    }
}
