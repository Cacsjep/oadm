using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Oadm.Sdk.Client.Controls;

namespace Oadm.Client.Tests;

/// <summary>The rail icons wobble once on hover, one icon at a time.</summary>
public sealed class RailWobbleTests
{
    [Fact]
    public async Task Hovering_a_rail_entry_turns_only_its_icon()
    {
        var done = await HeadlessSession.Shared.Dispatch(async () =>
        {
            var first = new Button { Classes = { "nav" }, Content = new IconLabel { Icon = Geometry.Parse("M0 0L24 24"), Text = "Devices" } };
            var second = new Button { Classes = { "nav" }, Content = new IconLabel { Icon = Geometry.Parse("M0 0L24 24"), Text = "Logs" } };
            var window = new Window { Width = 300, Height = 200, Content = new StackPanel { Children = { first, second } } };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var icons = window.GetVisualDescendants().OfType<OadmIcon>().ToList();
            Assert.Equal(2, icons.Count);
            Assert.NotSame(icons[0].RenderTransform, icons[1].RenderTransform); // never one shared transform

            window.MouseMove(first.TranslatePoint(new Avalonia.Point(10, 10), window)!.Value, RawInputModifiers.None);
            var turned = false;
            for (var i = 0; i < 40 && !turned; i++)
            {
                await Task.Delay(10);
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();
                turned = ((RotateTransform)icons[0].RenderTransform!).Angle != 0;
            }

            Assert.True(turned, "the hovered icon wobbles");
            Assert.Equal(0, ((RotateTransform)icons[1].RenderTransform!).Angle);
            window.Close();
            return true;
        }, CancellationToken.None);
        Assert.True(done);
    }
}
