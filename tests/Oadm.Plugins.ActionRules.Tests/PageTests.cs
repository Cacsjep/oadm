using System.Diagnostics;
using System.Runtime.CompilerServices;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Threading;

using Oadm.Core.Plugins;
using Oadm.Plugins.ActionRules.Client;
using Oadm.Plugins.ActionRules.Device;
using Oadm.Plugins.ActionRules.Rules;
using Oadm.Plugins.Shared;
using Oadm.Sdk.Client;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

using Xunit.Abstractions;

namespace Oadm.Plugins.ActionRules.Tests;

public sealed class HeadlessTestApp : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new StyleInclude(new Uri("avares://Oadm.Plugins.ActionRules.Tests/")) { Source = new Uri("avares://Oadm.Client/Themes/OadmTheme.axaml") });
    }
}

public static class HeadlessEntry
{
    private static readonly Lazy<HeadlessUnitTestSession> Shared = new(() => HeadlessUnitTestSession.StartNew(typeof(HeadlessEntry)));

    /// <summary>One headless application per test process (a second one fails to register its resources).</summary>
    public static HeadlessUnitTestSession Session => Shared.Value;

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<HeadlessTestApp>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        .WithInterFont();
}

/// <summary>The page context against the plugin in process (invoke + the event hub), with the role's rights.</summary>
internal sealed class PageContext(ActionRulesPlugin plugin, PluginEventHub hub, FakeDevices devices) : ICorePluginClientContext
{
    public HashSet<string> Rights { get; } = ["oadm.action-rules:show", "oadm.action-rules:manage", "oadm.action-rules:delete"];

    public List<string> Confirmations { get; } = [];

    public bool Confirm { get; set; } = true;

    public IReadOnlyList<IDeviceInfo> Devices => [.. devices.Items];

    public Dictionary<string, string> LocalSettings { get; } = [];

    /// <summary>The page methods called, with their payload.</summary>
    public List<(string Method, string? Payload)> Calls { get; } = [];

    /// <summary>Holds a method's call until the test lets it go (the page's indicators while it waits).</summary>
    public Dictionary<string, TaskCompletionSource> Gates { get; } = [];

    public string? GetLocalSetting(string key) => LocalSettings.GetValueOrDefault(key);

    public void SetLocalSetting(string key, string? value)
    {
        if (value is null)
        {
            LocalSettings.Remove(key);
        }
        else
        {
            LocalSettings[key] = value;
        }
    }

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

    public async Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct)
    {
        lock (Calls)
        {
            Calls.Add((method, payloadJson));
        }

        if (Gates.TryGetValue(method, out var gate))
        {
            await gate.Task.WaitAsync(ct);
        }

        return await plugin.InvokeAsync(method, payloadJson, ct);
    }

    public async IAsyncEnumerable<PluginEvent> WatchEventsAsync([EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var item in hub.WatchAsync(ActionRulesInfo.PluginId, ct))
        {
            yield return item;
        }
    }
}

/// <summary>Answers the page's dialogs with a fixed patch (null = cancel) and the first change to undo.</summary>
internal sealed class AnsweringDialogs : IRuleDialogs
{
    public Func<EditRulesViewModel, RulePatch?> Edit { get; set; } = _ => new RulePatch { Name = "Renamed" };

    public EditRulesViewModel? LastForm { get; private set; }

    public Task<RulePatch?> EditAsync(EditRulesViewModel form)
    {
        LastForm = form;
        return Task.FromResult(Edit(form));
    }

    public Task<Guid?> ChooseUndoAsync(UndoViewModel list) => Task.FromResult(list.Runs.Count > 0 ? list.Runs[0].Run.RunId : (Guid?)null);

    /// <summary>The devices the picker answers (only those the page allows).</summary>
    public List<Guid> Devices { get; } = [];

    public List<(Guid Device, string? Reason)> Offered { get; } = [];

    public Task<IReadOnlyList<Guid>?> ChooseDevicesAsync(string heading, Func<Guid, string?> unavailableReason)
    {
        Offered.Clear();
        Offered.AddRange(Devices.Select(d => (d, unavailableReason(d))));
        return Task.FromResult<IReadOnlyList<Guid>?>([.. Devices.Where(d => unavailableReason(d) is null)]);
    }

    public Func<RecipientFormViewModel, RecipientPatch?> Recipient { get; set; } = _ => null;

    public Task<RecipientPatch?> EditRecipientsAsync(RecipientFormViewModel form) => Task.FromResult(Recipient(form));

    public Func<ScheduleFormViewModel, bool> Schedule { get; set; } = _ => false;

    public Task<bool> EditScheduleAsync(ScheduleFormViewModel form) => Task.FromResult(Schedule(form));

    public Func<NewRuleViewModel, NewRule?> NewRule { get; set; } = _ => null;

    public NewRuleViewModel? LastNewRule { get; private set; }

    public Task<NewRule?> NewRuleAsync(NewRuleViewModel form)
    {
        LastNewRule = form;
        return Task.FromResult(NewRule(form));
    }
}

/// <summary>Plugin with fake cameras and the event hub, the page view model on top.</summary>
internal sealed class PageRig : IAsyncDisposable
{
    public PageRig()
    {
        Plugin = new ActionRulesPlugin(new DeviceSweepOptions { PublishInterval = TimeSpan.FromMilliseconds(20) });
        Plugin.StartAsync(new FakeContext(Devices, Cameras, Runner, new RecordingEvents(), Data) { Hub = Hub.For(ActionRulesInfo.PluginId) }, CancellationToken.None).GetAwaiter().GetResult();
        Context = new PageContext(Plugin, Hub, Devices);
        ViewModel = new ActionRulesViewModel(Context) { Dialogs = Dialogs };
        ViewModel.DeviceChoice.Set(Oadm.Sdk.Client.Controls.DeviceChoice.AllDevices); // the pages tested here read every device
    }

    public string Data { get; } = Path.Combine(Path.GetTempPath(), "oadm-action-rules-page-" + Guid.NewGuid().ToString("N"));

    public PluginEventHub Hub { get; } = new();

    public FakeDevices Devices { get; } = new();

    public FakeCameras Cameras { get; } = new();

    public RecordingRunner Runner { get; } = new();

    public AnsweringDialogs Dialogs { get; } = new();

    public ActionRulesPlugin Plugin { get; }

    public PageContext Context { get; }

    public ActionRulesViewModel ViewModel { get; }

    /// <summary>A device with its camera; <paramref name="setup"/> adds rules.</summary>
    public FakeDevice Add(string address, Action<RuleCamera>? setup = null, string os = "os12", DeviceStatus status = DeviceStatus.Ok, IReadOnlyList<string>? tags = null)
    {
        var device = new FakeDevice { Address = address, Status = status, FirmwareVersion = os == "os12" ? "12.11.77" : "10.12.338", Tags = tags ?? [] };
        var camera = new RuleCamera(os);
        setup?.Invoke(camera);
        Cameras.Cameras[device.Id] = camera;
        Devices.Items.Add(device);
        return device;
    }

    public async ValueTask DisposeAsync()
    {
        ViewModel.Dispose();
        await Plugin.DisposeAsync();
        if (Directory.Exists(Data))
        {
            Directory.Delete(Data, recursive: true);
        }
    }
}

public sealed class PageTests(ITestOutputHelper output)
{
    internal static readonly EventFilter VirtualInput = new(
        "tns1:Device/tnsaxis:IO/VirtualInput",
        "boolean(//SimpleItem[@Name=\"port\" and @Value=\"20\"]) and boolean(//SimpleItem[@Name=\"active\" and @Value=\"1\"])");

    internal static readonly EventFilter Schedule = new(
        RuleText.ScheduleIntervalTopic,
        "boolean(//SimpleItem[@Name=\"id\" and @Value=\"com.axis.schedules.office_hours\"]) and boolean(//SimpleItem[@Name=\"active\" and @Value=\"1\"])");

    internal static string AddTcp(RuleCamera camera, string name, string message, bool enabled = true) =>
        camera.Add(name, "com.axis.action.fixed.notification.tcp", [("host", "192.168.1.5"), ("port", "33"), ("qos", "0"), ("message", message)], start: VirtualInput, enabled: enabled);

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
    public async Task Rules_that_do_the_same_are_one_group_whatever_their_names()
    {
        await using var rig = new PageRig();
        rig.Add("10.0.0.1", c => AddTcp(c, "test", "camera 1"));
        rig.Add("10.0.0.2", c => AddTcp(c, "test", "camera 2", enabled: false));
        rig.Add("10.0.0.3", c =>
        {
            AddTcp(c, "Notify VMS", "camera 3");
            c.Add("Night light", "com.axis.action.unlimited.irlight", [("lightid", "led0"), ("activate", "yes")], conditions: [Schedule]);
        }, os: "os10");
        rig.Add("10.0.0.4", status: DeviceStatus.CredentialsRequired);
        var vm = rig.ViewModel;

        vm.Activate();
        await UntilAsync(() => !vm.IsReading && vm.Groups.Count == 3);

        Assert.True(vm.Groups[0].IsProblem); // the device that could not be read comes first
        var tcp = vm.Groups.Single(g => g.ActionText == "Send notification through TCP");
        Assert.Equal(("test and 1 other name", 3, "2 of 3 on"), (tcp.NamesText, tcp.DeviceCount, tcp.OnText));
        Assert.Contains("message: 3 values", tcp.ValuesText, StringComparison.Ordinal);
        Assert.Contains("host 192.168.1.5", tcp.ValuesText, StringComparison.Ordinal);
        Assert.Equal("While Schedule: Office Hours", vm.Groups.Single(g => g.ActionText.StartsWith("Use lights", StringComparison.Ordinal)).TriggerText);
        Assert.Equal("3 devices · 4 rules · 1 not read", vm.SummaryText);

        vm.GroupByIndex = 1; // by name: "test" and "Notify VMS" are apart
        Assert.Equal(4, vm.Groups.Count);
        vm.SearchText = "notify";
        Assert.Equal("Notify VMS", Assert.Single(vm.Groups).NamesText);
    }

    [Fact]
    [Trait("Category", "Timing")] // the plugin's events arrive on the thread pool, waited for in real time
    public async Task The_page_reads_only_the_chosen_devices_and_nothing_before_a_choice()
    {
        await using var rig = new PageRig();
        var a = rig.Add("10.0.0.1", c => AddTcp(c, "test", "camera 1"), tags: ["Building A"]);
        var b = rig.Add("10.0.0.2", c => AddTcp(c, "test", "camera 2"), tags: ["Building A"]);
        var c = rig.Add("10.0.0.3", c => AddTcp(c, "other", "camera 3"));
        rig.Context.LocalSettings.Clear(); // a new computer: no choice yet
        var vm = new ActionRulesViewModel(rig.Context) { Dialogs = rig.Dialogs };
        try
        {
            Assert.Equal("Choose devices", vm.DeviceChoice.ButtonText);
            vm.Activate();
            await Task.Delay(100);
            Assert.Empty(vm.Groups);
            Assert.DoesNotContain(rig.Context.Calls, call => call.Method == ActionRulesMethods.Read);
            Assert.All(rig.Cameras.Cameras.Values, camera => Assert.Empty(camera.Requests));
            Assert.Equal("Choose devices to read their action rules.", vm.EmptyText);
            Assert.False(vm.RefreshCommand.CanExecute(null));

            vm.PickDevices = () =>
            {
                vm.DeviceChoice.Set(Oadm.Sdk.Client.Controls.DeviceChoice.Devices([], ["Building A"]));
                return Task.FromResult(true);
            };
            await vm.ChooseDevicesCommand.ExecuteAsync(null);
            await UntilAsync(() => !vm.IsReading && vm.Groups.Count == 1);
            Assert.Equal("Selected devices: Tag Building A", vm.DeviceChoice.ButtonText);
            Assert.Equal(2, vm.Groups[0].DeviceCount);
            Assert.Empty(rig.Cameras.Cameras[c.Id].Requests); // the other device was not asked
            Assert.Equal("2 devices · 2 rules", vm.SummaryText);
            Assert.True(vm.RefreshCommand.CanExecute(null));

            // The pickers still list every device; the one outside the choice is greyed.
            Assert.Null(vm.NewRuleUnavailable(a.Id));
            Assert.Equal(ActionRulesViewModel.NotChosenText, vm.NewRuleUnavailable(c.Id));
            Assert.Equal(ActionRulesViewModel.NotChosenText, vm.CopyUnavailable(c.Id, a.Id));

            // Rows of other devices (another page's read) stay off this page.
            await rig.Plugin.InvokeAsync(ActionRulesMethods.Read, null, CancellationToken.None);
            await UntilAsync(() => rig.Cameras.Cameras[c.Id].Requests.Count > 0);
            await vm.RefreshCommand.ExecuteAsync(null);
            await UntilAsync(() => !vm.IsReading);
            Assert.Equal(2, vm.Groups.Sum(g => g.DeviceCount));
            vm.SetGroupSelection(vm.Groups);
            Assert.Equal(new[] { a.Id, b.Id }.Order(), vm.Targets.Select(t => t.Device.DeviceId).Order());
        }
        finally
        {
            vm.Dispose();
        }

        // The next page on this computer starts with the last choice.
        var again = new ActionRulesViewModel(rig.Context);
        Assert.Equal("Selected devices: Tag Building A", again.DeviceChoice.ButtonText);
        again.Dispose();
    }

    [Fact]
    [Trait("Category", "Timing")]
    public async Task The_progress_shows_from_the_click_on_and_while_a_form_is_prepared()
    {
        await using var rig = new PageRig();
        var a = rig.Add("10.0.0.1", c => AddTcp(c, "test", "camera 1"));
        rig.Add("10.0.0.2", c => AddTcp(c, "test", "camera 2"));
        var vm = rig.ViewModel;
        var read = new TaskCompletionSource();
        rig.Context.Gates[ActionRulesMethods.Read] = read;
        vm.Activate();
        Assert.True(vm.IsReading); // before the server answered
        Assert.True(vm.ShowProgress);
        Assert.Equal("Reading action rules 0 of 2", vm.ProgressRowText);
        Assert.Equal("Reading the action rules.", vm.EmptyText);
        Assert.False(vm.RefreshCommand.CanExecute(null));
        Assert.False(vm.ChooseDevicesCommand.CanExecute(null));
        read.SetResult();
        await UntilAsync(() => !vm.IsReading && vm.Groups.Count == 1);
        Assert.False(vm.ShowProgress);

        var events = new TaskCompletionSource();
        rig.Context.Gates[ActionRulesMethods.Events] = events;
        rig.Dialogs.Devices.Add(a.Id);
        var newRule = vm.NewRuleCommand.ExecuteAsync(null);
        Assert.True(vm.IsPreparing); // the form waits for the devices' events
        Assert.True(vm.ShowProgress);
        Assert.Equal(ActionRulesViewModel.PreparingText, vm.ProgressRowText);
        vm.SetGroupSelection(vm.Groups);
        Assert.False(vm.EditCommand.CanExecute(null));
        Assert.False(vm.Recipients.NewCommand.CanExecute(null));
        events.SetResult();
        await newRule;
        Assert.False(vm.IsPreparing);
        Assert.NotNull(rig.Dialogs.LastNewRule);
        Assert.True(vm.EditCommand.CanExecute(null));
    }

    [Fact]
    [Trait("Category", "Timing")]
    public async Task The_tools_run_on_the_selected_rules()
    {
        await using var rig = new PageRig();
        rig.Add("10.0.0.1", c => AddTcp(c, "test", "camera 1"));
        rig.Add("10.0.0.2", c => AddTcp(c, "test", "camera 2", enabled: false));
        var vm = rig.ViewModel;
        vm.Activate();
        await UntilAsync(() => !vm.IsReading && vm.Groups.Count == 1);
        Assert.False(vm.EditCommand.CanExecute(null));
        Assert.Equal("Select rules first.", vm.ToolsBlockedReason);

        vm.SetGroupSelection(vm.Groups);
        Assert.Equal(2, vm.Members.Count);
        Assert.Equal("2 rules on 2 devices", vm.TargetText);

        await vm.TurnOnCommand.ExecuteAsync(null); // only the rule that is off
        var run = Assert.Single(rig.Runner.Runs);
        Assert.Equal(ActionRulesInfo.TaskPluginId, run.PluginId);
        var payload = ActionRulesJson.Deserialize<RuleTaskPayload>(run.Payload);
        Assert.Equal(("Turn on action rules", true), (payload.Description, payload.Patch!.Enabled));
        Assert.Equal(rig.Devices.Items[1].Id, Assert.Single(payload.Targets).DeviceId);

        rig.Dialogs.Edit = form =>
        {
            Assert.Equal("Send notification through TCP", form.CurrentActionText);
            form.ChangeAction = true;
            form.SelectedAction = form.Actions.Single(a => a.Token == "com.axis.action.unlimited.notification.tcp");
            var period = form.Parameters.Single(p => p.Name == "period");
            Assert.True(period.IsRequired && period.Change); // the new setting must be set
            period.Value = "5";
            Assert.False(form.Parameters.Single(p => p.Name == "message").Change); // each camera keeps its own message
            form.ChangeTrigger = true;
            form.TriggerKindIndex = 1;
            Assert.True(form.CanApply);
            return form.Patch();
        };
        await vm.EditCommand.ExecuteAsync(null);
        payload = ActionRulesJson.Deserialize<RuleTaskPayload>(rig.Runner.Runs[1].Payload);
        Assert.Equal("com.axis.action.unlimited.notification.tcp", payload.Patch!.Template);
        Assert.Equal(TriggerKinds.Condition, payload.Patch.TriggerKind);
        Assert.Equal("5", Assert.Single(payload.Patch.Parameters!).Value);
        Assert.Equal(2, payload.Targets.Count);

        vm.SetMemberSelection([vm.Members[0]]); // a rule selected below narrows the target
        await vm.DeleteCommand.ExecuteAsync(null);
        Assert.Equal("Delete action rules: Delete 1 rule on 1 device? Undo can create them again for 30 days. [Delete]", Assert.Single(rig.Context.Confirmations));
        Assert.Single(ActionRulesJson.Deserialize<RuleTaskPayload>(rig.Runner.Runs[2].Payload).Targets);

        rig.Context.Rights.Remove("oadm.action-rules:delete");
        rig.Context.RaisePermissionsChanged();
        Assert.False(vm.DeleteCommand.CanExecute(null));
        Assert.True(vm.EditCommand.CanExecute(null));
    }

    [Fact]
    public void The_edit_form_needs_a_change_and_checks_its_values()
    {
        var device = new DeviceRulesRow(Guid.NewGuid(), "10.0.0.1", "M3215-LVE", "12.11.77", RowStates.Ok, null, true, [], "set", new Dictionary<string, string>(), new Dictionary<string, string>(), DateTimeOffset.UtcNow);
        var actions = SoapActionEngine.ParseActionTemplates(RuleCamera.Fixture("GetActionTemplates"));
        var recipients = SoapActionEngine.ParseRecipientTemplates(RuleCamera.Fixture("GetRecipientTemplates"));
        var set = TemplateSet.From(actions, recipients);
        var rule = new RuleView("1", "Upload", true, VirtualInput, [], null, "com.axis.action.fixed.send_images.ftp",
            [.. set.ParameterNames("com.axis.action.fixed.send_images.ftp").Select(n => new NameValue(n, n == "host" ? "192.0.2.1" : n == "password" ? string.Empty : "0"))], ["password"], "v1");
        var form = new EditRulesViewModel([new RuleMember(device, rule)], new Dictionary<Guid, TemplateSet> { [device.DeviceId] = TemplateSet.From(actions, recipients) });

        Assert.False(form.CanApply);
        Assert.Equal("Check at least one part to change.", form.ApplyBlockedReason);
        var password = form.Parameters.Single(p => p.Name == "password");
        Assert.True(password.IsSecret);
        Assert.Equal("Stays as it is unless you enter a new one.", password.Hint);
        Assert.Equal(string.Empty, password.Value);

        form.ChangeName = true;
        form.Name = " ";
        Assert.False(form.CanApply);
        form.Name = "Upload images";
        form.ChangeTimeout = true;
        form.TimeoutSeconds = 90_000;
        Assert.False(form.CanApply);
        form.TimeoutSeconds = 30;
        Assert.True(form.CanApply);
        var patch = form.Patch();
        Assert.Equal(("Upload images", 30), (patch.Name, patch.TimeoutSeconds));
        Assert.Null(patch.Parameters);
        Assert.Null(patch.Template);
        Assert.Equal("Folder", form.Parameters.Single(p => p.Name == "upload_path").Label);
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void Five_thousand_devices_group_search_and_select_fast()
    {
        var rows = new List<DeviceRulesRow>();
        for (var i = 0; i < 5_000; i++)
        {
            var views = new[]
            {
                new RuleView("1", "test", i % 3 != 0, VirtualInput, [], null, "com.axis.action.fixed.notification.tcp",
                    [new("host", "192.168.1.5"), new("port", "33"), new("qos", "0"), new("message", "camera " + i)], [], "a" + i),
                new RuleView("2", "Night", true, null, [Schedule], null, "com.axis.action.unlimited.irlight",
                    [new("lightid", "led0"), new("activate", "yes")], [], "b" + i),
            };
            rows.Add(new DeviceRulesRow(Guid.NewGuid(), $"10.{i / 65536}.{i / 256 % 256}.{i % 256}", "M3215-LVE", "12.11.77", RowStates.Ok, null, true, views, "set",
                new Dictionary<string, string>(), new Dictionary<string, string>(), DateTimeOffset.UtcNow));
        }

        var vm = new ActionRulesViewModel(new NullContext());
        var watch = Stopwatch.StartNew();
        vm.ApplyState(new RulesState(false, 5_000, 5_000, DateTimeOffset.UtcNow, rows), replaceRows: true);
        var build = watch.ElapsedMilliseconds;
        vm.SearchText = "night";
        vm.SearchText = string.Empty;
        var search = watch.ElapsedMilliseconds - build;
        vm.SetGroupSelection(vm.Groups);
        _ = vm.EditCommand.CanExecute(null);
        var select = watch.ElapsedMilliseconds - build - search;
        Assert.Equal(10_000, vm.Members.Count);
        Assert.Equal(10_000, vm.Targets.Count);
        vm.GroupByIndex = 1;
        output.WriteLine($"build {build} ms, search {search} ms, select all {select} ms, regroup {watch.ElapsedMilliseconds - build - search - select} ms");
        Assert.Equal(2, vm.Groups.Count);
        Assert.True(watch.ElapsedMilliseconds < 3_000);
    }

    [Fact]
    public async Task The_page_and_the_dialogs_render()
    {
        var session = HeadlessEntry.Session;
        var rig = await session.Dispatch(() =>
        {
            var rig = new PageRig();
            for (var i = 1; i <= 8; i++)
            {
                var n = i;
                rig.Add($"10.0.0.{40 + n}", c =>
                {
                    AddTcp(c, n == 3 ? "Notify VMS" : "test", "camera " + n, enabled: n != 5);
                    if (n % 2 == 0)
                    {
                        c.Add("Night light", "com.axis.action.unlimited.irlight", [("lightid", "led0"), ("activate", "yes")], conditions: [Schedule]);
                    }
                }, os: n == 7 ? "os10" : "os12");
            }

            rig.Add("10.0.0.23", status: DeviceStatus.CredentialsRequired);
            var view = new ActionRulesView { DataContext = rig.ViewModel };
            var page = new Oadm.Client.Shell.CorePluginPageView
            {
                DataContext = new Oadm.Client.Shell.CorePluginPageViewModel(ActionRulesInfo.PluginId, ActionRulesInfo.DisplayName, view, new ActionRulesPage().HasOwnCards),
            };
            var window = new Window { Width = 1600, Height = 620, Content = new Border { Padding = new Thickness(16), Child = page } };
            Oadm.Client.App.ApplyCrispText(window);
            window.Show();
            var until = DateTime.UtcNow.AddSeconds(10);
            while ((rig.ViewModel.Groups.Count < 3 || rig.ViewModel.IsReading) && DateTime.UtcNow < until)
            {
                Pump();
                Thread.Sleep(20);
            }

            var grid = view.FindControl<DataGrid>("GroupGrid")!;
            grid.SelectedItems.Add(rig.ViewModel.Groups.Single(g => g.ActionText == "Send notification through TCP"));
            Pump();
            Assert.Equal(8, rig.ViewModel.Members.Count);
            Capture(window, "action-rules-page.png");

            var targets = rig.ViewModel.Targets;
            var sets = targets.Select(t => t.Device).DistinctBy(d => d.DeviceId).ToDictionary(
                d => d.DeviceId,
                d => ActionRulesJson.Deserialize<List<TemplateSet>>(rig.Plugin.InvokeAsync(ActionRulesMethods.Templates, ActionRulesJson.Serialize(new TemplatesRequest([d.TemplateSetId!])), CancellationToken.None).GetAwaiter().GetResult())[0]);
            var form = new EditRulesViewModel(targets, sets);
            form.ChangeAction = true;
            form.SelectedAction = form.Actions.Single(a => a.Token == "com.axis.action.unlimited.notification.tcp");
            form.ChangeTrigger = true;
            form.TriggerKindIndex = 1;
            var edit = new EditRulesWindow { DataContext = form };
            edit.Show();
            Pump();
            Capture(edit, "action-rules-edit.png");
            edit.Close();

            var undo = new UndoWindow
            {
                DataContext = new UndoViewModel(
                [
                    new UndoRun(Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(-5), "Change action rules on 487 devices", RuleTaskActions.Edit, 487, false),
                    new UndoRun(Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(-2), "Turn off action rules on 12 devices", RuleTaskActions.Edit, 12, true),
                ]),
            };
            undo.Show();
            Pump();
            Capture(undo, "action-rules-undo.png");
            undo.Close();
            window.Close();
            return rig;
        }, CancellationToken.None);
        await rig.DisposeAsync();
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

    /// <summary>A context without a server (view model logic only).</summary>
    private sealed class NullContext : ICorePluginClientContext
    {
        public IReadOnlyList<IDeviceInfo> Devices => [];

        public string OwnerName => "joe@pc";

        public event EventHandler? DevicesChanged
        {
            add { }
            remove { }
        }

        public bool Has(string permission) => true;

        public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) => Task.FromResult<string?>(null);
    }
}
