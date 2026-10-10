using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Oadm.Sdk.Events;

/// <summary>Which devices a condition or an action means.</summary>
public enum DeviceSelectorMode
{
    /// <summary>Every managed device.</summary>
    All = 0,

    /// <summary>The devices in <see cref="DeviceSelector.DeviceIds"/>.</summary>
    Devices = 1,

    /// <summary>Devices with at least one of <see cref="DeviceSelector.Tags"/>.</summary>
    Tags = 2,

    /// <summary>
    /// The device the rule's trigger fired for: in a further condition "the same device", in an action "the device of
    /// the event". Not allowed on the trigger itself.
    /// </summary>
    EventDevice = 3,
}

public sealed class DeviceSelector
{
    public DeviceSelectorMode Mode { get; set; }

    public List<Guid> DeviceIds { get; set; } = [];

    public List<string> Tags { get; set; } = [];

    public DeviceSelector Clone() => new() { Mode = Mode, DeviceIds = [.. DeviceIds], Tags = [.. Tags] };
}

public enum RuleConditionKind
{
    /// <summary>An event (the first condition is the rule's trigger).</summary>
    Event = 0,

    /// <summary>A time window on weekdays, server time zone.</summary>
    Schedule = 1,
}

/// <summary>Weekdays plus from / to ("08:00", "18:00"); a "to" at or before "from" runs over midnight.</summary>
public sealed class RuleSchedule
{
    public List<DayOfWeek> Days { get; set; } = [];

    public string From { get; set; } = "00:00";

    public string To { get; set; } = "00:00";

    public RuleSchedule Clone() => new() { Days = [.. Days], From = From, To = To };
}

/// <summary>
/// One condition. Simple mode: the conditions of a rule must all hold. The first is the trigger (an event); only the
/// trigger may be stateless. Advanced mode (AND / OR groups) will extend this model.
/// </summary>
public sealed class RuleCondition
{
    public RuleConditionKind Kind { get; set; }

    public string? EventTypeId { get; set; }

    public DeviceSelector Devices { get; set; } = new();

    /// <summary>Optional filter: every listed event data field must equal its value.</summary>
    public Dictionary<string, string> Data { get; set; } = [];

    public RuleSchedule? Schedule { get; set; }

    public RuleCondition Clone() => new()
    {
        Kind = Kind,
        EventTypeId = EventTypeId,
        Devices = Devices.Clone(),
        Data = new Dictionary<string, string>(Data),
        Schedule = Schedule?.Clone(),
    };
}

/// <summary>
/// One action of a rule: the action plugin, its targets (devices for actions that act on devices) and its settings
/// (JSON object; password fields are never sent to clients, see <see cref="StoredSecretFields"/>).
/// </summary>
public sealed class RuleAction
{
    /// <summary>Stable id of the action within its rule (new actions: a new Guid): stored password values follow it.</summary>
    public Guid Key { get; set; } = Guid.NewGuid();

    public string ActionId { get; set; } = string.Empty;

    public DeviceSelector Targets { get; set; } = new() { Mode = DeviceSelectorMode.EventDevice };

    public JsonObject Settings { get; set; } = [];

    /// <summary>
    /// Read: password fields with a value stored on the server. Write: leave such a field empty to keep the stored
    /// value; a field missing from this list and empty clears it.
    /// </summary>
    public List<string> StoredSecretFields { get; set; } = [];

    public RuleAction Clone() => new()
    {
        Key = Key,
        ActionId = ActionId,
        Targets = Targets.Clone(),
        Settings = (JsonObject)Settings.DeepClone(),
        StoredSecretFields = [.. StoredSecretFields],
    };
}

/// <summary>An event rule as clients edit it (definition JSON of gRPC EventService).</summary>
public sealed class RuleDefinition
{
    public const int MaxNameLength = 64;
    public const int MaxConditions = 10;
    public const int MaxActions = 5;
    public const int DefaultCooldownSeconds = 10;
    public const int DefaultMaxRunsPerMinute = 60;

    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public bool Enabled { get; set; }

    public List<RuleCondition> Conditions { get; set; } = [];

    public List<RuleAction> Actions { get; set; } = [];

    /// <summary>Per device: at least this long between two "turns on" (0..3600).</summary>
    public int CooldownSeconds { get; set; } = DefaultCooldownSeconds;

    /// <summary>For the whole rule: at most this many action runs per minute (1..1000).</summary>
    public int MaxRunsPerMinute { get; set; } = DefaultMaxRunsPerMinute;

    public RuleDefinition Clone() => new()
    {
        Id = Id,
        Name = Name,
        Enabled = Enabled,
        Conditions = [.. Conditions.Select(c => c.Clone())],
        Actions = [.. Actions.Select(a => a.Clone())],
        CooldownSeconds = CooldownSeconds,
        MaxRunsPerMinute = MaxRunsPerMinute,
    };

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Throws <see cref="JsonException"/> for text that is not a rule.</summary>
    public static RuleDefinition FromJson(string json) =>
        JsonSerializer.Deserialize<RuleDefinition>(json, Json) ?? throw new JsonException("Not a rule.");
}
