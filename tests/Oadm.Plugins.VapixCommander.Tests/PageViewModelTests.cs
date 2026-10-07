using System.Diagnostics;
using System.Globalization;
using System.Net;

using Avalonia.Controls;

using Oadm.Plugins.VapixCommander.Client;
using Oadm.Sdk.Client;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.VapixCommander.Tests;

/// <summary>Host side of the page for tests: devices, selection and scripted confirmation answers.</summary>
internal sealed class FakeClientContext(VapixCommanderPlugin plugin) : ICorePluginClientContext
{
    public List<IDeviceInfo> DeviceList { get; } = [];

    public List<IDeviceInfo> Selection { get; } = [];

    public Queue<bool> ConfirmAnswers { get; } = new();

    public List<(string Title, string Message)> Confirmations { get; } = [];

    public List<string> Opened { get; } = [];

    public IReadOnlyList<IDeviceInfo> Devices => DeviceList;

    public IReadOnlyList<IDeviceInfo> SelectedDevices => Selection;

    public event EventHandler? DevicesChanged;

    public string OwnerName => "tech@pc";

    public Window? Owner => null;

    public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) => plugin.InvokeAsync(method, payloadJson, ct);

    public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;

    public Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        Confirmations.Add((title, message));
        return Task.FromResult(ConfirmAnswers.Count > 0 && ConfirmAnswers.Dequeue());
    }

    public Task OpenAsync(string hostPage)
    {
        Opened.Add(hostPage);
        return Task.CompletedTask;
    }

    public void RaiseDevicesChanged() => DevicesChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>The page against the real plugin backend (in process, fake devices).</summary>
internal sealed class PageFixture
{
    private PageFixture(VapixCommanderPlugin plugin, FakeCoreContext server, FakeClientContext client, CommanderViewModel vm)
    {
        Plugin = plugin;
        Server = server;
        Client = client;
        Vm = vm;
    }

    public VapixCommanderPlugin Plugin { get; }

    public FakeCoreContext Server { get; }

    public FakeClientContext Client { get; }

    public CommanderViewModel Vm { get; }

    public static FakeDevice NewCamera() => new() { Address = "10.0.0.48" };

    public static FakeDevice NewSpeaker() => new()
    {
        Address = "10.0.0.61",
        Model = "AXIS C1310-E",
        Serial = "B8A44F000061",
        Category = DeviceCategory.Speaker,
        Apis = [new DeviceApi("param-cgi", "1.0")],
    };

    public FakeDevice Camera { get; private init; } = null!;

    public FakeDevice Speaker { get; private init; } = null!;

    public RecordingRunner Runner => (RecordingRunner)Server.Tasks;

    public static async Task<PageFixture> CreateAsync()
    {
        var plugin = new VapixCommanderPlugin(Samples.Directory);
        var server = new FakeCoreContext();
        await plugin.StartAsync(server, CancellationToken.None);
        var client = new FakeClientContext(plugin);
        var camera = NewCamera();
        var speaker = NewSpeaker();
        server.DeviceList.All.AddRange([camera, speaker]);
        client.DeviceList.AddRange([camera, speaker]);
        client.Selection.Add(camera);
        var vm = new CommanderViewModel(new CommanderBackend(client), client);
        await vm.LoadAsync(CancellationToken.None);
        return new PageFixture(plugin, server, client, vm) { Camera = camera, Speaker = speaker };
    }

    public CommandListItem Item(string id) => Vm.LibraryTree.SelectMany(Flatten).First(n => n.Item?.Command.Id == id).Item!;

    public static IEnumerable<LibraryNodeViewModel> Flatten(LibraryNodeViewModel node) => [node, .. node.Children.SelectMany(Flatten)];
}

public sealed class PageViewModelTests
{
    [Fact]
    public async Task Loads_the_library_tree_and_preselects_the_devices_page_selection()
    {
        var page = await PageFixture.CreateAsync();
        var vm = page.Vm;

        var builtIn = vm.LibraryTree[0];
        Assert.Equal("Built-in", builtIn.Title);
        Assert.Equal("Common", builtIn.Children.Single().Title);
        Assert.Equal(3, builtIn.CommandCount);
        Assert.Equal("Saved", vm.LibraryTree[1].Title);
        Assert.Equal("3 built-in · 0 saved", vm.LibrarySummary);
        Assert.True(builtIn.Children[0].Children.Single(n => n.Item!.Command.Id == "common.daynight.shiftlevel").IsWrite);

        vm.LibrarySearch = "brand";
        Assert.Equal("Read brand parameters", vm.LibraryTree.SelectMany(PageFixture.Flatten).Single(n => n.IsCommand).Title);

        Assert.Equal(["10.0.0.48"], vm.SelectedTargets.Select(t => t.Address));
        Assert.Equal("1 of 2 selected", vm.TargetSummary);
        Assert.Equal("10.0.0.48", vm.TryDevice!.Address);
    }

    [Fact]
    public async Task Rollout_set_add_reorder_remove_and_compatibility()
    {
        var page = await PageFixture.CreateAsync();
        var vm = page.Vm;
        vm.Add(page.Item("common.brand.read"));
        vm.Add(page.Item("common.basicdeviceinfo.read"));
        var shift = vm.Add(page.Item("common.daynight.shiftlevel"));

        Assert.Same(shift, vm.SelectedRolloutItem);
        vm.MoveUpCommand.Execute(null); // keyboard (Ctrl+Up): the selected row
        Assert.Equal(["Read brand parameters", "Set day/night shift level", "Read basic device information"], vm.RolloutItems.Select(i => i.Name));
        Assert.Equal([1, 2, 3], vm.RolloutItems.Select(i => i.Position));
        Assert.Same(shift, vm.SelectedRolloutItem);

        var speaker = vm.Targets.Single(t => t.Address == "10.0.0.61");
        Assert.Equal(["Compatible", "Compatible", "Missing API basic-device-info"], speaker.Compatibility.Select(c => c.Text));
        Assert.True(speaker.Compatibility[2].IsError);
        Assert.Equal("Read basic device information: Missing API basic-device-info", speaker.CompatibilityText);
        Assert.True(speaker.IsCompatibilityError);
        Assert.True(vm.Targets.Single(t => t.Address == "10.0.0.48").AllCompatible);
        Assert.Equal("Compatible", vm.Targets.Single(t => t.Address == "10.0.0.48").CompatibilityText);
        // Only incompatible devices show a line; compatible ones stay quiet (thousands of green lines are noise).
        Assert.False(vm.Targets.Single(t => t.Address == "10.0.0.48").ShowCompatibility);
        Assert.True(speaker.ShowCompatibility);
        Assert.Equal("1 compatible · 1 not compatible", vm.CompatibilitySummary);

        vm.SelectCompatibleTargetsCommand.Execute(null);
        Assert.Equal(["10.0.0.48"], vm.SelectedTargets.Select(t => t.Address));

        // Per-row icon buttons act on their row; first row cannot move up, last cannot move down.
        var first = vm.RolloutItems[0];
        var last = vm.RolloutItems[2];
        Assert.False(vm.MoveUpCommand.CanExecute(first));
        Assert.True(vm.MoveDownCommand.CanExecute(first));
        Assert.True(vm.MoveUpCommand.CanExecute(last));
        Assert.False(vm.MoveDownCommand.CanExecute(last));
        vm.MoveDownCommand.Execute(first);
        Assert.Equal(["Set day/night shift level", "Read brand parameters", "Read basic device information"], vm.RolloutItems.Select(i => i.Name));
        Assert.Same(shift, vm.SelectedRolloutItem);

        vm.RemoveCommand.Execute(last); // not the selected row: the selection stays
        Assert.Equal(2, vm.RolloutItems.Count);
        Assert.Same(shift, vm.SelectedRolloutItem);
        Assert.Equal("2 commands × 1 device · 1 write", vm.RunSummary);
        Assert.Equal("2 compatible", vm.CompatibilitySummary); // the speaker lacked only basic-device-info

        vm.RemoveCommand.Execute(null); // keyboard (Delete): the selected row, the next one is selected
        Assert.Equal(["Read brand parameters"], vm.RolloutItems.Select(i => i.Name));
        Assert.Same(vm.RolloutItems[0], vm.SelectedRolloutItem);

        Assert.True(vm.ClearCommand.CanExecute(null));
        vm.ClearCommand.Execute(null);
        Assert.Empty(vm.RolloutItems);
        Assert.Null(vm.SelectedRolloutItem);
        Assert.False(vm.ClearCommand.CanExecute(null));
        Assert.Null(vm.CompatibilitySummary);
        Assert.False(speaker.HasCompatibility);
    }

    [Fact]
    public async Task Clicking_a_command_adds_it_and_clicking_a_group_toggles_it()
    {
        var page = await PageFixture.CreateAsync();
        var vm = page.Vm;
        var common = vm.LibraryTree[0].Children.Single();
        Assert.False(common.IsExpanded);

        vm.ActivateCommand.Execute(common);
        Assert.True(common.IsExpanded);
        Assert.Empty(vm.RolloutItems);
        vm.ActivateCommand.Execute(common);
        Assert.False(common.IsExpanded);

        var brand = common.Children.Single(n => n.Item!.Command.Id == "common.brand.read");
        vm.ActivateCommand.Execute(brand);
        vm.SelectedNode = brand;
        vm.ActivateCommand.Execute(null); // Enter in the tree: the selected node
        Assert.Equal(["Read brand parameters", "Read brand parameters"], vm.RolloutItems.Select(i => i.Name));
        Assert.Same(vm.RolloutItems[1], vm.SelectedRolloutItem);
        Assert.False(vm.DeleteSavedCommand.CanExecute(brand));
        Assert.False(vm.ExportSavedCommand.CanExecute(brand));
    }

    [Fact]
    public async Task Thousands_of_devices_filter_select_and_check_compatibility_fast()
    {
        var page = await PageFixture.CreateAsync();
        var devices = Enumerable.Range(0, 5000).Select(i => new FakeDevice
        {
            Address = string.Create(CultureInfo.InvariantCulture, $"10.{i / 65536}.{i / 256 % 256}.{i % 256}"),
            Serial = string.Create(CultureInfo.InvariantCulture, $"B8A44F{i:D6}"),
            Model = i % 25 == 0 ? "AXIS C1310-E" : "AXIS P3265-V",
            Category = i % 25 == 0 ? DeviceCategory.Speaker : DeviceCategory.Camera,
            Apis = i % 25 == 0 ? [new DeviceApi("param-cgi", "1.0")] : PageFixture.NewCamera().Apis,
        }).ToList();
        page.Client.DeviceList.Clear();
        page.Client.DeviceList.AddRange(devices);
        page.Client.Selection.Clear();
        page.Client.Selection.AddRange(devices.Take(1200));
        var vm = page.Vm;
        var watch = Stopwatch.StartNew();
        page.Client.RaiseDevicesChanged();
        var sync = watch.Elapsed;
        Assert.Equal(5000, vm.Targets.Count);
        Assert.Equal(["10.0.0.0", "10.0.0.1", "10.0.0.2"], vm.Targets.Take(3).Select(t => t.Address)); // numeric, not 10.0.0.10
        Assert.Equal("10.0.19.135", vm.Targets[^1].Address);

        watch.Restart();
        vm.TargetSearch = "P3265";
        var filter = watch.Elapsed;
        Assert.Equal(4800, vm.VisibleTargets.Count);

        watch.Restart();
        vm.SelectAllTargetsCommand.Execute(null);
        var selectAll = watch.Elapsed;
        Assert.Equal("4,800 of 5,000 selected", vm.TargetSummary);
        Assert.Equal(4800, vm.SelectedTargetCount);

        watch.Restart();
        vm.UseDevicesSelectionCommand.Execute(null);
        var devicesSelection = watch.Elapsed;
        Assert.Equal("1,200 of 5,000 selected", vm.TargetSummary);

        watch.Restart();
        vm.Add(page.Item("common.brand.read"));
        vm.Add(page.Item("common.basicdeviceinfo.read"));
        vm.Add(page.Item("common.daynight.shiftlevel"));
        var compatibility = watch.Elapsed / 3;
        Assert.Equal("4,800 compatible · 200 not compatible", vm.CompatibilitySummary);

        watch.Restart();
        vm.SelectCompatibleTargetsCommand.Execute(null);
        var selectCompatible = watch.Elapsed;
        Assert.Equal("4,800 of 5,000 selected", vm.TargetSummary);
        Assert.StartsWith("3 commands × 4,800 devices", vm.RunSummary, StringComparison.Ordinal);

        var limit = TimeSpan.FromMilliseconds(200);
        Assert.True(sync < limit * 2, $"device sync {sync.TotalMilliseconds} ms");
        Assert.True(filter < limit, $"filter {filter.TotalMilliseconds} ms");
        Assert.True(selectAll < limit, $"select all {selectAll.TotalMilliseconds} ms");
        Assert.True(devicesSelection < limit, $"devices page selection {devicesSelection.TotalMilliseconds} ms");
        Assert.True(compatibility < limit, $"compatibility {compatibility.TotalMilliseconds} ms");
        Assert.True(selectCompatible < limit, $"select compatible {selectCompatible.TotalMilliseconds} ms");
    }

    [Fact]
    public async Task Field_form_validates_like_the_server()
    {
        var page = await PageFixture.CreateAsync();
        var item = page.Vm.Add(page.Item("common.daynight.shiftlevel"));
        var level = item.Fields.Single(f => f.Name == "level");

        Assert.Equal("50", level.Text);
        Assert.True(item.IsValid);
        level.Text = "500";
        Assert.Equal("Must be at most 100.", level.Error);
        Assert.False(item.IsValid);
        level.Text = "70";
        Assert.True(item.IsValid);
        Assert.Equal("70", item.Values()["level"].GetString());
        Assert.Equal("0 to 100 %", level.Hint);
    }

    [Fact]
    public async Task Run_with_writes_asks_for_confirmation_then_starts_the_rollout()
    {
        var page = await PageFixture.CreateAsync();
        var vm = page.Vm;
        vm.Add(page.Item("common.brand.read"));
        vm.Add(page.Item("common.daynight.shiftlevel")).Fields.Single(f => f.Name == "level").Text = "65";

        page.Client.ConfirmAnswers.Enqueue(false);
        await vm.RunCommand.ExecuteAsync(null);
        Assert.Empty(page.Runner.Runs);
        var (title, message) = Assert.Single(page.Client.Confirmations);
        Assert.Equal("Run VAPIX commands", title);
        Assert.StartsWith("2 commands × 1 device", message, StringComparison.Ordinal);
        Assert.Contains("2. Set day/night shift level (Write)", message, StringComparison.Ordinal);
        Assert.Contains("Stop on first error", message, StringComparison.Ordinal);

        page.Client.ConfirmAnswers.Enqueue(true);
        vm.StopOnFirstError = false;
        await vm.RunCommand.ExecuteAsync(null);

        var run = Assert.Single(page.Runner.Runs);
        Assert.Equal([page.Camera.Id], run.Devices);
        Assert.Equal("tech@pc", run.Owner);
        Assert.Contains("\"stopOnFirstError\":false", run.Payload!, StringComparison.Ordinal);
        Assert.Contains("\"level\":\"65\"", run.Payload!, StringComparison.Ordinal);
        Assert.False(vm.IsStatusError);
        Assert.StartsWith("Started 1 task", vm.Status, StringComparison.Ordinal);
        await vm.ShowTasksCommand.ExecuteAsync(null);
        Assert.Equal([HostPages.Devices], page.Client.Opened);
    }

    [Fact]
    public async Task Run_without_targets_or_with_invalid_values_does_nothing()
    {
        var page = await PageFixture.CreateAsync();
        var vm = page.Vm;
        vm.ClearTargetsCommand.Execute(null);
        vm.Add(page.Item("common.brand.read"));

        await vm.RunCommand.ExecuteAsync(null);
        Assert.Equal("Select at least one device.", vm.Status);

        vm.UseDevicesSelectionCommand.Execute(null);
        vm.Add(page.Item("common.daynight.shiftlevel")).Fields.Single(f => f.Name == "level").Text = "x";
        vm.SelectedRolloutItem = vm.RolloutItems[0];
        await vm.RunCommand.ExecuteAsync(null);

        Assert.Equal("Check the values of \"Set day/night shift level\".", vm.Status);
        Assert.Same(vm.RolloutItems[1], vm.SelectedRolloutItem);
        Assert.Empty(page.Runner.Runs);
    }

    [Fact]
    public async Task Try_read_command_shows_the_exchange_and_errors()
    {
        var page = await PageFixture.CreateAsync();
        var vm = page.Vm;
        var vapix = page.Server.VapixFactory.For(page.Camera.Id);
        vapix.Handler = _ => FakeVapix.Text("root.Brand.Brand=AXIS\nroot.Brand.ProdNbr=P3265-V\n");
        vm.Add(page.Item("common.brand.read"));

        await vm.TryCommand.ExecuteAsync(null);

        Assert.True(vm.TryResult!.Success);
        Assert.Equal("Product: P3265-V", vm.TryResult.Summary);
        Assert.StartsWith("HTTP 200 OK · ", vm.TryResult.ExchangeLine, StringComparison.Ordinal);
        Assert.Contains("root.Brand.Brand=AXIS", vm.TryResult.Body, StringComparison.Ordinal);
        Assert.Empty(page.Client.Confirmations);

        vapix.Handler = _ => FakeVapix.Text("<html><head><title>401 Unauthorized</title></head></html>", HttpStatusCode.Unauthorized, "text/html");
        await vm.TryCommand.ExecuteAsync(null);
        Assert.False(vm.TryResult.Success);
        Assert.Equal("Unauthorized - HTTP 401 (check credentials): 401 Unauthorized", vm.TryResult.Summary);
        Assert.Equal("Failed", vm.TryResult.StatusText);
    }

    [Fact]
    public async Task Try_result_highlights_the_body_by_its_content_type_and_offers_the_raw_text()
    {
        var page = await PageFixture.CreateAsync();
        var vm = page.Vm;
        var vapix = page.Server.VapixFactory.For(page.Camera.Id);
        vm.Add(page.Item("common.basicdeviceinfo.read"));

        // Minified JSON: pretty by default, raw shows the exact device text.
        const string json = "{\"apiVersion\":\"1.3\",\"data\":{\"propertyList\":{\"ProdNbr\":\"P3265-V\",\"Version\":\"12.0.68\"}}}";
        vapix.Handler = _ => FakeVapix.Json(json);
        await vm.TryCommand.ExecuteAsync(null);
        var result = vm.TryResult!;
        Assert.Equal(Oadm.Sdk.Client.Controls.CodeLanguage.Json, result.BodyLanguage);
        Assert.True(result.CanShowRaw);
        Assert.True(result.IsPretty);
        Assert.StartsWith("{\n  \"apiVersion\": \"1.3\",", result.DisplayBody.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        result.ShowRawCommand.Execute(null);
        Assert.True(result.IsRaw);
        Assert.Equal(json, result.DisplayBody);
        result.ShowPrettyCommand.Execute(null);
        Assert.NotEqual(json, result.DisplayBody);

        // SOAP / XML by content type, even when the text alone would not say so.
        vapix.Handler = _ => FakeVapix.Text("<?xml version=\"1.0\"?><root><a>1</a></root>", HttpStatusCode.OK, "application/soap+xml");
        await vm.TryCommand.ExecuteAsync(null);
        Assert.Equal(Oadm.Sdk.Client.Controls.CodeLanguage.Xml, vm.TryResult!.BodyLanguage);
        Assert.Contains("  <a>1</a>", vm.TryResult.DisplayBody, StringComparison.Ordinal);
        Assert.Equal("<?xml version=\"1.0\"?><root><a>1</a></root>", vm.TryResult.RawBody);

        // param.cgi text: key=value, nothing to re-indent.
        vm.Add(page.Item("common.brand.read"));
        vapix.Handler = _ => FakeVapix.Text("root.Brand.Brand=AXIS\nroot.Brand.ProdNbr=P3265-V\n");
        await vm.TryCommand.ExecuteAsync(null);
        Assert.Equal(Oadm.Sdk.Client.Controls.CodeLanguage.KeyValue, vm.TryResult!.BodyLanguage);
        Assert.False(vm.TryResult.CanShowRaw);
        Assert.Equal(vm.TryResult.Body, vm.TryResult.RawBody);

        // HTML error pages stay plain.
        vapix.Handler = _ => FakeVapix.Text("<html><head><title>500</title></head></html>", HttpStatusCode.InternalServerError, "text/html");
        await vm.TryCommand.ExecuteAsync(null);
        Assert.Equal(Oadm.Sdk.Client.Controls.CodeLanguage.Plain, vm.TryResult!.BodyLanguage);
    }

    [Fact]
    public async Task Try_write_command_asks_first()
    {
        var page = await PageFixture.CreateAsync();
        var vm = page.Vm;
        vm.Add(page.Item("common.daynight.shiftlevel"));

        page.Client.ConfirmAnswers.Enqueue(false);
        await vm.TryCommand.ExecuteAsync(null);
        Assert.Null(vm.TryResult);
        Assert.Empty(page.Server.VapixFactory.For(page.Camera.Id).Requests);

        page.Client.ConfirmAnswers.Enqueue(true);
        await vm.TryCommand.ExecuteAsync(null);
        Assert.True(vm.TryResult!.Success);
        Assert.Equal("GET /axis-cgi/param.cgi?action=update&ImageSource.I0.DayNight.ShiftLevel=50", vm.TryResult.RequestLine);
    }

    [Fact]
    public async Task Save_with_values_creates_a_shared_saved_command()
    {
        var page = await PageFixture.CreateAsync();
        var vm = page.Vm;
        vm.Add(page.Item("common.daynight.shiftlevel")).Fields.Single(f => f.Name == "level").Text = "50";
        vm.SavePresetCommand.Execute(null);
        vm.PresetName = "Day Night Level 50";

        await vm.ConfirmPresetCommand.ExecuteAsync(null);

        Assert.False(vm.IsPresetOpen);
        var saved = vm.LibraryTree[1].Children.Single().Children.Single();
        Assert.Equal("Day Night Level 50", saved.Title);
        Assert.Equal("Custom", vm.LibraryTree[1].Children.Single().Title);
        Assert.Equal(CommandSources.Saved, saved.Item!.Source);
        Assert.Equal(50, saved.Item.Command.Fields.Single(f => f.Name == "level").Default!.Value.GetInt32());

        Assert.True(vm.ExportSavedCommand.CanExecute(saved));
        page.Client.ConfirmAnswers.Enqueue(true);
        await vm.DeleteSavedCommand.ExecuteAsync(saved); // the row icon, without selecting the row
        Assert.Empty(vm.LibraryTree[1].Children);
    }

    [Fact]
    public async Task Import_adds_saved_commands()
    {
        var page = await PageFixture.CreateAsync();
        var json = File.ReadAllText(Path.Combine(Samples.Directory, "Common.json"));

        await page.Vm.ImportJsonAsync(json);

        Assert.Equal("Imported 3 commands.", page.Vm.Status);
        Assert.Equal(3, page.Vm.LibraryTree[1].CommandCount);
    }

    [Fact]
    public async Task Raw_editor_makes_fields_prefills_requires_and_saves()
    {
        var page = await PageFixture.CreateAsync();
        var vm = page.Vm;
        var raw = vm.Raw;
        vm.ShowRawCommand.Execute(null);

        Assert.Equal("param-cgi", raw.RequiresApi);
        Assert.Equal("1.0", raw.RequiresVersion);
        Assert.Equal(ResponseKinds.ParamCgi, raw.ResponseKind);
        Assert.False(raw.Writes);

        raw.QueryRows[0].Value = "update";
        raw.QueryRows[1].Key = "ImageSource.I0.DayNight.ShiftLevel";
        raw.QueryRows[1].Value = "40";
        Assert.True(raw.Writes);
        var field = raw.MakeField(raw.QueryRows[1])!;
        Assert.Equal("shiftLevel", field.Name);
        Assert.Equal(FieldTypes.Integer, field.Type);
        Assert.Equal("{{shiftLevel}}", raw.QueryRows[1].Value);

        raw.HeaderRows.Add(new KeyValueRowViewModel { Key = "X-Note", Value = "{{note}}" });
        Assert.Contains(raw.Fields, f => f.Name == "note");
        raw.HeaderRows.Clear();
        raw.SyncFields();
        Assert.DoesNotContain(raw.Fields, f => f.Name == "note");

        var (command, problems) = raw.Build(forSave: false);
        Assert.Empty(problems);
        Assert.Equal(40, command.Fields.Single().Default!.Value.GetInt64());

        raw.Path = "/config/rest/ntp/v2/servers";
        Assert.Equal("ntp", raw.RequiresApi);
        Assert.Equal("2.0", raw.RequiresVersion);
        Assert.Equal(ResponseKinds.Rest, raw.ResponseKind);
        raw.Path = "/axis-cgi/param.cgi";

        raw.OpenSaveCommand.Execute(null);
        raw.SaveName = "Night shift 40";
        raw.SaveCommand.Execute(null);
        await TestUtil.WaitUntilAsync(() => !raw.IsSaveOpen);
        Assert.Contains(vm.LibraryTree[1].Children.SelectMany(c => c.Children), n => n.Title == "Night shift 40");

        vm.AddRawToRolloutCommand.Execute(null);
        Assert.Equal("Raw: GET /axis-cgi/param.cgi", vm.RolloutItems.Single().Name);
        Assert.Equal(CommandSources.Inline, vm.RolloutItems.Single().Ref.Source);
    }

    [Fact]
    public async Task Raw_send_reports_validation_problems()
    {
        var page = await PageFixture.CreateAsync();
        var vm = page.Vm;
        vm.ShowRawCommand.Execute(null);
        vm.Raw.RequiresApi = string.Empty;

        await vm.TryCommand.ExecuteAsync(null);

        // The required API follows the path for a plain request: the problem shows below the path.
        Assert.Contains("requires", vm.Raw.ErrorOf(nameof(vm.Raw.Path)), StringComparison.Ordinal);
        Assert.Null(vm.TryResult);
        Assert.False(vm.AddRawToRolloutCommand.CanExecute(null));
        Assert.Equal(vm.Raw.ErrorOf(nameof(vm.Raw.Path)), vm.Raw.BlockedReason);
    }
}

internal static class TestUtil
{
    public static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }
}
