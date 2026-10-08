namespace Oadm.Plugins.HardeningScan.Device;

/// <summary>
/// The parameters of one <c>param.cgi?action=list&amp;group=...</c> answer. A group the firmware does not have does not fail
/// the request: the answer contains a line <c># Error: Error -1 getting param in group 'Network.Filter'</c> and the other
/// groups still come back, but the group right after the error line came back without the <c>root.</c> prefix on AXIS OS
/// 12.11 (<c>System.WebInterfaceDisabled=no</c>). Both forms are accepted; an error line means "not available".
/// </summary>
public sealed class ParamList
{
    private readonly Dictionary<string, string> _values;

    private ParamList(Dictionary<string, string> values, IReadOnlyList<string> errors)
    {
        _values = values;
        Errors = errors;
    }

    public static ParamList Empty { get; } = new(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), []);

    /// <summary>The <c># Error</c> lines of the answer.</summary>
    public IReadOnlyList<string> Errors { get; }

    public int Count => _values.Count;

    public IReadOnlyDictionary<string, string> Values => _values;

    /// <summary>The value of a parameter (name without <c>root.</c>, case-insensitive), null when the device did not send it.</summary>
    public string? this[string name] => _values.TryGetValue(name, out var value) ? value : null;

    /// <summary>True for yes / true / on / 1 (case-insensitive), false for no / false / off / 0, null when missing or other.</summary>
    public bool? Bool(string name) => ParseBool(this[name]);

    public bool Has(string name) => _values.ContainsKey(name);

    /// <summary>The parameters whose name starts with <paramref name="prefix"/> (e.g. "Audio.A").</summary>
    public IEnumerable<KeyValuePair<string, string>> WithPrefix(string prefix) =>
        _values.Where(p => p.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    public static bool? ParseBool(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "YES" or "TRUE" or "ON" or "1" => true,
        "NO" or "FALSE" or "OFF" or "0" => false,
        _ => null,
    };

    public static ParamList Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Trim().Length == 0)
            {
                continue;
            }

            if (line.TrimStart().StartsWith('#'))
            {
                errors.Add(line.Trim());
                continue;
            }

            var equals = line.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0)
            {
                continue;
            }

            var name = line[..equals].Trim();
            if (name.StartsWith("root.", StringComparison.OrdinalIgnoreCase))
            {
                name = name[5..];
            }

            if (name.Length > 0)
            {
                values[name] = line[(equals + 1)..];
            }
        }

        return new ParamList(values, errors);
    }
}
