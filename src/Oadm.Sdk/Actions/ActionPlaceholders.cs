using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Oadm.Sdk.Actions;

/// <summary>How a placeholder value is written into a text with a format, so the text stays valid.</summary>
public enum PlaceholderEscape
{
    /// <summary>The value as it is (plain text, an email subject).</summary>
    None = 0,

    /// <summary>JSON string content: quotes, backslashes and control characters escaped (inside "..." of a JSON body).</summary>
    Json = 1,

    /// <summary>&amp; &lt; &gt; " ' escaped (XML and HTML bodies).</summary>
    Xml = 2,

    /// <summary>Percent-encoded (URL parts, form bodies).</summary>
    Url = 3,
}

/// <summary>A placeholder of the event rule actions: "{serial}" and what it stands for.</summary>
public sealed record ActionPlaceholder(string Name, string Group, string Description)
{
    /// <summary>"{serial}".</summary>
    public string Text => "{" + Name + "}";
}

/// <summary>
/// The placeholders of the event rule actions (user decision 2026-10-11: everything OADM knows about the rule, the event
/// and the device), one list for the server (<see cref="Fill"/>) and the rule dialog's placeholder panel. The values of
/// the event come on top, by their name ({objectId}, {port}); the fixed names win over event values of the same name.
/// </summary>
public static partial class ActionPlaceholders
{
    public const string RuleGroup = "Rule";
    public const string DeviceGroup = "Device";

    public static IReadOnlyList<ActionPlaceholder> Fixed { get; } =
    [
        new("rule", RuleGroup, "Name of the rule"),
        new("event", RuleGroup, "Name of the event"),
        new("state", RuleGroup, "on or off"),
        new("time", RuleGroup, "Server time, 2026-10-11 14:05:09"),
        new("date", RuleGroup, "Server date, 2026-10-11"),
        new("timeUtc", RuleGroup, "UTC time, 2026-10-11T12:05:09Z"),
        new("device", DeviceGroup, "Address and model, 10.0.0.48 (P3265-V)"),
        new("address", DeviceGroup, "IP address or host name"),
        new("hostName", DeviceGroup, "Host name"),
        new("model", DeviceGroup, "Model, AXIS P3265-V"),
        new("serial", DeviceGroup, "Serial number, B8A44F631339"),
        new("mac", DeviceGroup, "MAC address, B8:A4:4F:63:13:39"),
        new("firmware", DeviceGroup, "AXIS OS version"),
        new("category", DeviceGroup, "Camera, Speaker, I/O module ..."),
        new("tags", DeviceGroup, "Tags, Building A; PTZ"),
        new("status", DeviceGroup, "OK, Unreachable ..."),
        new("webUrl", DeviceGroup, "Link to the device web interface"),
    ];

    [GeneratedRegex(@"\{([A-Za-z_][A-Za-z0-9_.\-]*)\}", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    /// <summary>
    /// Fills every {name} that <paramref name="lookup"/> knows (case-insensitive names are the caller's choice) in one pass,
    /// so a value that contains "{...}" is never filled again; unknown names stay as they are.
    /// </summary>
    public static string Fill(string? text, Func<string, string?> lookup, PlaceholderEscape escape = PlaceholderEscape.None)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        if (string.IsNullOrEmpty(text) || !text.Contains('{', StringComparison.Ordinal))
        {
            return text ?? string.Empty;
        }

        return Pattern().Replace(text, m => lookup(m.Groups[1].Value) is { } value ? Escape(value, escape) : m.Value);
    }

    /// <summary>Writes <paramref name="value"/> for a text of the given format.</summary>
    public static string Escape(string value, PlaceholderEscape escape)
    {
        ArgumentNullException.ThrowIfNull(value);
        return escape switch
        {
            PlaceholderEscape.Json => JsonEncodedText.Encode(value, System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping).ToString(),
            PlaceholderEscape.Xml => XmlEscape(value),
            PlaceholderEscape.Url => Uri.EscapeDataString(value),
            _ => value,
        };
    }

    private static string XmlEscape(string value)
    {
        var builder = new StringBuilder(value.Length + 16);
        foreach (var c in value)
        {
            builder.Append(c switch
            {
                '&' => "&amp;",
                '<' => "&lt;",
                '>' => "&gt;",
                '"' => "&quot;",
                '\'' => "&#39;",
                _ => c.ToString(),
            });
        }

        return builder.ToString();
    }
}
