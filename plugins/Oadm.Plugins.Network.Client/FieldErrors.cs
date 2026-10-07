using System.Collections;
using System.ComponentModel;

namespace Oadm.Plugins.Network.Client;

/// <summary>
/// Field errors of a dialog view model for <see cref="INotifyDataErrorInfo"/>: one message per bound property, so
/// Avalonia shows it directly below the input (DataValidationErrors). Errors of table rows stay in the table.
/// </summary>
public sealed class FieldErrors
{
    private readonly Dictionary<string, string> _errors = new(StringComparer.Ordinal);
    private readonly Action<string> _changed;

    /// <param name="changed">Raises <see cref="INotifyDataErrorInfo.ErrorsChanged"/> for a property name.</param>
    public FieldErrors(Action<string> changed)
    {
        _changed = changed ?? throw new ArgumentNullException(nameof(changed));
    }

    public bool HasErrors => _errors.Count > 0;

    /// <summary>The first error, in the order the fields were set (for the disabled button's tooltip).</summary>
    public string? First => _errors.Values.FirstOrDefault();

    public string? this[string property] => _errors.GetValueOrDefault(property);

    public IEnumerable GetErrors(string? property) =>
        property is not null && _errors.TryGetValue(property, out var error) ? new[] { error } : Array.Empty<string>();

    /// <summary>Replaces all errors; raises the change for every property whose error changed.</summary>
    public void SetAll(IReadOnlyDictionary<string, string?> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        var changed = new List<string>();
        foreach (var name in _errors.Keys.Where(k => !errors.TryGetValue(k, out var e) || e is null).ToList())
        {
            _errors.Remove(name);
            changed.Add(name);
        }

        foreach (var (name, error) in errors)
        {
            if (error is null || _errors.GetValueOrDefault(name) == error)
            {
                continue;
            }

            _errors[name] = error;
            changed.Add(name);
        }

        foreach (var name in changed)
        {
            _changed(name);
        }
    }

    /// <summary>"IPv4: enter a subnet mask ..." becomes "Enter a subnet mask ...": the field already says what it is.</summary>
    public static string Clean(string error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var text = error;
        foreach (var prefix in new[] { "IPv4: ", "IPv6: ", "DNS: ", "Host name: " })
        {
            if (text.StartsWith(prefix, StringComparison.Ordinal))
            {
                text = text[prefix.Length..];
                break;
            }
        }

        return text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
    }
}
