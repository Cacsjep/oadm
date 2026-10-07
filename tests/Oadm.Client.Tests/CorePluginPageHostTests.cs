using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;

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
            window.Close();
        }, CancellationToken.None);
    }
}
