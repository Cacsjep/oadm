using System.Text.Json;

using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

using PluginCanary = Oadm.Sdk.Testing.PluginCanary;
using SdkTesting = Oadm.Sdk.Testing;

namespace Oadm.Plugins.VapixCommander.Tests;

/// <summary>The Commander for users with a device group (docs/spec/device-scope.md): the call's devices, the shared library, saved passwords.</summary>
public sealed class DeviceGroupTests
{
    private const string Brand = """{"source":"library","id":"common.brand.read"}""";

    [Fact]
    public async Task NoMethodReachesADeviceOutsideTheUsersGroup()
    {
        var ctx = new SdkTesting.FakeCoreContext();
        var inside = ctx.Add(new SdkTesting.FakeDevice());
        var outside = ctx.Add(new SdkTesting.FakeDevice { Address = "192.0.2.11" });
        var plugin = new VapixCommanderPlugin(Samples.Directory);
        await plugin.StartAsync(ctx, CancellationToken.None);

        await new PluginCanary(ctx, plugin, inside, outside)
            .WithoutDevices(CommanderMethods.ListLibrary, "the bundled commands, no device data")
            .WithoutDevices(CommanderMethods.ListSaved, "the saved commands, never their passwords")
            .WithoutDevices(CommanderMethods.Export, "saved commands as a file, never their passwords", "{}")
            .AllDevicesOnly(CommanderMethods.Save, """{"command":{}}""")
            .AllDevicesOnly(CommanderMethods.Delete, """{"id":"x"}""")
            .AllDevicesOnly(CommanderMethods.Import, """{"json":"{}"}""")
            .Method(CommanderMethods.TryRequest, $$"""{"deviceId":"{device}","command":{{Brand}}}""")
            .Method(CommanderMethods.CheckCompatibility, $$"""{"deviceIds":["{inside}","{device}"],"commands":[{{Brand}}]}""")
            .Method(CommanderMethods.Rollout, $$"""{"deviceIds":["{inside}","{device}"],"commands":[{"command":{{Brand}}}]}""")
            .RunAsync();
    }

    [Fact]
    public async Task A_rollout_of_a_group_user_is_the_users_own_on_the_users_devices()
    {
        var ctx = new SdkTesting.FakeCoreContext();
        var inside = ctx.Add(new SdkTesting.FakeDevice());
        var plugin = new VapixCommanderPlugin(Samples.Directory);
        await plugin.StartAsync(ctx, CancellationToken.None);
        var call = ctx.CallAsGroup([inside.Id], "tech2");

        var reply = CommandJson.Read<RolloutReply>(await ctx.InvokeAsync(plugin, call, CommanderMethods.Rollout,
            $$"""{"deviceIds":["{{inside.Id}}"],"commands":[{"command":{{Brand}}}],"owner":"admin"}"""));

        Assert.Null(reply.Error);
        var run = Assert.Single(ctx.TaskRuns);
        Assert.Equal([inside.Id], run.DeviceIds);
        Assert.Equal("tech2", run.Owner); // the caller, never the name the page sent
        Assert.Empty(ctx.RefusedAccesses);
    }

    [Fact]
    public async Task A_task_started_by_hand_never_joins_a_rollout_of_another_user()
    {
        var ctx = new SdkTesting.FakeCoreContext();
        var inside = ctx.Add(new SdkTesting.FakeDevice());
        var plugin = new VapixCommanderPlugin(Samples.Directory);
        await plugin.StartAsync(ctx, CancellationToken.None);
        var tech2 = ctx.CallAsGroup([inside.Id], "tech2");
        await ctx.InvokeAsync(plugin, tech2, CommanderMethods.Rollout, $$"""{"deviceIds":["{{inside.Id}}"],"commands":[{"command":{{Brand}}}],"stopOnFirstError":true}""");
        var payload = Assert.Single(ctx.TaskRuns).PayloadJson;
        var task = plugin.TaskPlugins.Single();

        // Another user names tech2's rollout in a task of their own: refused before the device is asked.
        var camera = new SdkTesting.FakeCamera();
        var eve = new CallerTaskContext(camera, ctx.CallAsGroup([inside.Id], "eve"));
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => task.ExecuteAsync(eve, inside, payload, CancellationToken.None));
        Assert.Equal(RolloutTaskPlugin.ForeignRolloutText, refused.Message);
        Assert.Empty(camera.Requests);
        Assert.False(plugin.Rollouts.All.Single().IsAborted);

        // tech2's own task joins it.
        var own = new CallerTaskContext(new SdkTesting.FakeCamera(), ctx.CallAsGroup([inside.Id], "tech2"));
        try
        {
            await task.ExecuteAsync(own, inside, payload, CancellationToken.None);
        }
        catch (Exception ex) when (ex.Message != RolloutTaskPlugin.ForeignRolloutText)
        {
        }

        Assert.NotEmpty(((SdkTesting.FakeCamera)own.Vapix).Requests);
    }

    /// <summary>A task's context with the user who started it.</summary>
    private sealed class CallerTaskContext(SdkTesting.FakeCamera camera, ICallContext caller) : ITaskExecutionContext
    {
        private readonly SdkTesting.FakeTaskContext _inner = new(camera);

        public Guid TaskId => _inner.TaskId;

        public Oadm.Sdk.Vapix.IVapixClient Vapix => _inner.Vapix;

        public Microsoft.Extensions.Logging.ILogger Logger => _inner.Logger;

        public ICorePlugin? Owner => null;

        public IUploadedFiles Files => _inner.Files;

        public ICallContext? Caller => caller;

        public void ReportProgress(int percent, string? message = null) => _inner.ReportProgress(percent, message);

        public void ReportWarning(string message) => _inner.ReportWarning(message);

        public void Log(TaskLogLevel level, string message) => _inner.Log(level, message);

        public void PlanSteps(params string[] names) => ((ITaskExecutionContext)_inner).PlanSteps(names);

        public ITaskStep BeginStep(string name) => ((ITaskExecutionContext)_inner).BeginStep(name);
    }

    [Fact]
    public async Task A_group_user_runs_a_saved_command_only_with_the_passwords_typed()
    {
        var host = new FakeCoreContext(); // with the master key: the saved password is stored
        var plugin = new VapixCommanderPlugin(Samples.Directory);
        await plugin.StartAsync(host, CancellationToken.None);
        var command = Samples.Parse("""
            { "id": "custom.pw", "version": 1, "name": "Password test", "category": "Custom", "requires": [ { "api": "param-cgi", "minVersion": "1.0" } ],
              "writes": false, "fields": [ { "name": "pwd", "label": "Password", "type": "password" } ],
              "request": { "method": "POST", "path": "/x", "bodyType": "form", "body": { "pwd": "{{pwd}}" } }, "response": { "kind": "text" } }
            """);
        command.Fields[0].Default = JsonSerializer.SerializeToElement("Stored1!");
        Assert.Null(CommandJson.Read<SaveCommandReply>(await plugin.InvokeAsync(CommanderMethods.Save, CommandJson.Write(new SaveCommandRequest { Command = command }), CancellationToken.None)).Error);

        var users = new SdkTesting.FakeCoreContext();
        var device = users.Add(new SdkTesting.FakeDevice());
        var group = users.CallAsGroup([device.Id], "tech2");
        var saved = new RolloutCommand { Command = new CommandRef { Source = CommandSources.Saved, Id = "custom.pw" } };

        // The stored password: refused, nothing starts, nothing is sent.
        var refused = CommandJson.Read<RolloutReply>(await plugin.InvokeAsync(group, CommanderMethods.Rollout,
            CommandJson.Write(new RolloutRequest { DeviceIds = [device.Id], Commands = [saved] }), CancellationToken.None));
        Assert.Contains(CommanderTexts.SavedPasswordsRefused, refused.Error, StringComparison.Ordinal);
        var tried = CommandJson.Read<TryCommandReply>(await plugin.InvokeAsync(group, CommanderMethods.TryRequest,
            CommandJson.Write(new TryCommandRequest { DeviceId = device.Id, Command = saved.Command }), CancellationToken.None));
        Assert.Equal(CommanderTexts.SavedPasswordsRefused, tried.Error);
        Assert.Empty(users.TaskRuns);
        Assert.Equal(0, users.ClientsCreated.GetValueOrDefault(device.Id));

        // The password typed: it runs with the typed one.
        var typed = new RolloutCommand { Command = saved.Command, Values = Samples.Values(("pwd", "Typed2!")) };
        var started = CommandJson.Read<RolloutReply>(await plugin.InvokeAsync(group, CommanderMethods.Rollout,
            CommandJson.Write(new RolloutRequest { DeviceIds = [device.Id], Commands = [typed] }), CancellationToken.None));
        Assert.Null(started.Error);
        var payload = Assert.Single(users.TaskRuns).PayloadJson!;
        Assert.Contains("Typed2!", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("Stored1!", payload, StringComparison.Ordinal);

        // A user with all devices still uses the stored one.
        var admin = users.CallAsAllDevices();
        await plugin.InvokeAsync(admin, CommanderMethods.Rollout, CommandJson.Write(new RolloutRequest { DeviceIds = [device.Id], Commands = [saved] }), CancellationToken.None);
        Assert.Contains("Stored1!", users.TaskRuns[^1].PayloadJson!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_page_of_a_group_user_hides_changing_the_saved_commands()
    {
        var page = await PageFixture.CreateAsync();
        Assert.True(page.Vm.MaySaved);

        page.Client.HasAllDevices = false;

        Assert.False(page.Vm.MaySaved);
        Assert.True(page.Vm.MaySend);
        Assert.True(page.Vm.MayRollout);
    }

    [Fact]
    public void Changing_the_saved_commands_is_for_users_with_all_devices()
    {
        var plugin = new VapixCommanderPlugin(Samples.Directory);

        Assert.True(plugin.RequiresAllDevices(CommanderMethods.Save));
        Assert.True(plugin.RequiresAllDevices(CommanderMethods.Delete));
        Assert.True(plugin.RequiresAllDevices(CommanderMethods.Import));
        Assert.False(plugin.RequiresAllDevices(CommanderMethods.Rollout));
        Assert.False(plugin.RequiresAllDevices(CommanderMethods.Export));
    }

    [Fact]
    public async Task Unknown_devices_of_a_user_with_all_devices_answer_as_before()
    {
        var ctx = new SdkTesting.FakeCoreContext();
        var plugin = new VapixCommanderPlugin(Samples.Directory);
        await plugin.StartAsync(ctx, CancellationToken.None);
        var admin = ctx.CallAsAllDevices();

        var tried = CommandJson.Read<TryCommandReply>(await ctx.InvokeAsync(plugin, admin, CommanderMethods.TryRequest,
            $$"""{"deviceId":"{{Guid.NewGuid()}}","command":{{Brand}}}"""));
        var compatibility = CommandJson.Read<CompatibilityReply>(await ctx.InvokeAsync(plugin, admin, CommanderMethods.CheckCompatibility,
            $$"""{"deviceIds":["{{Guid.NewGuid()}}"],"commands":[{{Brand}}]}"""));

        Assert.Equal(DeviceMessages.NoLongerAvailable(1), tried.Error);
        Assert.Equal("Device not found", Assert.Single(Assert.Single(compatibility.Devices).Commands).Text);
    }
}
