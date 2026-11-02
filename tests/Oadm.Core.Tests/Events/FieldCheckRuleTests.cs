using Oadm.Core.Events;
using Oadm.Sdk.Actions;
using Oadm.Sdk.Events;

namespace Oadm.Core.Tests.Events;

/// <summary>Rules with field checks in the engine: value changes of a stateful event, one-shot events, validation, old filters.</summary>
public sealed class FieldCheckRuleTests
{
    private static RuleCheck Check(string field, RuleCheckOperator op, string value = "") => new() { Field = field, Op = op, Value = value };

    private static Dictionary<string, string> Values(string temperature, string door = "false") =>
        new() { ["temperature"] = temperature, ["door"] = door, ["state"] = "active", ["site"] = "North" };

    [Fact]
    public async Task ATemperatureCrossingTheLimitTurnsTheRuleOnAndOff()
    {
        await using var rig = await EventRulesRig.StartAsync();
        var camera = await rig.AddDeviceAsync("10.0.0.1");
        rig.Publisher.ReplaceStates(EventRulesRig.Reading, []);
        var rule = EventRulesRig.Rule(EventRulesRig.Reading, rig.OnEventDevice());
        rule.Conditions[0].Checks = [Check("temperature", RuleCheckOperator.Gt, "40")];
        await rig.SaveAsync(rule);

        rig.Publisher.SetState(EventRulesRig.Reading, camera.Id, true, Values("38.5"));
        await rig.SettleAsync();
        Assert.Empty(rig.OnDevice.Edges); // on, but not above 40

        rig.Publisher.SetState(EventRulesRig.Reading, camera.Id, true, Values("41"));
        await rig.SettleAsync();
        Assert.Equal([(ActionEdge.TurnedOn, (Guid?)camera.Id)], rig.OnDevice.Edges);

        rig.Publisher.SetState(EventRulesRig.Reading, camera.Id, true, Values("45")); // still above: nothing new
        await rig.SettleAsync();
        Assert.Single(rig.OnDevice.Edges);

        rig.Publisher.SetState(EventRulesRig.Reading, camera.Id, true, Values("39.9"));
        await rig.SettleAsync();
        Assert.Equal([(ActionEdge.TurnedOn, (Guid?)camera.Id), (ActionEdge.TurnedOff, camera.Id)], rig.OnDevice.Edges);

        rig.Publisher.SetState(EventRulesRig.Reading, camera.Id, true, Values("warm")); // no number: fails
        await rig.SettleAsync();
        Assert.Equal(2, rig.OnDevice.Edges.Count);

        rig.Publisher.SetState(EventRulesRig.Reading, camera.Id, true, Values("50"));
        rig.Publisher.SetState(EventRulesRig.Reading, camera.Id, false); // the event ends: off whatever its values
        await rig.SettleAsync();
        Assert.Equal([ActionEdge.TurnedOn, ActionEdge.TurnedOff, ActionEdge.TurnedOn, ActionEdge.TurnedOff], rig.OnDevice.Edges.Select(e => e.Edge));
    }

    [Fact]
    public async Task AFurtherConditionIsCheckedOnItsLatestValues()
    {
        await using var rig = await EventRulesRig.StartAsync();
        var camera = await rig.AddDeviceAsync("10.0.0.1");
        rig.Publisher.ReplaceStates(EventRulesRig.Blur, [camera.Id]);
        rig.Publisher.ReplaceStates(EventRulesRig.Reading, []);
        var rule = EventRulesRig.Rule(EventRulesRig.Blur, rig.OnEventDevice());
        rule.Conditions.Add(new RuleCondition
        {
            Kind = RuleConditionKind.Event,
            EventTypeId = EventRulesRig.Reading,
            Devices = new DeviceSelector { Mode = DeviceSelectorMode.EventDevice },
            Checks = [Check("door", RuleCheckOperator.Yes), Check("state", RuleCheckOperator.StartsWith, "ACT")],
        });
        await rig.SaveAsync(rule);

        rig.Publisher.SetState(EventRulesRig.Reading, camera.Id, true, Values("20", door: "0"));
        await rig.SettleAsync();
        Assert.Empty(rig.OnDevice.Edges);

        rig.Publisher.SetState(EventRulesRig.Reading, camera.Id, true, Values("20", door: "true"));
        await rig.SettleAsync();
        Assert.Equal([(ActionEdge.TurnedOn, (Guid?)camera.Id)], rig.OnDevice.Edges);

        rig.Publisher.SetState(EventRulesRig.Reading, camera.Id, true, new Dictionary<string, string> { ["door"] = "true" }); // no state sent
        await rig.SettleAsync();
        Assert.Equal([(ActionEdge.TurnedOn, (Guid?)camera.Id), (ActionEdge.TurnedOff, camera.Id)], rig.OnDevice.Edges);
    }

    [Fact]
    public async Task AOneShotEventRunsOnlyWhenItsValuesPass()
    {
        await using var rig = await EventRulesRig.StartAsync();
        var camera = await rig.AddDeviceAsync("10.0.0.1");
        var rule = EventRulesRig.Rule(EventRulesRig.ReadingSent, rig.OnEventDevice());
        rule.Conditions[0].Checks = [Check("temperature", RuleCheckOperator.Ge, "40"), Check("state", RuleCheckOperator.Ne, "test")];
        await rig.SaveAsync(rule);

        rig.Publisher.Fire(EventRulesRig.ReadingSent, camera.Id, Values("39"));
        rig.Publisher.Fire(EventRulesRig.ReadingSent, camera.Id, Values("40"));
        rig.Publisher.Fire(EventRulesRig.ReadingSent, camera.Id, new Dictionary<string, string> { ["temperature"] = "41", ["state"] = "TEST" });
        rig.Publisher.Fire(EventRulesRig.ReadingSent, camera.Id); // no values at all
        await rig.SettleAsync();
        Assert.Equal([(ActionEdge.Fired, (Guid?)camera.Id)], rig.OnDevice.Edges);
    }

    [Fact]
    public async Task OlderEqualsFiltersStillWorkAndAreStoredAsChecks()
    {
        await using var rig = await EventRulesRig.StartAsync();
        var io = await rig.AddDeviceAsync("10.0.0.70");
        rig.Publisher.ReplaceStates(EventRulesRig.Input, []);
        var rule = EventRulesRig.Rule(EventRulesRig.Input, rig.OnEventDevice());
        rule.Conditions[0].Data = new Dictionary<string, string> { ["port"] = "1" }; // a rule of an older client
        var saved = await rig.SaveAsync(rule);

        var stored = Assert.Single(await rig.Store.ListAsync(CancellationToken.None)).Definition;
        var check = Assert.Single(stored.Conditions[0].Checks);
        Assert.Equal(("port", RuleCheckOperator.Eq, "1"), (check.Field, check.Op, check.Value));
        Assert.Empty(stored.Conditions[0].Data);
        Assert.Equal("Input active (all devices, Port equals 1)", RuleTexts.Conditions(stored, rig.Catalog, _ => null));

        rig.Publisher.SetState(EventRulesRig.Input, io.Id, true, new Dictionary<string, string> { ["port"] = "2" });
        await rig.SettleAsync();
        Assert.Empty(rig.OnDevice.Edges);
        rig.Publisher.SetState(EventRulesRig.Input, io.Id, true, new Dictionary<string, string> { ["port"] = "1" });
        await rig.SettleAsync();
        Assert.Equal([(ActionEdge.TurnedOn, (Guid?)io.Id)], rig.OnDevice.Edges);
        Assert.NotEqual(Guid.Empty, saved.Id);
    }

    [Fact]
    public async Task TheServerRefusesChecksThatDoNotFit()
    {
        await using var rig = await EventRulesRig.StartAsync();
        string? Problem(params RuleCheck[] checks)
        {
            var rule = EventRulesRig.Rule(EventRulesRig.Reading, rig.OnEventDevice());
            rule.Conditions[0].Checks = [.. checks];
            return RuleValidator.FirstProblem(rule, rig.Catalog);
        }

        Assert.Null(Problem([.. Enumerable.Range(0, RuleChecks.MaxPerCondition).Select(i => Check("temperature", RuleCheckOperator.Gt, i.ToString(System.Globalization.CultureInfo.InvariantCulture)))]));
        Assert.Equal("The trigger: at most 5 checks.", Problem([.. Enumerable.Range(0, 6).Select(_ => Check("temperature", RuleCheckOperator.Gt, "1"))]));
        Assert.Equal("The trigger: Reading has no value \"humidity\".", Problem(Check("humidity", RuleCheckOperator.Gt, "1")));
        Assert.Equal("The trigger: Door cannot be compared with \">\".", Problem(Check("door", RuleCheckOperator.Gt, "1")));
        Assert.Equal("The trigger: Temperature cannot be compared with \"contains\".", Problem(Check("temperature", RuleCheckOperator.Contains, "4")));
        Assert.Equal("The trigger: State cannot be compared with \"is yes\".", Problem(Check("state", RuleCheckOperator.Yes)));
        Assert.Equal("The trigger: Temperature: enter a number, e.g. 21.5.", Problem(Check("temperature", RuleCheckOperator.Le, "hot")));
        Assert.Equal("The trigger: State: at most 200 characters.", Problem(Check("state", RuleCheckOperator.Eq, new string('a', 201))));
        Assert.Equal("The trigger: Site cannot be checked.", Problem(Check("site", RuleCheckOperator.Eq, "North")));
        Assert.Equal("The trigger: a check is not valid.", Problem(Check("state", (RuleCheckOperator)42, "x")));

        var sixth = EventRulesRig.Rule(EventRulesRig.Reading, rig.OnEventDevice());
        sixth.Conditions[0].Checks = [.. Enumerable.Range(0, 6).Select(_ => Check("door", RuleCheckOperator.Yes))];
        var refused = await Assert.ThrowsAsync<RuleValidationException>(() => rig.SaveAsync(sixth));
        Assert.Equal("The trigger: at most 5 checks.", refused.Message);
        Assert.Empty(await rig.Store.ListAsync(CancellationToken.None));
    }
}
