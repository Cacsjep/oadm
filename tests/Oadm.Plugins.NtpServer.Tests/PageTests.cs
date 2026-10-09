using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Threading;

using Oadm.Core.Plugins;
using Oadm.Plugins.NtpServer.Client;
using Oadm.Sdk.Network;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.NtpServer.Tests;

/// <summary>The page view model against the in-process plugin and the real event hub.</summary>
public sealed class PageViewModelTests : IAsyncLifetime
{
    private readonly PluginEventHub _hub = new();
    private NtpServerPlugin _plugin = null!;
    private PluginPageContext _ctx = null!;

    public async Task InitializeAsync()
    {
        _plugin = new NtpServerPlugin(Options.Test());
        await _plugin.StartAsync(new TestCoreContext(new InMemoryPluginSettingsProvider().GetSettings(NtpServerPluginInfo.PluginId), _hub.For(NtpServerPluginInfo.PluginId)), CancellationToken.None);
        _ctx = new PluginPageContext(_plugin, _hub);
    }

    public async Task DisposeAsync() => await _plugin.DisposeAsync();

    [Fact]
    public async Task Activate_reads_state_and_interfaces_and_fills_the_form()
    {
        using var vm = new NtpServerViewModel(_ctx);
        vm.Activate();
        await Wait.UntilAsync(() => vm.Interfaces.Count > 0);

        Assert.Equal(["All interfaces", "Loopback - 127.0.0.1 (Software Loopback Interface)", "Ethernet - 192.0.2.17 (Intel(R) Ethernet Connection I219-LM)"], vm.Interfaces.Select(i => i.Label));
        Assert.Equal("all", vm.SelectedInterface!.Id);
        Assert.False(vm.IsEnabled);
        Assert.Equal("Stopped", vm.StatusText);
        Assert.False(vm.IsStatusOk || vm.IsStatusError || vm.IsStatusWarning);
        Assert.False(vm.HasRequests);
    }

    [Fact]
    public async Task Save_starts_the_server_and_requests_arrive_live_with_the_device_name()
    {
        _ctx.DeviceList.Add(new TestDevice("127.0.0.1", "P3265-V"));
        using var vm = new NtpServerViewModel(_ctx);
        vm.Activate();
        await Wait.UntilAsync(() => vm.Interfaces.Count > 0);
        await Wait.UntilAsync(() => _hub.WatcherCount(NtpServerPluginInfo.PluginId) == 1);

        vm.IsEnabled = true;
        vm.SelectedInterface = vm.Interfaces.Single(i => i.Id == "lo-id");
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.True(vm.IsStatusOk);
        Assert.StartsWith("Running on 127.0.0.1:", vm.StatusText, StringComparison.Ordinal);

        await NtpProbe.QueryAsync(_plugin.Service!.Endpoints[0], TimeSpan.FromSeconds(1));
        await Wait.UntilAsync(() => vm.Requests.Count == 1);
        var row = vm.Requests[0];
        Assert.Equal("127.0.0.1", row.Client);
        Assert.Equal("P3265-V (127.0.0.1)", row.Device);
        Assert.Equal("Answered", row.Result);
        Assert.True(row.IsAnswered);
        Assert.EndsWith(" ms", row.Offset, StringComparison.Ordinal);

        // Device changes re-resolve the column.
        _ctx.DeviceList.Clear();
        _ctx.RaiseDevicesChanged();
        Assert.Equal("-", vm.Requests[0].Device);
        vm.Deactivate();
        await Wait.UntilAsync(() => _hub.WatcherCount(NtpServerPluginInfo.PluginId) == 0);
    }

    [Fact]
    public void Live_requests_are_newest_first_capped_at_40_and_deduplicated()
    {
        using var vm = new NtpServerViewModel(_ctx);
        var start = new DateTime(2026, 10, 7, 16, 0, 0, DateTimeKind.Utc);
        var batch = Enumerable.Range(1, 50).Select(i => new RequestEntry(i, start.AddSeconds(i), "10.0.0." + i, i, RequestEntry.Answered)).ToList();
        vm.HandleEvent(new PluginEvent(NtpServerMethods.RequestsTopic, NtpJson.Serialize(new RequestsEvent(batch))));
        vm.HandleEvent(new PluginEvent(NtpServerMethods.RequestsTopic, NtpJson.Serialize(new RequestsEvent(batch[^3..]))));
        vm.HandleEvent(new PluginEvent(NtpServerMethods.RequestsTopic, NtpJson.Serialize(new RequestsEvent([new RequestEntry(51, start, "10.0.0.99", null, RequestEntry.RateLimited)]))));

        Assert.Equal(40, vm.Requests.Count);
        Assert.Equal(51, vm.Requests[0].Seq);
        Assert.Equal("-", vm.Requests[0].Offset);
        Assert.True(vm.Requests[0].IsLimited);
        Assert.Equal(50, vm.Requests[1].Seq);
        Assert.Equal(12, vm.Requests[^1].Seq);
    }

    [Fact]
    [Trait("Category", "Timing")] // 300 ms query timeout
    public async Task Upstream_field_errors_stay_under_the_field_and_block_saving()
    {
        var calls = 0;
        _ctx.Intercept = (method, payload) =>
        {
            calls++;
            return _plugin.InvokeAsync(method, payload, CancellationToken.None);
        };
        using var vm = new NtpServerViewModel(_ctx);
        await vm.LoadAsync();
        calls = 0;

        vm.Upstream = "two hosts";
        Assert.True(vm.HasErrors);
        Assert.Equal("Enter one host name or IP address.", Assert.Single(vm.GetErrors(nameof(vm.Upstream)).Cast<string>()));
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal(0, calls);

        await using var dead = new FakeUpstream(UpstreamBehavior.NoAnswer);
        vm.Upstream = dead.HostText;
        Assert.False(vm.HasErrors);
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal("127.0.0.1: No answer within 300 ms.", Assert.Single(vm.GetErrors(nameof(vm.Upstream)).Cast<string>()));

        await using var good = new FakeUpstream();
        vm.Upstream = good.HostText;
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.False(vm.HasErrors);
        Assert.Matches(@"^Answered: .+ off, \d+ ms round trip$", vm.UpstreamResult);
        Assert.True(vm.HasUpstreamResult);
    }

    [Fact]
    public async Task Saving_shows_the_check_without_blocking_and_disables_save()
    {
        var release = new TaskCompletionSource<string?>();
        using var vm = new NtpServerViewModel(_ctx);
        await vm.LoadAsync();
        _ctx.Intercept = (_, _) => release.Task;
        vm.Upstream = "pool.ntp.org";

        var saving = vm.SaveCommand.ExecuteAsync(null);
        Assert.True(vm.IsSaving);
        Assert.True(vm.IsCheckingUpstream);
        Assert.Equal("Checking pool.ntp.org", vm.CheckingText);
        Assert.False(vm.SaveCommand.CanExecute(null));

        var state = _plugin.Service!.GetState(false);
        release.SetResult(NtpJson.Serialize(new SaveReply(false, "pool.ntp.org: No answer within 2 s.", null, state)));
        await saving;
        Assert.False(vm.IsSaving);
        Assert.True(vm.SaveCommand.CanExecute(null));
        Assert.Equal("pool.ntp.org: No answer within 2 s.", Assert.Single(vm.GetErrors(nameof(vm.Upstream)).Cast<string>()));
    }

    [Fact]
    public async Task A_host_without_live_events_is_polled()
    {
        var reads = 0;
        var polling = new PollingContext(_plugin, () => reads++);
        NtpServerViewModel.ReconnectDelay = TimeSpan.FromMilliseconds(50);
        try
        {
            using var vm = new NtpServerViewModel(polling);
            vm.Activate();
            await Wait.UntilAsync(() => reads >= 3);
        }
        finally
        {
            NtpServerViewModel.ReconnectDelay = TimeSpan.FromSeconds(2);
        }
    }

    /// <summary>A host without WatchEventsAsync (SDK default: empty sequence).</summary>
    private sealed class PollingContext(NtpServerPlugin plugin, Action onRead) : Oadm.Sdk.Client.ICorePluginClientContext
    {
        public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct)
        {
            onRead();
            return plugin.InvokeAsync(method, payloadJson, ct);
        }
    }
}

public sealed class HeadlessTestApp : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new StyleInclude(new Uri("avares://Oadm.Plugins.NtpServer.Tests/")) { Source = new Uri("avares://Oadm.Client/Themes/OadmTheme.axaml") });
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
    [Trait("Category", "Timing")] // 200 ms answers, real-time refill
    public async Task Page_renders_running_with_requests_and_with_errors()
    {
        var outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        var hub = new PluginEventHub();
        await using var plugin = new NtpServerPlugin(Options.Test());
        await plugin.StartAsync(new TestCoreContext(new InMemoryPluginSettingsProvider().GetSettings(NtpServerPluginInfo.PluginId), hub.For(NtpServerPluginInfo.PluginId)), CancellationToken.None);
        await plugin.InvokeAsync(NtpServerMethods.Save, NtpJson.Serialize(new SaveRequest(true, "lo-id", null)), CancellationToken.None);
        for (var i = 0; i < 10; i++)
        {
            await NtpProbe.QueryAsync(plugin.Service!.Endpoints[0], TimeSpan.FromMilliseconds(200));
        }

        var ctx = new PluginPageContext(plugin, hub);
        ctx.DeviceList.Add(new TestDevice("127.0.0.1", "P3265-V"));

        var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessEntry));
        var rows = await session.Dispatch(async () =>
        {
            var view = new NtpServerView();
            using var vm = new NtpServerViewModel(ctx);
            var window = Host(view);
            window.Show();
            view.DataContext = vm; // after attaching: no live watch, deterministic content
            await vm.LoadAsync();
            Pump();
            Capture(window, outDir, "ntp-server-page.png");

            vm.Upstream = "two hosts";
            vm.HandleEvent(new PluginEvent(NtpServerMethods.StateTopic, NtpJson.Serialize(new NtpState
            {
                Config = new NtpConfig { Enabled = true, InterfaceId = "lo-id" },
                Status = Status.NtpStatusTexts.ForBindError(System.Net.Sockets.SocketError.AddressAlreadyInUse, 123, "Ethernet", HostOs.Windows, windowsTimeRunning: true),
            })));
            Pump();
            Capture(window, outDir, "ntp-server-page-errors.png");

            vm.Upstream = "pool.ntp.org";
            vm.HandleEvent(new PluginEvent(NtpServerMethods.StateTopic, NtpJson.Serialize(new NtpState
            {
                Config = new NtpConfig { Enabled = true, InterfaceId = "lo-id", Upstream = "pool.ntp.org" },
                Status = Status.NtpStatusTexts.UpstreamNotReachable("pool.ntp.org", "No answer within 2 s"),
                Upstream = new UpstreamInfo("pool.ntp.org", false, 2, 1.5, DateTime.UtcNow.AddMinutes(-5), "No answer within 2 s"),
                Stratum = 10,
            })));
            Pump();
            Capture(window, outDir, "ntp-server-page-upstream-warning.png");

            vm.IsSaving = true; // Save with an upstream: the check runs, the page stays usable
            Pump();
            Capture(window, outDir, "ntp-server-page-checking.png");
            vm.IsSaving = false;

            var count = vm.Requests.Count;
            window.Close();
            return count;
        }, CancellationToken.None);

        // The awaited dispatch may continue on the session's UI thread; Dispose waits for that thread, so dispose elsewhere.
        GC.KeepAlive(session); // not disposed: Avalonia's headless Dispose can throw a NullReferenceException on CI
        Assert.Equal(9, rows); // 8 answered, then one "Rate limited" entry for the same client
    }

    private static Window Host(Control view)
    {
        // The real host page (title, ui:PageHeader subtitle and status, card), like in the client.
        var page = new Oadm.Client.Shell.CorePluginPageView
        {
            DataContext = new Oadm.Client.Shell.CorePluginPageViewModel("oadm.ntp-server", "NTP server", view),
        };
        var window = new Window { Width = 1280, Height = 860, Content = new Border { Padding = new Thickness(16), Child = page } };
        Oadm.Client.App.ApplyCrispText(window); // same text rendering as the real app windows
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
            frame.Save(Path.Combine(outDir, name));
        }
    }
}
