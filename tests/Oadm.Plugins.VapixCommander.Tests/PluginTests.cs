using System.Net;
using System.Text.Json;

using Oadm.Sdk.Devices;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.VapixCommander.Tests;

public sealed class PluginTests
{
    private static async Task<(VapixCommanderPlugin Plugin, FakeCoreContext Ctx, FakeDevice Device)> StartAsync()
    {
        var plugin = new VapixCommanderPlugin(Samples.Directory);
        var ctx = new FakeCoreContext();
        var device = new FakeDevice();
        ctx.DeviceList.All.Add(device);
        await plugin.StartAsync(ctx, CancellationToken.None);
        return (plugin, ctx, device);
    }

    private static async Task<T> Invoke<T>(VapixCommanderPlugin plugin, string method, object? request)
        where T : class, new() =>
        CommandJson.Read<T>(await plugin.InvokeAsync(method, request is null ? null : CommandJson.Write(request), CancellationToken.None));

    [Fact]
    public async Task Lists_the_library_and_contributes_a_hidden_rollout_task()
    {
        var (plugin, _, _) = await StartAsync();

        var library = await Invoke<CommandListReply>(plugin, CommanderMethods.ListLibrary, null);

        Assert.Equal(3, library.Commands.Count);
        var task = Assert.Single(plugin.TaskPlugins);
        Assert.Equal(RolloutTaskPlugin.PluginId, task.Id);
        Assert.False(task.ShowInMenus);
        Assert.Equal("command", plugin.IconKey);
        await Assert.ThrowsAsync<NotSupportedException>(() => plugin.InvokeAsync("nope", null, CancellationToken.None));
    }

    [Fact]
    public async Task Try_read_command_runs_immediately_and_returns_the_exchange()
    {
        var (plugin, ctx, device) = await StartAsync();
        ctx.VapixFactory.For(device.Id).Handler = _ => FakeVapix.Text("root.Brand.Brand=AXIS\nroot.Brand.ProdNbr=P3265-V\n");

        var reply = await Invoke<TryCommandReply>(plugin, CommanderMethods.TryRequest, new TryCommandRequest
        {
            DeviceId = device.Id,
            Command = new CommandRef { Source = CommandSources.Library, Id = "common.brand.read" },
        });

        Assert.Null(reply.Error);
        Assert.True(reply.Outcome!.Success);
        Assert.Equal("Product: P3265-V", reply.Outcome.Summary);
        Assert.Equal(200, reply.Outcome.StatusCode);
        Assert.Equal("GET /axis-cgi/param.cgi?action=list&group=Brand", reply.Outcome.RequestLine);
        Assert.Contains(reply.Outcome.Headers, h => h.Name == "Content-Type");
        Assert.Equal(0, ctx.VapixFactory.For(device.Id).ApiListCalls);
        Assert.False(ctx.VapixFactory.For(device.Id).Disposed);
    }

    [Fact]
    public async Task Try_write_command_needs_confirmation_and_a_fresh_api_list()
    {
        var (plugin, ctx, device) = await StartAsync();
        var vapix = ctx.VapixFactory.For(device.Id);
        var request = new TryCommandRequest
        {
            DeviceId = device.Id,
            Command = new CommandRef { Source = CommandSources.Library, Id = "common.daynight.shiftlevel" },
            Values = Samples.Values(("level", 70)),
        };

        var unconfirmed = await Invoke<TryCommandReply>(plugin, CommanderMethods.TryRequest, request);
        Assert.True(unconfirmed.NeedsConfirmation);
        Assert.Empty(vapix.Requests);

        request.Confirmed = true;
        vapix.ApiList = [new DeviceApi("basic-device-info", "1.3")];
        var incompatible = await Invoke<TryCommandReply>(plugin, CommanderMethods.TryRequest, request);
        Assert.Contains("Nothing was changed", incompatible.Error, StringComparison.Ordinal);
        Assert.Empty(vapix.Requests);

        vapix.ApiList = Samples.P3265Apis;
        vapix.Handler = _ => FakeVapix.Text("# Error: Error setting 'root.ImageSource.I0.DayNight.ShiftLevel' to '70'!", HttpStatusCode.OK);
        var failed = await Invoke<TryCommandReply>(plugin, CommanderMethods.TryRequest, request);
        Assert.False(failed.Outcome!.Success);
        Assert.StartsWith("Error: Error setting", failed.Outcome.Summary, StringComparison.Ordinal);
        Assert.Equal(2, vapix.ApiListCalls);
    }

    [Fact]
    public async Task Try_reports_validation_and_device_problems_without_sending()
    {
        var (plugin, ctx, device) = await StartAsync();
        var bad = await Invoke<TryCommandReply>(plugin, CommanderMethods.TryRequest, new TryCommandRequest
        {
            DeviceId = device.Id,
            Command = new CommandRef { Source = CommandSources.Library, Id = "common.daynight.shiftlevel" },
            Values = Samples.Values(("level", 500)),
            Confirmed = true,
        });
        var missing = await Invoke<TryCommandReply>(plugin, CommanderMethods.TryRequest, new TryCommandRequest
        {
            DeviceId = Guid.NewGuid(),
            Command = new CommandRef { Source = CommandSources.Library, Id = "common.brand.read" },
        });

        Assert.Equal("Shift level: must be at most 100.", bad.Error);
        Assert.Equal("Device not found.", missing.Error);
        Assert.Empty(ctx.VapixFactory.For(device.Id).Requests);
    }

    [Fact]
    public async Task Check_compatibility_per_device_and_command()
    {
        var (plugin, ctx, device) = await StartAsync();
        var old = new FakeDevice { Apis = [new DeviceApi("param-cgi", "1.0")], Category = DeviceCategory.Speaker };
        ctx.DeviceList.All.Add(old);

        var reply = await Invoke<CompatibilityReply>(plugin, CommanderMethods.CheckCompatibility, new CompatibilityRequest
        {
            DeviceIds = [device.Id, old.Id],
            Commands =
            [
                new CommandRef { Source = CommandSources.Library, Id = "common.brand.read" },
                new CommandRef { Source = CommandSources.Library, Id = "common.basicdeviceinfo.read" },
            ],
        });

        Assert.All(reply.Devices[0].Commands, c => Assert.Equal(CompatibilityState.Compatible, c.State));
        Assert.Equal(CompatibilityState.Compatible, reply.Devices[1].Commands[0].State);
        Assert.Equal("Missing API basic-device-info", reply.Devices[1].Commands[1].Text);
    }

    [Fact]
    public async Task Rollout_validates_confirms_and_starts_one_task_per_device()
    {
        var (plugin, ctx, device) = await StartAsync();
        var runner = (RecordingRunner)ctx.Tasks;
        var request = new RolloutRequest
        {
            DeviceIds = [device.Id, Guid.NewGuid()],
            Commands =
            [
                new RolloutCommand { Command = new CommandRef { Source = CommandSources.Library, Id = "common.brand.read" } },
                new RolloutCommand { Command = new CommandRef { Source = CommandSources.Library, Id = "common.daynight.shiftlevel" }, Values = Samples.Values(("level", 101)) },
            ],
            Owner = "tech@pc",
        };

        var invalid = await Invoke<RolloutReply>(plugin, CommanderMethods.Rollout, request);
        Assert.Equal("Command 2 (Set day/night shift level): Shift level: must be at most 100.", invalid.Error);

        request.Commands[1].Values = Samples.Values(("level", 60));
        Assert.True((await Invoke<RolloutReply>(plugin, CommanderMethods.Rollout, request)).NeedsConfirmation);
        Assert.Empty(runner.Runs);

        request.Confirmed = true;
        var started = await Invoke<RolloutReply>(plugin, CommanderMethods.Rollout, request);
        Assert.Null(started.Error);
        Assert.Equal(2, started.TaskIds.Count);
        var run = Assert.Single(runner.Runs);
        Assert.Equal(RolloutTaskPlugin.PluginId, run.PluginId);
        Assert.Equal("tech@pc", run.Owner);
        Assert.Contains("ShiftLevel", run.Payload!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Saved_commands_use_their_stored_password_in_rollouts()
    {
        var (plugin, ctx, _) = await StartAsync();
        var command = Samples.Parse("""
            { "id": "custom.pw", "version": 1, "name": "Password test", "category": "Custom", "requires": [ { "api": "param-cgi", "minVersion": "1.0" } ],
              "writes": false, "fields": [ { "name": "pwd", "label": "Password", "type": "password" } ],
              "request": { "method": "POST", "path": "/x", "bodyType": "form", "body": { "pwd": "{{pwd}}" } }, "response": { "kind": "text" } }
            """);
        command.Fields[0].Default = JsonSerializer.SerializeToElement("Stored1!");
        var saved = await Invoke<SaveCommandReply>(plugin, CommanderMethods.Save, new SaveCommandRequest { Command = command });
        Assert.Null(saved.Error);
        Assert.Equal(["pwd"], saved.Saved!.StoredSecretFields);

        var listed = await plugin.InvokeAsync(CommanderMethods.ListSaved, null, CancellationToken.None);
        Assert.DoesNotContain("Stored1!", listed!, StringComparison.Ordinal);

        await Invoke<RolloutReply>(plugin, CommanderMethods.Rollout, new RolloutRequest
        {
            DeviceIds = [Guid.NewGuid()],
            Commands = [new RolloutCommand { Command = new CommandRef { Source = CommandSources.Saved, Id = "custom.pw" } }],
        });
        Assert.Contains("Stored1!", ((RecordingRunner)ctx.Tasks).Runs.Single().Payload!, StringComparison.Ordinal);
    }
}
