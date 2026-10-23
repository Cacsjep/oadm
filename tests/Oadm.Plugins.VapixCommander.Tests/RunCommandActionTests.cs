using System.Net;
using System.Text.Json;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Sdk.Actions;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.VapixCommander.Tests;

/// <summary>The event rule action "Run VAPIX command" contributed by the Commander.</summary>
public sealed class RunCommandActionTests
{
    private static async Task<(VapixCommanderPlugin Plugin, RunCommandAction Action, FakeCoreContext Ctx, FakeDevice Device)> StartAsync()
    {
        var plugin = new VapixCommanderPlugin(Samples.Directory);
        var ctx = new FakeCoreContext();
        var device = new FakeDevice();
        ctx.DeviceList.All.Add(device);
        await plugin.StartAsync(ctx, CancellationToken.None);
        var action = Assert.IsType<RunCommandAction>(Assert.Single(plugin.ActionPlugins));
        return (plugin, action, ctx, device);
    }

    private static async Task<string> SaveAsync(VapixCommanderPlugin plugin, CommandDefinition command)
    {
        var reply = CommandJson.Read<SaveCommandReply>(await plugin.InvokeAsync(CommanderMethods.Save, CommandJson.Write(new SaveCommandRequest { Command = command }), CancellationToken.None));
        Assert.Null(reply.Error);
        return reply.Saved!.Command.Id;
    }

    [Fact]
    public async Task Offers_saved_commands_first_then_library_commands_that_need_no_input()
    {
        var (plugin, action, ctx, _) = await StartAsync();
        var shift = Samples.ShiftLevel;
        shift.Name = "Night at 20 %";
        var id = await SaveAsync(plugin, shift);

        var choices = await action.ListChoicesAsync(new QueryContext(ctx), "command", CancellationToken.None);

        Assert.Equal(new ActionChoice(RunCommandAction.SavedPrefix + id, "Saved: Night at 20 %"), choices[0]);
        Assert.Contains(choices, c => c.Value == RunCommandAction.LibraryPrefix + "common.brand.read");
        Assert.Contains(choices, c => c.Value == RunCommandAction.LibraryPrefix + "common.daynight.shiftlevel"); // every field has a default
        Assert.True(action.NeedsDevice);
        Assert.True(action.SupportsTurnOff);
    }

    [Fact]
    public async Task Runs_a_saved_command_with_its_values_and_checks_a_fresh_api_list_before_a_write()
    {
        var (plugin, action, ctx, device) = await StartAsync();
        var shift = Samples.ShiftLevel;
        shift.Fields.Single(f => f.Name == "level").Default = JsonSerializer.SerializeToElement(20);
        var id = await SaveAsync(plugin, shift);
        var vapix = ctx.VapixFactory.For(device.Id);
        vapix.Handler = _ => FakeVapix.Text("OK");

        var log = await RunAsync(action, vapix, device, $$"""{"command":"saved:{{id}}"}""", ActionEdge.TurnedOn);

        var request = Assert.Single(vapix.Requests);
        Assert.Contains("ImageSource.I0.DayNight.ShiftLevel=20", request.Uri, StringComparison.Ordinal);
        Assert.Equal(1, vapix.ApiListCalls);
        Assert.Equal("Set day/night shift level", log[0]);
    }

    [Fact]
    public async Task The_off_command_runs_only_when_one_is_chosen()
    {
        var (_, action, ctx, device) = await StartAsync();
        var vapix = ctx.VapixFactory.For(device.Id);
        vapix.Handler = _ => FakeVapix.Text("root.Brand.Brand=AXIS\nroot.Brand.ProdNbr=P3265-V\n");
        using var onlyOn = JsonDocument.Parse("""{"command":"library:common.brand.read"}""");
        using var both = JsonDocument.Parse("""{"command":"library:common.brand.read","offCommand":"library:common.basicdeviceinfo.read"}""");

        Assert.False(action.RunsOn(ActionEdge.TurnedOff, onlyOn.RootElement));
        Assert.True(action.RunsOn(ActionEdge.TurnedOff, both.RootElement));
        Assert.Equal($"Run \"{Samples.Brand.Name}\", when off \"{Samples.BasicDeviceInfo.Name}\"", action.Describe(both.RootElement));

        vapix.Handler = seen => seen.Uri.Contains("basicdeviceinfo", StringComparison.Ordinal)
            ? FakeVapix.Json("""{"apiVersion":"1.3","data":{"propertyList":{"ProdNbr":"P3265-V"}}}""")
            : FakeVapix.Text("root.Brand.ProdNbr=P3265-V\n");
        await RunAsync(action, vapix, device, both.RootElement.GetRawText(), ActionEdge.TurnedOff);
        Assert.Contains("basicdeviceinfo", Assert.Single(vapix.Requests).Uri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_device_answer_with_an_error_fails_the_run()
    {
        var (_, action, ctx, device) = await StartAsync();
        var vapix = ctx.VapixFactory.For(device.Id);
        vapix.Handler = _ => FakeVapix.Text("# Error: Error -1 getting param in group 'Brand'\n");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(action, vapix, device, """{"command":"library:common.brand.read"}""", ActionEdge.Fired));
        Assert.StartsWith(Samples.Brand.Name + ": ", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_command_the_device_cannot_run_is_not_sent()
    {
        var (_, action, ctx, _) = await StartAsync();
        var device = new FakeDevice { Apis = [new("basic-device-info", "1.3")] };
        var vapix = ctx.VapixFactory.For(device.Id);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(action, vapix, device, """{"command":"library:common.brand.read"}""", ActionEdge.Fired));
        Assert.EndsWith("Nothing was sent.", ex.Message, StringComparison.Ordinal);
        Assert.Empty(vapix.Requests);
    }

    [Fact]
    public async Task Dangerous_commands_need_the_check_box()
    {
        var (plugin, action, ctx, device) = await StartAsync();
        var shift = Samples.ShiftLevel;
        shift.Dangerous = true;
        var id = await SaveAsync(plugin, shift);
        using var refused = JsonDocument.Parse($$"""{"command":"saved:{{id}}","allowDangerous":false}""");
        using var allowed = JsonDocument.Parse($$"""{"command":"saved:{{id}}","allowDangerous":true}""");

        Assert.Contains("Allow dangerous commands", action.Validate(refused.RootElement, true)["command"], StringComparison.Ordinal);
        Assert.Empty(action.Validate(allowed.RootElement, true));
        var vapix = ctx.VapixFactory.For(device.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(action, vapix, device, refused.RootElement.GetRawText(), ActionEdge.Fired));
        Assert.Empty(vapix.Requests);
        using var unknown = JsonDocument.Parse("""{"command":"saved:gone"}""");
        Assert.Equal("The command no longer exists.", action.Validate(unknown.RootElement, true)["command"]);
    }

    [Fact]
    public void Text_fields_get_the_event_values_and_other_fields_stay()
    {
        var command = new CommandDefinition
        {
            Fields =
            [
                new CommandField { Name = "text", Type = FieldTypes.String, Default = JsonSerializer.SerializeToElement("Car {carID} at {device}") },
                new CommandField { Name = "plain", Type = FieldTypes.String, Default = JsonSerializer.SerializeToElement("no placeholder") },
                new CommandField { Name = "secret", Type = FieldTypes.Password },
                new CommandField { Name = "level", Type = FieldTypes.Integer, Default = JsonSerializer.SerializeToElement(20) },
            ],
        };
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal) { ["secret"] = JsonSerializer.SerializeToElement("{carID}") };
        var ctx = new FillContext();

        RunCommandAction.FillEventValues(ctx, command, values);

        Assert.Equal("Car ABC123 at 10.0.0.48", values["text"].GetString());
        Assert.False(values.ContainsKey("plain")); // nothing to fill: the stored value is used as before
        Assert.Equal("{carID}", values["secret"].GetString()); // passwords are never filled
        Assert.False(values.ContainsKey("level"));
    }

    private sealed class FillContext : IActionContext
    {
        public IVapixClient? Vapix => null;
        public ILogger Logger => NullLogger.Instance;
        public ICorePlugin? Owner => null;
        public IPluginSettings Settings => throw new NotSupportedException();
        public void Log(string message)
        {
        }

        public string Text(string? text) => (text ?? string.Empty).Replace("{carID}", "ABC123", StringComparison.Ordinal).Replace("{device}", "10.0.0.48", StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_rule_of_a_device_group_never_runs_a_saved_command_with_stored_passwords()
    {
        var (plugin, action, ctx, device) = await StartAsync();
        var command = Samples.Parse("""
            { "id": "custom.pw", "version": 1, "name": "Password test", "category": "Custom", "requires": [ { "api": "param-cgi", "minVersion": "1.0" } ],
              "writes": false, "fields": [ { "name": "pwd", "label": "Password", "type": "password" } ],
              "request": { "method": "POST", "path": "/x", "bodyType": "form", "body": { "pwd": "{{pwd}}" } }, "response": { "kind": "text" } }
            """);
        command.Fields[0].Default = JsonSerializer.SerializeToElement("Stored1!");
        var id = await SaveAsync(plugin, command);
        var shift = Samples.ShiftLevel;
        shift.Fields.Single(f => f.Name == "level").Default = JsonSerializer.SerializeToElement(20);
        var plain = await SaveAsync(plugin, shift);
        var vapix = ctx.VapixFactory.For(device.Id);

        // The rule dialog of a user with a device group does not offer it.
        var choices = await action.ListChoicesAsync(new QueryContext(ctx, hasAllDevices: false), "command", CancellationToken.None);
        Assert.DoesNotContain(choices, c => c.Value == RunCommandAction.SavedPrefix + id);
        Assert.Contains(choices, c => c.Value == RunCommandAction.SavedPrefix + plain);
        Assert.Contains(await action.ListChoicesAsync(new QueryContext(ctx), "command", CancellationToken.None), c => c.Value == RunCommandAction.SavedPrefix + id);

        // A rule of a group that has it anyway is refused before anything is sent.
        var refused = await Assert.ThrowsAsync<CommandValidationException>(() =>
            RunAsync(action, vapix, device, $$"""{"command":"saved:{{id}}"}""", ActionEdge.Fired, ruleHasAllDevices: false));
        Assert.Equal(CommanderTexts.SavedPasswordsRefused, refused.Message);
        Assert.Empty(vapix.Requests);

        // A rule with all devices runs it as before.
        await RunAsync(action, vapix, device, $$"""{"command":"saved:{{id}}"}""", ActionEdge.Fired);
        Assert.Contains("Stored1", Assert.Single(vapix.Requests).Body, StringComparison.Ordinal);
    }

    private static async Task<List<string>> RunAsync(RunCommandAction action, IVapixClient vapix, FakeDevice device, string settingsJson, ActionEdge edge, bool ruleHasAllDevices = true)
    {
        using var settings = JsonDocument.Parse(settingsJson);
        var context = new RunContext(vapix, ruleHasAllDevices);
        var trigger = new ActionTrigger("t", "Test", device.Id, device.Address, device.Model, true, DateTimeOffset.UtcNow, new Dictionary<string, string>());
        await action.ExecuteAsync(context, new ActionInvocation(Guid.NewGuid(), "Rule", edge, settings.RootElement.Clone(), device, trigger), CancellationToken.None);
        return context.Lines;
    }

    private sealed class RunContext(IVapixClient vapix, bool ruleHasAllDevices = true) : IActionContext
    {
        public bool RuleHasAllDevices => ruleHasAllDevices;
        public List<string> Lines { get; } = [];
        public IVapixClient? Vapix { get; } = vapix;
        public ILogger Logger => NullLogger.Instance;
        public ICorePlugin? Owner => null;
        public IPluginSettings Settings => throw new NotSupportedException();
        public void Log(string message) => Lines.Add(message);
        public string Text(string? text) => text ?? string.Empty;
    }

    private sealed class QueryContext(FakeCoreContext ctx, bool hasAllDevices = true) : IActionQueryContext
    {
        public bool HasAllDevices => hasAllDevices;
        public ILogger Logger => NullLogger.Instance;
        public ICorePlugin? Owner => null;
        public IPluginSettings Settings => ctx.Settings;
        public IDeviceRepository Devices => ctx.Devices;
    }
}
