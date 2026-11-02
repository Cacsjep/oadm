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
    public void OlderEqualsFiltersAreReadAsEqualsChecksAndStoredAsChecks()
    {
        var old = """
            {"name":"Gate","enabled":true,"conditions":[{"kind":"event","eventTypeId":"test.input","devices":{"mode":"all"},"data":{"port":"1"}}],"actions":[]}
            """;
        var rule = RuleDefinition.FromJson(old);
        var check = Assert.Single(rule.Conditions[0].Checks);
        Assert.Equal(("port", RuleCheckOperator.Eq, "1"), (check.Field, check.Op, check.Value));
        Assert.Empty(rule.Conditions[0].Data);

        var json = rule.ToJson();
        var stored = JsonNode.Parse(json)!["conditions"]![0]!;
        Assert.Equal("""[{"field":"port","op":"eq","value":"1"}]""", stored["checks"]!.ToJsonString());
        var again = RuleDefinition.FromJson(json);
        Assert.Equal(("port", RuleCheckOperator.Eq, "1"), (again.Conditions[0].Checks[0].Field, again.Conditions[0].Checks[0].Op, again.Conditions[0].Checks[0].Value));

        // Every operator has its short name in the stored JSON.
        var ops = Enum.GetValues<RuleCheckOperator>();
        var all = new RuleDefinition { Conditions = [new RuleCondition { Checks = [.. ops.Select(o => new RuleCheck { Field = "f", Op = o, Value = "1" })] }] };
        var names = JsonNode.Parse(all.ToJson())!["conditions"]![0]!["checks"]!.AsArray().Select(c => c!["op"]!.GetValue<string>());
        Assert.Equal(["eq", "ne", "contains", "startsWith", "gt", "ge", "lt", "le", "yes", "no"], names);
        Assert.Equal(ops, RuleDefinition.FromJson(all.ToJson()).Conditions[0].Checks.Select(c => c.Op));
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
        Assert.Equal("Site cannot be checked.", RuleChecks.Problem(new RuleCheck { Field = "site", Op = RuleCheckOperator.Eq, Value = "A" }, fields));
        Assert.Null(RuleChecks.Problem(new RuleCheck { Field = "door", Op = RuleCheckOperator.No }, fields)); // no value needed
    }
}
