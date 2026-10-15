using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

using Oadm.Plugins.ActionRules.Device;

namespace Oadm.Plugins.ActionRules.Rules;

/// <summary>
/// Event filters compared and shown the same way on server and page. Devices store a filter in their own form: AXIS OS 10
/// appends <c>//.</c> to a condition's topic, AXIS OS 12 reorders the <c>and</c> terms of the message filter. So a filter is
/// compared by its canonical form: the topic without <c>//.</c> and the sorted <c>Name=Value</c> terms.
/// </summary>
public static partial class RuleText
{
    /// <summary>The topic of a schedule's interval (a rule that runs while the schedule is active).</summary>
    public const string ScheduleIntervalTopic = "tns1:UserAlarm/tnsaxis:Recurring/Interval";

    /// <summary>The topic of a schedule's pulse.</summary>
    public const string SchedulePulseTopic = "tns1:UserAlarm/tnsaxis:Recurring/Pulse";

    /// <summary>The topic as compared: trimmed, without the <c>//.</c> AXIS OS 10 appends.</summary>
    public static string Topic(string topic) => SoapActionEngine.Topic(topic);

    /// <summary>The <c>Name=Value</c> terms of a message filter in the order written.</summary>
    public static IReadOnlyList<(string Name, string Value)> Terms(string? message) =>
        message is null ? [] : [.. TermPattern().Matches(message).Select(m => (m.Groups["n"].Value, m.Groups["v"].Value))];

    /// <summary>One string per filter that is equal for filters that mean the same.</summary>
    public static string Key(EventFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var key = new StringBuilder(Topic(filter.Topic));
        if (filter.Message is { } message)
        {
            var terms = Terms(message).Select(t => t.Name + "=" + t.Value).Order(StringComparer.Ordinal);
            key.Append('|').Append(string.Join("&", terms));
            var rest = WhitespacePattern().Replace(TermPattern().Replace(message, " "), " ")
                .Replace(" and ", " ", StringComparison.Ordinal).Trim();
            if (rest is not ("" or "and"))
            {
                key.Append('|').Append(rest);
            }
        }

        return key.ToString();
    }

    /// <summary>The trigger of a rule (start event, conditions in any order, timeout) as one comparable string.</summary>
    public static string TriggerKey(EventFilter? start, IEnumerable<EventFilter> conditions, string? timeout) =>
        "start:" + (start is null ? "-" : Key(start))
        + ";if:" + string.Join(",", conditions.Select(Key).Order(StringComparer.Ordinal))
        + ";t:" + (timeout ?? "-");

    /// <summary>
    /// The filter of an event with the chosen item values, as the web interface writes it:
    /// <c>boolean(//SimpleItem[@Name="port" and @Value="1"]) and boolean(//SimpleItem[@Name="active" and @Value="1"])</c>.
    /// </summary>
    public static EventFilter Filter(string topic, IEnumerable<NameValue> values)
    {
        ArgumentNullException.ThrowIfNull(topic);
        ArgumentNullException.ThrowIfNull(values);
        var terms = values.Select(v => $"boolean(//SimpleItem[@Name=\"{v.Name}\" and @Value=\"{v.Value}\"])").ToList();
        return new EventFilter(Topic(topic), terms.Count == 0 ? null : string.Join(" and ", terms));
    }

    /// <summary>The condition "while the schedule is active".</summary>
    public static EventFilter ScheduleCondition(string scheduleId) =>
        Filter(ScheduleIntervalTopic, [new NameValue("id", scheduleId), new NameValue("active", "1")]);

    /// <summary>A name or value a filter can hold: 1 to 256 characters without quotes or control characters.</summary>
    public static bool IsFilterText(string? text) =>
        text is { Length: > 0 and <= 256 } && !text.Any(c => c is '"' or '\'' or '<' or '>' or '&' || char.IsControl(c));

    /// <summary>The filter with another schedule id in its message (the id term rebuilt, any quotes).</summary>
    public static EventFilter WithScheduleId(EventFilter filter, string id)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (filter.Message is null)
        {
            return filter;
        }

        var message = TermPattern().Replace(filter.Message, m => m.Groups["n"].Value == "id"
            ? $"boolean(//SimpleItem[@Name=\"id\" and @Value=\"{id}\"])"
            : m.Value);
        return filter with { Message = message };
    }

    /// <summary>The schedule id a filter names (a schedule's interval or pulse), or null.</summary>
    public static string? ScheduleId(EventFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var topic = Topic(filter.Topic);
        return topic is ScheduleIntervalTopic or SchedulePulseTopic
            ? Terms(filter.Message).FirstOrDefault(t => t.Name == "id").Value
            : null;
    }

    /// <summary>
    /// A filter in words: "Virtual input (port 20, active)", "Schedule: Office Hours". <paramref name="topicNames"/> are the
    /// device's own names (event list), <paramref name="scheduleNames"/> its schedules by id.
    /// </summary>
    public static string Describe(EventFilter filter, IReadOnlyDictionary<string, string>? topicNames = null, IReadOnlyDictionary<string, string>? scheduleNames = null)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (ScheduleId(filter) is { } scheduleId)
        {
            var name = scheduleNames is not null && scheduleNames.TryGetValue(scheduleId, out var n) ? n : scheduleId;
            return "Schedule: " + name;
        }

        var topic = Topic(filter.Topic);
        var title = topicNames is not null && topicNames.TryGetValue(topic, out var nice) && nice.Length > 0 ? nice : TopicTitle(topic);
        var terms = Terms(filter.Message).Select(t => t.Name + " " + t.Value).ToList();
        return terms.Count == 0 ? title : title + " (" + string.Join(", ", terms) + ")";
    }

    /// <summary>A readable title from the topic alone: "CameraApplicationPlatform / faceverifier / FaceUnknown".</summary>
    public static string TopicTitle(string topic)
    {
        var parts = Topic(topic).Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Contains(':', StringComparison.Ordinal) ? p[(p.IndexOf(':', StringComparison.Ordinal) + 1)..] : p)
            .ToList();
        return parts.Count == 0 ? topic : string.Join(" / ", parts.Count > 3 ? parts.Skip(parts.Count - 3) : parts);
    }

    /// <summary>
    /// Parameters that hold a secret: never shown or logged. HTTP headers count too: they may carry an Authorization header.
    /// </summary>
    public static bool IsSecret(string parameterName) =>
        parameterName.Equals("http_headers", StringComparison.OrdinalIgnoreCase)
        || parameterName.Contains("password", StringComparison.OrdinalIgnoreCase)
        || parameterName.Contains("passphrase", StringComparison.OrdinalIgnoreCase)
        || parameterName.Contains("private_key", StringComparison.OrdinalIgnoreCase)
        || parameterName.Contains("secret", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Settings the device takes only with a value: the SFTP server's key fingerprint is given as MD5 or as SHA-256, and the
    /// other one is left out (empty is refused: "Parameter "ssh_host_pub_key_md5" contains invalid value"; verified on
    /// AXIS OS 10.12, 11.9 and 12.11 over SOAP and REST).
    /// </summary>
    public static bool OmitWhenEmpty(string parameterName) => parameterName is "ssh_host_pub_key_md5" or "ssh_host_pub_key_sha256";

    /// <summary>What the page shows instead of the password of a login inside an address (<c>ftp://user:pass@host</c>).</summary>
    public const string HiddenPassword = "********";

    /// <summary>
    /// The value with the password of every login inside an address replaced by <see cref="HiddenPassword"/>
    /// (<c>http://user:********@host/</c>), so it never reaches the page.
    /// </summary>
    public static string HideLogin(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var logins = Logins(value);
        for (var i = logins.Count - 1; i >= 0; i--)
        {
            if (logins[i].Length > 0)
            {
                value = value[..logins[i].Start] + HiddenPassword + value[(logins[i].Start + logins[i].Length)..];
            }
        }

        return value;
    }

    /// <summary>Whether the value holds a password inside an address (stored encrypted for undo like a password).</summary>
    public static bool HasLogin(string value) => Logins(value ?? string.Empty).Any(l => l.Length > 0);

    /// <summary>
    /// A value from the page with each hidden password put back from <paramref name="current"/> (the device's value, or the
    /// copied rule's), only for the same scheme, user and host: a hidden password never goes to another server. Unchanged
    /// without <see cref="HiddenPassword"/>; throws when there is no such password to put back.
    /// </summary>
    public static string KeepLogin(string written, string? current)
    {
        ArgumentNullException.ThrowIfNull(written);
        var logins = Logins(written);
        if (!logins.Any(l => written.AsSpan(l.Start, l.Length).SequenceEqual(HiddenPassword)))
        {
            return written;
        }

        var own = Logins(current ?? string.Empty);
        for (var i = logins.Count - 1; i >= 0; i--)
        {
            var login = logins[i];
            if (!written.AsSpan(login.Start, login.Length).SequenceEqual(HiddenPassword))
            {
                continue;
            }

            var match = own.FirstOrDefault(o => o.Length > 0 && o.Prefix == login.Prefix && string.Equals(o.Host, login.Host, StringComparison.OrdinalIgnoreCase));
            if (match.Prefix is null)
            {
                throw new ActionEngineException("Enter the password in the address again: a hidden password is kept only for the same user and server. Nothing was changed.");
            }

            written = string.Concat(written.AsSpan(0, login.Start), current.AsSpan(match.Start, match.Length), written.AsSpan(login.Start + login.Length));
        }

        return written;
    }

    /// <summary>
    /// The logins inside addresses of a value: per "scheme://user:password@host", where the password runs to the last "@" of
    /// the address part (before the first "/", "?", "#" or blank), like URL parsers read it.
    /// </summary>
    private static List<(int Start, int Length, string Prefix, string Host)> Logins(string value)
    {
        var result = new List<(int, int, string, string)>();
        for (var at = value.IndexOf("://", StringComparison.Ordinal); at > 0; at = value.IndexOf("://", at + 3, StringComparison.Ordinal))
        {
            var scheme = at;
            while (scheme > 0 && (char.IsAsciiLetterOrDigit(value[scheme - 1]) || value[scheme - 1] is '+' or '.' or '-'))
            {
                scheme--;
            }

            if (scheme == at || !char.IsAsciiLetter(value[scheme]))
            {
                continue;
            }

            var start = at + 3;
            var end = start;
            while (end < value.Length && value[end] is not ('/' or '?' or '#') && !char.IsWhiteSpace(value[end]))
            {
                end++;
            }

            var last = value.LastIndexOf('@', end - 1, end - start);
            if (last < 0)
            {
                continue;
            }

            var colon = value.IndexOf(':', start, last - start);
            if (colon < 0)
            {
                continue;
            }

            result.Add((colon + 1, last - colon - 1, value[scheme..colon], value[(last + 1)..end]));
        }

        return result;
    }

    /// <summary>The ISO 8601 duration of an activation timeout in words ("10 s"); the raw text when it is unusual.</summary>
    public static string TimeoutText(string? timeout)
    {
        if (string.IsNullOrEmpty(timeout))
        {
            return string.Empty;
        }

        return TimeoutSeconds(timeout) is { } seconds ? seconds.ToString("N0", CultureInfo.CurrentCulture) + " s" : timeout;
    }

    /// <summary>The seconds of a timeout like <c>PT10S</c> or <c>PT1M30S</c>, or null.</summary>
    public static int? TimeoutSeconds(string? timeout)
    {
        if (timeout is null || DurationPattern().Match(timeout) is not { Success: true } m)
        {
            return null;
        }

        static int Part(Group g) => g.Success ? int.Parse(g.Value, CultureInfo.InvariantCulture) : 0;
        return Part(m.Groups["d"]) * 86400 + Part(m.Groups["h"]) * 3600 + Part(m.Groups["m"]) * 60 + Part(m.Groups["s"]);
    }

    /// <summary>A timeout in seconds as the device writes it (<c>PT10S</c>); 0 or less = none.</summary>
    public static string? Timeout(int seconds) => seconds <= 0 ? null : string.Create(CultureInfo.InvariantCulture, $"PT{seconds}S");

    [GeneratedRegex("""boolean\(\s*//SimpleItem\[\s*@Name\s*=\s*["'](?<n>[^"']*)["']\s+and\s+@Value\s*=\s*["'](?<v>[^"']*)["']\s*\]\s*\)""")]
    private static partial Regex TermPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();

    [GeneratedRegex(@"^P(?:(?<d>\d+)D)?(?:T(?:(?<h>\d+)H)?(?:(?<m>\d+)M)?(?:(?<s>\d+)S)?)?$")]
    private static partial Regex DurationPattern();
}
