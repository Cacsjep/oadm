using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;

using Oadm.Client.Api;
using Oadm.Client.Devices;
using Oadm.Core.Plugins;
using Oadm.Plugins.DeviceTriggers.Client;
using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

using Xunit.Abstractions;

namespace Oadm.Plugins.DeviceTriggers.Tests;

/// <summary>The page and the trigger dialog against the plugin in process (and the fake backend).</summary>
public sealed class PageTests(ITestOutputHelper output)
{
    private static readonly Dictionary<string, string> Port3 = new() { ["port"] = "3" };
    private static readonly Dictionary<string, string> On = new() { ["active"] = "1" };
    private static readonly TimeSpan FastPoll = TimeSpan.FromMilliseconds(20); // the editor asks for its reads this often

    [Fact]
    public async Task The_page_lists_triggers_with_their_state_and_the_devices_of_the_selected_one()
    {
        // "Connection lost" waits an hour of the plugin's clock: the test moves the clock once it saw the state before.
        var clock = new ShiftedClock();
        await using var rig = new Rig(lostDelay: TimeSpan.FromHours(1), time: clock);
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
        clock.Shift(TimeSpan.FromHours(1));
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
    public async Task Choosing_devices_reads_their_events_and_offers_every_event_any_of_them_has()
    {
        await using var rig = new Rig();
        var a = rig.AddDevice("10.0.0.48");
        var b = rig.AddDevice("10.0.0.49");
        var offline = rig.AddDevice("10.0.0.50");
        var old = rig.AddDevice("10.0.0.20", hasApi: false);
        rig.Cameras[b.Id].Declarations = WithoutVirtualInput();
        rig.Cameras[offline.Id].Refuse = new HttpRequestException("Connection refused");
        await rig.StartAsync();
        var ctx = new PageContext(rig.Plugin, rig.Hub, await rig.Devices.ListAsync(CancellationToken.None));
        using var editor = new TriggerEditorViewModel(ctx, null, ["Existing"]) { ReadPollInterval = FastPoll };
        Guid[] pick = [a.Id, b.Id, offline.Id];
        DevicePickerViewModel? shown = null;
        editor.ShowPicker = picker =>
        {
            shown = picker;
            picker.SelectNoneCommand.Execute(null);
            foreach (var item in picker.Visible.Where(i => pick.Contains(i.Id)))
            {
                item.IsChecked = true;
            }

            return Task.FromResult(true);
        };

        Assert.Empty(editor.VisibleDevices); // a new trigger starts without devices
        Assert.True(editor.HasNoDevices);
        Assert.Equal("Enter a name.", editor.Problem);
        editor.Name = "Gate open";
        Assert.Equal("Choose at least one device or tag.", editor.Problem);

        // OK in the picker reads the events of the chosen devices at once (the server 16 at a time).
        await editor.ChooseDevicesCommand.ExecuteAsync(null);
        var pickOld = shown!.Visible.Single(i => i.Id == old.Id);
        Assert.Equal((false, DeviceTriggersPluginInfo.NotSupportedReason), (pickOld.IsAvailable, pickOld.UnavailableReason));
        Assert.Equal(3, editor.VisibleDevices.Count);
        Assert.Equal("42 events", editor.VisibleDevices.Single(d => d.Id == a.Id).StatusText);
        var down = editor.VisibleDevices.Single(d => d.Id == offline.Id);
        Assert.True(down.IsWarning);
        Assert.StartsWith("Not read: Unreachable", down.StatusText, StringComparison.Ordinal);
        Assert.EndsWith("It stays in the trigger.", down.StatusText, StringComparison.Ordinal);

        // Every event any device offers: the virtual input comes from 10.0.0.48 only.
        Assert.Contains(editor.EventOptions, o => o.Event.Topic == Fixture.VirtualInput);
        Assert.Equal(43, editor.EventOptions.Count); // the 42 of 10.0.0.48 plus the renamed one of 10.0.0.49
        Assert.Equal("Choose an event.", editor.Problem);

        // The select is grouped like the device lists its events: grey headers, the short names below them.
        var group = editor.EventChoices.First(c => c.IsHeader && c.Text == "Input ports");
        var groupIndex = editor.EventChoices.IndexOf(group);
        Assert.Contains(editor.EventChoices.Skip(groupIndex + 1).TakeWhile(c => !c.IsHeader), c => c.Text == "Virtual input");
        Assert.All(editor.EventChoices.Where(c => c.IsHeader), h => Assert.False(h.IsSelectable));
        editor.SelectedEventChoice = editor.EventChoices.Single(c => !c.IsHeader && ((TriggerEventOption)c.Value!).Event.Topic == Fixture.VirtualInput);
        Assert.Equal(Fixture.VirtualInput, editor.SelectedEvent?.Event.Topic);
        Assert.Equal("Turns on and off. Rules can use it as \"When\" and \"And\".", editor.EventKindText);
        var port = Assert.Single(editor.Filters);
        Assert.Equal(("Port number", 65), (port.Label, port.Options.Count)); // Any + 64
        port.Selected = port.Options.Single(o => o.Value == "3");

        // 10.0.0.49 was read and lacks the event: it blocks Save. The offline device may stay.
        Assert.Equal("Does not offer this event", editor.VisibleDevices.Single(d => d.Id == b.Id).StatusText);
        Assert.Equal("Remove the devices that do not offer this event: 10.0.0.49.", editor.Problem);
        Assert.Equal(editor.Problem, editor.DevicesError);

        pick = [a.Id, offline.Id]; // unchoose 10.0.0.49 in the picker
        await editor.ChooseDevicesCommand.ExecuteAsync(null);
        Assert.Equal([a.Id, offline.Id], editor.VisibleDevices.Select(d => d.Id));
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
        Assert.Equal([a.Id, offline.Id], saved.DeviceIds);
    }

    [Fact]
    public async Task Editing_a_trigger_whose_devices_are_offline_keeps_its_event()
    {
        await using var rig = new Rig();
        var a = rig.AddDevice("10.0.0.48");
        await rig.StartAsync();
        var trigger = await rig.SaveAsync(Fixture.VirtualInputTrigger("Gate open", [a.Id], "5"));
        rig.Cameras[a.Id].Refuse = new HttpRequestException("Connection refused");
        using var editor = new TriggerEditorViewModel(new PageContext(rig.Plugin, rig.Hub, await rig.Devices.ListAsync(CancellationToken.None)), trigger, []) { ReadPollInterval = FastPoll };

        await editor.StartAsync();

        Assert.True(Assert.Single(editor.VisibleDevices).IsWarning);
        Assert.Equal(Fixture.VirtualInput, editor.SelectedEvent?.Event.Topic);
        Assert.Equal("5", Assert.Single(editor.Filters).Selected.Value);
        Assert.Null(editor.Problem);
    }

    [Fact]
    public async Task Editing_reads_the_devices_again_and_keeps_event_and_values()
    {
        await using var rig = new Rig();
        var a = rig.AddDevice("10.0.0.48");
        await rig.StartAsync();
        var trigger = await rig.SaveAsync(Fixture.VirtualInputTrigger("Gate open", [a.Id], "5"));
        using var editor = new TriggerEditorViewModel(new PageContext(rig.Plugin, rig.Hub, await rig.Devices.ListAsync(CancellationToken.None)), trigger, ["Other"]) { ReadPollInterval = FastPoll };

        await editor.StartAsync();

        Assert.Equal(Fixture.VirtualInput, editor.SelectedEvent?.Event.Topic);
        Assert.Equal("5", Assert.Single(editor.Filters).Selected.Value);
        Assert.Null(editor.Problem);
        editor.Name = "other ";
        Assert.Equal("Another trigger has this name.", editor.Problem);
    }

    [Fact]
    public async Task Editing_an_all_devices_trigger_can_save_before_every_device_is_read()
    {
        await using var rig = new Rig();
        var a = rig.AddDevice("10.0.0.48");
        var b = rig.AddDevice("10.0.0.49");
        await rig.StartAsync();
        var trigger = await rig.SaveAsync(Fixture.VirtualInputTrigger("Gate open", [a.Id], "5") with { AllDevices = true });
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Cameras[b.Id].HoldReads = release.Task; // a slow device of All devices
        using var editor = new TriggerEditorViewModel(new PageContext(rig.Plugin, rig.Hub, await rig.Devices.ListAsync(CancellationToken.None)), trigger, []) { ReadPollInterval = FastPoll };

        var start = editor.StartAsync();
        var chosen = editor.VisibleDevices.Single(d => d.Id == a.Id);
        var implicitDevice = editor.VisibleDevices.Single(d => d.Id == b.Id);
        await Wait.UntilAsync(() => chosen.StatusText == "42 events" && implicitDevice.IsReading, "the chosen device read, the other one still reading");

        Assert.Equal(Fixture.VirtualInput, editor.SelectedEvent?.Event.Topic);
        Assert.Null(editor.Problem); // only chosen devices hold Save back
        Assert.True(editor.CanSave);
        release.SetResult();
        await start;
        Assert.Equal("42 events", implicitDevice.StatusText);
    }

    [Fact]
    public async Task A_new_trigger_on_all_devices_waits_for_an_event_before_save()
    {
        await using var rig = new Rig();
        var a = rig.AddDevice("10.0.0.48");
        await rig.StartAsync();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Cameras[a.Id].HoldReads = release.Task;
        using var editor = new TriggerEditorViewModel(new PageContext(rig.Plugin, rig.Hub, await rig.Devices.ListAsync(CancellationToken.None)), null, []) { Name = "Gate open", ReadPollInterval = FastPoll };
        editor.ShowPicker = picker =>
        {
            picker.SelectAllCommand.Execute(null);
            return Task.FromResult(true);
        };

        var choose = editor.ChooseDevicesCommand.ExecuteAsync(null);
        await Wait.UntilAsync(() => editor.IsReading, "reading");
        Assert.Equal("Wait until the events are read.", editor.Problem); // no event known yet
        release.SetResult();
        await choose;
        Assert.Equal("Choose an event.", editor.Problem);
    }

    [Fact]
    public async Task The_dialog_takes_30000_devices_from_the_picker() => await TakeThirtyThousandAsync();

    [Fact]
    [Trait("Category", "Perf")]
    public async Task The_dialog_takes_30000_devices_from_the_picker_fast()
    {
        var elapsed = await TakeThirtyThousandAsync();
        Assert.True(elapsed < 30_000, $"took {elapsed} ms (measured well below 3 s)");
    }

    private async Task<long> TakeThirtyThousandAsync()
    {
        var devices = Enumerable.Range(0, Oadm.Sdk.Devices.DeviceLimits.MaxDevices).Select(i => (IDeviceInfo)new TestDevice($"10.1.{i / 250}.{i % 250 + 1}")).ToList();
        var ctx = new PageContext(null, null, devices);
        var watch = Stopwatch.StartNew();
        using var editor = new TriggerEditorViewModel(ctx, null, []);
        var created = watch.ElapsedMilliseconds;
        editor.ShowPicker = picker =>
        {
            picker.SearchText = "10.1.7.";
            picker.SelectAllCommand.Execute(null);
            picker.SearchText = string.Empty;
            return Task.FromResult(true);
        };
        await editor.ChooseDevicesCommand.ExecuteAsync(null);
        var picked = watch.ElapsedMilliseconds - created;
        output.WriteLine($"create {created} ms, pick 250 of 30,000 {picked} ms");
        Assert.Equal(250, editor.VisibleDevices.Count);
        Assert.Equal("250 chosen devices: 250 devices now", editor.DevicesSummary);
        return watch.ElapsedMilliseconds;
    }

    [Fact]
    public async Task A_tag_takes_its_devices_and_a_tagged_device_without_the_event_does_not_block_save()
    {
        var devices = new List<IDeviceInfo>
        {
            new TestDevice("10.0.0.1") { Tags = ["Gate"] },
            new TestDevice("10.0.0.2"),
            new TestDevice("10.0.0.3", HasApi: false) { Tags = ["Gate"] },
        };
        var ctx = new PageContext(null, null, devices);
        using var editor = new TriggerEditorViewModel(ctx, null, []);
        editor.ShowPicker = picker =>
        {
            Assert.True(picker.HasChoices);
            Assert.Null(picker.Options.EventDeviceText); // triggers have no "device of the event"
            picker.Tags.Single(t => t.Name == "Gate").IsChecked = true;
            return Task.FromResult(true);
        };
        await editor.ChooseDevicesCommand.ExecuteAsync(null);

        var row = Assert.Single(editor.VisibleDevices); // the device without the stream API is not taken by the tag
        Assert.Equal(("10.0.0.1", "Tag Gate", true), (row.Address, row.IncludedBy, row.IsImplicit));
        Assert.Equal("Tag Gate: 1 device now", editor.DevicesSummary);
        Assert.NotEqual("Choose at least one device or tag.", editor.Problem);
    }

    [Fact]
    public async Task The_page_says_how_many_devices_a_save_or_a_duplicate_left_out()
    {
        var id = Guid.NewGuid();
        var copyId = Guid.NewGuid();
        var dropped = 2;
        var fail = false;
        var ctx = new ScriptedContext((method, _) => method switch
        {
            DeviceTriggersMethods.Duplicate when fail => throw new InvalidOperationException("The trigger could not be copied."),
            DeviceTriggersMethods.List => TriggerJson.Serialize(new ListReply(
            [
                new TriggerSummary(id, "Door", false, "Virtual input", string.Empty, true, 1, 0, 0, 0, null),
                new TriggerSummary(copyId, "Door (copy)", false, "Virtual input", string.Empty, true, 1, 0, 0, 0, null),
            ])),
            DeviceTriggersMethods.Get => TriggerJson.Serialize(new GetReply(null, [])),
            DeviceTriggersMethods.Duplicate => TriggerJson.Serialize(new SaveReply(new TriggerDefinition { Id = copyId, Name = "Door (copy)", Topic = "t" }, null, null) { Dropped = dropped }),
            _ => null,
        });
        using var vm = new DeviceTriggersViewModel(ctx) { ShowEditor = editor => { editor.SavedDropped = 1; return Task.FromResult(true); } };
        await vm.ReloadAsync();
        Assert.False(vm.HasNotice);

        await vm.NewTriggerCommand.ExecuteAsync(null);
        Assert.Equal("Saved without 1 device that is no longer in the device group.", vm.NoticeText);

        vm.SelectedTrigger = vm.Triggers.Single(t => t.Id == id);
        await vm.DuplicateCommand.ExecuteAsync(null);
        Assert.Equal("Saved without 2 devices that are no longer in the device group.", vm.NoticeText);
        Assert.True(vm.HasNotice);
        Assert.Equal(copyId, vm.SelectedTrigger?.Id);

        // An error in the same place below the table hides the notice (never both on top of each other).
        fail = true;
        vm.SelectedTrigger = vm.Triggers.Single(t => t.Id == id);
        await vm.DuplicateCommand.ExecuteAsync(null);
        Assert.True(vm.HasError);
        Assert.False(vm.HasNotice);
        fail = false;

        // A duplicate that kept every device clears it.
        dropped = 0;
        vm.SelectedTrigger = vm.Triggers.Single(t => t.Id == id);
        await vm.DuplicateCommand.ExecuteAsync(null);
        Assert.False(vm.HasNotice);
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

        using var editor = new TriggerEditorViewModel(ctx, null, [.. vm.Triggers.Select(t => t.Name)]) { ReadPollInterval = FastPoll };
        editor.Name = "Person at the door";
        editor.ShowPicker = picker =>
        {
            picker.Visible.Single(i => i.Address == "10.0.0.48").IsChecked = true;
            return Task.FromResult(true);
        };
        await editor.ChooseDevicesCommand.ExecuteAsync(null);
        Assert.Contains(editor.EventChoices, c => c.IsHeader && c.Text == "Applications");
        Assert.DoesNotContain(editor.EventChoices, c => c.IsHeader && c.Text is "AXIS Object Analytics" or "Video Motion Detection");
        editor.SelectedEvent = editor.EventOptions.Single(o => o.Text == "Object Analytics: Scenario 1");
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
            var a = rig.AddDevice("10.0.0.48", tags: ["Gate"]);
            var b = rig.AddDevice("10.0.0.31", tags: ["Gate", "Outdoor"]);
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
            var until = DateTime.UtcNow.AddSeconds(30);
            while ((vm.Triggers.Count < 3 || !vm.Triggers.Any(t => t.StateText.Contains("connection lost", StringComparison.Ordinal))) && DateTime.UtcNow < until)
            {
                Headless.Pump();
                await Task.Delay(20);
            }

            vm.SelectedTrigger = vm.Triggers.First(t => t.Name == "Gate open");
            until = DateTime.UtcNow.AddSeconds(30);
            while (vm.Devices.Count < 3 && DateTime.UtcNow < until)
            {
                Headless.Pump();
                await Task.Delay(20);
            }

            Headless.Pump();
            Headless.Capture(window, "device-triggers-page.png");
            window.Close();
            vm.Dispose();

            var editor = new TriggerEditorViewModel(ctx, rig.Plugin.Triggers.Single(t => t.Name == "Gate open"), ["Camera tampering", "Alarm panel"]) { ReadPollInterval = FastPoll };
            var dialog = new TriggerEditorWindow { Height = 1000 };
            dialog.Attach(editor);
            dialog.Show();
            await editor.StartAsync();
            Headless.Pump();
            Headless.Capture(dialog, "device-triggers-editor.png");

            // The grouped event list, open (headless draws a popup in a top level of its own).
            var events = dialog.GetVisualDescendants().OfType<ComboBox>().First(c => c.Classes.Contains("grouped"));
            events.IsDropDownOpen = true;
            Headless.Pump();
            if (events.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.Popup>().FirstOrDefault()?.Child is { } content && TopLevel.GetTopLevel(content) is { } list)
            {
                Headless.Capture(list, "device-triggers-event-list.png");
            }

            events.IsDropDownOpen = false;

            // The shared device picker the dialog opens with Choose devices: All devices, tags and single devices.
            var picker = new DevicePickerWindow(new DevicePickerViewModel(
                await rig.Devices.ListAsync(CancellationToken.None),
                DeviceChoice.Devices(editor.VisibleDevices.Select(d => d.Id)),
                new DevicePickerOptions { AllowAll = true, AllowTags = true },
                d => d.Apis.Any(a => a.Id == DeviceTriggersPluginInfo.ApiId) ? null : DeviceTriggersPluginInfo.NotSupportedReason)) { Heading = "Devices of the trigger" };
            picker.Show();
            Headless.Pump();
            Headless.Capture(picker, "device-picker.png");
            picker.Close();
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

    /// <summary>A page context whose answers the test scripts per method.</summary>
    private sealed class ScriptedContext(Func<string, string?, string?> answer) : ICorePluginClientContext
    {
        public IReadOnlyList<IDeviceInfo> Devices { get; } = [];

        public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) => Task.FromResult(answer(method, payloadJson));
    }

    private sealed class FakeModeContext(FakeOadmApi api, IReadOnlyList<IDeviceInfo> devices) : ICorePluginClientContext
    {
        public IReadOnlyList<IDeviceInfo> Devices { get; } = devices;

        public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) =>
            api.InvokeCorePluginAsync(DeviceTriggersPluginInfo.PluginId, method, payloadJson, ct);
    }
}
