using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Oadm.Sdk.Client.Controls;

namespace Oadm.Client.Tests;

/// <summary>The shared toolbar row of plugin pages: entries that do not fit go behind its own More button.</summary>
public sealed class ToolbarOverflowPanelTests
{
    [Fact]
    public async Task Entries_that_do_not_fit_go_into_the_own_more_menu()
    {
        await HeadlessSession.Shared.Dispatch(() =>
        {
            var panel = new ToolbarOverflowPanel();
            for (int i = 0; i < 8; i++)
            {
                panel.Children.Add(new ToolbarButton { IconKey = "refresh", Text = "Entry " + i });
            }

            var window = new Window { Width = 1400, Height = 200, Content = panel };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var more = window.GetVisualDescendants().OfType<ToolbarButton>().Single(b => b.IconKey == "more");
            Assert.False(panel.HasOverflow);
            Assert.True(more.Bounds.X < 0); // hidden while everything fits

            window.Width = 400;
            Dispatcher.UIThread.RunJobs();
            Assert.True(panel.HasOverflow);
            Assert.NotEmpty(panel.OverflowItems);
            Assert.InRange(more.Bounds.Right, 0, panel.Bounds.Width + 0.5); // the More button sits inside the row
            Assert.All(panel.Children.Except(panel.OverflowItems), c => Assert.True(c.Bounds.Right <= more.Bounds.X));

            more.Flyout!.ShowAt(more);
            Dispatcher.UIThread.RunJobs();
            int moved = 8 - panel.Children.Count;
            Assert.True(moved > 0);
            more.Flyout.Hide();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(8, panel.Children.Count);
            Assert.Equal("Entry 0", ((ToolbarButton)panel.Children[0]).Text);
            window.Close();
            return Task.CompletedTask;
        }, CancellationToken.None);
    }
}
