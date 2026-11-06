using System.Text.Json;

using Oadm.Core.Plugins;
using Oadm.Core.Tasks;
using Oadm.Plugins.VapixCommander.Client;

namespace Oadm.Plugins.VapixCommander.Tests;

/// <summary>
/// What a request does to a device is decided from the request, on the server, by <see cref="RequestSafety"/>; a
/// command's own flags can only add to it. Changes need the user's confirmation, dangerous requests a second one, and
/// both travel in the request: the page cannot leave them out.
/// </summary>
public sealed class RequestSafetyTests
{
    private static CommandDefinition Raw(string method, string path, string? body = null, string bodyType = BodyTypes.None, bool writes = false, bool dangerous = false)
    {
        var command = Samples.Parse($$"""
            { "id": "custom.raw", "version": 1, "name": "Raw", "category": "Custom", "requires": [ { "api": "param-cgi", "minVersion": "1.0" } ],
              "writes": false, "fields": [],
              "request": { "method": "{{method}}", "path": "{{path}}" }, "response": { "kind": "text" } }
            """);
        command.Writes = writes;
        command.Dangerous = dangerous;
        if (body is not null)
        {
            command.Request.BodyType = bodyType;
            command.Request.Body = bodyType is BodyTypes.Json or BodyTypes.Form ? System.Text.Json.Nodes.JsonNode.Parse(body) : System.Text.Json.Nodes.JsonValue.Create(body);
        }

        return command;
    }

    private static SafetyLevel Sent(CommandDefinition command) => RequestSafety.Of(command, CommandRenderer.Render(command, Samples.Values()));

    [Theory]
    [InlineData("/axis-cgi/param.cgi?action=list&group=Brand")]
    [InlineData("/axis-cgi/param.cgi?action=listdefinitions&listformat=xmlschema")]
    [InlineData("/axis-cgi/pwdgrp.cgi?action=get")]
    [InlineData("/axis-cgi/jpg/image.cgi?resolution=640x480")]
    [InlineData("/axis-cgi/com/ptz.cgi?query=position&camera=1")]
    [InlineData("/axis-cgi/io/port.cgi?checkactive=1")]
    [InlineData("/axis-cgi/disks/list.cgi?diskid=all")]
    [InlineData("/config/rest/time/v1")]
    public void A_get_that_only_reads_is_a_read(string path)
    {
        var command = Raw("GET", path);

        Assert.Equal(SafetyLevel.Read, RequestSafety.Of(command));
        Assert.Equal(SafetyLevel.Read, Sent(command));
    }

    [Theory]
    [InlineData("/axis-cgi/param.cgi?action=update&ImageSource.I0.Sensor.Brightness=50")]
    [InlineData("/axis-cgi/param.cgi?action=add&template=x&group=Event")]
    [InlineData("/axis-cgi/param.cgi?action=remove&group=Event.E0")]
    [InlineData("/axis-cgi/param.cgi?ImageSource.I0.Sensor.Brightness=50")]
    [InlineData("/axis-cgi/applications/control.cgi?action=start&package=x")]
    [InlineData("/axis-cgi/mediaclip.cgi?action=play&clip=1")]
    [InlineData("/axis-cgi/io/port.cgi?action=1%3A%2F")]
    [InlineData("/axis-cgi/com/ptz.cgi?move=home&camera=1")]
    [InlineData("/axis-cgi/virtualinput/activate.cgi?schemaversion=1&port=1")]
    [InlineData("/axis-cgi/geolocation/set.cgi?lat=1&lng=2")]
    [InlineData("/axis-cgi/some/new.cgi?action=apply")]
    public void A_get_on_a_changing_path_is_a_write(string path)
    {
        var command = Raw("GET", path);

        Assert.Equal(SafetyLevel.Write, RequestSafety.Of(command));
        Assert.Equal(SafetyLevel.Write, Sent(command));
    }

    [Theory]
    [InlineData("/axis-cgi/factorydefault.cgi")]
    [InlineData("/axis-cgi/hardfactorydefault.cgi")]
    [InlineData("/axis-cgi/restart.cgi")]
    [InlineData("/axis-cgi/admin/restart.cgi")]
    [InlineData("/axis-cgi/firmwareupgrade.cgi?type=normal")]
    [InlineData("/axis-cgi/factorydefault%2Ecgi")]
    [InlineData("/axis-cgi/Restart.CGI")]
    [InlineData("/axis-cgi/pwdgrp.cgi?action=remove&user=root")]
    [InlineData("/axis-cgi/pwdgrp.cgi?action=update&user=root&pwd=x")]
    [InlineData("/axis-cgi/pwdgrp.cgi")]
    [InlineData("/axis-cgi/applications/control.cgi?action=remove&package=x")]
    [InlineData("/axis-cgi/disks/format.cgi?diskid=SD_DISK&filesystem=ext4")]
    [InlineData("/axis-cgi/param.cgi?action=update&Network.eth0.IPAddress=10.0.0.9")]
    [InlineData("/axis-cgi/param.cgi?action=update&root.Network.RTSP.Port=555")]
    [InlineData("/axis-cgi/param.cgi?action=update&HTTPS.Enabled=no")]
    [InlineData("/axis-cgi/param.cgi?action=update&System.BoaGroupPolicy.admin=http")]
    public void A_get_that_resets_restarts_or_changes_accounts_is_dangerous(string path)
    {
        var command = Raw("GET", path);

        Assert.Equal(SafetyLevel.Dangerous, RequestSafety.Of(command));
        Assert.Equal(SafetyLevel.Dangerous, Sent(command));
    }

    [Theory]
    [InlineData("/axis-cgi/basicdeviceinfo.cgi", """{"apiVersion":"1.0","method":"getAllProperties"}""", SafetyLevel.Read)]
    [InlineData("/axis-cgi/firmwaremanagement.cgi", """{"apiVersion":"1.0","method":"status"}""", SafetyLevel.Read)]
    [InlineData("/axis-cgi/network_settings.cgi", """{"apiVersion":"1.0","method":"getNetworkInfo"}""", SafetyLevel.Read)]
    [InlineData("/axis-cgi/time.cgi", """{"apiVersion":"1.0","method":"setDateTime","params":{}}""", SafetyLevel.Write)]
    [InlineData("/axis-cgi/firmwaremanagement.cgi", """{"apiVersion":"1.0","method":"commit"}""", SafetyLevel.Write)]
    [InlineData("/axis-cgi/time.cgi", """[{"method":"getDateTimeInfo"}]""", SafetyLevel.Write)]
    [InlineData("/axis-cgi/firmwaremanagement.cgi", """{"apiVersion":"1.0","method":"factoryDefault","params":{"factoryDefaultMode":"hard"}}""", SafetyLevel.Dangerous)]
    [InlineData("/axis-cgi/firmwaremanagement.cgi", """{"apiVersion":"1.0","method":"reboot"}""", SafetyLevel.Dangerous)]
    [InlineData("/axis-cgi/network_settings.cgi", """{"apiVersion":"1.0","method":"setIPv4AddressConfiguration","params":{}}""", SafetyLevel.Dangerous)]
    public void A_post_is_what_its_json_method_does(string path, string body, SafetyLevel expected)
    {
        var command = Raw("POST", path, body, BodyTypes.Json);

        Assert.Equal(expected, RequestSafety.Of(command));
        Assert.Equal(expected, Sent(command));
    }

    [Fact]
    public void Other_bodies_and_methods_change_the_device()
    {
        const string soap = "<s:Envelope xmlns:s=\"http://www.w3.org/2003/05/soap-envelope\" xmlns:a=\"http://www.axis.com/vapix/ws/action1\"><s:Body><a:{0}/></s:Body></s:Envelope>";

        Assert.Equal(SafetyLevel.Read, Sent(Raw("POST", "/vapix/services", string.Format(System.Globalization.CultureInfo.InvariantCulture, soap, "GetActionRules"), BodyTypes.Xml)));
        Assert.Equal(SafetyLevel.Write, Sent(Raw("POST", "/vapix/services", string.Format(System.Globalization.CultureInfo.InvariantCulture, soap, "RemoveActionRule"), BodyTypes.Xml)));
        Assert.Equal(SafetyLevel.Write, Sent(Raw("POST", "/vapix/services", "<not-soap/>", BodyTypes.Xml)));
        Assert.Equal(SafetyLevel.Write, Sent(Raw("POST", "/axis-cgi/time.cgi", """{"method":"getDateTimeInfo"}""", BodyTypes.Text))); // not sent as JSON
        Assert.Equal(SafetyLevel.Dangerous, Sent(Raw("POST", "/axis-cgi/pwdgrp.cgi", """{"action":"add","user":"x","pwd":"Secret1!","grp":"users","sgrp":"admin"}""", BodyTypes.Form)));
        Assert.Equal(SafetyLevel.Dangerous, Sent(Raw("POST", "/axis-cgi/firmwaremanagement.cgi", "factoryDefault", BodyTypes.Text)));
        Assert.Equal(SafetyLevel.Write, Sent(Raw("PUT", "/config/rest/time/v1")));
        Assert.Equal(SafetyLevel.Write, Sent(Raw("DELETE", "/config/rest/time/v1")));
        Assert.Equal(SafetyLevel.Write, Sent(Raw("POST", "/axis-cgi/whatever.cgi")));
    }

    [Fact]
    public void The_flags_of_a_command_only_add()
    {
        Assert.Equal(SafetyLevel.Write, Sent(Raw("GET", "/axis-cgi/param.cgi?action=list", writes: true)));
        Assert.Equal(SafetyLevel.Dangerous, Sent(Raw("GET", "/axis-cgi/param.cgi?action=list", dangerous: true)));
        Assert.Equal(SafetyLevel.Dangerous, Sent(Raw("GET", "/axis-cgi/restart.cgi", writes: true)));
    }

    [Fact]
    public void A_value_still_to_be_filled_in_counts_as_the_worst_case_until_it_is_sent()
    {
        var command = Samples.Parse("""
            { "id": "custom.param", "version": 1, "name": "Set a parameter", "category": "Custom", "requires": [ { "api": "param-cgi", "minVersion": "1.0" } ],
              "writes": false,
              "fields": [ { "name": "name", "label": "Parameter", "type": "string" }, { "name": "action", "label": "Action", "type": "string" } ],
              "request": { "method": "GET", "path": "/axis-cgi/param.cgi", "query": { "action": "{{action}}", "{{name}}": "1" } }, "response": { "kind": "text" } }
            """);

        Assert.Equal(SafetyLevel.Dangerous, RequestSafety.Of(command)); // the page: it could become a network parameter
        Assert.Equal(SafetyLevel.Read, RequestSafety.Of(command, CommandRenderer.Render(command, Samples.Values(("action", "list"), ("name", "group")))));
        Assert.Equal(SafetyLevel.Write, RequestSafety.Of(command, CommandRenderer.Render(command, Samples.Values(("action", "update"), ("name", "Image.I0.Text.String")))));
        Assert.Equal(SafetyLevel.Dangerous, RequestSafety.Of(command, CommandRenderer.Render(command, Samples.Values(("action", "update"), ("name", "Network.Enabled")))));
    }

    [Fact]
    public void The_changing_paths_know_every_bundled_command()
    {
        var library = CommandLibrary.Load(Path.Combine(AppContext.BaseDirectory, "Library"));
        Assert.True(library.Commands.Count >= 100);

        foreach (var command in library.Commands)
        {
            // The rule alone, without the command's flags, finds what the library says: nothing bundled changes its kind.
            var flags = command.Dangerous ? SafetyLevel.Dangerous : command.Writes ? SafetyLevel.Write : SafetyLevel.Read;
            var bare = command.Clone();
            bare.Writes = false;
            bare.Dangerous = false;
            Assert.True(flags == RequestSafety.Of(bare), $"{command.Id}: the library says {flags}, the request {RequestSafety.Of(bare)}");
        }
    }

    // ------------------------------------------------------------ the server

    private static async Task<(VapixCommanderPlugin Plugin, FakeCoreContext Ctx, FakeDevice Device)> StartAsync()
    {
        var plugin = new VapixCommanderPlugin(Samples.Directory);
        var ctx = new FakeCoreContext();
        var device = new FakeDevice();
        ctx.DeviceList.All.Add(device);
        await plugin.StartAsync(ctx, CancellationToken.None);
        return (plugin, ctx, device);
    }

    private static async Task<T> Invoke<T>(VapixCommanderPlugin plugin, string method, object request)
        where T : class, new() =>
        CommandJson.Read<T>(await plugin.InvokeAsync(method, CommandJson.Write(request), CancellationToken.None));

    /// <summary>A saved "GET /axis-cgi/factorydefault.cgi" that says it only reads.</summary>
    private static async Task<CommandRef> SaveFactoryDefaultAsReadAsync(VapixCommanderPlugin plugin)
    {
        var command = Raw("GET", "/axis-cgi/factorydefault.cgi");
        command.Id = string.Empty;
        command.Name = "Read something";
        var saved = await Invoke<SaveCommandReply>(plugin, CommanderMethods.Save, new SaveCommandRequest { Command = command });
        Assert.Null(saved.Error);
        Assert.False(saved.Saved!.Command.Writes);
        return new CommandRef { Source = CommandSources.Saved, Id = saved.Saved.Command.Id };
    }

    [Fact]
    public async Task A_saved_factory_default_marked_as_a_read_is_dangerous_for_the_server()
    {
        var (plugin, ctx, device) = await StartAsync();
        var vapix = ctx.VapixFactory.For(device.Id);
        var request = new TryCommandRequest { DeviceId = device.Id, Command = await SaveFactoryDefaultAsReadAsync(plugin) };

        var unconfirmed = await Invoke<TryCommandReply>(plugin, CommanderMethods.TryRequest, request);
        Assert.True(unconfirmed.NeedsConfirmation);
        Assert.True(unconfirmed.Dangerous);

        request.Confirmed = true; // an older page: it confirms changes only
        var half = await Invoke<TryCommandReply>(plugin, CommanderMethods.TryRequest, request);
        Assert.True(half.NeedsConfirmation);
        Assert.True(half.Dangerous);
        Assert.Empty(vapix.Requests);
        Assert.Equal(0, vapix.ApiListCalls);

        request.ConfirmedDangerous = true;
        var sent = await Invoke<TryCommandReply>(plugin, CommanderMethods.TryRequest, request);
        Assert.True(sent.Outcome!.Success);
        Assert.Equal(1, vapix.ApiListCalls); // a change: the fresh API list first
        Assert.Equal("axis-cgi/factorydefault.cgi", Assert.Single(vapix.Requests).Uri);
    }

    [Fact]
    public async Task A_rollout_of_it_is_refused_without_the_confirmations()
    {
        var (plugin, ctx, device) = await StartAsync();
        var runner = (RecordingRunner)ctx.Tasks;
        var request = new RolloutRequest
        {
            DeviceIds = [device.Id],
            Commands =
            [
                new RolloutCommand { Command = new CommandRef { Source = CommandSources.Library, Id = "common.brand.read" } },
                new RolloutCommand { Command = await SaveFactoryDefaultAsReadAsync(plugin) },
            ],
        };

        var unconfirmed = await Invoke<RolloutReply>(plugin, CommanderMethods.Rollout, request);
        Assert.True(unconfirmed.NeedsConfirmation);
        Assert.True(unconfirmed.Dangerous);
        Assert.Equal(["Read something"], unconfirmed.ConfirmCommands);
        Assert.Equal("These commands can restart devices, reset them or cut them off the network, and were not confirmed. Nothing was changed.", unconfirmed.Error);

        request.Confirmed = true; // an older page: it always said so, and never for the dangerous question
        var half = await Invoke<RolloutReply>(plugin, CommanderMethods.Rollout, request);
        Assert.True(half.Dangerous);
        Assert.Equal(CommanderTexts.RolloutDangerousNotConfirmed, half.Error);
        Assert.Empty(runner.Runs);

        request.ConfirmedDangerous = true;
        var started = await Invoke<RolloutReply>(plugin, CommanderMethods.Rollout, request);
        Assert.Null(started.Error);
        Assert.Single(runner.Runs);
    }

    [Fact]
    public async Task A_rollout_that_changes_devices_is_refused_without_the_confirmation_and_a_read_runs_without_one()
    {
        var (plugin, ctx, device) = await StartAsync();
        var runner = (RecordingRunner)ctx.Tasks;
        var command = Raw("GET", "/axis-cgi/param.cgi?action=update&Image.I0.Text.String=x"); // marked as a read
        var change = new RolloutRequest { DeviceIds = [device.Id], Commands = [new RolloutCommand { Command = new CommandRef { Source = CommandSources.Inline, Command = command } }] };

        var refused = await Invoke<RolloutReply>(plugin, CommanderMethods.Rollout, change);
        Assert.True(refused.NeedsConfirmation);
        Assert.False(refused.Dangerous);
        Assert.Equal("These commands change the devices and were not confirmed. Nothing was changed.", refused.Error);
        Assert.Empty(runner.Runs);

        var read = new RolloutRequest { DeviceIds = [device.Id], Commands = [new RolloutCommand { Command = new CommandRef { Source = CommandSources.Library, Id = "common.brand.read" } }] };
        var started = await Invoke<RolloutReply>(plugin, CommanderMethods.Rollout, read);
        Assert.Null(started.Error);
        Assert.False(started.NeedsConfirmation);
        Assert.Single(runner.Runs);
    }

    // ------------------------------------------------------------ the task

    [Fact]
    public async Task A_task_started_by_hand_is_refused_without_the_confirmations_in_its_payload()
    {
        var registry = new PluginRegistry();
        var devices = new FakeDevices();
        var vapix = new FakeVapixFactory();
        var plugin = new VapixCommanderPlugin(Samples.Directory);
        await using var engine = new TaskEngine(new InMemoryTaskStore(), registry, devices, vapix, options: new TaskEngineOptions { MaxParallelTasksPerPlugin = 2 });
        await using var host = new CorePluginHost(registry, devices, vapix, engine, new InMemoryPluginSettingsProvider(), secrets: TestSecrets.Create());
        Assert.True(registry.RegisterCorePlugin(plugin, new PluginOrigin(VapixCommanderPlugin.PluginId, "0.1.0", null)));
        await host.StartAllAsync(CancellationToken.None);
        var device = new FakeDevice();
        devices.All.Add(device);

        async Task<TaskRecord> RunAsync(RolloutPayload payload)
        {
            var ids = await engine.RunAsync(RolloutTaskPlugin.PluginId, [device.Id], JsonSerializer.Serialize(payload, CommandJson.Api), "tech@pc", CancellationToken.None);
            await engine.WaitForCompletionAsync(ids[0], CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(60));
            return (await engine.GetAsync(ids[0], CancellationToken.None))!;
        }

        RolloutPayload Payload(string path) => new()
        {
            RolloutId = Guid.NewGuid(),
            Commands = [new RolloutPayloadCommand { Command = Raw("GET", path) }], // marked as a read
        };

        var restart = Payload("/axis-cgi/restart.cgi");
        var refused = await RunAsync(restart);
        Assert.Equal(TaskState.Failed, refused.State);
        Assert.Equal(CommanderTexts.RolloutDangerousNotConfirmed, refused.Devices[0].Message);

        restart.Confirmed = true;
        Assert.Equal(CommanderTexts.RolloutDangerousNotConfirmed, (await RunAsync(restart)).Devices[0].Message);

        var change = await RunAsync(Payload("/axis-cgi/param.cgi?action=update&Image.I0.Text.String=x"));
        Assert.Equal(CommanderTexts.RolloutNotConfirmed, change.Devices[0].Message);
        Assert.Empty(vapix.For(device.Id).Requests);
        Assert.Equal(0, vapix.For(device.Id).ApiListCalls);

        // Marked as a read, but it changes the device: the fresh API list is read before it goes.
        restart.ConfirmedDangerous = true;
        restart.RolloutId = Guid.NewGuid();
        Assert.Equal(TaskState.Done, (await RunAsync(restart)).State);
        Assert.Equal(1, vapix.For(device.Id).ApiListCalls);
        Assert.Equal("axis-cgi/restart.cgi", Assert.Single(vapix.For(device.Id).Requests).Uri);
    }

    // ------------------------------------------------------------ the page

    [Fact]
    public async Task The_page_shows_and_asks_what_the_request_does_and_sends_the_answers()
    {
        var page = await PageFixture.CreateAsync();
        await SaveFactoryDefaultAsReadAsync(page.Plugin);
        await page.Vm.LoadAsync(CancellationToken.None);
        var saved = PageFixture.Flatten(page.Vm.LibraryTree[^1]).First(n => n.Item?.Command.Name == "Read something");
        Assert.True(saved.IsDangerous);
        var item = page.Vm.Add(saved.Item!);
        Assert.Equal("Write · dangerous", item.KindText);

        // No to the dangerous question: nothing starts.
        page.Client.ConfirmAnswers.Enqueue(true);
        page.Client.ConfirmAnswers.Enqueue(false);
        await page.Vm.RunCommand.ExecuteAsync(null);
        Assert.Equal(["Run VAPIX commands", "Dangerous commands"], page.Client.Confirmations.Select(c => c.Title));
        Assert.Empty(page.Runner.Runs);

        page.Client.ConfirmAnswers.Enqueue(true);
        page.Client.ConfirmAnswers.Enqueue(true);
        await page.Vm.RunCommand.ExecuteAsync(null);
        var run = Assert.Single(page.Runner.Runs);
        Assert.Contains("\"confirmed\":true", run.Payload!, StringComparison.Ordinal);
        Assert.Contains("\"confirmedDangerous\":true", run.Payload!, StringComparison.Ordinal);

        // One device: one question that says what it can do.
        page.Client.Confirmations.Clear();
        page.Client.ConfirmAnswers.Enqueue(true);
        await page.Vm.TryCommand.ExecuteAsync(null);
        Assert.Equal(("Send command", "\"Read something\" can restart or reset 10.0.0.48 or cut it off the network. Send it now?"), Assert.Single(page.Client.Confirmations));
        Assert.True(page.Vm.TryResult!.Success);
        Assert.Equal("axis-cgi/factorydefault.cgi", Assert.Single(page.Server.VapixFactory.For(page.Camera.Id).Requests).Uri);
    }

    [Fact]
    public async Task The_page_asks_again_when_the_server_sees_more_in_the_filled_in_request()
    {
        var page = await PageFixture.CreateAsync();
        var command = Samples.Parse("""
            { "id": "", "version": 1, "name": "Call a CGI", "category": "Custom", "requires": [ { "api": "param-cgi", "minVersion": "1.0" } ],
              "writes": false, "fields": [ { "name": "cgi", "label": "CGI", "type": "string" } ],
              "request": { "method": "GET", "path": "/axis-cgi/{{cgi}}.cgi" }, "response": { "kind": "text" } }
            """);
        Assert.Null((await page.Plugin.SaveAsync(new SaveCommandRequest { Command = command }, CancellationToken.None)).Error);
        await page.Vm.LoadAsync(CancellationToken.None);
        var item = page.Vm.Add(PageFixture.Flatten(page.Vm.LibraryTree[^1]).First(n => n.Item?.Command.Name == "Call a CGI").Item!);
        Assert.Equal("Write", item.KindText); // the page cannot know more yet
        item.Fields.Single().Text = "restart";

        // The page asks for the change; the server sees a restart and asks the dangerous question. No: nothing starts.
        page.Client.ConfirmAnswers.Enqueue(true);
        page.Client.ConfirmAnswers.Enqueue(false);
        await page.Vm.RunCommand.ExecuteAsync(null);
        Assert.Equal(["Run VAPIX commands", "Dangerous commands"], page.Client.Confirmations.Select(c => c.Title));
        Assert.Contains("• Call a CGI", page.Client.Confirmations[1].Message, StringComparison.Ordinal);
        Assert.Empty(page.Runner.Runs);
        Assert.False(page.Vm.HasStatus);

        page.Client.ConfirmAnswers.Enqueue(true);
        page.Client.ConfirmAnswers.Enqueue(true);
        await page.Vm.RunCommand.ExecuteAsync(null);
        Assert.Single(page.Runner.Runs);

        // The same for one device.
        page.Client.Confirmations.Clear();
        page.Client.ConfirmAnswers.Enqueue(true);
        page.Client.ConfirmAnswers.Enqueue(false);
        await page.Vm.TryCommand.ExecuteAsync(null);
        Assert.Equal(2, page.Client.Confirmations.Count);
        Assert.Contains("can restart or reset 10.0.0.48", page.Client.Confirmations[1].Message, StringComparison.Ordinal);
        Assert.Empty(page.Server.VapixFactory.For(page.Camera.Id).Requests);
    }

    [Fact]
    public void The_request_builder_keeps_the_boxes_ticked_for_a_request_that_changes_the_device()
    {
        var editor = new RawEditorViewModel { Path = "/axis-cgi/factorydefault.cgi" };
        Assert.True(editor.Writes);
        Assert.True(editor.Dangerous);

        editor.Writes = false;
        editor.Dangerous = false;
        Assert.True(editor.Writes);
        Assert.True(editor.Dangerous);
        var (command, _) = editor.Build(forSave: false);
        Assert.True(command.Writes);
        Assert.True(command.Dangerous);

        editor.Path = "/axis-cgi/param.cgi?action=list&group=Brand";
        editor.Dangerous = false;
        editor.Writes = false;
        Assert.False(editor.Writes);
        Assert.False(editor.Dangerous);
    }
}
