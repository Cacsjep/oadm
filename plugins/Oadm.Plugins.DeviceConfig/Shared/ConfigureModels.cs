using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Oadm.Plugins.DeviceConfig;

/// <summary>Ids, names and limits of Configure devices and Create configuration file.</summary>
public static class ConfigureIds
{
    public const string Configure = "oadm.device-config.configure";

    public const string ConfigureName = "Configure devices";

    public const string Create = "oadm.device-config.create";

    public const string CreateName = "Create configuration file";

    /// <summary>Query: parameters (definitions with the device's values), action rules, schedules and stream profiles.</summary>
    public const string ReadQuery = "read";

    /// <summary>Query: the parameter definitions only (a device of another model or AXIS OS of the selection).</summary>
    public const string DefinitionsQuery = "definitions";

    /// <summary>Devices the dialog reads at the same time (one per model and AXIS OS).</summary>
    public const int ReadParallelism = 4;

    /// <summary>Parameter groups read from a device at the same time.</summary>
    public const int GroupParallelism = 4;

    /// <summary>Most parameters, rules and profiles one run may set (a whole device has about 1,000 writable parameters).</summary>
    public const int MaxParameters = 5000;

    public const int MaxRules = 200;

    public const int MaxStreamProfiles = 100;

    /// <summary>A stream profile name the device accepts (streamprofile.cgi: 1..64 characters).</summary>
    public const int MaxStreamProfileName = 64;

    public static string Count(int n, string noun) =>
        string.Create(CultureInfo.InvariantCulture, $"{n:N0} {noun}{(n == 1 ? string.Empty : "s")}");
}

/// <summary>The permission keys of Configure devices ("task:oadm.device-config.configure:&lt;key&gt;").</summary>
public static class ConfigurePermissions
{
    public const string Configure = "configure";

    /// <summary>The parameters <see cref="DangerousParameters"/> warns about; without it they are not offered.</summary>
    public const string Dangerous = "dangerous";
}

/// <summary>The type of a device parameter (param.cgi <c>listdefinitions</c>).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ParamKind>))]
public enum ParamKind
{
    Text,
    Number,
    Bool,
    Enum,
    Hostname,
    Ip,
    IpList,
    Password,
}

/// <summary>One choice of an enum parameter: the device value and its display text.</summary>
public sealed record ParamChoice(string Value, string? Text = null)
{
    public string Display => string.IsNullOrEmpty(Text) ? Value : Text;
}

/// <summary>
/// A writable parameter of a device: its full name ("Image.I0.Appearance.Compression"), the device's display name
/// ("Compression"), its type with range or choices, and the device's current value (empty for passwords).
/// </summary>
public sealed record ParamDefinition(string Name, ParamKind Kind)
{
    public string? NiceName { get; init; }

    public string? Value { get; init; }

    public long? Min { get; init; }

    public long? Max { get; init; }

    public int? MaxLength { get; init; }

    public IReadOnlyList<ParamChoice> Choices { get; init; } = [];

    /// <summary>The device's text for true and false of a bool ("yes" / "no", "true" / "false", "password" / ...).</summary>
    public string? TrueValue { get; init; }

    public string? FalseValue { get; init; }

    /// <summary>The group part of the name ("Image.I0.Appearance").</summary>
    [JsonIgnore]
    public string Group => Name.LastIndexOf('.') is var dot and > 0 ? Name[..dot] : string.Empty;

    /// <summary>The last part of the name ("Compression").</summary>
    [JsonIgnore]
    public string ShortName => Name.LastIndexOf('.') is var dot and >= 0 ? Name[(dot + 1)..] : Name;

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(NiceName) ? ShortName : NiceName!;
}

/// <summary>A name and a value (parameters of a file, of an action, of a run).</summary>
public sealed record NameValue(string Name, string Value);

/// <summary>A schedule (recurrence) of the device's event service: id, name and iCalendar text.</summary>
public sealed record ConfigSchedule(string Id, string Name, string Definition)
{
    /// <summary>The schedules every device has (<c>com.axis.schedules.office_hours</c> ...); never created or removed.</summary>
    [JsonIgnore]
    public bool IsBuiltIn => IsBuiltInId(Id);

    public static bool IsBuiltInId(string id) =>
        id.StartsWith("com.axis.schedules.", StringComparison.Ordinal) && !id.StartsWith("com.axis.schedules.genid.", StringComparison.Ordinal);
}

/// <summary>An event filter of an action rule: the topic and an optional message filter.</summary>
public sealed record ConfigEventFilter(string Topic, string? Message);

/// <summary>The action of a rule, copied in like ADM does: name, template and its parameters.</summary>
public sealed record ConfigAction(string Name, string Template, IReadOnlyList<NameValue> Parameters)
{
    /// <summary>The device's configuration id when read from a device or a file (informational; never written).</summary>
    public string? Id { get; init; }
}

/// <summary>An action rule with its action (ADM's <c>ActionRule</c>).</summary>
public sealed record ConfigActionRule(string Name, bool Enabled, ConfigEventFilter? StartEvent, IReadOnlyList<ConfigEventFilter> Conditions, ConfigAction Action)
{
    /// <summary>How long the rule stays active after its start event, exactly as the device or file says it; null = not set.</summary>
    public string? ActivationTimeout { get; init; }

    /// <summary>The own (not built-in) schedule ids the rule's filters name.</summary>
    [JsonIgnore]
    public IEnumerable<string> ScheduleIds =>
        new[] { StartEvent }.Concat(Conditions).OfType<ConfigEventFilter>()
            .SelectMany(f => ScheduleRefs.Find(f.Message))
            .Where(id => !ConfigSchedule.IsBuiltInId(id))
            .Distinct(StringComparer.Ordinal);
}

/// <summary>A stream profile (streamprofile.cgi): name, description and its stream parameters.</summary>
public sealed record ConfigStreamProfile(string Name, string? Description, string Parameters)
{
    public bool IsDefault { get; init; }
}

/// <summary>Schedule ids inside a rule's message filter (<c>@Value="com.axis.schedules.genid.id-0"</c>).</summary>
public static class ScheduleRefs
{
    private const string Prefix = "com.axis.schedules.";

    public static IEnumerable<string> Find(string? filter)
    {
        if (string.IsNullOrEmpty(filter))
        {
            yield break;
        }

        var at = 0;
        while ((at = filter.IndexOf(Prefix, at, StringComparison.Ordinal)) >= 0)
        {
            var end = at;
            while (end < filter.Length && filter[end] is not ('"' or '\'' or ' ' or ')' or ']'))
            {
                end++;
            }

            yield return filter[at..end];
            at = end;
        }
    }

    /// <summary>
    /// The filter with each schedule id replaced by its new id (ids not in the map stay), in one pass over the filter: a new
    /// id that equals another old id is never replaced a second time (genid ids are numbered on every device).
    /// </summary>
    public static string? Rewrite(string? filter, IReadOnlyDictionary<string, string> newIds)
    {
        ArgumentNullException.ThrowIfNull(newIds);
        if (string.IsNullOrEmpty(filter) || newIds.Count == 0)
        {
            return filter;
        }

        var result = new System.Text.StringBuilder(filter.Length);
        var at = 0;
        while (true)
        {
            var start = filter.IndexOf(Prefix, at, StringComparison.Ordinal);
            if (start < 0)
            {
                result.Append(filter, at, filter.Length - at);
                return result.ToString();
            }

            var end = start;
            while (end < filter.Length && filter[end] is not ('"' or '\'' or ' ' or ')' or ']'))
            {
                end++;
            }

            var id = filter[start..end];
            result.Append(filter, at, start - at).Append(newIds.TryGetValue(id, out var renewed) ? renewed : id);
            at = end;
        }
    }
}

/// <summary>Answer of the read and definitions queries: what one device has, or why it could not be read.</summary>
public sealed record DeviceSettings
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public string? Model { get; init; }

    public string? Firmware { get; init; }

    public string? Serial { get; init; }

    /// <summary>The writable parameters with their definitions (and values for the read query).</summary>
    public IReadOnlyList<ParamDefinition> Parameters { get; init; } = [];

    public IReadOnlyList<ConfigActionRule> Rules { get; init; } = [];

    public IReadOnlyList<ConfigSchedule> Schedules { get; init; } = [];

    public IReadOnlyList<ConfigStreamProfile> StreamProfiles { get; init; } = [];

    /// <summary>Why the device could not be read at all; null when it could.</summary>
    public string? Error { get; init; }

    /// <summary>Parts that could not be read ("Action rules: ..."); the rest is usable.</summary>
    public IReadOnlyList<string> Problems { get; init; } = [];

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static DeviceSettings Parse(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? new DeviceSettings { Error = "The server sent no answer." }
            : JsonSerializer.Deserialize<DeviceSettings>(json, Options) ?? new DeviceSettings { Error = "The server sent no answer." };
}

/// <summary>
/// The payload of Configure devices: what is applied to every selected device. Kept in memory only (it may hold passwords);
/// never logged.
/// </summary>
public sealed record ConfigurePayload
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public IReadOnlyList<NameValue> Parameters { get; init; } = [];

    public IReadOnlyList<ConfigActionRule> Rules { get; init; } = [];

    /// <summary>The own schedules the rules use (built-in ones are not sent).</summary>
    public IReadOnlyList<ConfigSchedule> Schedules { get; init; } = [];

    public IReadOnlyList<ConfigStreamProfile> StreamProfiles { get; init; } = [];

    /// <summary>"10.0.0.48 (AXIS P3265-V)" or the file name; for the log.</summary>
    public string? Source { get; init; }

    /// <summary>
    /// The device the settings were taken from, when it is one: it keeps its own action rules, schedules and stream profiles
    /// (writing them back would only remove and recreate them); its parameters are set like on every device.
    /// </summary>
    public Guid? SourceDeviceId { get; init; }

    [JsonIgnore]
    public bool IsEmpty => Parameters.Count == 0 && Rules.Count == 0 && StreamProfiles.Count == 0;

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    /// <summary>Longest text OADM sends in a rule, schedule or stream profile field.</summary>
    public const int MaxTextLength = 4096;

    /// <summary>Most parameters of one action.</summary>
    public const int MaxActionParameters = 100;

    /// <summary>Reads and checks the payload; throws <see cref="ArgumentException"/> with a user text.</summary>
    public static ConfigurePayload Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new ArgumentException("Nothing to apply.");
        }

        ConfigurePayload payload;
        try
        {
            payload = JsonSerializer.Deserialize<ConfigurePayload>(json, Options) ?? throw new ArgumentException("Nothing to apply.");
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("The settings could not be read.", ex);
        }

        if (payload.IsEmpty)
        {
            throw new ArgumentException("Nothing to apply.");
        }

        if (payload.Parameters.Count > ConfigureIds.MaxParameters || payload.Rules.Count > ConfigureIds.MaxRules || payload.StreamProfiles.Count > ConfigureIds.MaxStreamProfiles)
        {
            throw new ArgumentException("Too many settings in one run.");
        }

        if (payload.Parameters.Any(p => string.IsNullOrWhiteSpace(p.Name) || p.Value is null)
            || payload.Parameters.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != payload.Parameters.Count)
        {
            throw new ArgumentException("A parameter has no name or is listed twice.");
        }

        if (payload.Rules.Any(r => r is null || string.IsNullOrWhiteSpace(r.Name) || string.IsNullOrWhiteSpace(r.Action?.Template)
                || r.Conditions is null || r.Action.Parameters is null || r.Action.Parameters.Count > MaxActionParameters
                || r.Action.Parameters.Any(p => p is null || string.IsNullOrWhiteSpace(p.Name) || p.Value is null)
                || new[] { r.StartEvent }.Concat(r.Conditions).Any(f => f is not null && string.IsNullOrWhiteSpace(f.Topic))))
        {
            throw new ArgumentException("An action rule has no name, no action or an incomplete event.");
        }

        if (payload.Rules.Select(r => r.Name).Distinct(StringComparer.Ordinal).Count() != payload.Rules.Count)
        {
            throw new ArgumentException("Two action rules have the same name.");
        }

        if (payload.Schedules.Count > ConfigureIds.MaxRules
            || payload.Schedules.Any(s => s is null || string.IsNullOrWhiteSpace(s.Id) || string.IsNullOrWhiteSpace(s.Definition) || s.Name is null))
        {
            throw new ArgumentException("A schedule is incomplete.");
        }

        // Every own schedule a rule uses must come along: a rule never points to an id the device may not have.
        var known = payload.Schedules.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        if (payload.Rules.SelectMany(r => r.ScheduleIds).FirstOrDefault(id => !known.Contains(id)) is { } missing)
        {
            throw new ArgumentException($"An action rule uses a schedule that is not included ({missing}).");
        }

        if (payload.StreamProfiles.Any(p => p is null || string.IsNullOrWhiteSpace(p.Name) || p.Name.Length > ConfigureIds.MaxStreamProfileName || p.Parameters is null))
        {
            throw new ArgumentException("A stream profile has no name or a name longer than 64 characters.");
        }

        if (payload.StreamProfiles.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != payload.StreamProfiles.Count)
        {
            throw new ArgumentException("Two stream profiles have the same name.");
        }

        if (Texts(payload).Any(t => t.Length > MaxTextLength || t.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t'))))
        {
            throw new ArgumentException("A value is too long or has control characters.");
        }

        return payload;
    }

    /// <summary>Every text of the rules, schedules and profiles (for the length and character check).</summary>
    private static IEnumerable<string> Texts(ConfigurePayload payload) =>
        payload.Rules.SelectMany(r => new[] { r.Name, r.ActivationTimeout ?? string.Empty, r.Action.Name, r.Action.Template }
                .Concat(r.Action.Parameters.SelectMany(p => new[] { p.Name, p.Value }))
                .Concat(new[] { r.StartEvent }.Concat(r.Conditions).OfType<ConfigEventFilter>().SelectMany(f => new[] { f.Topic, f.Message ?? string.Empty })))
            .Concat(payload.Schedules.SelectMany(s => new[] { s.Id, s.Name, s.Definition }))
            .Concat(payload.StreamProfiles.SelectMany(p => new[] { p.Name, p.Description ?? string.Empty, p.Parameters }));

    /// <summary>
    /// "Configure 12 parameters", "Configure 1 action rule"; with several kinds the short nouns keep it within the 48
    /// characters of a task name: "Configure 12 parameters, 1 rule, 3 profiles".
    /// </summary>
    public string TaskName()
    {
        var kinds = (Parameters.Count > 0 ? 1 : 0) + (Rules.Count > 0 ? 1 : 0) + (StreamProfiles.Count > 0 ? 1 : 0);
        var parts = new List<string>();
        if (Parameters.Count > 0)
        {
            parts.Add(ConfigureIds.Count(Parameters.Count, "parameter"));
        }

        if (Rules.Count > 0)
        {
            parts.Add(ConfigureIds.Count(Rules.Count, kinds > 1 ? "rule" : "action rule"));
        }

        if (StreamProfiles.Count > 0)
        {
            parts.Add(ConfigureIds.Count(StreamProfiles.Count, kinds > 1 ? "profile" : "stream profile"));
        }

        return "Configure " + string.Join(", ", parts);
    }
}
