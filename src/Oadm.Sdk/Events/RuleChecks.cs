using System.Globalization;

namespace Oadm.Sdk.Events;

/// <summary>How a field check compares an event value (SDK 0.9.0). JSON: eq, ne, contains, startsWith, gt, ge, lt, le, yes, no.</summary>
public enum RuleCheckOperator
{
    /// <summary>Text: equals (case-insensitive). Number: =.</summary>
    Eq = 0,

    /// <summary>Text: does not equal (case-insensitive). Number: ≠.</summary>
    Ne = 1,

    /// <summary>Text: contains (case-insensitive).</summary>
    Contains = 2,

    /// <summary>Text: starts with (case-insensitive).</summary>
    StartsWith = 3,

    /// <summary>Number: greater than.</summary>
    Gt = 4,

    /// <summary>Number: greater than or equal.</summary>
    Ge = 5,

    /// <summary>Number: less than.</summary>
    Lt = 6,

    /// <summary>Number: less than or equal.</summary>
    Le = 7,

    /// <summary>Yes / no: is yes ("true", "1", "yes").</summary>
    Yes = 8,

    /// <summary>Yes / no: is no (sent, and not yes).</summary>
    No = 9,
}

/// <summary>
/// One check of an event condition on a value of its event (SDK 0.9.0): "temperature" <see cref="RuleCheckOperator.Gt"/>
/// "40". All checks of a condition must hold; a value the event did not send fails.
/// </summary>
public sealed class RuleCheck
{
    /// <summary>The name of the event's data field (<see cref="EventField.Name"/>).</summary>
    public string Field { get; set; } = string.Empty;

    /// <summary>How the value is compared; it must fit the field's <see cref="EventField.Kind"/>.</summary>
    public RuleCheckOperator Op { get; set; }

    /// <summary>The value to compare with (numbers in invariant culture, "21.5"); empty for yes / no.</summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>A copy.</summary>
    public RuleCheck Clone() => new() { Field = Field, Op = Op, Value = Value };
}

/// <summary>The rules of field checks shared by the server and the rule dialog (SDK 0.9.0).</summary>
public static class RuleChecks
{
    /// <summary>Most checks per event condition.</summary>
    public const int MaxPerCondition = 5;

    /// <summary>Longest value of a check.</summary>
    public const int MaxValueLength = 200;

    private static readonly RuleCheckOperator[] TextOperators = [RuleCheckOperator.Eq, RuleCheckOperator.Ne, RuleCheckOperator.Contains, RuleCheckOperator.StartsWith];

    private static readonly RuleCheckOperator[] NumberOperators =
        [RuleCheckOperator.Eq, RuleCheckOperator.Ne, RuleCheckOperator.Gt, RuleCheckOperator.Ge, RuleCheckOperator.Lt, RuleCheckOperator.Le];

    private static readonly RuleCheckOperator[] YesNoOperators = [RuleCheckOperator.Yes, RuleCheckOperator.No];

    /// <summary>The operators a field of <paramref name="kind"/> offers, in the order of the dialog.</summary>
    public static IReadOnlyList<RuleCheckOperator> OperatorsFor(EventValueKind kind) => kind switch
    {
        EventValueKind.Number => NumberOperators,
        EventValueKind.YesNo => YesNoOperators,
        _ => TextOperators,
    };

    /// <summary>Whether <paramref name="op"/> can check a field of <paramref name="kind"/>.</summary>
    public static bool Fits(RuleCheckOperator op, EventValueKind kind) => OperatorsFor(kind).Contains(op);

    /// <summary>False for yes / no: they have no value.</summary>
    public static bool NeedsValue(RuleCheckOperator op) => op is not (RuleCheckOperator.Yes or RuleCheckOperator.No);

    /// <summary>Whether the check compares numbers (every number operator, and = / ≠ on a number field).</summary>
    public static bool IsNumeric(RuleCheckOperator op, EventValueKind kind) =>
        op is RuleCheckOperator.Gt or RuleCheckOperator.Ge or RuleCheckOperator.Lt or RuleCheckOperator.Le
        || (kind == EventValueKind.Number && op is RuleCheckOperator.Eq or RuleCheckOperator.Ne);

    /// <summary>A finite number in invariant culture ("21.5", "-3", "1e3").</summary>
    public static bool TryParseNumber(string? text, out double value) =>
        double.TryParse(text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

    /// <summary>"true", "1" and "yes" (any case) count as yes.</summary>
    public static bool IsYes(string? text)
    {
        var value = text?.Trim();
        return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) || value == "1"
            || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The operator in the dialog: "equals", "does not equal", "contains", "starts with", "=", "≠", "&gt;", "≥", "&lt;", "≤", "is yes", "is no".</summary>
    public static string Text(RuleCheckOperator op, EventValueKind kind) => op switch
    {
        RuleCheckOperator.Eq => kind == EventValueKind.Number ? "=" : "equals",
        RuleCheckOperator.Ne => kind == EventValueKind.Number ? "≠" : "does not equal",
        RuleCheckOperator.Contains => "contains",
        RuleCheckOperator.StartsWith => "starts with",
        RuleCheckOperator.Gt => ">",
        RuleCheckOperator.Ge => "≥",
        RuleCheckOperator.Lt => "<",
        RuleCheckOperator.Le => "≤",
        RuleCheckOperator.Yes => "is yes",
        RuleCheckOperator.No => "is no",
        _ => op.ToString(),
    };

    /// <summary>The check in one short phrase: "Temperature &gt; 40", "Door is yes", "State equals active".</summary>
    public static string Describe(RuleCheck check, string fieldName, EventValueKind kind)
    {
        ArgumentNullException.ThrowIfNull(check);
        var text = fieldName + " " + Text(check.Op, kind);
        return NeedsValue(check.Op) ? text + " " + check.Value.Trim() : text;
    }

    /// <summary>
    /// The problem of one check against the fields of its event, or null: the field must be one of the event's checkable
    /// fields (<see cref="EventField.Filter"/>), the operator must fit its kind, the value must be there (a number for
    /// number checks) and at most <see cref="MaxValueLength"/> characters long.
    /// </summary>
    public static string? Problem(RuleCheck check, IReadOnlyList<EventField> fields)
    {
        ArgumentNullException.ThrowIfNull(check);
        ArgumentNullException.ThrowIfNull(fields);
        var field = fields.FirstOrDefault(f => string.Equals(f.Name, check.Field, StringComparison.Ordinal));
        if (field is null)
        {
            return $"The event has no value \"{check.Field}\".";
        }

        if (!field.Filter)
        {
            return $"{field.DisplayName} cannot be checked.";
        }

        if (!Fits(check.Op, field.Kind))
        {
            return $"{field.DisplayName} cannot be compared with \"{Text(check.Op, field.Kind)}\".";
        }

        return ValueProblem(check.Op, field.Kind, check.Value) is { } problem
            ? field.DisplayName + ": " + char.ToLowerInvariant(problem[0]) + problem[1..]
            : null;
    }

    /// <summary>The problem of the value of a check, or null (also the rule of the value box of the dialog).</summary>
    public static string? ValueProblem(RuleCheckOperator op, EventValueKind kind, string? value)
    {
        if (!NeedsValue(op))
        {
            return null;
        }

        var text = value?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return "Enter a value.";
        }

        if (text.Length > MaxValueLength)
        {
            return string.Create(CultureInfo.InvariantCulture, $"At most {MaxValueLength} characters.");
        }

        return IsNumeric(op, kind) && !TryParseNumber(text, out _) ? "Enter a number, e.g. 21.5." : null;
    }
}
