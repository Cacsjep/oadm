using System.Text.Json.Nodes;

using Oadm.Core.Events;
using Oadm.Sdk.Events;

namespace Oadm.Core.Tests.Events;

public sealed class RuleStoreTests
{
    [Fact]
    public async Task PasswordsAreStoredEncryptedNeverSentBackAndKeptWhenLeftEmpty()
    {
        await using var rig = await EventRulesRig.StartAsync();
        var camera = await rig.AddDeviceAsync("10.0.0.1");
        var rule = EventRulesRig.Rule(EventRulesRig.Pulse, rig.WithoutDevice(new JsonObject { ["text"] = "hi", ["secret"] = "s3cret" }));
        var saved = await rig.SaveAsync(rule);

        var action = Assert.Single(saved.Actions);
        Assert.False(action.Settings.ContainsKey("secret"));
        Assert.Equal(["secret"], action.StoredSecretFields);
        var stored = await rig.Store.GetAsync(saved.Id, CancellationToken.None);
        Assert.StartsWith(RuleStore.SecretPrefix, stored!.Definition.Actions[0].Settings["secret"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.DoesNotContain("s3cret", stored.Definition.ToJson(), StringComparison.Ordinal);

        // Saved again without the value: the stored one stays and reaches the action decrypted.
        saved.Actions[0].Settings["text"] = "changed";
        saved = await rig.SaveAsync(saved);
        rig.Publisher.Fire(EventRulesRig.Pulse, camera.Id);
        await rig.SettleAsync();
        Assert.Equal("changed|s3cret", Assert.Single(rig.NoDevice.Runs).Text);

        // Not listed as stored and empty: cleared.
        saved.Actions[0].StoredSecretFields = [];
        saved = await rig.SaveAsync(saved);
        Assert.Empty(saved.Actions[0].StoredSecretFields);
    }

    [Fact]
    public async Task ACopyIsOffNamedCopyAndKeepsThePasswords()
    {
        await using var rig = await EventRulesRig.StartAsync();
        var camera = await rig.AddDeviceAsync("10.0.0.1");
        var original = await rig.SaveAsync(EventRulesRig.Rule(EventRulesRig.Pulse, rig.WithoutDevice(new JsonObject { ["secret"] = "pw" })));

        var copy = await rig.Rules.DuplicateAsync(original.Id, CancellationToken.None);

        Assert.NotNull(copy);
        Assert.NotEqual(original.Id, copy.Value.Definition.Id);
        Assert.Equal(original.Name + " (copy)", copy.Value.Definition.Name);
        Assert.False(copy.Value.Definition.Enabled);
        Assert.Equal(["secret"], copy.Value.Definition.Actions[0].StoredSecretFields);

        await rig.Rules.SetEnabledAsync(original.Id, false, CancellationToken.None);
        await rig.Rules.SetEnabledAsync(copy.Value.Definition.Id, true, CancellationToken.None);
        rig.Publisher.Fire(EventRulesRig.Pulse, camera.Id);
        await rig.SettleAsync();
        Assert.Equal("|pw", Assert.Single(rig.NoDevice.Runs).Text);
    }

    [Theory]
    [InlineData("", "Enter a name.")]
    [InlineData("no-conditions", "The rule needs an event as its first condition.")]
    [InlineData("no-actions", "Add at least one action.")]
    [InlineData("unknown-event", "The trigger: choose an event.")]
    [InlineData("stateless-second", "Condition 2: only the first condition can be an event that happens once.")]
    [InlineData("same-device-trigger", "The trigger: choose which devices.")]
    [InlineData("no-days", "Condition 2: choose at least one day.")]
    [InlineData("bad-time", "Condition 2: enter the times as HH:mm, e.g. 08:00.")]
    [InlineData("bad-number", "Record test.no-device: Count: Must be 1 to 10.")]
    [InlineData("event-device-on-server-trigger", "Record test.on-device: the trigger is not about a device; choose the devices.")]
    [InlineData("unknown-data", "The trigger: Blur detected has no value \"x\".")]
    public async Task TheServerRefusesRulesThatCannotWork(string kind, string expected)
    {
        await using var rig = await EventRulesRig.StartAsync();
        var rule = EventRulesRig.Rule(EventRulesRig.Blur, rig.OnEventDevice());
        switch (kind)
        {
            case "":
                rule.Name = " ";
                break;
            case "no-conditions":
                rule.Conditions.Clear();
                break;
            case "no-actions":
                rule.Actions.Clear();
                break;
            case "unknown-event":
                rule.Conditions[0].EventTypeId = "nope";
                break;
            case "stateless-second":
                rule.Conditions.Add(new RuleCondition { Kind = RuleConditionKind.Event, EventTypeId = EventRulesRig.Pulse });
                break;
            case "same-device-trigger":
                rule.Conditions[0].Devices.Mode = DeviceSelectorMode.EventDevice;
                break;
            case "no-days":
                rule.Conditions.Add(new RuleCondition { Kind = RuleConditionKind.Schedule, Schedule = new RuleSchedule { From = "08:00", To = "18:00" } });
                break;
            case "bad-time":
                rule.Conditions.Add(new RuleCondition { Kind = RuleConditionKind.Schedule, Schedule = new RuleSchedule { Days = [DayOfWeek.Monday], From = "8 am", To = "18:00" } });
                break;
            case "bad-number":
                rule.Actions = [rig.WithoutDevice(new JsonObject { ["count"] = 11 })];
                break;
            case "event-device-on-server-trigger":
                rule.Conditions[0].EventTypeId = EventRulesRig.ServerFlag;
                break;
            case "unknown-data":
                rule.Conditions[0].Data["x"] = "1";
                break;
        }

        var ex = await Assert.ThrowsAsync<RuleValidationException>(() => rig.Rules.SaveAsync(rule, CancellationToken.None));
        Assert.Equal(expected, ex.Message);
    }

    [Fact]
    public async Task SummariesDescribeConditionsActionsAndState()
    {
        await using var rig = await EventRulesRig.StartAsync();
        var io = await rig.AddDeviceAsync("10.0.0.70");
        rig.Publisher.ReplaceStates(EventRulesRig.Blur, []);
        var rule = EventRulesRig.Rule(EventRulesRig.Blur, rig.OnDevices(io.Id), rig.WithoutDevice());
        rule.Conditions.Add(new RuleCondition
        {
            Kind = RuleConditionKind.Schedule,
            Schedule = new RuleSchedule { Days = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday], From = "08:00", To = "18:00" },
        });
        await rig.SaveAsync(rule);

        var summary = Assert.Single(await rig.Rules.ListAsync(CancellationToken.None));
        Assert.Equal("Blur detected (all devices) · Mon-Fri 08:00-18:00", summary.ConditionsText);
        Assert.Equal("Record test.on-device on 10.0.0.70 (P3265-V) · Record test.no-device", summary.ActionsText);
        Assert.Equal("Off", summary.StateText);
        Assert.True(summary.Stateful);
        Assert.Null(summary.Problem);
    }

    [Fact]
    public async Task TheEventLogIsPrunedByAgeAndCount()
    {
        await using var rig = await EventRulesRig.StartAsync();
        for (var i = 0; i < 150; i++)
        {
            rig.Log.Write(EventLogKind.System, EventLogResult.Info, "Line " + i);
        }

        await rig.Log.FlushAsync(CancellationToken.None);
        Assert.Equal(50, await rig.Log.PruneAsync(null, 100, CancellationToken.None));
        var (lines, total) = await rig.Log.ListAsync(0, 0, null, CancellationToken.None);
        Assert.Equal(100, total);
        Assert.Equal("Line 149", lines[0].What);

        rig.Time.Advance(TimeSpan.FromDays(31));
        rig.Log.Write(EventLogKind.System, EventLogResult.Info, "New");
        await rig.Log.FlushAsync(CancellationToken.None);
        Assert.Equal(100, await rig.Log.PruneAsync(TimeSpan.FromDays(30), null, CancellationToken.None));
    }

    [Theory]
    [InlineData("08:00", "18:00", DayOfWeek.Monday, "10:00", true)]
    [InlineData("08:00", "18:00", DayOfWeek.Monday, "18:00", false)]
    [InlineData("08:00", "18:00", DayOfWeek.Tuesday, "10:00", false)]
    [InlineData("22:00", "06:00", DayOfWeek.Monday, "23:00", true)]
    [InlineData("22:00", "06:00", DayOfWeek.Tuesday, "05:00", true)] // after midnight belongs to Monday
    [InlineData("22:00", "06:00", DayOfWeek.Monday, "05:00", false)]
    public void SchedulesRunOverMidnight(string from, string to, DayOfWeek day, string at, bool inside)
    {
        var schedule = new RuleSchedule { Days = [DayOfWeek.Monday], From = from, To = to };
        var monday = new DateTime(2026, 10, 12); // a Monday
        var date = monday.AddDays(((int)day - (int)DayOfWeek.Monday + 7) % 7);
        var local = date + TimeOnly.ParseExact(at, "HH:mm", System.Globalization.CultureInfo.InvariantCulture).ToTimeSpan();

        Assert.Equal(inside, RuleTimes.IsInside(schedule, local));
    }
}
