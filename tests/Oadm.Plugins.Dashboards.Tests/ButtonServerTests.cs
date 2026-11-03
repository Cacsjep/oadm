using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using NSubstitute;

using Oadm.Sdk.Events;
using Oadm.Sdk.Plugins;

using Testing = Oadm.Sdk.Testing;

namespace Oadm.Plugins.Dashboards.Tests;

/// <summary>
/// Button widgets on the server: each is a stateless server event of the event rules that follows the saves and deletes; a
/// press fires it with who pressed it and says whether a rule uses it; the limits and the label rules; never a device.
/// </summary>
public sealed class ButtonServerTests
{
    private static Widget Button(string id, int x, int y, string label = "Open gate") => new()
    {
        Id = id,
        Kind = WidgetKind.Button,
        X = x,
        Y = y,
        Width = 4,
        Height = 2,
        Name = label,
        Color = "Accent",
    };

    private static Widget Number(string id, int x, int y) => new()
    {
        Id = id,
        Kind = WidgetKind.Number,
        X = x,
        Y = y,
        TypeId = Types.Temperature,
        Field = "temperature",
        DeviceId = Guid.NewGuid(),
    };

    private static SaveDashboardRequest New(string name, params Widget[] widgets) => new(null, name, 0, widgets);

    private static T Read<T>(string? json) => JsonSerializer.Deserialize<T>(json!, DashboardsPluginInfo.Json)!;

    [Fact]
    public async Task Buttons_are_event_types_that_follow_saves_and_deletes()
    {
        using var host = new PluginHost();
        Assert.Empty(host.Plugin.EventTypes);
        var changes = 0;
        host.Plugin.EventTypesChanged += (_, _) => changes++;

        var gate = await host.Plugin.SaveAsync(New("Gate house", Button("open", 0, 0), Button("close", 4, 0, "Close gate"), Number("t", 0, 4)), CancellationToken.None);
        Assert.Equal(1, changes);
        var types = host.Plugin.EventTypes;
        Assert.Equal(2, types.Count); // the buttons only, not the value widget
        var open = types.Single(t => t.DisplayName == "Open gate");
        Assert.Equal(DashboardsPluginInfo.ButtonTypeId(gate.Id, "open"), open.Id);
        Assert.Equal("oadm.dashboards.button." + gate.Id + ".open", open.Id);
        Assert.Equal((EventKind.Stateless, EventScope.Server, "Dashboards"), (open.Kind, open.Scope, open.Group));
        Assert.Equal(["Dashboards", "Gate house"], open.Folders);
        Assert.Equal("Someone pressed this button on the dashboard.", open.Description);
        Assert.Null(open.DeviceIds);
        Assert.Equal(["user", "dashboard", "button"], open.DataFields.Select(f => f.Name));
        Assert.Equal([true, false, false], open.DataFields.Select(f => f.Filter));

        // Two "Open gate" buttons of different dashboards: two events in two folders.
        var yard = await host.Plugin.SaveAsync(New("Yard", Button("open", 0, 0)), CancellationToken.None);
        Assert.Equal(2, changes);
        Assert.Equal(2, host.Plugin.EventTypes.Count(t => t.DisplayName == "Open gate"));
        Assert.Equal(["Gate house", "Yard"], host.Plugin.EventTypes.Where(t => t.DisplayName == "Open gate").Select(t => t.Folders[1]).Order());

        // A move changes no event; a new label or dashboard name does.
        gate = await host.Plugin.SaveAsync(new SaveDashboardRequest(gate.Id, "Gate house", gate.Version, [Button("open", 0, 2), Button("close", 4, 0, "Close gate")]), CancellationToken.None);
        Assert.Equal(2, changes);
        gate = await host.Plugin.SaveAsync(new SaveDashboardRequest(gate.Id, "Gate house", gate.Version, [Button("open", 0, 2, "Open barrier"), Button("close", 4, 0, "Close gate")]), CancellationToken.None);
        Assert.Equal(3, changes);
        Assert.Contains(host.Plugin.EventTypes, t => t.Id == open.Id && t.DisplayName == "Open barrier"); // the id stays
        gate = await host.Plugin.SaveAsync(new SaveDashboardRequest(gate.Id, "North gate", gate.Version, gate.Widgets), CancellationToken.None);
        Assert.Equal(4, changes);
        Assert.Equal(["Dashboards", "North gate"], host.Plugin.EventTypes.Single(t => t.Id == open.Id).Folders);

        // A removed button and a deleted dashboard take their events away.
        gate = await host.Plugin.SaveAsync(new SaveDashboardRequest(gate.Id, "North gate", gate.Version, [gate.Widgets[0]]), CancellationToken.None);
        Assert.Equal(5, changes);
        Assert.DoesNotContain(host.Plugin.EventTypes, t => t.DisplayName == "Close gate");
        await host.Plugin.DeleteAsync(yard.Id, CancellationToken.None);
        Assert.Equal(6, changes);
        Assert.Equal([open.Id], host.Plugin.EventTypes.Select(t => t.Id));

        // A restart offers the stored buttons at once (the host read the types before the start: the start tells it).
        var restarted = new DashboardsPlugin(new FakeTimeProvider());
        var restartChanges = 0;
        restarted.EventTypesChanged += (_, _) => restartChanges++;
        var ctx = Substitute.For<ICorePluginContext>();
        ctx.Settings.Returns(host.Settings);
        ctx.Logger.Returns(NullLogger.Instance);
        using (restarted)
        {
            Assert.Empty(restarted.EventTypes);
            await restarted.StartAsync(ctx, CancellationToken.None);
            Assert.Equal(1, restartChanges);
            Assert.Equal([open.Id], restarted.EventTypes.Select(t => t.Id));
        }
    }

    [Fact]
    public async Task A_press_fires_the_event_with_who_pressed_it_and_says_whether_a_rule_uses_it()
    {
        using var host = new PluginHost();
        var gate = await host.Plugin.SaveAsync(New("Gate house", Button("open", 0, 0)), CancellationToken.None);
        var anna = Substitute.For<ICallContext>();
        anna.UserName.Returns("anna");
        anna.HasAllDevices.Returns(true);
        anna.DeviceScopeKey.Returns(ICallContext.AllDevicesScopeKey);
        var press = JsonSerializer.Serialize(new PressRequest(gate.Id, "open"), DashboardsPluginInfo.Json);

        var reply = Read<PressReply>(await host.Plugin.InvokeAsync(anna, DashboardsMethods.Press, press, CancellationToken.None));
        Assert.False(reply.UsedByRule); // Dashboards' own subscription is no rule
        var fired = Assert.Single(host.Publisher.Fired);
        Assert.Equal(DashboardsPluginInfo.ButtonTypeId(gate.Id, "open"), fired.TypeId);
        Assert.Null(fired.DeviceId); // a button never names a device
        Assert.Equal("anna", fired.Data["user"]);
        Assert.Equal("Gate house", fired.Data["dashboard"]);
        Assert.Equal("Open gate", fired.Data["button"]);

        // The audit entry names the button and its dashboard; unknown ids and other methods keep the method name.
        Assert.Equal("Pressed Open gate on Gate house", host.Plugin.AuditDetail(DashboardsMethods.Press, press));
        Assert.Null(host.Plugin.AuditDetail(DashboardsMethods.Press, JsonSerializer.Serialize(new PressRequest(gate.Id, "gone"), DashboardsPluginInfo.Json)));
        Assert.Null(host.Plugin.AuditDetail(DashboardsMethods.Press, "not json"));
        Assert.Null(host.Plugin.AuditDetail(DashboardsMethods.Save, press));

        host.Publisher.Rules.Add(fired.TypeId);
        Assert.True(Read<PressReply>(await host.Plugin.InvokeAsync(anna, DashboardsMethods.Press, press, CancellationToken.None)).UsedByRule);
        Assert.Equal(2, host.Publisher.Fired.Count); // no lock after a press: the rule's dead time handles repeats

        // The press is not a value of the dashboards and its event is not offered as one.
        host.Feed.ChangeTypes(t => t.AddRange(host.Plugin.EventTypes.Select(i => new SubscribedEventType(i, DashboardsPluginInfo.PluginId, "Dashboards", true))));
        host.Feed.Publish(fired.TypeId, null, PublishedEventKind.Fired, ("user", "anna"));
        Assert.Null(host.Plugin.Values.Get(new ValueKey(fired.TypeId, null)));
        Assert.DoesNotContain(host.Plugin.Catalog().Events, e => e.TypeId == fired.TypeId);
    }

    [Fact]
    public async Task A_press_of_an_unknown_or_other_widget_is_refused_and_fires_nothing()
    {
        using var host = new PluginHost();
        var gate = await host.Plugin.SaveAsync(New("Gate house", Button("open", 0, 0), Number("t", 0, 4)), CancellationToken.None);

        async Task<string> Refused(PressRequest request) =>
            (await Assert.ThrowsAsync<ArgumentException>(() => host.Plugin.PressAsync(request, CancellationToken.None))).Message;

        Assert.Equal(DashboardRules.ButtonGone, await Refused(new PressRequest(gate.Id, "gone")));
        Assert.Equal(DashboardRules.ButtonGone, await Refused(new PressRequest(gate.Id, "t"))); // a value widget is no button
        Assert.Equal(DashboardRules.ButtonGone, await Refused(new PressRequest("other", "open")));
        Assert.Empty(host.Publisher.Fired);

        // A server without event rules says so.
        host.Context.EventPublisher.Returns((IEventPublisher?)null);
        var none = await Assert.ThrowsAsync<InvalidOperationException>(() => host.Plugin.PressAsync(new PressRequest(gate.Id, "open"), CancellationToken.None));
        Assert.Equal(DashboardRules.NoEventRules, none.Message);
    }

    [Fact]
    public void Pressing_is_a_right_of_its_own_and_audited()
    {
        using var host = new PluginHost();
        var press = Assert.Single(host.Plugin.Permissions, p => p.Key == DashboardsPluginInfo.PressKey);
        Assert.Equal("Press dashboard buttons", press.Name);
        Assert.True(press.OperatorDefault);
        Assert.False(press.ViewerDefault);
        Assert.False(press.FullDeviceControl);
        Assert.Equal(DashboardsPluginInfo.PressKey, host.Plugin.RequiredPermission(DashboardsMethods.Press));
        Assert.True(host.Plugin.IsAudited(DashboardsMethods.Press));
        Assert.False(host.Plugin.IsAudited(DashboardsMethods.Watch));
    }

    [Fact]
    public async Task At_most_20_buttons_and_the_label_rules_are_checked_by_the_server()
    {
        using var host = new PluginHost();

        async Task<string> Refused(params Widget[] widgets) =>
            (await Assert.ThrowsAsync<ArgumentException>(() => host.Plugin.SaveAsync(New("X", widgets), CancellationToken.None))).Message;

        var twenty = Enumerable.Range(0, 20).Select(i => Button("b" + i, i % 5 * 4, i / 5 * 2)).ToArray();
        await host.Plugin.SaveAsync(New("Twenty", twenty), CancellationToken.None);
        Assert.Equal(20, host.Plugin.EventTypes.Count);
        Assert.Equal(DashboardRules.TooManyButtons, await Refused([.. twenty, Button("b20", 0, 10)]));
        Assert.Equal("A dashboard holds at most 20 buttons.", DashboardRules.TooManyButtons);

        Assert.Equal(DashboardRules.LabelMissing, await Refused(Button("a", 0, 0, " ")));
        Assert.Equal(DashboardRules.LabelTooLong, await Refused(Button("a", 0, 0, new string('x', 33))));
        await host.Plugin.SaveAsync(New("Long", Button("a", 0, 0, new string('x', 32))), CancellationToken.None);
        Assert.Equal(DashboardRules.BadSetting, await Refused(Button("a", 0, 0) with { DeviceId = Guid.NewGuid() })); // a button has no device
        Assert.Equal(DashboardRules.BadSetting, await Refused(Button("a", 0, 0) with { TypeId = Types.Temperature }));
        await host.Plugin.SaveAsync(New("Light", Button("a", 0, 0) with { Color = "Text" }, Button("b", 4, 0) with { Color = "Secondary" }), CancellationToken.None); // every color is a fill
        Assert.Equal(DashboardRules.BadSetting, await Refused(Button("a", 0, 0) with { Color = "Gold" }));
        Assert.Equal(DashboardRules.BadSetting, await Refused(Button("a", 0, 0) with { Icon = "rocket" }));
        await host.Plugin.SaveAsync(New("Icon", Button("a", 0, 0) with { Icon = "lockOpen", Color = "Red" }), CancellationToken.None);

        // The size: Large, Medium, Small as text; an unknown one is refused.
        var sized = await host.Plugin.SaveAsync(New("Sizes", Button("a", 0, 0) with { ButtonSize = ButtonSize.Small }), CancellationToken.None);
        Assert.Equal(ButtonSize.Small, sized.Widgets[0].ButtonSize);
        Assert.Contains("\"buttonSize\":\"Small\"", host.Settings.Values["dashboards"], StringComparison.Ordinal);
        Assert.Equal(DashboardRules.BadSetting, await Refused(Button("a", 0, 0) with { ButtonSize = (ButtonSize)7 }));

        // A button without a chosen size is Small.
        var plain = await host.Plugin.SaveAsync(New("Plain", Button("a", 0, 0)), CancellationToken.None);
        Assert.Equal(ButtonSize.Small, plain.Widgets[0].ButtonSize);
    }

    [Fact]
    public async Task Group_users_press_only_the_buttons_of_their_group()
    {
        var fake = new Testing.FakeCoreContext();
        var a = fake.Add(new Testing.FakeDevice { Address = "10.0.0.1" });
        var b = fake.Add(new Testing.FakeDevice { Address = "10.0.0.2" });
        var keyA = fake.FakeScopes.Add("Building A", [a.Id]);
        var keyB = fake.FakeScopes.Add("Building B", [b.Id]);
        var publisher = new FakePublisher();
        var ctx = Substitute.For<ICorePluginContext>();
        ctx.Settings.Returns(fake.PluginSettings);
        ctx.Events.Returns(fake);
        ctx.Logger.Returns(NullLogger.Instance);
        ctx.DeviceScopes.Returns(fake.FakeScopes);
        ctx.Jobs.Returns(fake.FakeJobs);
        ctx.EventPublisher.Returns(publisher);
        using var plugin = new DashboardsPlugin(new FakeTimeProvider());
        await plugin.StartAsync(ctx, CancellationToken.None);
        var anna = Testing.FakeCallContext.WithGroup(fake, [a.Id], "anna", keyA);
        var bob = Testing.FakeCallContext.WithGroup(fake, [b.Id], "bob", keyB);
        var admin = fake.CallAsAllDevices();

        var saved = Read<Dashboard>(await plugin.InvokeAsync(anna, DashboardsMethods.Save,
            JsonSerializer.Serialize(New("Doors", Button("open", 0, 0)), DashboardsPluginInfo.Json), CancellationToken.None));
        Assert.Equal(keyA, plugin.EventTypes.Single().OwnerScopeKey);
        var press = JsonSerializer.Serialize(new PressRequest(saved.Id, "open"), DashboardsPluginInfo.Json);

        await plugin.InvokeAsync(anna, DashboardsMethods.Press, press, CancellationToken.None);
        Assert.Equal("anna", Assert.Single(publisher.Fired).Data["user"]);
        var refused = await Assert.ThrowsAsync<ArgumentException>(() => plugin.InvokeAsync(bob, DashboardsMethods.Press, press, CancellationToken.None));
        Assert.Equal(DashboardRules.ButtonGone, refused.Message); // another group's dashboard answers like a deleted one
        await plugin.InvokeAsync(admin, DashboardsMethods.Press, press, CancellationToken.None);
        Assert.Equal(["anna", "admin"], publisher.Fired.Select(f => f.Data["user"]));
    }
}
