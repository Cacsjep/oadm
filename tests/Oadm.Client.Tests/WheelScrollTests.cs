using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Oadm.Client.Infrastructure;

namespace Oadm.Client.Tests;

/// <summary>
/// The wheel over a focused select or number field scrolls the form and keeps the value (Avalonia steps both by itself);
/// focused fields show the accent border.
/// </summary>
public sealed class WheelScrollTests
{
    [Fact]
    public async Task Wheel_over_focused_fields_scrolls_the_form_and_keeps_their_values()
    {
        HeadlessUnitTestSession session = HeadlessSession.Shared;
        await session.Dispatch(() =>
        {
            WheelScroll.Attach();
            var action = new ComboBox { ItemsSource = new[] { "Send email", "Send HTTP request", "Set output" }, SelectedIndex = 0, Width = 200 };
            var number = new NumericUpDown { Value = 5, Increment = 1, Width = 200 };
            var form = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Left };
            form.Children.Add(action);
            form.Children.Add(number);
            form.Children.Add(new Border { Height = 3000 });
            var scroll = new ScrollViewer { Content = form };
            var window = new Window { Width = 400, Height = 300, Content = scroll };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            action.Focus();
            Dispatcher.UIThread.RunJobs();
            Border box = action.GetVisualDescendants().OfType<Border>().First(b => b.Name == "Background");
            Assert.Equal(((ISolidColorBrush)window.FindResource("Oadm.AccentBrush")!).Color, ((ISolidColorBrush)box.BorderBrush!).Color);

            window.MouseWheel(Center(action, window), new Vector(0, -1));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(0, action.SelectedIndex);
            var afterAction = scroll.Offset.Y;
            Assert.True(afterAction > 0, "The form did not scroll over the select field.");

            number.GetVisualDescendants().OfType<TextBox>().First().Focus();
            Dispatcher.UIThread.RunJobs();
            window.MouseWheel(Center(number, window), new Vector(0, 1));
            window.MouseWheel(Center(number, window), new Vector(0, -1));
            window.MouseWheel(Center(number, window), new Vector(0, -1));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(5, number.Value);
            Assert.True(scroll.Offset.Y > afterAction, "The form did not scroll over the number field.");

            // An open list keeps the wheel: the form stays where it is.
            action.IsDropDownOpen = true;
            Dispatcher.UIThread.RunJobs();
            var withOpenList = scroll.Offset.Y;
            window.MouseWheel(Center(action, window), new Vector(0, -1));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(withOpenList, scroll.Offset.Y);
            action.IsDropDownOpen = false;
            window.Close();
            return Task.CompletedTask;
        }, CancellationToken.None);
    }

    private static Point Center(Control control, Window window) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
}
