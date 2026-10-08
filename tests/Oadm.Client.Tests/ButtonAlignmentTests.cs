using System.Runtime.InteropServices;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Oadm.Sdk.Client.Controls;

using Xunit.Abstractions;

namespace Oadm.Client.Tests;

/// <summary>
/// Measures the rendered pixels of the shared buttons: the visual center of the text (cap height to baseline, words
/// without descenders) must sit on the center of the icon and of the button, within half a pixel.
/// </summary>
public sealed class ButtonAlignmentTests(ITestOutputHelper output)
{
    private const double Tolerance = 0.6;

    [Fact]
    public async Task Icon_and_text_are_vertically_centered_in_every_button_kind()
    {
        HeadlessUnitTestSession session = HeadlessSession.Shared;
        await session.Dispatch(() =>
        {
            var refresh = (Geometry)Application.Current!.FindResource("Icon.refresh")!;
            var secondary = new Button { Content = new IconLabel { Icon = refresh, Text = "Restart" } };
            secondary.Classes.Add("secondary");
            var primary = new Button { Content = "Save" };
            primary.Classes.Add("primary");
            var toolbar = new ToolbarButton { IconKey = "refresh", Text = "Restart" };
            var chip = new StatusChip { Text = "Started", IsOk = true };
            var panel = new StackPanel { Spacing = 24, Margin = new Thickness(24), Children = { secondary, primary, toolbar, chip } };
            foreach (Control c in panel.Children)
            {
                c.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
            }

            var window = new Window { Width = 400, Height = 400, Content = panel };
            App.ApplyCrispText(window);
            window.Show();
            Dispatcher.UIThread.RunJobs();
            WriteableBitmap frame = window.CaptureRenderedFrame()!;
            Save(frame, "button-alignment.png");

            var results = new List<(string Name, double Delta)>();
            foreach (var (name, control) in new (string, Control)[] { ("secondary", secondary), ("toolbar", toolbar), ("status", chip) })
            {
                OadmIcon icon = control.GetVisualDescendants().OfType<OadmIcon>().First(i => i.IsVisible);
                TextBlock text = control.GetVisualDescendants().OfType<TextBlock>().First(t => t.IsVisible && !string.IsNullOrEmpty(t.Text));
                double iconCenter = InkCenter(frame, Bounds(icon, window));
                double textCenter = InkCenter(frame, Bounds(text, window));
                output.WriteLine($"{name}: icon ink center {iconCenter:F1}, text ink center {textCenter:F1}, text - icon {textCenter - iconCenter:F1} px");
                results.Add((name, textCenter - iconCenter));
            }

            TextBlock saveText = primary.GetVisualDescendants().OfType<TextBlock>().First();
            Rect buttonBounds = Bounds(primary, window);
            double buttonCenter = buttonBounds.Y + (buttonBounds.Height / 2);
            double saveCenter = InkCenter(frame, Bounds(saveText, window));
            output.WriteLine($"primary: button center {buttonCenter:F1}, text ink center {saveCenter:F1}, text - button {saveCenter - buttonCenter:F1} px");
            results.Add(("primary", saveCenter - buttonCenter));

            Assert.All(results, r => Assert.True(Math.Abs(r.Delta) <= Tolerance, $"{r.Name}: text is {r.Delta:F1} px off center"));
        }, CancellationToken.None);
    }

    private static Rect Bounds(Visual visual, Window window)
    {
        Point topLeft = visual.TranslatePoint(default, window)!.Value;
        return new Rect(topLeft, visual.Bounds.Size);
    }

    /// <summary>Middle between the first and the last pixel row with ink (differs from the corner pixel) in <paramref name="area"/>.</summary>
    private static double InkCenter(WriteableBitmap frame, Rect area)
    {
        using ILockedFramebuffer fb = frame.Lock();
        int x0 = Math.Max(0, (int)Math.Floor(area.X));
        int x1 = Math.Min(fb.Size.Width - 1, (int)Math.Ceiling(area.Right));
        int y0 = Math.Max(0, (int)Math.Floor(area.Y) - 2);
        int y1 = Math.Min(fb.Size.Height - 1, (int)Math.Ceiling(area.Bottom) + 2);
        int background = Pixel(fb, x0, y0);
        int first = -1, last = -1;
        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                if (Distance(Pixel(fb, x, y), background) > 90)
                {
                    if (first < 0)
                    {
                        first = y;
                    }

                    last = y;
                    break;
                }
            }
        }

        Assert.True(first >= 0, "no ink found");
        return (first + last + 1) / 2.0;
    }

    private static int Pixel(ILockedFramebuffer fb, int x, int y) => Marshal.ReadInt32(fb.Address, (y * fb.RowBytes) + (x * 4));

    private static int Distance(int a, int b) =>
        Math.Abs((a & 0xFF) - (b & 0xFF)) + Math.Abs(((a >> 8) & 0xFF) - ((b >> 8) & 0xFF)) + Math.Abs(((a >> 16) & 0xFF) - ((b >> 16) & 0xFF));

    private static void Save(WriteableBitmap frame, string name)
    {
        string? outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        if (!string.IsNullOrEmpty(outDir))
        {
            Directory.CreateDirectory(outDir);
            frame.Save(Path.Combine(outDir, name));
        }
    }
}
