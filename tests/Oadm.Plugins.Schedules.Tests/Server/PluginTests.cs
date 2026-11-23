using System.Text.Json;

using Microsoft.Extensions.Time.Testing;

using Oadm.Plugins.Provisioning.Model;
using Oadm.Plugins.Schedules.Engine;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Schedules.Tests.Server;

/// <summary>The page's methods (docs/spec/schedules.md "Rights and audit", "The page", "MCP"): rights, groups, checks, tools.</summary>
public sealed class PluginTests : IAsyncLifetime
{
    private readonly FakeTimeProvider _time = new(EngineHarness.Start);
    private readonly FakeCoreContext _ctx = new() { EventPublisher = new FakeEventPublisher() };
    private readonly FakeUserCalls _users = new();
    private SchedulesPlugin _plugin = null!;

    public async Task InitializeAsync()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.Utc);
        _ctx.UserCalls = _users;
        _plugin = new SchedulesPlugin(new SchedulesOptions(), _time);
        await _plugin.StartAsync(_ctx, CancellationToken.None);
    }

    public async Task DisposeAsync() => await _plugin.DisposeAsync();

    private static JsonSerializerOptions Json => SchedulesJson.Options;

    private async Task<T> CallAsync<T>(ICallContext call, string method, object? payload = null) =>
        PluginJson.Parse<T>(await _ctx.InvokeAsync(_plugin, call, method, payload is null ? null : JsonSerializer.Serialize(payload, Json)), Json);

    private static Schedule NewSchedule(string name, ScheduleDevices? devices = null, params FlowNode[] actions) => new()
    {
        Name = name,
        Devices = devices ?? new ScheduleDevices { Kind = ScheduleDevicesKind.All },
        When = new ScheduleWhen { Recurrence = Recurrence.Daily, Time = new TimeOnly(23, 0) },
        Actions = actions.Length > 0 ? actions : [EngineHarness.Action("a1", NodeKind.Restart)],
    };

    [Fact]
    public void Rights_show_for_operators_manage_off_and_changes_audited()
    {
        var show = _plugin.Permissions.Single(p => p.Key == PermissionKeys.Show);
        var manage = _plugin.Permissions.Single(p => p.Key == PermissionKeys.Manage);
        Assert.Equal((true, false), (show.OperatorDefault, show.ViewerDefault));
        Assert.Equal((false, false), (manage.OperatorDefault, manage.ViewerDefault));
        Assert.Equal(PermissionKeys.Show, _plugin.RequiredPermission(SchedulesMethods.List));
        Assert.Equal(PermissionKeys.Show, _plugin.RequiredPermission(SchedulesMethods.Policies));
        Assert.Equal(PermissionKeys.Manage, _plugin.RequiredPermission(SchedulesMethods.Save));
        Assert.Equal(PermissionKeys.Manage, _plugin.RequiredPermission(SchedulesMethods.RunNow));
        Assert.Equal(PermissionKeys.Manage, _plugin.RequiredPermission("tool.run_now"));
        Assert.True(_plugin.IsAudited(SchedulesMethods.RunNow));
        Assert.False(_plugin.IsAudited("tool.save.preview"));
        Assert.False(_plugin.IsAudited(SchedulesMethods.List));
        Assert.Equal((CorePluginGroup.Automation, true, "calendarClock"), (_plugin.Group, _plugin.HasPage, _plugin.IconKey));
        Assert.Equal("Created schedule Nightly", _plugin.AuditDetail(SchedulesMethods.Save, JsonSerializer.Serialize(new ScheduleRequest(NewSchedule("Nightly")), Json)));
        var task = Assert.Single(_plugin.TaskPlugins);
        Assert.Equal((SchedulesInfo.TaskPluginId, false), (task.Id, task.ShowInMenus));
        Assert.Equal(SchedulesInfo.RunEndedEvent, Assert.Single(_plugin.EventTypes).Id);
    }

    [Fact]
    public async Task Saving_needs_the_right_of_each_action_and_makes_the_saver_the_owner()
    {
        var device = _ctx.Add(new FakeDevice());
        var withoutRestart = new TestCall(_ctx.CallAsAllDevices("olaf"), new HashSet<string> { ActionCatalog.Manage.Permission, ActionCatalog.Upgrade.Permission });
        var refused = await Assert.ThrowsAsync<ArgumentException>(() => CallAsync<ScheduleReply>(withoutRestart, SchedulesMethods.Save, new ScheduleRequest(NewSchedule("Nightly"))));
        Assert.Equal("Your role does not allow: Restart.", refused.Message);
        var check = await CallAsync<ScheduleCheck>(withoutRestart, SchedulesMethods.Check, new ScheduleRequest(NewSchedule("Nightly")));
        Assert.Equal("Your role does not allow: Restart.", check.ActionsError);
        Assert.Equal(1, check.DeviceCount);

        var anna = _ctx.CallAsAllDevices("anna");
        var saved = await CallAsync<ScheduleReply>(anna, SchedulesMethods.Save, new ScheduleRequest(NewSchedule("Nightly")));
        Assert.Equal("anna", saved.Schedule.Owner);
        Assert.Equal(new DateTimeOffset(2026, 11, 23, 23, 0, 0, TimeSpan.Zero), saved.Check.NextRun);
        var duplicate = await Assert.ThrowsAsync<ArgumentException>(() => CallAsync<ScheduleReply>(anna, SchedulesMethods.Save, new ScheduleRequest(NewSchedule("nightly"))));
        Assert.Equal("A schedule named nightly exists already.", duplicate.Message);

        // Not Network, Users or any kind outside the actions.
        var network = await CallAsync<ScheduleCheck>(anna, SchedulesMethods.Check, new ScheduleRequest(NewSchedule("Net", null, EngineHarness.Action("n1", NodeKind.Network))));
        Assert.Equal(ActionCatalog.NotAnAction, network.ActionErrors["n1"]);
        Assert.Equal(ActionKinds.All, (await CallAsync<IReadOnlyList<ActionKindInfo>>(anna, SchedulesMethods.ActionKinds)).Select(k => k.Kind));
        Assert.Equal("Restart the device", (await CallAsync<IReadOnlyList<ActionKindInfo>>(anna, SchedulesMethods.ActionKinds)).Single(k => k.Kind == NodeKind.Restart).StepName);
        _ = device;
    }

    [Fact]
    public async Task The_estimate_warns_when_a_run_takes_longer_than_the_time_between_runs()
    {
        foreach (var i in Enumerable.Range(0, 200))
        {
            _ctx.Add(new FakeDevice { Address = $"10.0.1.{i}", Serial = $"ACCC8E0001{i:X2}" });
        }

        var anna = _ctx.CallAsAllDevices("anna");
        var check = await CallAsync<ScheduleCheck>(anna, SchedulesMethods.Check, new ScheduleRequest(NewSchedule("Upgrade", null, EngineHarness.Action("u1", NodeKind.UpgradeOnline)) with { Stages = true }));

        // 200 devices: stages 1, 4, 10, 20, 40, 125; 16 at once: 1+1+1+2+3+8 waves of 10 min.
        Assert.Equal([1, 4, 10, 20, 40, 125], check.StageSizes);
        Assert.Equal(TimeSpan.FromMinutes(160), check.Estimate);
        Assert.Null(check.WhenWarning);

        var weeklyShort = NewSchedule("Upgrade", null, EngineHarness.Action("u1", NodeKind.UpgradeOnline)) with
        {
            When = new ScheduleWhen { Recurrence = Recurrence.Weekly, Days = [DayOfWeek.Monday, DayOfWeek.Tuesday], Time = new TimeOnly(23, 0) },
            Stages = false,
        };
        var more = Enumerable.Range(0, 3_000).Select(i => _ctx.Add(new FakeDevice { Address = $"10.1.{i / 256}.{i % 256}", Serial = $"ACCC8E1{i:X5}" })).Count();
        var warned = await CallAsync<ScheduleCheck>(anna, SchedulesMethods.Check, new ScheduleRequest(weeklyShort));
        Assert.Equal(200 + more, warned.DeviceCount);
        Assert.Equal("A run takes about 33 h 20 min, longer than the 24 h between runs. A start during a run is skipped.", warned.WhenWarning);
        Assert.True(warned.IsValid);
    }

    [Fact]
    public async Task Users_with_a_device_group_see_only_the_schedules_of_their_group()
    {
        var inside = _ctx.Add(new FakeDevice { Address = "10.0.0.1", Serial = "ACCC8E000001" });
        _ctx.Add(new FakeDevice { Address = "10.0.0.2", Serial = "ACCC8E000002" });
        var admin = _ctx.CallAsAllDevices("admin");
        var olaf = FakeCallContext.WithGroup(_ctx, [inside.Id], "olaf", "g1");
        var per = FakeCallContext.WithGroup(_ctx, [inside.Id], "per", "g1");
        var other = FakeCallContext.WithGroup(_ctx, [inside.Id], "eva", "g2");
        _users.Set("admin", admin).Set("olaf", olaf);

        var all = await CallAsync<ScheduleReply>(admin, SchedulesMethods.Save, new ScheduleRequest(NewSchedule("All")));
        var group = await CallAsync<ScheduleReply>(olaf, SchedulesMethods.Save, new ScheduleRequest(NewSchedule("Group", new ScheduleDevices { DeviceIds = [inside.Id] })));

        Assert.Equal(["All", "Group"], (await CallAsync<SchedulesReply>(admin, SchedulesMethods.List)).Schedules.Select(s => s.Name));
        Assert.Equal(["Group"], (await CallAsync<SchedulesReply>(per, SchedulesMethods.List)).Schedules.Select(s => s.Name));
        Assert.Empty((await CallAsync<SchedulesReply>(other, SchedulesMethods.List)).Schedules);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => CallAsync<ScheduleReply>(per, SchedulesMethods.Get, new IdRequest(all.Schedule.Id)));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => CallAsync<ScheduleRun>(other, SchedulesMethods.RunNow, new IdRequest(group.Schedule.Id)));
        Assert.Equal("Group", (await CallAsync<ScheduleReply>(per, SchedulesMethods.Get, new IdRequest(group.Schedule.Id))).Schedule.Name);

        // The changed event reaches users with all devices and the owner's group.
        Assert.Contains(_ctx.PublishedEvents, e => e.Topic == SchedulesMethods.ChangedTopic && e.Audience.Kind == PluginEventAudienceKind.DeviceScope && e.Audience.ScopeKey == "g1");
    }

    [Fact]
    public async Task Run_now_runs_as_the_owner_and_turned_off_schedules_show_no_next_run()
    {
        var device = _ctx.Add(new FakeDevice());
        _users.Set("anna", _ctx.CallAsAllDevices("anna"));
        var anna = _ctx.CallAsAllDevices("anna");
        var saved = await CallAsync<ScheduleReply>(anna, SchedulesMethods.Save, new ScheduleRequest(NewSchedule("Nightly")));

        var run = await CallAsync<ScheduleRun>(_ctx.CallAsAllDevices("olaf"), SchedulesMethods.RunNow, new IdRequest(saved.Schedule.Id));
        Assert.Equal("olaf", run.StartedBy);
        await Eventually.TrueAsync(() => _ctx.TaskRuns.Any(r => r.PluginId == SchedulesInfo.TaskPluginId && r.DeviceIds.Contains(device.Id)), "the device's task started");
        Assert.Contains("anna", _users.Asked);

        var off = await CallAsync<ScheduleSummary>(anna, SchedulesMethods.TurnOn, new TurnOnRequest(saved.Schedule.Id, false));
        Assert.Null(off.NextRun);
        var runs = await CallAsync<RunsReply>(anna, SchedulesMethods.Runs, new IdRequest(saved.Schedule.Id));
        Assert.Equal(run.Id, Assert.Single(runs.Runs).Id);
    }

    [Fact]
    public async Task A_schedule_whose_owner_lost_a_right_shows_why()
    {
        _users.Set("anna", new TestCall(_ctx.CallAsAllDevices("anna"), new HashSet<string> { ActionCatalog.Manage.Permission }));
        var saved = await CallAsync<ScheduleReply>(_ctx.CallAsAllDevices("anna"), SchedulesMethods.Save, new ScheduleRequest(NewSchedule("Nightly")));
        var summary = Assert.Single((await CallAsync<SchedulesReply>(_ctx.CallAsAllDevices("admin"), SchedulesMethods.List)).Schedules);
        Assert.Equal((saved.Schedule.Id, "The owner no longer has the right Restart."), (summary.Id, summary.Warning));
    }

    [Fact]
    public async Task An_access_key_cannot_own_or_run_a_schedule()
    {
        _ctx.Add(new FakeDevice());
        var anna = _ctx.CallAsAllDevices("anna");
        var saved = await CallAsync<ScheduleReply>(anna, SchedulesMethods.Save, new ScheduleRequest(NewSchedule("Nightly")));
        var key = _ctx.CallAsAllDevices("MCP bot");

        var save = await Assert.ThrowsAsync<InvalidOperationException>(() => CallAsync<ScheduleReply>(key, SchedulesMethods.Save, new ScheduleRequest(NewSchedule("Other"))));
        Assert.Equal(SchedulesPlugin.KeyCannotOwn, save.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => CallAsync<ScheduleRun>(key, SchedulesMethods.RunNow, new IdRequest(saved.Schedule.Id)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => CallAsync<ScheduleSummary>(key, SchedulesMethods.TurnOn, new TurnOnRequest(saved.Schedule.Id, true)));
        var preview = await Assert.ThrowsAsync<InvalidOperationException>(() => _ctx.InvokeAsync(_plugin, _ctx.CallAsAllDevices("mcp Bot"), "tool.run_now.preview", JsonSerializer.Serialize(new { id = saved.Schedule.Id })));
        Assert.Equal(SchedulesPlugin.KeyCannotOwn, preview.Message);

        // Reading and turning off stay possible.
        Assert.Single((await CallAsync<SchedulesReply>(key, SchedulesMethods.List)).Schedules);
        Assert.False((await CallAsync<ScheduleSummary>(key, SchedulesMethods.TurnOn, new TurnOnRequest(saved.Schedule.Id, false))).On);
        Assert.Equal("anna", RunningOwner(saved.Schedule.Id));
    }

    private string? RunningOwner(Guid id) => _plugin.RunningStore.Find(id)?.Schedule.Owner;

    [Fact]
    public async Task Visibility_follows_the_owners_current_view_and_a_run_stores_it()
    {
        var inside = _ctx.Add(new FakeDevice { Address = "10.0.0.1", Serial = "ACCC8E000001" });
        var olaf = FakeCallContext.WithGroup(_ctx, [inside.Id], "olaf", "g1");
        var per = FakeCallContext.WithGroup(_ctx, [inside.Id], "per", "g1");
        _users.Set("olaf", olaf);
        var saved = await CallAsync<ScheduleReply>(olaf, SchedulesMethods.Save, new ScheduleRequest(NewSchedule("Group", new ScheduleDevices { DeviceIds = [inside.Id] })));
        Assert.Single((await CallAsync<SchedulesReply>(per, SchedulesMethods.List)).Schedules);

        // Olaf moves to another group: after the owner check's cache time per no longer sees or runs it.
        _users.Set("olaf", FakeCallContext.WithGroup(_ctx, [inside.Id], "olaf", "g2"));
        _time.Advance(TimeSpan.FromSeconds(31));
        Assert.Empty((await CallAsync<SchedulesReply>(per, SchedulesMethods.List)).Schedules);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => CallAsync<ScheduleRun>(per, SchedulesMethods.RunNow, new IdRequest(saved.Schedule.Id)));

        // A run stores the owner's current view (the page's event goes there).
        await CallAsync<ScheduleRun>(_ctx.CallAsAllDevices("admin"), SchedulesMethods.RunNow, new IdRequest(saved.Schedule.Id));
        await Eventually.TrueAsync(() => _plugin.RunningStore.Find(saved.Schedule.Id)?.OwnerScopeKey == "g2", "the run stored the owner's view");
    }

    [Fact]
    public async Task The_list_asks_the_users_once_per_owner_within_30_seconds()
    {
        _ctx.Add(new FakeDevice());
        _users.Set("anna", _ctx.CallAsAllDevices("anna"));
        var anna = _ctx.CallAsAllDevices("anna");
        foreach (var name in new[] { "One", "Two", "Three" })
        {
            await CallAsync<ScheduleReply>(anna, SchedulesMethods.Save, new ScheduleRequest(NewSchedule(name)));
        }

        await CallAsync<SchedulesReply>(anna, SchedulesMethods.List);
        await CallAsync<SchedulesReply>(anna, SchedulesMethods.List);
        Assert.Single(_users.Asked);

        _time.Advance(TimeSpan.FromSeconds(31));
        await CallAsync<SchedulesReply>(anna, SchedulesMethods.List);
        Assert.Equal(2, _users.Asked.Count);
    }

    [Fact]
    public async Task Every_method_keeps_to_the_callers_group()
    {
        var inside = _ctx.Add(new FakeDevice { Address = "10.0.0.48" });
        var outside = _ctx.Add(new FakeDevice { Address = "10.0.0.49", Serial = "ACCC8E000002" });
        const string schedule = """{"schedule":{"name":"Canary","devices":{"kind":"chosen","deviceIds":["{device}"]},"when":{"recurrence":"daily","time":"23:00:00"},"actions":[{"id":"a1","kind":"restart"}]}}""";
        const string id = """{"id":"00000000-0000-0000-0000-000000000001"}""";
        await new PluginCanary(_ctx, _plugin, inside, outside)
            .Method(SchedulesMethods.Check, schedule)
            .Method(SchedulesMethods.Save, schedule)
            .WithoutDevices(SchedulesMethods.List, "the schedules of the caller's group")
            .WithoutDevices(SchedulesMethods.Get, "a schedule of the caller's group by id", id)
            .WithoutDevices(SchedulesMethods.ActionKinds, "the action kinds")
            .WithoutDevices(SchedulesMethods.Delete, "a schedule of the caller's group by id", id)
            .WithoutDevices(SchedulesMethods.TurnOn, "a schedule of the caller's group by id", """{"id":"00000000-0000-0000-0000-000000000001","on":false}""")
            .WithoutDevices(SchedulesMethods.RunNow, "runs as the owner, in the background", id)
            .WithoutDevices(SchedulesMethods.Runs, "the runs of a schedule of the caller's group", id)
            .WithoutDevices(SchedulesMethods.RunFailures, "the failed devices of a run of the caller's group", """{"scheduleId":"00000000-0000-0000-0000-000000000001","runId":"00000000-0000-0000-0000-000000000002"}""")
            .WithoutDevices(SchedulesMethods.Policies, "PKI's policies")
            .WithoutDevices(SchedulesMethods.VapixCommands, "the command library")
            .WithoutDevices(SchedulesMethods.StoreFile, "the caller's own upload", """{"uploadId":"0123456789abcdef0123456789abcdef"}""")
            .WithoutDevices(SchedulesMethods.ReadConfigFile, "a kept file", id)
            .RunAsync();
    }

    [Fact]
    public async Task The_tools_preview_and_save_a_schedule_with_the_pages_checks()
    {
        _ctx.Add(new FakeDevice());
        var anna = _ctx.CallAsAllDevices("anna");
        Assert.Equal(
            ["list", "get", "runs", "actions", "save", "delete", "turn_on", "turn_off", "run_now", "file_store"],
            _plugin.Tools.Select(t => t.Name));
        const string args = """{"name":"Nightly","devices":{"all":true},"when":{"recurrence":"weekly","time":"23:00","days":["monday"],"window_end":"05:00"},"actions":[{"id":"a1","kind":"restart"}],"stages":true}""";
        using (var plan = JsonDocument.Parse((await _ctx.InvokeAsync(_plugin, anna, "tool.save.preview", args))!))
        {
            Assert.Equal("Nightly", plan.RootElement.GetProperty("name").GetString());
            Assert.Equal(1, plan.RootElement.GetProperty("devices").GetInt32());
            Assert.Equal(1, plan.RootElement.GetProperty("will_change").GetInt32());
        }

        Assert.Empty((await CallAsync<SchedulesReply>(anna, SchedulesMethods.List)).Schedules);
        await _ctx.InvokeAsync(_plugin, anna, "tool.save", args);
        var saved = Assert.Single((await CallAsync<SchedulesReply>(anna, SchedulesMethods.List)).Schedules);
        Assert.Equal((Recurrence.Weekly, new TimeOnly(5, 0), true), (saved.When.Recurrence, saved.When.WindowEnd!.Value, saved.Stages));

        using var list = JsonDocument.Parse((await _ctx.InvokeAsync(_plugin, anna, "tool.list", "{}"))!);
        Assert.Equal(1, list.RootElement.GetProperty("total").GetInt32());
        Assert.Equal("Mondays 23:00, no new device after 05:00", list.RootElement.GetProperty("schedules")[0].GetProperty("when").GetString());

        var bad = await _ctx.InvokeAsync(_plugin, anna, "tool.save.preview", args.Replace("\"23:00\"", "\"11pm\"", StringComparison.Ordinal));
        Assert.Contains("when.time", bad, StringComparison.Ordinal);
    }
}
