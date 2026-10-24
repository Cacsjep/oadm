using Avalonia.Controls;
using Avalonia.VisualTree;

using Oadm.Plugins.Radius.Api;
using Oadm.Plugins.Radius.Client;

namespace Oadm.Plugins.Radius.Tests.Client;

/// <summary>
/// The Server tab in three, two and one column by the page width, the one Save below the cards, and the log rows of
/// dropped requests with Add switch (headless, screenshots with OADM_SCREENSHOT_DIR; checks are content and rules).
/// </summary>
[Collection(HeadlessSessions.Name)]
public sealed class ServerLayoutTests
{
    [Theory]
    [InlineData(2100, 3)]
    [InlineData(1500, 2)]
    [InlineData(900, 1)]
    public async Task The_server_tab_uses_three_two_or_one_column_by_width_and_save_sits_below_the_cards(int width, int columns)
    {
        var ctx = new FakePageContext(new FakeRadiusBackend());
        var checks = await HeadlessEntry.Session.Dispatch(async () =>
        {
            var view = new RadiusView();
            using var vm = new RadiusViewModel(ctx);
            var window = HeadlessPageTests.Host(view, width, 1100);
            window.Show();
            view.DataContext = vm;
            await vm.LoadServerAsync(CancellationToken.None);
            HeadlessPageTests.Pump();
            HeadlessPageTests.Capture(window, $"radius-server-{columns}-columns.png");
            var cards = window.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("card") && b.IsEffectivelyVisible).ToList();
            var save = window.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Save");
            var saveInCard = save.GetVisualAncestors().OfType<Border>().Any(b => b.Classes.Contains("card"));
            var status = window.GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsEffectivelyVisible && t.Text == "Running on port 1812");
            var count = view.ServerColumnCount;
            // Every column of the switches is at its width: never squeezed by a pass at a width the card does not fit.
            var switches = window.GetVisualDescendants().OfType<DataGrid>().First(g => g.Name == "SwitchesGrid");
            var squeezed = switches.Columns.Skip(1).Any(c => c.ActualWidth < c.Width.Value);
            window.Close();
            return (count, cards.Count, saveInCard, status, squeezed);
        }, CancellationToken.None);

        Assert.Equal(columns, checks.count);
        Assert.Equal(3, checks.Item2); // General, Switches, VLANs
        Assert.False(checks.saveInCard);
        Assert.True(checks.status);
        Assert.False(checks.squeezed);
    }

    [Fact]
    public async Task Log_rows_of_dropped_requests_render_and_offer_add_switch_for_a_sender_that_is_not_added()
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
            await vm.ShowLogTabCommand.ExecuteAsync(null);
            HeadlessPageTests.Pump();
            vm.Log.Selected = vm.Log.Rows[0];
            HeadlessPageTests.Pump();
            HeadlessPageTests.Capture(window, "radius-log-dropped.png");
            var texts = window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
            var addFromUnknown = vm.Log.AddSwitchCommand.CanExecute(null);
            vm.Log.Selected = vm.Log.Rows[1];
            var addFromWrongSecret = vm.Log.AddSwitchCommand.CanExecute(null);
            vm.Log.Selected = vm.Log.Rows[2];
            var addFromSession = vm.Log.AddSwitchCommand.CanExecute(null);
            var view1 = vm.Log.ViewCertificateCommand.CanExecute(null);
            vm.Log.Selected = vm.Log.Rows[0];
            var viewDropped = vm.Log.ViewCertificateCommand.CanExecute(null);
            window.Close();
            return (texts, addFromUnknown, addFromWrongSecret, addFromSession, view1, viewDropped);
        }, CancellationToken.None);

        Assert.Contains("10.0.0.9 (not added)", checks.texts);
        Assert.Contains(checks.texts, t => t?.Contains(DroppedRequestTexts.NotAdded + " 12 requests in the last minute.", StringComparison.Ordinal) == true);
        Assert.Contains(checks.texts, t => t?.Contains(DroppedRequestTexts.WrongSecret, StringComparison.Ordinal) == true);
        Assert.Contains("Add switch", checks.texts);
        Assert.True(checks.addFromUnknown);
        Assert.False(checks.addFromWrongSecret);
        Assert.False(checks.addFromSession);
        Assert.True(checks.view1);
        Assert.False(checks.viewDropped); // no certificate
    }

    [Fact]
    public async Task Add_switch_of_a_log_row_opens_the_switch_dialog_with_the_address_and_site_filled_in()
    {
        var server = new FakeRadiusBackend();
        var site = FakeRadiusBackend.BuildingB;
        server.Log.Insert(0, FakeRadiusBackend.DroppedEntry(DateTime.UtcNow, "192.168.1.2 at Building B (not added)", DroppedRequestTexts.NotAdded, 1, "192.168.1.2", site));
        var ctx = new FakePageContext(server);
        using var vm = new RadiusViewModel(ctx);
        await vm.LoadServerAsync(CancellationToken.None);
        SwitchDialogViewModel? shown = null;
        vm.Switches.Dialogs = new Dialogs(d =>
        {
            shown = d;
            return Task.FromResult(false);
        });
        await vm.ShowLogTabCommand.ExecuteAsync(null);
        vm.Log.Selected = vm.Log.Rows[0];

        await vm.Log.AddSwitchCommand.ExecuteAsync(null);

        Assert.NotNull(shown);
        Assert.False(shown.IsEditing);
        Assert.Equal("192.168.1.2", shown.Address);
        Assert.Equal(site, shown.Site.Value);
        Assert.Equal(string.Empty, shown.Name);
    }

    [Fact]
    public async Task Add_switch_of_a_log_row_is_off_without_the_manage_permission()
    {
        var server = new FakeRadiusBackend();
        server.Log.Insert(0, FakeRadiusBackend.DroppedEntry(DateTime.UtcNow, "10.0.0.9 (not added)", DroppedRequestTexts.NotAdded, 1, "10.0.0.9"));
        var ctx = new FakePageContext(server) { Manage = false };
        using var vm = new RadiusViewModel(ctx);
        await vm.ShowLogTabCommand.ExecuteAsync(null);
        vm.Log.Selected = vm.Log.Rows[0];

        Assert.False(vm.Log.AddSwitchCommand.CanExecute(null));
    }

    private sealed class Dialogs(Func<SwitchDialogViewModel, Task<bool>> show) : ISwitchDialogs
    {
        public Task<bool> ShowSwitchAsync(SwitchDialogViewModel dialog) => show(dialog);
    }
}
