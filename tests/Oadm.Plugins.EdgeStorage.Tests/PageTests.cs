using System.Diagnostics;
using System.Runtime.CompilerServices;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Threading;

using Oadm.Core.Plugins;
using Oadm.Plugins.EdgeStorage.Client;
using Oadm.Plugins.Shared;
using Oadm.Sdk.Client;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

using Xunit.Abstractions;

namespace Oadm.Plugins.EdgeStorage.Tests;

public sealed class HeadlessTestApp : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new StyleInclude(new Uri("avares://Oadm.Plugins.EdgeStorage.Tests/")) { Source = new Uri("avares://Oadm.Client/Themes/OadmTheme.axaml") });
    }
}

public static class HeadlessEntry
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<HeadlessTestApp>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        .WithInterFont();
}

/// <summary>The page context against the plugin in process (invoke + the event hub), with the role's rights.</summary>
internal sealed class PageContext(EdgeStoragePlugin plugin, PluginEventHub hub) : ICorePluginClientContext
{
    public HashSet<string> Rights { get; } = ["oadm.edge-storage:show", "oadm.edge-storage:manage", "oadm.edge-storage:format"];

    public List<string> Confirmations { get; } = [];

    public bool Confirm { get; set; } = true;

    public IReadOnlyList<IDeviceInfo> Devices => [];

    public string OwnerName => "joe@pc";

    public event EventHandler? DevicesChanged
    {
        add { }
        remove { }
    }

    public event EventHandler? PermissionsChanged;

    public void RaisePermissionsChanged() => PermissionsChanged?.Invoke(this, EventArgs.Empty);

    public bool Has(string permission) => Rights.Contains(permission);

    public Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        Confirmations.Add($"{title}: {message} [{confirmText}]");
        return Task.FromResult(Confirm);
    }

    public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) => plugin.InvokeAsync(method, payloadJson, ct);

    public async IAsyncEnumerable<PluginEvent> WatchEventsAsync([EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var item in hub.WatchAsync(EdgeStorageInfo.PluginId, ct))
        {
            yield return item;
        }
    }
}

internal sealed class AnsweringDialogs : IStorageDialogs
{
    public int? Retention { get; set; } = 30;

    public string? Passphrase { get; set; } = "a passphrase";

    public Task<int?> AskRetentionAsync(int? current, int cameras) => Task.FromResult(Retention);

    public Task<string?> AskPassphraseAsync(int cameras) => Task.FromResult(Passphrase);
}

/// <summary>Plugin with the fake cameras and the event hub, the page view model on top.</summary>
internal sealed class PageRig : IAsyncDisposable
{
    public PageRig()
    {
        Plugin = new EdgeStoragePlugin(new DeviceSweepOptions { PublishInterval = TimeSpan.FromMilliseconds(20) });
        Plugin.StartAsync(new FakeContext(Devices, Cameras, Runner, new RecordingEvents()) { Hub = Hub.For(EdgeStorageInfo.PluginId) }, CancellationToken.None).GetAwaiter().GetResult();
        Context = new PageContext(Plugin, Hub);
        ViewModel = new EdgeStorageViewModel(Context) { Dialogs = Dialogs };
    }

    public PluginEventHub Hub { get; } = new();

    public FakeDevices Devices { get; } = new();

    public FakeCameras Cameras { get; } = new();

    public RecordingRunner Runner { get; } = new();

    public AnsweringDialogs Dialogs { get; } = new();

    public EdgeStoragePlugin Plugin { get; }

    public PageContext Context { get; }

    public EdgeStorageViewModel ViewModel { get; }

    public async ValueTask DisposeAsync()
    {
        ViewModel.Dispose();
        await Plugin.DisposeAsync();
    }
}

public sealed class PageTests(ITestOutputHelper output)
{
    private static async Task UntilAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, "Timed out waiting for the condition.");
            await Task.Delay(10);
        }
    }

    [Fact]
    [Trait("Category", "Timing")] // the plugin's events arrive on the thread pool, waited for in real time
    public async Task The_page_reads_the_cards_and_runs_the_tools_on_the_selected_cameras()
    {
        await using var rig = new PageRig();
        var a = new FakeDevice { Address = "10.0.0.48" };
        var b = new FakeDevice { Address = "10.0.0.49" };
        var empty = new FakeDevice { Address = "10.0.0.50" };
        rig.Devices.Items.AddRange([a, b, empty]);
        rig.Cameras.Cameras[empty.Id] = new SdCamera { HasCard = false };
        var vm = rig.ViewModel;

        vm.Activate();
        await UntilAsync(() => vm.Rows.Count == 3 && vm.Rows.All(r => !r.IsReading) && !vm.IsReading);
        Assert.Equal(("2", "1"), (vm.OkText, vm.NoCardText));
        Assert.Equal(("59.3 GB", "0 % · 59.3 GB free", "1 %", "No", "7 days"), (vm.Rows[0].SizeText, vm.Rows[0].UsedText, vm.Rows[0].WearText, vm.Rows[0].EncryptedText, vm.Rows[0].RetentionText));
        Assert.False(vm.FormatCommand.CanExecute(null)); // nothing selected
        Assert.Equal("Select cameras with an SD card.", vm.ToolsBlockedReason);

        vm.SetSelection(vm.Rows); // the camera without a card is no target
        Assert.Equal(2, vm.Targets.Count);
        await vm.FormatCommand.ExecuteAsync(null);
        Assert.Equal("Format SD cards: Format the SD card of 2 cameras? Every recording on them is deleted. [Format]", Assert.Single(rig.Context.Confirmations));
        var run = Assert.Single(rig.Runner.Runs);
        Assert.Equal((2, "joe@pc"), (run.DeviceIds.Count, run.Owner));
        Assert.Contains("\"action\":\"format\"", run.Payload, StringComparison.Ordinal);

        await vm.SetRetentionCommand.ExecuteAsync(null);
        Assert.Contains("\"retentionDays\":30", rig.Runner.Runs[1].Payload, StringComparison.Ordinal);

        await vm.EncryptCommand.ExecuteAsync(null);
        Assert.Contains("\"passphrase\":\"a passphrase\"", rig.Runner.Runs[2].Payload, StringComparison.Ordinal);

        rig.Context.Confirm = false;
        await vm.CheckCommand.ExecuteAsync(null);
        Assert.Equal(3, rig.Runner.Runs.Count); // declined: nothing runs
    }

    [Fact]
    [Trait("Category", "Timing")]
    public async Task The_tools_follow_the_rights_of_the_role()
    {
        await using var rig = new PageRig();
        rig.Devices.Items.Add(new FakeDevice());
        var vm = rig.ViewModel;
        vm.Activate();
        await UntilAsync(() => vm.Rows.Count == 1 && vm.Rows[0].HasCard);
        vm.SetSelection(vm.Rows);
        Assert.True(vm.MayFormat && vm.FormatCommand.CanExecute(null));

        rig.Context.Rights.Remove("oadm.edge-storage:format");
        rig.Context.RaisePermissionsChanged();
        Assert.False(vm.MayFormat);
        Assert.False(vm.FormatCommand.CanExecute(null));
        Assert.True(vm.CheckCommand.CanExecute(null));
    }

    [Fact]
    public void The_dialogs_check_their_values_below_the_fields()
    {
        var retention = new RetentionViewModel(7, 3);
        Assert.Equal("Recordings older than this are deleted from the SD cards of 3 cameras.", retention.Intro);
        retention.Days = 7001;
        Assert.False(retention.IsFormValid);
        retention.Days = null;
        Assert.False(retention.IsFormValid);
        retention.Days = 7.5m;
        Assert.False(retention.IsFormValid);
        retention.NoLimit = true;
        Assert.True(retention.IsFormValid);
        Assert.Equal(0, retention.Result);
        retention.NoLimit = false; // the days field counts again
        Assert.False(retention.IsFormValid);

        var passphrase = new PassphraseViewModel(1);
        passphrase.Passphrase = "secret";
        passphrase.ConfirmPassphrase = "other";
        Assert.False(passphrase.IsFormValid);
        passphrase.ConfirmPassphrase = "secret";
        Assert.True(passphrase.IsFormValid);
    }

    [Fact]
    [Trait("Category", "Perf")]
    public async Task Five_thousand_rows_search_and_select_fast()
    {
        await using var rig = new PageRig();
        for (var i = 0; i < 5_000; i++)
        {
            rig.Devices.Items.Add(new FakeDevice { Address = $"10.{i / 65536}.{i / 256 % 256}.{i % 256}" });
        }

        var vm = rig.ViewModel;
        vm.Activate();
        await UntilAsync(() => vm.Rows.Count == 5_000 && !vm.IsReading && vm.OkText == "5,000");
        var watch = Stopwatch.StartNew();
        vm.SearchText = "10.0.1.";
        var search = watch.ElapsedMilliseconds;
        vm.SearchText = string.Empty;
        vm.SetSelection(vm.Rows);
        _ = vm.FormatCommand.CanExecute(null);
        output.WriteLine($"search {search} ms, clear + select all {watch.ElapsedMilliseconds - search} ms");
        Assert.True(watch.ElapsedMilliseconds < 2_000);
    }

    [Fact]
    public async Task The_page_and_the_dialogs_render()
    {
        var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessEntry));
        var rig = await session.Dispatch(() =>
        {
            var rig = new PageRig();
            var worn = new FakeDevice { Address = "10.0.0.21", Model = "AXIS M4317-PLVE" };
            var locked = new FakeDevice { Address = "10.0.0.23", Model = "AXIS P1468-LE", Status = DeviceStatus.CredentialsRequired };
            var empty = new FakeDevice { Address = "10.0.0.24", Model = "AXIS M3106-L Mk II" };
            var encrypted = new FakeDevice { Address = "10.0.0.32", Model = "AXIS P3727-PLE" };
            rig.Devices.Items.AddRange([new FakeDevice(), worn, locked, empty, encrypted]);
            rig.Cameras.Cameras[worn.Id] = new SdCamera { Wear = 93 };
            rig.Cameras.Cameras[empty.Id] = new SdCamera { HasCard = false };
            rig.Cameras.Cameras[encrypted.Id] = new SdCamera { Encrypted = true, EncryptionEnabled = true, MaxAge = 0 };

            var view = new EdgeStorageView { DataContext = rig.ViewModel };
            var page = new Oadm.Client.Shell.CorePluginPageView
            {
                DataContext = new Oadm.Client.Shell.CorePluginPageViewModel(EdgeStorageInfo.PluginId, EdgeStorageInfo.DisplayName, view, new EdgeStoragePage().HasOwnCards),
            };
            var window = new Window { Width = 1500, Height = 560, Content = new Border { Padding = new Thickness(16), Child = page } };
            Oadm.Client.App.ApplyCrispText(window);
            window.Show();
            var until = DateTime.UtcNow.AddSeconds(10);
            while ((rig.ViewModel.Rows.Count < 5 || rig.ViewModel.IsReading) && DateTime.UtcNow < until)
            {
                Pump();
                Thread.Sleep(20); // the sweep runs on the thread pool; its events arrive through the UI dispatcher
            }

            var grid = view.FindControl<DataGrid>("RowGrid")!;
            grid.SelectedItems.Add(rig.ViewModel.Rows[0]);
            grid.SelectedItems.Add(rig.ViewModel.Rows[4]);
            Pump();
            Assert.Equal(2, rig.ViewModel.Targets.Count);
            Capture(window, "edge-storage-page.png");

            var retention = new RetentionWindow { DataContext = new RetentionViewModel(7, 2) };
            retention.Show();
            Pump();
            Capture(retention, "edge-storage-retention.png");
            retention.Close();

            var passphraseVm = new PassphraseViewModel(2) { Passphrase = "secret", ConfirmPassphrase = "other" };
            var passphrase = new PassphraseWindow { DataContext = passphraseVm };
            passphrase.Show();
            passphraseVm.FieldLeft(nameof(PassphraseViewModel.ConfirmPassphrase));
            Pump();
            Capture(passphrase, "edge-storage-encrypt.png");
            passphrase.Close();
            window.Close();
            return rig;
        }, CancellationToken.None);
        await rig.DisposeAsync();
        session.Dispose();
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

    private static void Capture(Window window, string name)
    {
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        using (frame)
        {
            if (Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR") is { Length: > 0 } outDir)
            {
                Directory.CreateDirectory(outDir);
                frame.Save(Path.Combine(outDir, name), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }
        }
    }
}
