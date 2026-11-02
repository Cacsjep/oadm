using System.Text.Json.Nodes;

using Oadm.Core.Events;
using Oadm.Sdk.Events;

namespace Oadm.Core.Tests.Events;

/// <summary>Field checks of event conditions: each operator per kind, missing fields, numbers, the stored format.</summary>
public sealed class FieldCheckTests
{
    private static readonly EventTypeInfo Reading = new("test.reading", "Reading", "Test", EventKind.Stateful, Fields:
    [
        new EventField("state", "State"),
        new EventField("temperature", "Temperature") { Kind = EventValueKind.Number },
        new EventField("door", "Door") { Kind = EventValueKind.YesNo },
        new EventField("site", "Site") { Filter = false },
    ]);

    private static bool Pass(string field, RuleCheckOperator op, string value, string? actual)
    {
        var condition = new RuleCondition { Checks = [new RuleCheck { Field = field, Op = op, Value = value }] };
        var data = actual is null ? new Dictionary<string, string>() : new Dictionary<string, string> { [field] = actual };
        return CompiledChecks.Compile(condition, Reading).Pass(data);
    }

    [Theory]
    [InlineData(RuleCheckOperator.Eq, "Active", "active", true)] // case-insensitive, like the older filter
    [InlineData(RuleCheckOperator.Eq, "active", "inactive", false)]
    [InlineData(RuleCheckOperator.Ne, "active", "ACTIVE", false)]
    [InlineData(RuleCheckOperator.Ne, "active", "idle", true)]
    [InlineData(RuleCheckOperator.Contains, "TIV", "inactive", true)]
    [InlineData(RuleCheckOperator.Contains, "x", "inactive", false)]
    [InlineData(RuleCheckOperator.StartsWith, "in", "Inactive", true)]
    [InlineData(RuleCheckOperator.StartsWith, "act", "inactive", false)]
    public void TextOperators(RuleCheckOperator op, string value, string actual, bool expected) =>
        Assert.Equal(expected, Pass("state", op, value, actual));

    [Theory]
    [InlineData(RuleCheckOperator.Eq, "40", "40.0", true)] // compared as numbers, not as text
    [InlineData(RuleCheckOperator.Eq, "40", "40.5", false)]
    [InlineData(RuleCheckOperator.Ne, "40", "40.5", true)]
    [InlineData(RuleCheckOperator.Ne, "40", "40", false)]
    [InlineData(RuleCheckOperator.Gt, "40", "40.1", true)]
    [InlineData(RuleCheckOperator.Gt, "40", "40", false)]
    [InlineData(RuleCheckOperator.Ge, "40", "40", true)]
    [InlineData(RuleCheckOperator.Ge, "40", "39.9", false)]
    [InlineData(RuleCheckOperator.Lt, "21.5", "-3", true)]
    [InlineData(RuleCheckOperator.Lt, "21.5", "21.5", false)]
    [InlineData(RuleCheckOperator.Le, "21.5", "21.5", true)]
    [InlineData(RuleCheckOperator.Le, "21.5", "1e3", false)]
    public void NumberOperators(RuleCheckOperator op, string value, string actual, bool expected) =>
        Assert.Equal(expected, Pass("temperature", op, value, actual));

    [Theory]
    [InlineData(RuleCheckOperator.Yes, "true", true)]
    [InlineData(RuleCheckOperator.Yes, "TRUE", true)]
    [InlineData(RuleCheckOperator.Yes, "1", true)]
    [InlineData(RuleCheckOperator.Yes, "Yes", true)]
    [InlineData(RuleCheckOperator.Yes, "false", false)]
    [InlineData(RuleCheckOperator.Yes, "0", false)]
    [InlineData(RuleCheckOperator.No, "false", true)]
    [InlineData(RuleCheckOperator.No, "0", true)]
    [InlineData(RuleCheckOperator.No, "1", false)]
    [InlineData(RuleCheckOperator.No, "yes", false)]
    public void YesNoOperators(RuleCheckOperator op, string actual, bool expected) =>
        Assert.Equal(expected, Pass("door", op, string.Empty, actual));

    [Theory]
    [InlineData("state", RuleCheckOperator.Ne, "x")]
    [InlineData("temperature", RuleCheckOperator.Le, "100")]
    [InlineData("door", RuleCheckOperator.No, "")]
    public void AFieldTheEventDidNotSendFails(string field, RuleCheckOperator op, string value) =>
        Assert.False(Pass(field, op, value, actual: null)); // also "does not equal" and "is no"

    [Theory]
    [InlineData("warm")]
    [InlineData("")]
    [InlineData("NaN")]
    [InlineData("12,5")] // invariant culture: a comma is no decimal point
    public void AValueThatIsNoNumberFailsANumberCheck(string actual)
    {
        Assert.False(Pass("temperature", RuleCheckOperator.Gt, "-1000", actual));
        Assert.False(Pass("temperature", RuleCheckOperator.Ne, "40", actual));
    }

    [Fact]
    public void AllChecksOfAConditionMustHold()
    {
        var condition = new RuleCondition
        {
            Checks =
            [
                new RuleCheck { Field = "temperature", Op = RuleCheckOperator.Ge, Value = "20" },
                new RuleCheck { Field = "temperature", Op = RuleCheckOperator.Lt, Value = "30" }, // a range is two checks
                new RuleCheck { Field = "door", Op = RuleCheckOperator.Yes },
            ],
        };
        var checks = CompiledChecks.Compile(condition, Reading);
        Assert.Equal(3, checks.Count);
        Assert.True(checks.Pass(new Dictionary<string, string> { ["temperature"] = "25", ["door"] = "true" }));
        Assert.False(checks.Pass(new Dictionary<string, string> { ["temperature"] = "30", ["door"] = "true" }));
        Assert.False(checks.Pass(new Dictionary<string, string> { ["temperature"] = "25", ["door"] = "false" }));
        Assert.True(CompiledChecks.Compile(new RuleCondition(), Reading).Pass(new Dictionary<string, string>()));
        Assert.Same(CompiledChecks.None, CompiledChecks.Compile(new RuleCondition(), Reading));
    }

    [Fact]
    public void OlderEqualsFiltersStayInDataAndAreReadAsEqualsChecks()
    {
        var old = """
            {"name":"Gate","enabled":true,"conditions":[{"kind":"event","eventTypeId":"test.input","devices":{"mode":"all"},"data":{"port":"1"}}],"actions":[]}
            """;
        var rule = RuleDefinition.FromJson(old);
        Assert.Empty(rule.Conditions[0].Checks);
        var check = Assert.Single(rule.Conditions[0].AllChecks());
        Assert.Equal(("port", RuleCheckOperator.Eq, "1", true), (check.Field, check.Op, check.Value, check.IsLegacy));

        // An unchanged older rule is stored as it was: the filter stays an older one (checked as leniently as before).
        var stored = JsonNode.Parse(rule.ToJson())!["conditions"]![0]!;
        Assert.Equal("""{"port":"1"}""", stored["data"]!.ToJsonString());
        Assert.Equal("[]", stored["checks"]!.ToJsonString());

        // Checks first, then the older filters.
        rule.Conditions[0].Checks.Add(new RuleCheck { Field = "state", Op = RuleCheckOperator.Ne, Value = "x" });
        Assert.Equal([("state", false), ("port", true)], rule.Conditions[0].AllChecks().Select(c => (c.Field, c.IsLegacy)));

        // Every operator has its short name in the stored JSON.
        var ops = Enum.GetValues<RuleCheckOperator>();
        var all = new RuleDefinition { Conditions = [new RuleCondition { Checks = [.. ops.Select(o => new RuleCheck { Field = "f", Op = o, Value = "1" })] }] };
        var names = JsonNode.Parse(all.ToJson())!["conditions"]![0]!["checks"]!.AsArray().Select(c => c!["op"]!.GetValue<string>());
        Assert.Equal(["eq", "ne", "contains", "startsWith", "gt", "ge", "lt", "le", "yes", "no"], names);
        Assert.Equal(ops, RuleDefinition.FromJson(all.ToJson()).Conditions[0].Checks.Select(c => c.Op));
    }

    [Theory]
    [InlineData("""{"name":"R","conditions":[{"kind":"event","eventTypeId":"test.reading","data":null,"checks":null}],"actions":null}""")]
    [InlineData("""{"name":"R","conditions":[{"kind":"event","eventTypeId":"test.reading","devices":null,"checks":[null,{"field":"state","op":"eq","value":null}],"data":{"state":null}}]}""")]
    [InlineData("""{"name":null,"conditions":[null],"actions":[null,{"actionId":null,"targets":null,"settings":null,"storedSecretFields":null}]}""")]
    public void JsonNullsReadAsEmpty(string json)
    {
        var rule = RuleDefinition.FromJson(json);
        Assert.NotNull(rule.Name);
        Assert.NotNull(rule.Actions);
        Assert.All(rule.Actions, a => Assert.NotNull(a.Targets));
        foreach (var condition in rule.Conditions)
        {
            Assert.NotNull(condition.Devices);
            Assert.All(condition.AllChecks(), c => Assert.Equal(string.Empty, c.Value)); // a null value reads as empty
            CompiledChecks.Compile(condition, Reading).Pass(new Dictionary<string, string>());
            Assert.NotNull(RuleTexts.Checks(condition, Reading));
        }

        Assert.Equal(string.Empty, new RuleCheck { Field = null!, Value = null! }.Field + new RuleCheck { Value = null! }.Value);
    }

    [Fact]
    public void AnEqualsCheckOnANumberComparesNumbersElseText()
    {
        // Both numbers: compared as numbers. Else as text, so an older text filter on a number value still matches.
        Assert.True(Pass("temperature", RuleCheckOperator.Eq, "40", "40.0"));
        Assert.True(Pass("temperature", RuleCheckOperator.Eq, "n/a", "N/A"));
        Assert.False(Pass("temperature", RuleCheckOperator.Eq, "n/a", "40"));
        Assert.False(Pass("temperature", RuleCheckOperator.Eq, "40", "n/a"));

        var legacy = new RuleCondition { Data = new Dictionary<string, string> { ["temperature"] = "high", ["door"] = "TRUE", ["site"] = "A" } };
        var checks = CompiledChecks.Compile(legacy, Reading);
        // An older filter on a yes / no value and on a value only compares text, as before field checks.
        Assert.True(checks.Pass(new Dictionary<string, string> { ["temperature"] = "High", ["door"] = "true", ["site"] = "a" }));
        Assert.False(checks.Pass(new Dictionary<string, string> { ["temperature"] = "High", ["door"] = "1", ["site"] = "a" }));
    }

    [Fact]
    public void AFieldWithoutAKindTakesItFromItsType()
    {
        Assert.Equal(EventValueKind.Number, new EventField("vlan", "VLAN", EventFieldType.Number).Kind);
        Assert.Equal(EventValueKind.Text, new EventField("kind", "Kind", EventFieldType.Choice, ["New", "Renewal"]).Kind);
        Assert.Equal(EventValueKind.Text, new EventField("state", "State").Kind);
        Assert.Equal(EventValueKind.YesNo, new EventField("on", "On", EventFieldType.Number) { Kind = EventValueKind.YesNo }.Kind); // declared wins
        Assert.Equal(EventValueKind.Text, new EventField("n", "N", EventFieldType.Number) { Kind = EventValueKind.Text }.Kind); // also Text declared
        Assert.NotEqual(new EventField("n", "N", EventFieldType.Number), new EventField("n", "N", EventFieldType.Number) { Kind = EventValueKind.Text });

        // The operators follow: a Number type offers number checks and = compares numbers.
        var type = new EventTypeInfo("test.vlan", "VLAN", "Test", EventKind.Stateless, Fields: [new EventField("vlan", "VLAN", EventFieldType.Number)]);
        Assert.Null(RuleChecks.Problem(new RuleCheck { Field = "vlan", Op = RuleCheckOperator.Gt, Value = "10" }, type.DataFields));
        var condition = new RuleCondition { Checks = [new RuleCheck { Field = "vlan", Op = RuleCheckOperator.Eq, Value = "10" }] };
        Assert.True(CompiledChecks.Compile(condition, type).Pass(new Dictionary<string, string> { ["vlan"] = "10.0" }));
    }

    [Fact]
    public void ACheckIsDescribedInOneShortPhrase()
    {
        Assert.Equal("Temperature > 40", RuleChecks.Describe(new RuleCheck { Field = "temperature", Op = RuleCheckOperator.Gt, Value = "40" }, "Temperature", EventValueKind.Number));
        Assert.Equal("Temperature ≠ 40", RuleChecks.Describe(new RuleCheck { Field = "temperature", Op = RuleCheckOperator.Ne, Value = "40" }, "Temperature", EventValueKind.Number));
        Assert.Equal("Door is yes", RuleChecks.Describe(new RuleCheck { Field = "door", Op = RuleCheckOperator.Yes, Value = "ignored" }, "Door", EventValueKind.YesNo));
        Assert.Equal("State does not equal idle", RuleChecks.Describe(new RuleCheck { Field = "state", Op = RuleCheckOperator.Ne, Value = "idle" }, "State", EventValueKind.Text));
    }

    [Fact]
    public void TheSharedRulesOfAValueAndAnOperator()
    {
        var fields = Reading.DataFields;
        Assert.Null(RuleChecks.Problem(new RuleCheck { Field = "temperature", Op = RuleCheckOperator.Gt, Value = "21.5" }, fields));
        Assert.Equal("Temperature: enter a number, e.g. 21.5.", RuleChecks.Problem(new RuleCheck { Field = "temperature", Op = RuleCheckOperator.Gt, Value = "warm" }, fields));
        Assert.Equal("State: enter a value.", RuleChecks.Problem(new RuleCheck { Field = "state", Op = RuleCheckOperator.Contains, Value = " " }, fields));
        Assert.Equal("State: at most 200 characters.", RuleChecks.Problem(new RuleCheck { Field = "state", Op = RuleCheckOperator.Eq, Value = new string('x', 201) }, fields));
        Assert.Null(RuleChecks.Problem(new RuleCheck { Field = "state", Op = RuleCheckOperator.Eq, Value = new string('x', 200) }, fields));
        Assert.Equal("Door cannot be compared with \"contains\".", RuleChecks.Problem(new RuleCheck { Field = "door", Op = RuleCheckOperator.Contains, Value = "1" }, fields));
        Assert.Equal("State cannot be compared with \">\".", RuleChecks.Problem(new RuleCheck { Field = "state", Op = RuleCheckOperator.Gt, Value = "1" }, fields));
        // A value only, and one the event lacks: one phrase (the rule dialog shows it below the Field).
        Assert.Equal("Site: the event no longer has this value.", RuleChecks.Problem(new RuleCheck { Field = "site", Op = RuleCheckOperator.Eq, Value = "A" }, fields));
        Assert.Equal("port: the event no longer has this value.", RuleChecks.Problem(new RuleCheck { Field = "port", Op = RuleCheckOperator.Eq, Value = "1" }, fields));
        Assert.Equal("The event no longer has this value.", RuleChecks.NoSuchValueText);
        Assert.Null(RuleChecks.Problem(new RuleCheck { Field = "door", Op = RuleCheckOperator.No }, fields)); // no value needed

        // An older equals filter is checked as before field checks: a value the event declares, nothing else.
        Assert.Null(RuleChecks.Problem(new RuleCheck { Field = "site", Op = RuleCheckOperator.Eq, Value = "A", IsLegacy = true }, fields));
        Assert.Null(RuleChecks.Problem(new RuleCheck { Field = "door", Op = RuleCheckOperator.Eq, Value = "on", IsLegacy = true }, fields));
        Assert.Null(RuleChecks.Problem(new RuleCheck { Field = "temperature", Op = RuleCheckOperator.Eq, Value = "high", IsLegacy = true }, fields));
        Assert.Equal("port: the event no longer has this value.", RuleChecks.Problem(new RuleCheck { Field = "port", Op = RuleCheckOperator.Eq, Value = "1", IsLegacy = true }, fields));

        // More than 5: too many, unless all are older equals filters (they had no limit).
        var legacy = Enumerable.Range(0, 6).Select(i => new RuleCheck { Field = "f" + i, IsLegacy = true }).ToList();
        Assert.False(RuleChecks.TooMany(legacy));
        Assert.True(RuleChecks.TooMany([.. legacy, new RuleCheck { Field = "state" }]));
        Assert.False(RuleChecks.TooMany([.. legacy.Take(4), new RuleCheck { Field = "state" }]));
        Assert.Equal("At most 5 checks.", RuleChecks.TooManyText);
    }
}
