using Oadm.Sdk.Events;

namespace Oadm.Core.Events;

/// <summary>
/// The field checks of one event condition, compiled once per rule (and again when its event's fields change): the
/// operator, the kind of comparison and the parsed number of each check. <see cref="Pass"/> runs per event without
/// allocations; a value the event did not send, or no number for a number check, fails.
/// </summary>
public sealed class CompiledChecks
{
    /// <summary>No checks: every event passes.</summary>
    public static readonly CompiledChecks None = new([]);

    private readonly Check[] _checks;

    private CompiledChecks(Check[] checks) => _checks = checks;

    /// <summary>Whether there is nothing to check.</summary>
    public bool IsEmpty => _checks.Length == 0;

    /// <summary>How many checks.</summary>
    public int Count => _checks.Length;

    /// <summary>
    /// Compiles the checks of <paramref name="condition"/> (and older equals filters of <see cref="RuleCondition.Data"/>)
    /// with the kinds of the fields of <paramref name="type"/>; a field it does not declare counts as text.
    /// </summary>
    public static CompiledChecks Compile(RuleCondition condition, EventTypeInfo? type)
    {
        ArgumentNullException.ThrowIfNull(condition);
        var all = condition.AllChecks();
        if (all.Count == 0)
        {
            return None;
        }

        var kinds = type?.DataFields.GroupBy(f => f.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Kind, StringComparer.Ordinal);
        return new CompiledChecks([.. all.Select(check => Check.From(check.Field, check.Op, check.Value, kinds))]);
    }

    /// <summary>Whether every check holds for the values of an event.</summary>
    public bool Pass(IReadOnlyDictionary<string, string> data)
    {
        ArgumentNullException.ThrowIfNull(data);
        foreach (var check in _checks)
        {
            if (!check.Holds(data))
            {
                return false;
            }
        }

        return true;
    }

    private sealed class Check
    {
        private readonly string _field;
        private readonly RuleCheckOperator _op;
        private readonly bool _numeric;
        private readonly string _text;
        private readonly double _number;
        private readonly bool _valid;

        private Check(string field, RuleCheckOperator op, bool numeric, string text, double number, bool valid)
        {
            _field = field;
            _op = op;
            _numeric = numeric;
            _text = text;
            _number = number;
            _valid = valid;
        }

        public static Check From(string field, RuleCheckOperator op, string? value, Dictionary<string, EventValueKind>? kinds)
        {
            var kind = kinds is not null && kinds.TryGetValue(field, out var k) ? k : EventValueKind.Text;
            var text = value?.Trim() ?? string.Empty;
            var numeric = RuleChecks.IsNumeric(op, kind);
            var isNumber = RuleChecks.TryParseNumber(text, out var number);
            if (numeric && op == RuleCheckOperator.Eq && !isNumber)
            {
                // = on a number value with a text (an older equals filter): compared as text, as before field checks.
                numeric = false;
            }

            // Any other number check whose own value is no number never holds (the server refuses it; a stored rule may be older).
            var valid = !numeric || isNumber;
            return new Check(field, op, numeric, text, number, valid);
        }

        public bool Holds(IReadOnlyDictionary<string, string> data)
        {
            if (!_valid || !data.TryGetValue(_field, out var actual) || actual is null)
            {
                return false;
            }

            if (_numeric)
            {
                if (!RuleChecks.TryParseNumber(actual, out var value))
                {
                    // = falls back to text when the event's value is no number ("n/a" = "n/a"); every other number check fails.
                    return _op == RuleCheckOperator.Eq && string.Equals(actual.Trim(), _text, StringComparison.OrdinalIgnoreCase);
                }

                return _op switch
                {
                    RuleCheckOperator.Eq => value == _number,
                    RuleCheckOperator.Ne => value != _number,
                    RuleCheckOperator.Gt => value > _number,
                    RuleCheckOperator.Ge => value >= _number,
                    RuleCheckOperator.Lt => value < _number,
                    RuleCheckOperator.Le => value <= _number,
                    _ => false,
                };
            }

            return _op switch
            {
                RuleCheckOperator.Eq => string.Equals(actual.Trim(), _text, StringComparison.OrdinalIgnoreCase),
                RuleCheckOperator.Ne => !string.Equals(actual.Trim(), _text, StringComparison.OrdinalIgnoreCase),
                RuleCheckOperator.Contains => actual.Contains(_text, StringComparison.OrdinalIgnoreCase),
                RuleCheckOperator.StartsWith => actual.TrimStart().StartsWith(_text, StringComparison.OrdinalIgnoreCase),
                RuleCheckOperator.Yes => RuleChecks.IsYes(actual),
                RuleCheckOperator.No => !RuleChecks.IsYes(actual),
                _ => false,
            };
        }
    }
}
