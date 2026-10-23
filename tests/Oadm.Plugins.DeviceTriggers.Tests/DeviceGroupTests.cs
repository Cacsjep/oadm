using PluginCanary = Oadm.Sdk.Testing.PluginCanary;
using Testing = Oadm.Sdk.Testing;

namespace Oadm.Plugins.DeviceTriggers.Tests;

/// <summary>
/// Device groups (docs/spec/device-scope.md, Shared objects): every trigger belongs to the device scope of the user who
/// made it and watches only that scope's devices; group users see and change only their group's triggers; the editor's
/// reads are jobs of the user who started them.
/// </summary>
public sealed class DeviceGroupTests
{
    [Fact]
    public async Task NoMethodReachesADeviceOutsideTheUsersGroup()
    {
        var ctx = new Testing.FakeCoreContext();
        var inside = ctx.Add(new Testing.FakeDevice { Address = "10.0.0.1" });
        var outside = ctx.Add(new Testing.FakeDevice { Address = "10.0.0.2" });
        await using var plugin = new DeviceTriggersPlugin(Options());
        await plugin.StartAsync(ctx, CancellationToken.None);

        await new PluginCanary(ctx, plugin, inside, outside)
            .Method(DeviceTriggersMethods.StartRead, """{"deviceIds":["{inside}","{device}"]}""")
            .Method(DeviceTriggersMethods.Save, """{"trigger":{"name":"Door","topic":"tns1:Device/tnsaxis:IO/VirtualInput","enabled":false,"deviceIds":["{inside}","{device}"]}}""")
            .Job(DeviceTriggersMethods.ReadStatus, """{"jobId":"{job}"}""", async owner =>
                TriggerJson.Deserialize<ReadStatusReply>(await ctx.InvokeAsync(plugin, owner, DeviceTriggersMethods.StartRead, $$"""{"deviceIds":["{{inside.Id}}"]}""")).JobId)
            .WithoutDevices(DeviceTriggersMethods.List, "the triggers of the caller's own group")
            .WithoutDevices(DeviceTriggersMethods.Get, "a trigger of the caller's own group (others answer like unknown ones)", """{"id":"00000000-0000-0000-0000-000000000001"}""")
            .WithoutDevices(DeviceTriggersMethods.Delete, "only a trigger of the caller's own group", """{"id":"00000000-0000-0000-0000-000000000001"}""")
            .WithoutDevices(DeviceTriggersMethods.SetEnabled, "only a trigger of the caller's own group", """{"id":"00000000-0000-0000-0000-000000000001","enabled":true}""")
            .WithoutDevices(DeviceTriggersMethods.Duplicate, "only a trigger of the caller's own group", """{"id":"00000000-0000-0000-0000-000000000001"}""")
            .RunAsync();
    }

    [Fact]
    public async Task AGroupsTriggerWatchesOnlyTheGroupsDevicesAndOnlyItsUsersSeeIt()
    {
        var ctx = new Testing.FakeCoreContext();
        var a = ctx.Add(new Testing.FakeDevice { Address = "10.0.0.1" });
        var b = ctx.Add(new Testing.FakeDevice { Address = "10.0.0.2" });
        var keyA = ctx.FakeScopes.Add("Building A", [a.Id]);
        var keyB = ctx.FakeScopes.Add("Building B", [b.Id]);
        await using var plugin = new DeviceTriggersPlugin(Options());
        await plugin.StartAsync(ctx, CancellationToken.None);
        var anna = Testing.FakeCallContext.WithGroup(ctx, [a.Id], "anna", keyA);
        var ben = Testing.FakeCallContext.WithGroup(ctx, [b.Id], "ben", keyB);
        var admin = ctx.CallAsAllDevices();

        // "All devices" of a group's trigger is the group's devices.
        var saved = await SaveAsync(ctx, plugin, anna, new TriggerDefinition { Name = "Door", Topic = "t", Enabled = false, AllDevices = true });
        Assert.Equal(keyA, saved.Owner);
        Assert.All(plugin.EventTypes, t => Assert.Equal([a.Id], t.DeviceIds!));
        Assert.Empty(ctx.RefusedAccesses);

        // The same name in another group; each group sees only its own; the administrator every trigger.
        var other = await SaveAsync(ctx, plugin, ben, new TriggerDefinition { Name = "Door", Topic = "t", Enabled = false, DeviceIds = [b.Id] });
        Assert.Equal([saved.Id], (await ListAsync(ctx, plugin, anna)).Select(t => t.Id));
        Assert.Equal([other.Id], (await ListAsync(ctx, plugin, ben)).Select(t => t.Id));
        Assert.Equal(2, (await ListAsync(ctx, plugin, admin)).Count);
        Assert.Null(TriggerJson.Deserialize<GetReply>(await ctx.InvokeAsync(plugin, ben, DeviceTriggersMethods.Get, TriggerJson.Serialize(new IdRequest(saved.Id)))).Trigger);
        await ctx.InvokeAsync(plugin, ben, DeviceTriggersMethods.Delete, TriggerJson.Serialize(new IdRequest(saved.Id)));
        Assert.Equal(2, plugin.Triggers.Count);

        // The administrator's edit keeps the owner; a duplicate belongs to the user who duplicates.
        var edited = await SaveAsync(ctx, plugin, admin, saved with { Name = "Door 2" });
        Assert.Equal(keyA, edited.Owner);
        var copy = TriggerJson.Deserialize<SaveReply>(await ctx.InvokeAsync(plugin, admin, DeviceTriggersMethods.Duplicate, TriggerJson.Serialize(new IdRequest(saved.Id)))).Trigger!;
        Assert.Null(copy.Owner);

        // A deleted group's trigger watches nothing.
        ctx.FakeScopes.Delete(keyA);
        await Testing.Eventually.TrueAsync(() => plugin.EventTypes.Where(t => t.Id == DeviceTriggersPluginInfo.TypeId(saved.Id)).All(t => t.DeviceIds!.Count == 0), "the trigger of the deleted group has no devices");
    }

    private static async Task<TriggerDefinition> SaveAsync(Testing.FakeCoreContext ctx, DeviceTriggersPlugin plugin, Testing.FakeCallContext call, TriggerDefinition trigger)
    {
        var reply = TriggerJson.Deserialize<SaveReply>(await ctx.InvokeAsync(plugin, call, DeviceTriggersMethods.Save, TriggerJson.Serialize(new SaveRequest(trigger))));
        return reply.Trigger ?? throw new InvalidOperationException(reply.Error ?? string.Join(", ", reply.Errors?.Values ?? []));
    }

    private static async Task<IReadOnlyList<TriggerSummary>> ListAsync(Testing.FakeCoreContext ctx, DeviceTriggersPlugin plugin, Testing.FakeCallContext call) =>
        TriggerJson.Deserialize<ListReply>(await ctx.InvokeAsync(plugin, call, DeviceTriggersMethods.List, null)).Triggers;

    private static Runtime.DeviceTriggersOptions Options() => new() { ResolveInterval = TimeSpan.FromHours(1) };
}
