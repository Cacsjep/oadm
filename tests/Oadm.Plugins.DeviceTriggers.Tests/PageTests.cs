using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;

using Oadm.Client.Api;
using Oadm.Client.Devices;
using Oadm.Core.Plugins;
using Oadm.Plugins.DeviceTriggers.Client;
using Oadm.Sdk.Client;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

using Xunit.Abstractions;

namespace Oadm.Plugins.DeviceTriggers.Tests;

/// <summary>The page and the trigger dialog against the plugin in process (and the fake backend).</summary>
public sealed class PageTests(ITestOutputHelper output)
{
    private static readonly Dictionary<string, string> Port3 = new() { ["port"] = "3" };
    private static readonly Dictionary<string, string> On = new() { ["active"] = "1" };

    [Fact]
    public async Task The_page_lists_triggers_with_their_state_and_the_devices_of_the_selected_one()
    {
        await using var rig = new Rig();
        var a = rig.AddDevice("10.0.0.48");
        var b = rig.AddDevice("10.0.0.49", status: DeviceStatus.CredentialsRequired);
        rig.Cameras[a.Id].SetState(Fixture.VirtualInput, Port3, On);
        await rig.StartAsync();
        var gate = await rig.SaveAsync(Fixture.VirtualInputTrigger("Gate open", [a.Id, b.Id]));
        await rig.SaveAsync(Fixture.TamperingTrigger("Tampering", [a.Id]) with { Enabled = false });
        var ctx = new PageContext(rig.Plugin, rig.Hub, await rig.Devices.ListAsync(CancellationToken.None));
        using var vm = new DeviceTriggersViewModel(ctx);
        vm.Activate();

        await Wait.UntilAsync(() => vm.Triggers.Count == 2 && vm.Triggers[0].StateText.StartsWith("On", StringComparison.Ordinal), "the list");
        Assert.Equal(["Gate open", "Tampering"], vm.Triggers.Select(t => t.Name));
        Assert.Equal("On for 1 of 2 · 1 connecting", vm.Triggers[0].StateText); // the refused device has a problem, not lost yet
        Assert.Equal("Turned off", vm.Triggers[1].StateText);
        Assert.Equal("2 triggers · 1 turned on", vm.StatusText);

        vm.SelectedTrigger = vm.Triggers[0];
        await Wait.UntilAsync(() => vm.Devices.Count == 2, "the devices");
        Assert.Equal(("Connected", "On"), (vm.Devices[0].ConnectionText, vm.Devices[0].StateText));
        Assert.Equal(DeviceMessages.CredentialsRequired, vm.Devices[1].ConnectionText);
        Assert.Equal("1 of 2 devices connected · 1 on", vm.DevicesSummary);

        // Lost after the delay, pushed through the plugin's events.
        await Wait.UntilAsync(() => vm.Triggers[0].StateText == "On for 1 of 2 · 1 connection lost" && vm.Devices[1].ConnectionText == "Connection lost", "connection lost");
        Assert.Equal(DeviceMessages.CredentialsRequired, vm.Devices[1].Problem);

        vm.Triggers[0].Enabled = false;
        await Wait.UntilAsync(() => !rig.Plugin.Triggers.Single(t => t.Id == gate.Id).Enabled, "turned off on the server");

        ctx.ConfirmResult = true;
        await vm.DeleteCommand.ExecuteAsync(null);
        Assert.DoesNotContain(rig.Plugin.Triggers, t => t.Id == gate.Id);
        Assert.Contains("Delete the trigger Gate open?", ctx.Confirmations.Single(), StringComparison.Ordinal);
        vm.Deactivate();
    }

    [Fact]
    public async Task The_dialog_reads_the_events_of_the_chosen_devices_and_saves_the_trigger()
    {
        await using var rig = new Rig();
        var a = rig.AddDevice("10.0.0.48");
        var b = rig.AddDevice("10.0.0.49");
        var old = rig.AddDevice("10.0.0.20", hasApi: false);
        rig.Cameras[b.Id].Declarations = WithoutVirtualInput();
        await rig.StartAsync();
        TriggerEditorViewModel.ReadPollInterval = TimeSpan.FromMilliseconds(20);
        var ctx = new PageContext(rig.Plugin, rig.Hub, await rig.Devices.ListAsync(CancellationToken.None));
        using var editor = new TriggerEditorViewModel(ctx, null, ["Existing"]);

        var choiceOld = editor.VisibleDevices.Single(d => d.Id == old.Id);
        Assert.False(choiceOld.Supported);
        Assert.Equal(DeviceTriggersPluginInfo.NotSupportedReason, choiceOld.StatusText);
        Assert.Equal("Enter a name.", editor.Problem);

        editor.Name = "Gate open";
        editor.VisibleDevices.Single(d => d.Id == a.Id).IsChecked = true;
        Assert.Equal("Read the events of the chosen device first.", editor.Problem);
        await editor.ReadEventsCommand.ExecuteAsync(null);
        Assert.Equal(42, editor.EventOptions.Count);
        Assert.Equal("42 events", editor.VisibleDevices.Single(d => d.Id == a.Id).StatusText);
        Assert.Equal("Choose an event.", editor.Problem);

        editor.SelectedEvent = editor.EventOptions.Single(o => o.Event.Topic == Fixture.VirtualInput);
        Assert.Equal("Turns on and off. Rules can use it as \"When\" and \"And\".", editor.EventKindText);
        var port = Assert.Single(editor.Filters);
        Assert.Equal(("Port number", 65), (port.Label, port.Options.Count)); // Any + 64
        port.Selected = port.Options.Single(o => o.Value == "3");

        // A second device without the event: it stays listed with the reason and blocks Save.
        editor.VisibleDevices.Single(d => d.Id == b.Id).IsChecked = true;
        Assert.Equal("Read the events of the chosen device first.", editor.Problem);
        await editor.ReadEventsCommand.ExecuteAsync(null);
        Assert.Equal(Fixture.VirtualInput, editor.SelectedEvent!.Event.Topic);
        Assert.Equal("Does not offer this event", editor.VisibleDevices.Single(d => d.Id == b.Id).StatusText);
        Assert.Equal("Remove the devices that do not offer this event: 10.0.0.49.", editor.Problem);
        Assert.Equal(editor.Problem, editor.DevicesError);

        editor.VisibleDevices.Single(d => d.Id == b.Id).IsChecked = false;
        Assert.Null(editor.Problem);
        var closed = false;
        editor.CloseRequested += (_, ok) => closed = ok;
        await editor.SaveCommand.ExecuteAsync(null);

        Assert.True(closed);
        var saved = rig.Plugin.Triggers.Single();
        Assert.Equal(("Gate open", Fixture.VirtualInput, true, "active"), (saved.Name, saved.Topic, saved.Stateful, saved.StateItem));
        Assert.Equal([new TriggerFilter("port", "3")], saved.Filters);
        Assert.Equal("Port number: 3", saved.FilterText);
        Assert.Equal(["port", "active"], saved.Values);
        Assert.Equal([a.Id], saved.DeviceIds);
    }

    [Fact]
    public async Task Editing_reads_the_devices_again_and_keeps_event_and_values()
    {
        await using var rig = new Rig();
        var a = rig.AddDevice("10.0.0.48");
        await rig.StartAsync();
        var trigger = await rig.SaveAsync(Fixture.VirtualInputTrigger("Gate open", [a.Id], "5"));
        TriggerEditorViewModel.ReadPollInterval = TimeSpan.FromMilliseconds(20);
        using var editor = new TriggerEditorViewModel(new PageContext(rig.Plugin, rig.Hub, await rig.Devices.ListAsync(CancellationToken.None)), trigger, ["Other"]);

        await editor.StartAsync();

        Assert.Equal(Fixture.VirtualInput, editor.SelectedEvent?.Event.Topic);
        Assert.Equal("5", Assert.Single(editor.Filters).Selected.Value);
        Assert.Null(editor.Problem);
        editor.Name = "other ";
        Assert.Equal("Another trigger has this name.", editor.Problem);
    }

    [Fact]
    public void The_dialog_lists_and_searches_5000_devices_fast()
    {
        var devices = Enumerable.Range(0, 5000).Select(i => (IDeviceInfo)new TestDevice($"10.1.{i / 250}.{i % 250 + 1}")).ToList();
        var ctx = new PageContext(null, null, devices);
        var watch = Stopwatch.StartNew();
        using var editor = new TriggerEditorViewModel(ctx, null, []);
        var created = watch.ElapsedMilliseconds;
        editor.DeviceSearch = "10.1.7.";
        var searched = watch.ElapsedMilliseconds - created;
        foreach (var device in editor.VisibleDevices)
        {
            device.IsChecked = true;
        }

        var checkedAll = watch.ElapsedMilliseconds - created - searched;
        output.WriteLine($"create {created} ms, search {searched} ms, check {editor.VisibleDevices.Count} {checkedAll} ms");
        Assert.Equal(250, editor.VisibleDevices.Count);
        Assert.Equal("250 devices chosen", editor.DevicesSummary);
        Assert.True(watch.ElapsedMilliseconds < 3000, $"took {watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task The_fake_backend_answers_with_the_plugin_contract()
    {
        using var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
        Assert.DoesNotContain(await api.ListCorePluginsAsync(CancellationToken.None), p => p.Id == DeviceTriggersPluginInfo.PluginId);
        await api.SetPluginPackageEnabledAsync(DeviceTriggersPluginInfo.PluginId, true, CancellationToken.None);
        Assert.Contains(await api.ListCorePluginsAsync(CancellationToken.None), p => p.Id == DeviceTriggersPluginInfo.PluginId && p.IconKey == DeviceTriggersPluginInfo.IconKey);

        var devices = (await api.ListDevicesAsync(CancellationToken.None)).Select(d => (IDeviceInfo)new DeviceRowViewModel(d)).ToList();
        var ctx = new FakeModeContext(api, devices);
        using var vm = new DeviceTriggersViewModel(ctx);
        await vm.ReloadAsync();
        Assert.Equal(3, vm.Triggers.Count);
        Assert.Contains(vm.Triggers, t => t.Name == "Gate contact" && t.StateText == "Off · 1 connection lost");

        TriggerEditorViewModel.ReadPollInterval = TimeSpan.FromMilliseconds(20);
        using var editor = new TriggerEditorViewModel(ctx, null, [.. vm.Triggers.Select(t => t.Name)]);
        editor.Name = "Person at the door";
        editor.VisibleDevices.Single(d => d.Address == "10.0.0.48").IsChecked = true;
        await editor.ReadEventsCommand.ExecuteAsync(null);
        editor.SelectedEvent = editor.EventOptions.Single(o => o.Text == "AXIS Object Analytics › Scenario 1");
        await editor.SaveCommand.ExecuteAsync(null);
        await vm.ReloadAsync();
        Assert.Equal(4, vm.Triggers.Count);
    }

    [Fact]
    public async Task Page_and_dialog_render()
    {
        var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessEntry));
        var done = await session.Dispatch(async () =>
        {
            await using var rig = new Rig();
            var a = rig.AddDevice("10.0.0.48");
            var b = rig.AddDevice("10.0.0.31");
            var c = rig.AddDevice("10.0.0.23", status: DeviceStatus.CredentialsRequired);
            rig.AddDevice("10.0.0.20", hasApi: false);
            rig.Cameras[a.Id].SetState(Fixture.VirtualInput, Port3, On);
            await rig.StartAsync();
            await rig.SaveAsync(Fixture.VirtualInputTrigger("Gate open", [a.Id, b.Id, c.Id]));
            await rig.SaveAsync(Fixture.TamperingTrigger("Camera tampering", [a.Id, b.Id]));
            await rig.SaveAsync(Fixture.VirtualInputTrigger("Alarm panel", [b.Id], "8") with { Enabled = false });
            var ctx = new PageContext(rig.Plugin, rig.Hub, await rig.Devices.ListAsync(CancellationToken.None));
            var vm = new DeviceTriggersViewModel(ctx);
            var page = new Oadm.Client.Shell.CorePluginPageView
            {
                DataContext = new Oadm.Client.Shell.CorePluginPageViewModel(DeviceTriggersPluginInfo.PluginId, DeviceTriggersPluginInfo.DisplayName, new DeviceTriggersView { DataContext = vm }, true),
            };
            var window = new Window { Width = 1500, Height = 900, Content = new Border { Padding = new Thickness(16), Child = page } };
            Oadm.Client.App.ApplyCrispText(window);
            window.Show();
            var until = DateTime.UtcNow.AddSeconds(10);
            while ((vm.Triggers.Count < 3 || !vm.Triggers.Any(t => t.StateText.Contains("connection lost", StringComparison.Ordinal))) && DateTime.UtcNow < until)
            {
                Headless.Pump();
                await Task.Delay(20);
            }

            vm.SelectedTrigger = vm.Triggers.First(t => t.Name == "Gate open");
            until = DateTime.UtcNow.AddSeconds(5);
            while (vm.Devices.Count < 3 && DateTime.UtcNow < until)
            {
                Headless.Pump();
                await Task.Delay(20);
            }

            Headless.Pump();
            Headless.Capture(window, "device-triggers-page.png");
            window.Close();
            vm.Dispose();

            TriggerEditorViewModel.ReadPollInterval = TimeSpan.FromMilliseconds(20);
            var editor = new TriggerEditorViewModel(ctx, rig.Plugin.Triggers.Single(t => t.Name == "Gate open"), ["Camera tampering", "Alarm panel"]);
            var dialog = new TriggerEditorWindow { Height = 1000 };
            dialog.Attach(editor);
            dialog.Show();
            await editor.StartAsync();
            editor.VisibleDevices.Single(d => d.Address == "10.0.0.23").IsChecked = false;
            Headless.Pump();
            Headless.Capture(dialog, "device-triggers-editor.png");
            dialog.Close();
            editor.Dispose();
            return true;
        }, CancellationToken.None);
        Assert.True(done);
    }

    private static string WithoutVirtualInput() => Fixture.Recorded
        .Replace("<VirtualInput ", "<VirtualInputGone ", StringComparison.Ordinal)
        .Replace("</VirtualInput>", "</VirtualInputGone>", StringComparison.Ordinal);

    /// <summary>The page context against the plugin in process (invoke + the event hub).</summary>
    private sealed class PageContext(DeviceTriggersPlugin? plugin, PluginEventHub? hub, IReadOnlyList<IDeviceInfo> devices) : ICorePluginClientContext
    {
        public ConcurrentQueue<string> Confirmations { get; } = new();

        public bool ConfirmResult { get; set; }

        public IReadOnlyList<IDeviceInfo> Devices { get; } = devices;

        public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) =>
            plugin?.InvokeAsync(method, payloadJson, ct) ?? Task.FromResult<string?>(null);

        public async IAsyncEnumerable<PluginEvent> WatchEventsAsync([EnumeratorCancellation] CancellationToken ct)
        {
            if (hub is null)
            {
                yield break;
            }

            await foreach (var item in hub.WatchAsync(DeviceTriggersPluginInfo.PluginId, ct))
            {
                yield return item;
            }
        }

        public Task<bool> ConfirmAsync(string title, string message, string confirmText)
        {
            Confirmations.Enqueue(message);
            return Task.FromResult(ConfirmResult);
        }
    }

    private sealed class FakeModeContext(FakeOadmApi api, IReadOnlyList<IDeviceInfo> devices) : ICorePluginClientContext
    {
        public IReadOnlyList<IDeviceInfo> Devices { get; } = devices;

        public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) =>
            api.InvokeCorePluginAsync(DeviceTriggersPluginInfo.PluginId, method, payloadJson, ct);
    }
}
