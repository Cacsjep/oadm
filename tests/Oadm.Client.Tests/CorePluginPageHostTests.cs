using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Oadm.Client.Shell;

namespace Oadm.Client.Tests;

/// <summary>
/// The host page around a core plugin view. Regression: the same view was bound into two
/// presenters (card and own-cards) and Avalonia threw "already has a visual parent" on first
/// layout, crashing the client when the VAPIX Commander page opened.
/// </summary>
public sealed class CorePluginPageHostTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Plugin_view_is_hosted_once_with_and_without_own_cards(bool hasOwnCards)
    {
        HeadlessUnitTestSession session = HeadlessSession.Shared;
        await session.Dispatch(() =>
        {
            var view = new Border { Child = new TextBlock { Text = "plugin page" } };
            var page = new CorePluginPageViewModel("oadm.test", "Test page", view, hasOwnCards);
            var window = new Window
            {
                Width = 800,
                Height = 600,
                Content = new CorePluginPageView { DataContext = page },
            };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            Assert.NotNull(view.Parent);
            Assert.Equal(hasOwnCards ? page.OwnCardsView : page.CardView, view);
            Assert.Null(hasOwnCards ? page.CardView : page.OwnCardsView);
            Assert.True(view.IsEffectivelyVisible);
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<Oadm.Client.Tasks.TasksPaneView>(), p => p.IsEffectivelyVisible);
            window.Close();
        }, CancellationToken.None);
    }

    /// <summary>
    /// A live status in the page header (License Plate Center's "2 of 11 cameras connected", Text recognition's load) ends on
    /// the right edge of the page's cards.
    /// </summary>
    [Fact]
    public async Task A_header_status_ends_on_the_right_edge_of_the_cards()
    {
        string? outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        string appearance = string.Equals(Environment.GetEnvironmentVariable("OADM_TEST_APPEARANCE"), "Light", StringComparison.OrdinalIgnoreCase) ? "light" : "dark";
        await HeadlessSession.Shared.Dispatch(() =>
        {
            var card = new Border { Height = 200 };
            card.Classes.Add("card");
            var chip = new Oadm.Sdk.Client.Controls.StatusChip { Text = "2 of 11 cameras connected", IsWarning = true };
            var trailing = new StackPanel { Margin = new Avalonia.Thickness(0, 13, 0, -18), Orientation = Avalonia.Layout.Orientation.Horizontal, Children = { chip } };
            Oadm.Sdk.Client.Controls.PageHeader.SetTrailing(card, trailing);
            var page = new CorePluginPageViewModel("oadm.test", "License Plate Center", card, hasOwnCards: true);
            var host = new CorePluginPageView { DataContext = page };
            var window = new Window { Width = 1300, Height = 400, Content = new Border { Padding = new Avalonia.Thickness(8, 0, 16, 16), Child = host } };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            double chipRight = chip.TranslatePoint(new Avalonia.Point(chip.Bounds.Width, 0), host)!.Value.X;
            double cardRight = card.TranslatePoint(new Avalonia.Point(card.Bounds.Width, 0), host)!.Value.X;
            Assert.True(Math.Abs(chipRight - cardRight) < 1, $"status ends at {chipRight}, card at {cardRight}");
            if (!string.IsNullOrEmpty(outDir))
            {
                Directory.CreateDirectory(outDir);
                window.CaptureRenderedFrame()!.Save(Path.Combine(outDir, $"core-page-header-status-{appearance}.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }

            window.Close();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Page_with_ShowTasksPane_shows_the_shared_tasks_pane_below_it()
    {
        HeadlessUnitTestSession session = HeadlessSession.Shared;
        using var f = new DevicesFixture();
        await session.Dispatch(() =>
        {
            var view = new Border { Child = new TextBlock { Text = "commander" } };
            var page = new CorePluginPageViewModel("oadm.test", "Commander", view, hasOwnCards: true, tasks: f.Devices.Tasks);
            var window = new Window
            {
                Width = 1200,
                Height = 800,
                Content = new CorePluginPageView { DataContext = page },
            };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var pane = Assert.Single(window.GetVisualDescendants().OfType<Oadm.Client.Tasks.TasksPaneView>());
            Assert.True(pane.IsEffectivelyVisible);
            Assert.Same(f.Devices.Tasks, pane.DataContext);
            Assert.True(view.IsEffectivelyVisible);
            window.Close();
        }, CancellationToken.None);
    }
}
