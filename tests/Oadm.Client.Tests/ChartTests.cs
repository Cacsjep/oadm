using System.Globalization;

using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

using Oadm.Sdk.Client.Controls;
using Oadm.Theme;

namespace Oadm.Client.Tests;

/// <summary>
/// The shared charts (ui:LineChart, ui:BarChart): the axis math, the point reduction, hover texts, empty text and the look
/// in both appearances. Set OADM_SCREENSHOT_DIR to write the PNGs.
/// </summary>
public sealed class ChartTests
{
    private static readonly string[] BrushNames =
    [
        "TextPrimary", "TextSecondary", "Border", "InputBorder", "SurfaceHover", "CardBackground", "MenuBackground", "WindowBackground",
        "Accent", "Teal", "StatusOk", "StatusWarning", "StatusError", "StatusNeutral",
    ];

    [Theory]
    [InlineData(0, 812, 4, 0, 1000, 250)]
    [InlineData(0, 1.2, 4, 0, 1.5, 0.5)]
    [InlineData(0, 0, 4, 0, 1, 0.25)]
    [InlineData(-3, 7, 5, -4, 8, 2)]
    [InlineData(0, 37, 4, 0, 40, 10)]
    [InlineData(0, 0.03, 3, 0, 0.03, 0.01)]
    public void Value_axes_take_round_steps(double min, double max, int ticks, double expectedMin, double expectedMax, double expectedStep)
    {
        var axis = ChartScale.Nice(min, max, ticks);
        Assert.Equal(expectedMin, axis.Min, 9);
        Assert.Equal(expectedMax, axis.Max, 9);
        Assert.Equal(expectedStep, axis.Step, 9);
        Assert.Equal(axis.Min, axis.Ticks()[0], 9);
        Assert.Equal(axis.Max, axis.Ticks()[^1], 9);
    }

    [Fact]
    public void A_fixed_maximum_stays_the_top()
    {
        var axis = ChartScale.Nice(0, 100, 4, fixedMax: true);
        Assert.Equal([0, 25, 50, 75, 100], axis.Ticks());
        var odd = ChartScale.Nice(0, 90, 4, fixedMax: true);
        Assert.Equal(90, odd.Max);
        Assert.True(odd.Ticks()[^1] <= 90);
        Assert.Equal(2, ChartScale.Decimals(0.25));
        Assert.Equal(0, ChartScale.Decimals(250));
    }

    [Fact]
    public void Numbers_times_and_values_read_plainly()
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            Assert.Equal("0.5", ChartScale.Nice(0, 1.2, 4).Label(0.5));
            Assert.Equal("1", ChartScale.Nice(0, 1.2, 4).Label(1));
            Assert.Equal("1,000", ChartScale.Nice(0, 812, 4).Label(1000));
            Assert.Equal("812 Mbit/s", ChartScale.FormatValue(812.4, "Mbit/s"));
            Assert.Equal("81.2 Mbit/s", ChartScale.FormatValue(81.24, "Mbit/s"));
            Assert.Equal("0.4 %", ChartScale.FormatValue(0.4, "%"));
            Assert.Equal("1.25 ms", ChartScale.FormatValue(1.25, "ms"));
            Assert.Equal("3", ChartScale.FormatValue(3, null));
            Assert.Equal("No value", ChartScale.FormatValue(double.NaN, "%"));
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }

        Assert.Equal("0:00", ChartScale.FormatTime(TimeSpan.Zero));
        Assert.Equal("2:30", ChartScale.FormatTime(TimeSpan.FromSeconds(150)));
        Assert.Equal("1:05:00", ChartScale.FormatTime(TimeSpan.FromSeconds(3900)));
    }

    [Theory]
    [InlineData(300, 1, 6, 60)]
    [InlineData(300, 1, 11, 30)]
    [InlineData(3600, 1, 10, 600)]
    [InlineData(10, 1, 20, 1)]
    [InlineData(600, 2, 6, 150)]
    public void Time_labels_fall_on_round_times(int count, double stepSeconds, int maxLabels, int expectedEvery)
    {
        var every = ChartScale.TimeLabelEvery(count, TimeSpan.FromSeconds(stepSeconds), maxLabels);
        Assert.Equal(expectedEvery, every);
        Assert.True((count - 1) / every + 1 <= maxLabels);
    }

    [Fact]
    public void Long_series_are_reduced_to_the_pixel_columns_and_keep_their_peaks()
    {
        var values = Enumerable.Range(0, 3600).Select(i => i == 1234 ? 1000.0 : i % 7).ToArray();
        var plot = new Rect(0, 0, 300, 100);
        var points = new List<Point>();
        var figures = 0;
        var count = ChartScale.Reduce(values, values.Length, plot, v => 100 - v / 10, (p, start) =>
        {
            points.Add(p);
            figures += start ? 1 : 0;
        });

        Assert.Equal(points.Count, count);
        Assert.True(count <= 2 * 301, $"{count} points for 300 columns");
        Assert.Equal(1, figures);
        Assert.Contains(points, p => Math.Abs(p.Y) < 1e-9); // the spike to 1000 is kept
        Assert.Equal(points.OrderBy(p => p.X).Select(p => p.X), points.Select(p => p.X)); // left to right
    }

    [Fact]
    public void Gaps_break_the_line_and_short_series_keep_every_point()
    {
        double[] values = [1, 2, double.NaN, double.NaN, 3, 4, 5, double.NaN, 6];
        var figures = 0;
        var count = ChartScale.Reduce(values, values.Length, new Rect(0, 0, 400, 100), v => v, (_, start) => figures += start ? 1 : 0);
        Assert.Equal(6, count);
        Assert.Equal(3, figures);

        var reduced = 0;
        var gapped = Enumerable.Range(0, 5000).Select(i => i is >= 2000 and < 2100 ? double.NaN : i).ToArray();
        ChartScale.Reduce(gapped, gapped.Length, new Rect(0, 0, 200, 100), v => v, (_, start) => reduced += start ? 1 : 0);
        Assert.Equal(2, reduced);
    }

    [Fact]
    public async Task Line_chart_renders_phases_threshold_and_hover_in_both_appearances()
    {
        var outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        var done = await HeadlessSession.Shared.Dispatch(() =>
        {
            var culture = CultureInfo.CurrentCulture;
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            try
            {
                foreach (var light in new[] { false, true })
                {
                    var name = light ? "light" : "dark";
                    var rate = new LineChart { Series = [new ChartSeries("Received", RateValues())], Unit = "Mbit/s", Bands = Phases(), Height = 220 };
                    var loss = new LineChart
                    {
                        Series = [new ChartSeries("Average", LossValues(0)), new ChartSeries("Worst camera", LossValues(1), ChartColor.Warning)],
                        Unit = "%",
                        Threshold = 1,
                        Bands = Phases(),
                        Height = 200,
                    };
                    var (window, _) = Show(light, 900, 520, rate, loss);

                    Assert.Equal(0, rate.LastAxis!.Value.Min);
                    Assert.True(rate.LastAxis.Value.Max >= RateValues().Where(double.IsFinite).Max());
                    Assert.Contains("0:00", rate.LastTimeLabels);
                    Assert.Contains("1:00", rate.LastTimeLabels);
                    Assert.True(rate.LastPointCount > 0);
                    Assert.True(loss.LastAxis!.Value.Max >= 1); // the threshold is on the axis
                    Assert.Equal(["Average", "Worst camera"], loss.LastLegend);
                    Assert.Empty(rate.LastLegend); // one line: no legend
                    Assert.Equal(4, rate.LastBandEdges);
                    Assert.Equal(["25 %", "50 %", "75 %", "Normal", "Low light"], rate.LastBandLabels.Select(l => l.Text));
                    Save(window, outDir, $"line-{name}.png");

                    // Hover: the time with its phase and each line's value with the unit.
                    window.MouseMove(loss.TranslatePoint(new Point(loss.Bounds.Width * 0.62, loss.Bounds.Height / 2), window)!.Value, RawInputModifiers.None);
                    Pump();
                    Assert.Matches(@"^(25 %|50 %|75 %|Normal|Low light), \d+:\d\d$", loss.LastHoverLines[0]);
                    Assert.Contains(loss.LastHoverLines, l => l.StartsWith("Average: ", StringComparison.Ordinal) && l.EndsWith(" %", StringComparison.Ordinal));
                    Assert.Contains(loss.LastHoverLines, l => l.StartsWith("Worst camera: ", StringComparison.Ordinal));
                    Assert.Empty(rate.LastHoverLines);
                    Save(window, outDir, $"line-hover-{name}.png");

                    window.MouseMove(new Point(2, 2), RawInputModifiers.None); // off the charts
                    Pump();
                    Assert.Empty(loss.LastHoverLines);
                    window.Close();
                }
            }
            finally
            {
                CultureInfo.CurrentCulture = culture;
            }

            return Task.FromResult(true);
        }, CancellationToken.None);
        Assert.True(done);
    }

    [Fact]
    public async Task Many_narrow_phases_keep_their_edges_and_thin_out_their_labels()
    {
        var outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        var done = await HeadlessSession.Shared.Dispatch(() =>
        {
            // 3 batches x 5 phases of 20 values on a chart about 340 px wide (the full run on the Load test page).
            string[] names = ["25 %", "50 %", "75 %", "Normal", "Low light"];
            var bands = Enumerable.Range(0, 15).Select(i => new ChartBand(i * 20, i * 20 + 20, names[i % 5])).ToArray();
            var values = Enumerable.Range(0, 300).Select(i => 100.0 * (i % 100 / 20 + 1) + Math.Sin(i * 0.7) * 8).ToArray();
            var chart = new LineChart { Series = [new ChartSeries("Received", values)], Unit = "Mbit/s", Bands = bands, Height = 150 };
            var (window, _) = Show(false, 412, 230, chart);
            Assert.InRange(chart.Bounds.Width, 300, 380);

            Assert.Equal(14, chart.LastBandEdges); // every phase start, labelled or not
            Assert.InRange(chart.LastBandLabels.Count, 2, 14);
            Assert.Equal("25 %", chart.LastBandLabels[0].Text);
            for (var i = 1; i < chart.LastBandLabels.Count; i++)
            {
                Assert.True(chart.LastBandLabels[i].Box.Left > chart.LastBandLabels[i - 1].Box.Right, "labels never touch");
            }

            Save(window, outDir, "line-narrow-phases-dark.png");

            // The hover title keeps the phase name, also where its label is left out.
            window.MouseMove(chart.TranslatePoint(new Point(chart.Bounds.Width * 0.55, chart.Bounds.Height / 2), window)!.Value, RawInputModifiers.None);
            Pump();
            Assert.Matches(@"^(25 %|50 %|75 %|Normal|Low light), \d+:\d\d$", chart.LastHoverLines[0]);
            window.Close();
            return Task.FromResult(true);
        }, CancellationToken.None);
        Assert.True(done);
    }

    [Fact]
    public async Task Line_chart_hover_over_a_gap_says_there_is_no_value()
    {
        var done = await HeadlessSession.Shared.Dispatch(() =>
        {
            var values = Enumerable.Repeat(double.NaN, 100).ToArray();
            var chart = new LineChart { Series = [new ChartSeries("Jitter", values)], Unit = "ms", Height = 200 };
            var (window, _) = Show(false, 600, 260, chart);
            window.MouseMove(chart.TranslatePoint(new Point(chart.Bounds.Width / 2, chart.Bounds.Height / 2), window)!.Value, RawInputModifiers.None);
            Pump();
            Assert.Equal(2, chart.LastHoverLines.Count);
            Assert.Equal("No value", chart.LastHoverLines[1]);
            Assert.Equal(0, chart.LastPointCount);
            window.Close();
            return Task.FromResult(true);
        }, CancellationToken.None);
        Assert.True(done);
    }

    [Fact]
    public async Task Charts_without_values_show_their_empty_text_and_name_it_for_screen_readers()
    {
        var outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        var done = await HeadlessSession.Shared.Dispatch(() =>
        {
            var line = new LineChart { Series = [], EmptyText = "No values yet", Unit = "Mbit/s", Height = 160 };
            var bar = new BarChart { EmptyText = "No ramp yet", Height = 160 };
            var (window, _) = Show(false, 600, 400, line, bar);
            Assert.Null(line.LastAxis);
            Assert.Null(bar.LastAxis);
            Assert.Equal("No values yet", ControlAutomationPeer.CreatePeerForElement(line).GetName());
            Assert.Equal("No ramp yet", ControlAutomationPeer.CreatePeerForElement(bar).GetName());
            Assert.Equal(AutomationControlType.Image, ControlAutomationPeer.CreatePeerForElement(line).GetAutomationControlType());
            Save(window, outDir, "charts-empty-dark.png");

            // A new list redraws: the values arrive.
            line.Series = [new ChartSeries("Received", [10, 20, 30])];
            bar.Bars = [new ChartBar("25 %", 0.1)];
            Pump();
            Assert.NotNull(line.LastAxis);
            Assert.Equal(3, line.LastPointCount);
            Assert.NotNull(bar.LastAxis);

            // The page's own name wins over the summary.
            AutomationProperties.SetName(line, "Received rate");
            Assert.Equal("Received rate", ControlAutomationPeer.CreatePeerForElement(line).GetName());
            Assert.StartsWith("Bar chart: 25 % 0.1", ControlAutomationPeer.CreatePeerForElement(bar).GetName(), StringComparison.Ordinal);
            window.Close();
            return Task.FromResult(true);
        }, CancellationToken.None);
        Assert.True(done);
    }

    [Fact]
    public async Task Many_lines_and_long_lines_stay_bounded()
    {
        var outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        var done = await HeadlessSession.Shared.Dispatch(() =>
        {
            // 200 camera streams x 300 s: one line each, every value (fewer values than pixels).
            var cameras = Enumerable.Range(0, 200)
                .Select(c => new ChartSeries($"Camera {c + 1}", Enumerable.Range(0, 300).Select(i => 4 + c % 9 + Math.Sin((i + c * 13) * 0.21) * 1.5).ToArray()))
                .ToArray();
            var many = new LineChart { Series = cameras, Unit = "Mbit/s", Height = 220 };

            // One series of an hour: reduced to the pixel columns.
            var hour = new LineChart { Series = [new ChartSeries("Received", Enumerable.Range(0, 3600).Select(i => 800 + Math.Sin(i * 0.9) * 40).ToArray())], Unit = "Mbit/s", Height = 200 };
            var (window, _) = Show(false, 700, 500, many, hour);

            Assert.True(many.LastPointCount <= 200 * 300);
            Assert.True(many.LastPointCount > 0);
            Assert.True(hour.LastPointCount <= 2 * ((int)hour.Bounds.Width + 2), $"{hour.LastPointCount} points at {hour.Bounds.Width:0} px");
            Assert.Empty(many.LastLegend); // a line per camera: the hover names them
            Assert.Contains("10:00", hour.LastTimeLabels);

            // Hover over 200 lines lists the highest few and counts the rest.
            window.MouseMove(many.TranslatePoint(new Point(many.Bounds.Width / 2, many.Bounds.Height / 2), window)!.Value, RawInputModifiers.None);
            Pump();
            Assert.Equal(1 + LineChart.MaxHoverSeries + 1, many.LastHoverLines.Count);
            Assert.EndsWith(" more", many.LastHoverLines[^1], StringComparison.Ordinal);
            Assert.Equal("Line chart, 200 lines", ControlAutomationPeer.CreatePeerForElement(many).GetName());
            Save(window, outDir, "line-many-dark.png");
            window.Close();
            return Task.FromResult(true);
        }, CancellationToken.None);
        Assert.True(done);
    }

    [Fact]
    public async Task Bar_chart_renders_steps_threshold_and_hover_in_both_appearances()
    {
        var outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        var done = await HeadlessSession.Shared.Dispatch(() =>
        {
            var culture = CultureInfo.CurrentCulture;
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            try
            {
                foreach (var light in new[] { false, true })
                {
                    ChartBar[] steps = [new("25 %", 0.05, ChartColor.Ok), new("50 %", 0.18, ChartColor.Ok), new("75 %", 0.62, ChartColor.Ok),
                        new("Normal", 1.4, ChartColor.Error), new("Low light", 2.1, ChartColor.Error)];
                    var chart = new BarChart { Bars = steps, Unit = "%", Threshold = 1, Height = 220 };
                    var (window, _) = Show(light, 600, 280, chart);
                    Assert.Equal(steps.Select(s => s.Label), chart.LastLabels);
                    Assert.True(chart.LastAxis!.Value.Max >= 2.1);
                    Save(window, outDir, $"bar-{(light ? "light" : "dark")}.png");

                    window.MouseMove(chart.TranslatePoint(new Point(chart.Bounds.Width * 0.7, chart.Bounds.Height / 2), window)!.Value, RawInputModifiers.None);
                    Pump();
                    var hovered = Assert.Single(steps, s => s.Label == chart.LastHoverLines[0]);
                    Assert.Equal(ChartScale.FormatValue(hovered.Value, "%"), chart.LastHoverLines[1]);
                    Save(window, outDir, $"bar-hover-{(light ? "light" : "dark")}.png");
                    window.Close();
                }
            }
            finally
            {
                CultureInfo.CurrentCulture = culture;
            }

            return Task.FromResult(true);
        }, CancellationToken.None);
        Assert.True(done);
    }

    [Fact]
    public async Task Bar_labels_thin_out_when_they_would_touch()
    {
        var done = await HeadlessSession.Shared.Dispatch(() =>
        {
            var bars = Enumerable.Range(1, 40).Select(i => new ChartBar($"Step {i}", i)).ToArray();
            var chart = new BarChart { Bars = bars, Height = 200 };
            var (window, _) = Show(false, 360, 260, chart);
            Assert.InRange(chart.LastLabels.Count, 1, 39);
            Assert.Equal("Step 1", chart.LastLabels[0]);
            window.Close();
            return Task.FromResult(true);
        }, CancellationToken.None);
        Assert.True(done);
    }

    private static ChartBand[] Phases() => [new(0, 60, "25 %"), new(60, 120, "50 %"), new(120, 180, "75 %"), new(180, 240, "Normal"), new(240, 300, "Low light")];

    private static double[] RateValues() => Enumerable.Range(0, 300).Select(i =>
    {
        var level = new[] { 210.0, 420, 630, 840, 905 }[i / 60];
        return i is >= 60 and < 63 ? double.NaN : level + Math.Sin(i * 0.7) * 14 + Math.Sin(i * 0.13) * 9;
    }).ToArray();

    private static double[] LossValues(int worst) => Enumerable.Range(0, 300).Select(i =>
    {
        var phase = i / 60;
        var basis = new[] { 0.02, 0.05, 0.12, 0.35, 0.6 }[phase] * (worst == 1 ? 2.6 : 1);
        return Math.Max(0, basis + Math.Abs(Math.Sin(i * 0.5 + worst)) * basis * 0.6);
    }).ToArray();

    /// <summary>The charts on a card in a window, dark or light (the light colours put on the window over the theme).</summary>
    private static (Window Window, Border Card) Show(bool light, double width, double height, params Control[] charts)
    {
        var panel = new StackPanel { Spacing = 24 };
        foreach (var chart in charts)
        {
            panel.Children.Add(chart);
        }

        var card = new Border { Padding = new Thickness(20), Margin = new Thickness(16), CornerRadius = new CornerRadius(8), Child = panel };
        var window = new Window { Width = width, Height = height, Content = card };
        if (light)
        {
            var colors = OadmColors.Load(Appearance.Light);
            foreach (var name in BrushNames)
            {
                window.Resources[$"Oadm.{name}Brush"] = new SolidColorBrush((Color)colors[$"Oadm.{name}Color"]!);
            }

            window.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
        }

        window.Background = (IBrush)window.FindResource("Oadm.WindowBackgroundBrush")!;
        card.Background = (IBrush)window.FindResource("Oadm.CardBackgroundBrush")!;
        window.Show();
        Pump();
        return (window, card);
    }

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Save(Window window, string? outDir, string name)
    {
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        if (!string.IsNullOrEmpty(outDir))
        {
            Directory.CreateDirectory(outDir);
            frame.Save(Path.Combine(outDir, name), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }
    }
}
