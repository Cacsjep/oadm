using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Oadm.Plugins.MetadataMonitor.Client;
using Oadm.Plugins.MetadataMonitor.Parsing;
using Oadm.Sdk.Devices;

namespace Oadm.Plugins.MetadataMonitor.Tests;

public sealed class HeadlessTestApp : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new StyleInclude(new Uri("avares://Oadm.Plugins.MetadataMonitor.Tests/")) { Source = new Uri("avares://Oadm.Client/Themes/OadmTheme.axaml") });
    }
}

public static class HeadlessEntry
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<HeadlessTestApp>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        .WithInterFont();
}

/// <summary>Renders the page offscreen like the client shell (page title above the host card). OADM_SCREENSHOT_DIR writes PNGs.</summary>
public sealed class HeadlessPageTests
{
    [Fact]
    public async Task Page_renders_live_list_with_detail_and_an_error()
    {
        var outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        await using var rig = await new Rig().StartAsync();
        var ctx = new PluginPageContext(rig.Plugin, rig.Hub);
        ctx.DeviceList.Add(rig.Camera);
        ctx.DeviceList.Add(new TestDevice("10.0.0.60", "M3106-L Mk II"));
        var settings = new MetadataClientSettingsStore(Path.Combine(Path.GetTempPath(), "oadm-mm-tests", Guid.NewGuid().ToString("N"), "client.json"));

        var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessEntry));
        var result = await session.Dispatch(() =>
        {
            var view = new MetadataMonitorView();
            using var vm = new MetadataMonitorViewModel(ctx, settings);
            var window = Host(view);
            window.Show();
            view.DataContext = vm; // after attaching: no live watch, deterministic content
            Pump();

            vm.SelectedCamera = vm.Cameras.First(c => c.Id == rig.Camera.Id);
            vm.UseStreamForTests("s1"); // no RTSP: the recorded messages arrive as from a live stream
            var seq = 0L;
            var capture = new DateTimeOffset(2026, 10, 8, 9, 30, 1, TimeSpan.FromHours(0));
            var recorded = RecordedEvents.Documents.SelectMany(MetadataParser.Parse).Select(m => m.WithSeq(++seq, capture)).ToList();
            vm.ApplyMessages(recorded);
            var changes = new List<MetadataMessage>();
            for (var i = 0; i < 6; i++)
            {
                var xml = RecordedEvents.Notification("tns1:Device/tnsaxis:IO/VirtualInput", "Changed", 5, (i + 1) % 2, new DateTime(2026, 10, 8, 9, 31, 2 + i, DateTimeKind.Utc));
                changes.Add(MetadataParser.Parse(xml)[0].WithSeq(++seq, capture.AddSeconds(62 + i)));
            }

            vm.ApplyMessages(changes);
            vm.HandleEvent(State("s1", MonitorStates.Live, null, seq, 0));
            Pump();
            vm.SelectedMessage = vm.Messages[^2];
            Pump();
            var bar = view.GetVisualDescendants().OfType<ScrollBar>().First(b => b.Orientation == Avalonia.Layout.Orientation.Vertical && b.IsVisible);
            var atEnd = bar.Value >= bar.Maximum - 2;
            Capture(window, outDir, "metadata-monitor-page.png");

            // Autoscroll off (the user scrolled up): a new batch must not move the list.
            vm.Autoscroll = false;
            var grid = view.FindControl<DataGrid>("MessageGrid")!;
            grid.ScrollIntoView(vm.Messages[20], null);
            Pump();
            var before = bar.Value;
            vm.ApplyMessages([MetadataParser.Parse(RecordedEvents.Notification("tns1:Device/tnsaxis:IO/VirtualInput", "Changed", 6, 1))[0].WithSeq(++seq, capture)]);
            vm.ApplyMessages(Enumerable.Range(0, 20).Select(i => MetadataParser.Parse(RecordedEvents.Notification("tns1:Device/tnsaxis:IO/VirtualInput", "Changed", 7, i % 2))[0].WithSeq(++seq, capture)).ToList());
            Pump();
            var after = bar.Value;
            var selectionKept = vm.SelectedMessage is not null && ReferenceEquals(grid.SelectedItem, vm.SelectedMessage);

            vm.HandleEvent(State("s1", MonitorStates.Error, "Unauthorized - HTTP 401", seq, 0));
            vm.FilterText = "VirtualInput";
            Pump();
            Capture(window, outDir, "metadata-monitor-page-error.png");
            window.Close();
            return (atEnd, before, after, selectionKept);
        }, CancellationToken.None);

        GC.KeepAlive(session); // not disposed: Avalonia's headless Dispose can throw a NullReferenceException on CI
        Assert.True(result.atEnd, "autoscroll keeps the newest row visible");
        Assert.True(Math.Abs(result.after - result.before) < 1, $"scroll position kept without autoscroll ({result.before} -> {result.after})");
        Assert.True(result.selectionKept, "the grid keeps the selected row through batches");
    }

    private static Oadm.Sdk.Plugins.PluginEvent State(string streamId, string state, string? text, long messages, long lost) =>
        new(MetadataMethods.StateTopic, MetadataJson.Serialize(new MonitorState(streamId, Guid.Empty, state, text, messages, lost)));

    private static Window Host(Control view)
    {
        // The real host page (title, ui:PageHeader subtitle, card), like in the client.
        var page = new Oadm.Client.Shell.CorePluginPageView
        {
            DataContext = new Oadm.Client.Shell.CorePluginPageViewModel(MetadataMonitorPluginInfo.PluginId, MetadataMonitorPluginInfo.DisplayName, view),
        };
        var window = new Window { Width = 1280, Height = 860, Content = new Border { Padding = new Thickness(16), Child = page } };
        Oadm.Client.App.ApplyCrispText(window);
        return window;
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
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        if (string.IsNullOrEmpty(outDir))
        {
            frame.Dispose();
            return;
        }

        Directory.CreateDirectory(outDir);
        using (frame)
        {
            frame.Save(Path.Combine(outDir, name), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }
    }
}
