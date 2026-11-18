using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Oadm.Client.Controls;
using Oadm.Sdk.Client.Controls;

namespace Oadm.Client.Tests;

/// <summary>The folding cards of the Settings page: the whole header toggles the card, the arrow included.</summary>
public sealed class CollapsibleCardTests
{
    [Fact]
    public async Task A_click_on_the_arrow_or_the_title_opens_and_closes_the_card()
    {
        var outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        var done = await HeadlessSession.Shared.Dispatch(() =>
        {
            // As wide as a card on the Settings page: the press must not move the arrow out from under the pointer.
            var card = new CollapsibleCard
            {
                Title = "Polling",
                Body = new StackPanel
                {
                    Classes = { "form" },
                    Children = { new FormField { Label = "Allowed clock difference (s)", Input = new NumericUpDown { Minimum = 1, Maximum = 3600, FormatString = "0", Value = 10 } } },
                },
            };
            var window = new Window { Width = 1100, Height = 300, Content = new Border { Padding = new Thickness(16), Child = new StackPanel { Children = { card } } } };
            App.ApplyCrispText(window);
            window.Show();
            Pump();
            Capture(window, outDir, "settings-card-closed.png");

            var chevron = card.GetVisualDescendants().OfType<OadmIcon>().Single(i => i.Name == "PART_Chevron");
            var arrow = chevron.TranslatePoint(new Point(chevron.Bounds.Width / 2, chevron.Bounds.Height / 2), window)!.Value;
            Click(window, arrow);
            Assert.True(card.IsExpanded);
            Capture(window, outDir, "settings-card-open.png");

            Click(window, arrow);
            Assert.False(card.IsExpanded);

            var title = card.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "Polling");
            Click(window, title.TranslatePoint(new Point(title.Bounds.Width / 2, title.Bounds.Height / 2), window)!.Value);
            Assert.True(card.IsExpanded);

            window.Close();
            return true;
        }, CancellationToken.None);
        Assert.True(done);
    }

    /// <summary>Press, let the pressed look render (the hit test of the release sees it), release.</summary>
    private static void Click(Window window, Point point)
    {
        window.MouseMove(point, RawInputModifiers.None);
        window.MouseDown(point, MouseButton.Left);
        Pump();
        window.MouseUp(point, MouseButton.Left);
        Pump();
    }

    private static void Pump()
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }

        Dispatcher.UIThread.RunJobs();
    }

    private static void Capture(Window window, string? outDir, string name)
    {
        WriteableBitmap? frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        if (!string.IsNullOrEmpty(outDir))
        {
            Directory.CreateDirectory(outDir);
            frame.Save(Path.Combine(outDir, name), PngBitmapEncoderOptions.Default);
        }
    }
}
