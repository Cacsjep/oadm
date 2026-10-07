using System.Collections;
using System.ComponentModel;

namespace Oadm.Sdk.Client.Validation;

/// <summary>
/// Field validation of a form view model for <see cref="INotifyDataErrorInfo"/>: every rule reports on the
/// property it belongs to, so Avalonia shows the message directly below that input (DataValidationErrors,
/// inside <c>ui:FormField</c>). Rules always run; an error becomes visible once the user edited the field
/// (<see cref="Touch"/>) or tried to submit (<see cref="ShowAll"/>), never on an untouched empty form.
/// Server-side errors (<see cref="SetServerError"/>) are visible at once and cleared when the field is edited.
/// Submit buttons use <see cref="IsValid"/> / <see cref="FirstError"/> (all errors, visible or not) for
/// <c>IsEnabled</c> and the tooltip that says why. Use <see cref="ValidatingViewModel"/> as base class, or
/// own one of these and forward <see cref="INotifyDataErrorInfo"/> to it.
/// </summary>
public sealed class FormValidator
{
    private readonly List<(string Property, Func<string?> Rule)> _rules = [];
    private readonly List<(string[] Properties, Func<IReadOnlyDictionary<string, string?>> Rules)> _ruleSets = [];
    private readonly List<string> _fields = [];
    private readonly HashSet<string> _touched = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _serverErrors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _errors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _visible = new(StringComparer.Ordinal);
    private readonly Action<string> _errorsChanged;
    private readonly Action? _stateChanged;
    private bool _showAll;
    private bool _validating;

    /// <param name="errorsChanged">Raises <see cref="INotifyDataErrorInfo.ErrorsChanged"/> for a property.</param>
    /// <param name="stateChanged">Called after a validation pass changed any error (refresh commands, tooltips).</param>
    public FormValidator(Action<string> errorsChanged, Action? stateChanged = null)
    {
        _errorsChanged = errorsChanged ?? throw new ArgumentNullException(nameof(errorsChanged));
        _stateChanged = stateChanged;
    }

    /// <summary>Any error is visible (<see cref="INotifyDataErrorInfo.HasErrors"/>).</summary>
    public bool HasErrors => _visible.Count > 0;

    /// <summary>No error at all, shown or not yet shown: the form can be submitted.</summary>
    public bool IsValid => _errors.Count == 0;

    /// <summary>The first error in field order, shown or not (tooltip of the disabled submit button); null when valid.</summary>
    public string? FirstError => FirstErrorOf(null);

    /// <summary>The fields with rules, in registration order.</summary>
    public IReadOnlyList<string> Fields => _fields;

    /// <summary>The visible error of a property (null when none or not shown yet).</summary>
    public string? this[string property] => _visible.GetValueOrDefault(property);

    /// <summary>Adds a rule for one property: returns the message, or null when the value is fine.</summary>
    public FormValidator Rule(string property, Func<string?> rule)
    {
        ArgumentException.ThrowIfNullOrEmpty(property);
        ArgumentNullException.ThrowIfNull(rule);
        _rules.Add((property, rule));
        AddField(property);
        return this;
    }

    /// <summary>
    /// Adds a rule set that checks several properties at once (a shared validator returning property -> message).
    /// Of several rules on the same property the first registered one with a message wins.
    /// </summary>
    public FormValidator Rules(IEnumerable<string> properties, Func<IReadOnlyDictionary<string, string?>> rules)
    {
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentNullException.ThrowIfNull(rules);
        string[] names = properties.ToArray();
        _ruleSets.Add((names, rules));
        foreach (string name in names)
        {
            AddField(name);
        }

        return this;
    }

    /// <summary>True when the property has a rule.</summary>
    public bool IsField(string? property) => property is not null && _fields.Contains(property, StringComparer.Ordinal);

    /// <summary>The user edited the property: its error becomes visible, its server error is cleared; validates.</summary>
    public void Touch(string? property)
    {
        if (property is not null && IsField(property))
        {
            _touched.Add(property);
            _serverErrors.Remove(property);
        }

        Validate();
    }

    /// <summary>The user tried to submit: every error becomes visible (or only those of <paramref name="properties"/>).</summary>
    public void ShowAll(params string[] properties)
    {
        ArgumentNullException.ThrowIfNull(properties);
        if (properties.Length == 0)
        {
            _showAll = true;
        }
        else
        {
            foreach (string property in properties)
            {
                _touched.Add(property);
            }
        }

        Validate();
    }

    /// <summary>
    /// Back to an untouched form (after loading or prefilling values, after a successful submit, when an editor
    /// reopens): errors are hidden again until the next edit or submit, server errors are cleared.
    /// Only <paramref name="properties"/> when given.
    /// </summary>
    public void Reset(params string[] properties)
    {
        ArgumentNullException.ThrowIfNull(properties);
        if (properties.Length == 0)
        {
            _showAll = false;
            _touched.Clear();
            _serverErrors.Clear();
        }
        else
        {
            foreach (string property in properties)
            {
                _touched.Remove(property);
                _serverErrors.Remove(property);
            }
        }

        Validate();
    }

    /// <summary>
    /// An error the server (or the device) reported for this field, e.g. "The login failed." on the password.
    /// Shown at once, below the field; cleared when the user edits the field. Null clears it.
    /// </summary>
    public void SetServerError(string property, string? error)
    {
        ArgumentException.ThrowIfNullOrEmpty(property);
        AddField(property);
        if (string.IsNullOrEmpty(error))
        {
            _serverErrors.Remove(property);
        }
        else
        {
            _serverErrors[property] = error;
        }

        Validate();
    }

    /// <summary>True when none of the given properties has an error (shown or not).</summary>
    public bool IsValidFor(params string[] properties)
    {
        ArgumentNullException.ThrowIfNull(properties);
        return properties.All(p => !_errors.ContainsKey(p));
    }

    /// <summary>The first error among the given properties (all when null), in field order.</summary>
    public string? FirstErrorOf(IEnumerable<string>? properties)
    {
        HashSet<string>? filter = properties is null ? null : new HashSet<string>(properties, StringComparer.Ordinal);
        foreach (string field in _fields)
        {
            if ((filter is null || filter.Contains(field)) && _errors.TryGetValue(field, out string? error))
            {
                return error;
            }
        }

        return null;
    }

    /// <summary>The current error of a property, shown or not.</summary>
    public string? ErrorOf(string property) => _errors.GetValueOrDefault(property);

    public IEnumerable GetErrors(string? property) =>
        property is not null && _visible.TryGetValue(property, out string? error) ? new[] { error } : Array.Empty<string>();

    /// <summary>Runs every rule again; raises the change for every property whose visible error changed.</summary>
    public void Validate()
    {
        if (_validating)
        {
            return; // a rule or a change handler set a property: the running pass covers it
        }

        _validating = true;
        try
        {
            var errors = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach ((string property, Func<string?> rule) in _rules)
            {
                if (!errors.ContainsKey(property) && rule() is { Length: > 0 } message)
                {
                    errors[property] = message;
                }
            }

            foreach ((string[] _, Func<IReadOnlyDictionary<string, string?>> rules) in _ruleSets)
            {
                foreach ((string property, string? message) in rules())
                {
                    if (!string.IsNullOrEmpty(message) && !errors.ContainsKey(property))
                    {
                        errors[property] = message;
                    }
                }
            }

            foreach ((string property, string message) in _serverErrors)
            {
                errors[property] = message; // what the server said is the more specific answer
            }

            bool changed = !SameContent(_errors, errors);
            _errors.Clear();
            foreach ((string property, string message) in errors)
            {
                _errors[property] = message;
            }

            var visible = errors
                .Where(e => _showAll || _touched.Contains(e.Key) || _serverErrors.ContainsKey(e.Key))
                .ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);
            var changedProperties = _visible.Keys.Union(visible.Keys)
                .Where(p => _visible.GetValueOrDefault(p) != visible.GetValueOrDefault(p))
                .ToList();
            _visible.Clear();
            foreach ((string property, string message) in visible)
            {
                _visible[property] = message;
            }

            foreach (string property in changedProperties)
            {
                _errorsChanged(property);
            }

            if (changed || changedProperties.Count > 0)
            {
                _stateChanged?.Invoke();
            }
        }
        finally
        {
            _validating = false;
        }
    }

    private void AddField(string property)
    {
        if (!_fields.Contains(property, StringComparer.Ordinal))
        {
            _fields.Add(property);
        }
    }

    private static bool SameContent(Dictionary<string, string> a, Dictionary<string, string> b) =>
        a.Count == b.Count && a.All(e => b.TryGetValue(e.Key, out string? v) && v == e.Value);
}
