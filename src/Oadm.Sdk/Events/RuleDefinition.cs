using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Oadm.Sdk.Events;

/// <summary>Which devices a condition or an action means.</summary>
public enum DeviceSelectorMode
{
    /// <summary>Every managed device.</summary>
    All = 0,

    /// <summary>
    /// A choice (tags and devices together): the devices in <see cref="DeviceSelector.DeviceIds"/>
    /// and every device with at least one of <see cref="DeviceSelector.Tags"/>.
    /// </summary>
    Devices = 1,

    /// <summary>Older rules: the same as <see cref="Devices"/> (devices with a tag, plus listed devices).</summary>
    Tags = 2,

    /// <summary>
    /// The device the rule's trigger fired for: in a further condition "the same device", in an action "the device of
    /// the event". Not allowed on the trigger itself.
    /// </summary>
    EventDevice = 3,
}

/// <summary>Which devices a condition or an action applies to.</summary>
public sealed class DeviceSelector
{
    /// <summary>How the devices are chosen.</summary>
    public DeviceSelectorMode Mode { get; set; }

    /// <summary>Chosen devices.</summary>
    public List<Guid> DeviceIds { get; set; } = [];

    /// <summary>Chosen tags: every device with one of them.</summary>
    public List<string> Tags { get; set; } = [];

    /// <summary>A copy.</summary>
    public DeviceSelector Clone() => new() { Mode = Mode, DeviceIds = [.. DeviceIds], Tags = [.. Tags] };

    /// <summary>A choice of devices and tags (<see cref="DeviceSelectorMode.Devices"/> or the older Tags).</summary>
    [JsonIgnore]
    public bool IsChoice => Mode is DeviceSelectorMode.Devices or DeviceSelectorMode.Tags;

    /// <summary>A choice with neither devices nor tags.</summary>
    [JsonIgnore]
    public bool IsEmptyChoice => IsChoice && DeviceIds.Count == 0 && Tags.Count == 0;

    /// <summary>Whether a device with <paramref name="deviceTags"/> belongs to a choice: listed, or one of its tags chosen.</summary>
    public bool ChoiceMatches(Guid deviceId, IEnumerable<string> deviceTags)
    {
        ArgumentNullException.ThrowIfNull(deviceTags);
        return DeviceIds.Contains(deviceId) || (Tags.Count > 0 && deviceTags.Any(t => Tags.Contains(t, StringComparer.OrdinalIgnoreCase)));
    }
}

/// <summary>What a condition checks.</summary>
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
    /// <summary>The weekdays.</summary>
    public List<DayOfWeek> Days { get; set; } = [];

    /// <summary>Start, "HH:mm" server time.</summary>
    public string From { get; set; } = "00:00";

    /// <summary>End, "HH:mm" server time; earlier than From spans midnight.</summary>
    public string To { get; set; } = "00:00";

    /// <summary>A copy.</summary>
    public RuleSchedule Clone() => new() { Days = [.. Days], From = From, To = To };
}

/// <summary>
/// One condition. Simple mode: the conditions of a rule must all hold. The first is the trigger (an event); only the
/// trigger may be stateless. Advanced mode (AND / OR groups) will extend this model.
/// </summary>
public sealed class RuleCondition
{
    /// <summary>An event or a schedule.</summary>
    public RuleConditionKind Kind { get; set; }

    /// <summary>The event type of an event condition.</summary>
    public string? EventTypeId { get; set; }

    /// <summary>The devices whose event counts.</summary>
    public DeviceSelector Devices { get; set; } = new();

    /// <summary>
    /// Older rules: every listed event data field must equal its value (case-insensitive). Kept as they are (an unchanged
    /// older rule saves as before) and read as <see cref="RuleCheckOperator.Eq"/> checks by <see cref="AllChecks"/>.
    /// </summary>
    public Dictionary<string, string> Data { get; set; } = [];

    /// <summary>
    /// The checks on the event's values (SDK 0.9.0), at most <see cref="RuleChecks.MaxPerCondition"/>; all must hold.
    /// JSON <c>[{"field","op","value"}]</c>.
    /// </summary>
    public List<RuleCheck> Checks { get; set; } = [];

    /// <summary>The time window of a schedule condition.</summary>
    public RuleSchedule? Schedule { get; set; }

    /// <summary>A copy.</summary>
    public RuleCondition Clone() => new()
    {
        Kind = Kind,
        EventTypeId = EventTypeId,
        Devices = Devices.Clone(),
        Data = new Dictionary<string, string>(Data),
        Checks = [.. Checks.Select(c => c.Clone())],
        Schedule = Schedule?.Clone(),
    };

    /// <summary>
    /// Every check of the condition (SDK 0.9.0): <see cref="Checks"/>, then the older equals filters of <see cref="Data"/>
    /// as <see cref="RuleCheckOperator.Eq"/> checks marked <see cref="RuleCheck.IsLegacy"/>. The engine, the validation, the
    /// texts and the rule dialog read checks only through this.
    /// </summary>
    public IReadOnlyList<RuleCheck> AllChecks()
    {
        var checks = new List<RuleCheck>((Checks?.Count ?? 0) + (Data?.Count ?? 0));
        foreach (var check in Checks ?? [])
        {
            if (check is not null)
            {
                checks.Add(check);
            }
        }

        foreach (var (field, value) in Data ?? [])
        {
            checks.Add(new RuleCheck { Field = field, Op = RuleCheckOperator.Eq, Value = value, IsLegacy = true });
        }

        return checks;
    }

    /// <summary>JSON nulls ("data": null, "checks": null, a null check) read as empty.</summary>
    internal void Normalize()
    {
        Devices ??= new DeviceSelector();
        Devices.DeviceIds ??= [];
        Devices.Tags ??= [];
        Data ??= [];
        foreach (var key in Data.Where(d => d.Value is null).Select(d => d.Key).ToList())
        {
            Data[key] = string.Empty;
        }

        Checks = [.. (Checks ?? []).Where(c => c is not null)];
    }
}

/// <summary>
/// One action of a rule: the action plugin, its targets (devices for actions that act on devices) and its settings
/// (JSON object; password fields are never sent to clients, see <see cref="StoredSecretFields"/>).
/// </summary>
public sealed class RuleAction
{
    /// <summary>Stable id of the action within its rule (new actions: a new Guid): stored password values follow it.</summary>
    public Guid Key { get; set; } = Guid.NewGuid();

    /// <summary>The id of the action.</summary>
    public string ActionId { get; set; } = string.Empty;

    /// <summary>The devices it runs on.</summary>
    public DeviceSelector Targets { get; set; } = new() { Mode = DeviceSelectorMode.EventDevice };

    /// <summary>The action's settings by field key.</summary>
    public JsonObject Settings { get; set; } = [];

    /// <summary>
    /// Read: password fields with a value stored on the server. Write: leave such a field empty to keep the stored
    /// value; a field missing from this list and empty clears it.
    /// </summary>
    public List<string> StoredSecretFields { get; set; } = [];

    /// <summary>A copy.</summary>
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
    /// <summary>Longest rule name.</summary>
    public const int MaxNameLength = 64;
    /// <summary>The refusal of a name with a line break, a tab or another control character (rule and recipient names).</summary>
    public const string NameInvalidText = "Use letters, digits and punctuation only.";
    /// <summary>Most conditions per rule.</summary>
    public const int MaxConditions = 10;
    /// <summary>Most actions per rule.</summary>
    public const int MaxActions = 5;
    /// <summary>Default dead time per device.</summary>
    public const int DefaultCooldownSeconds = 10;
    /// <summary>Default limit of action runs per minute.</summary>
    public const int DefaultMaxRunsPerMinute = 60;

    /// <summary>The rule id; empty for a new rule.</summary>
    public Guid Id { get; set; }

    /// <summary>The rule name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Turned on.</summary>
    public bool Enabled { get; set; }

    /// <summary>The conditions; the first is the trigger.</summary>
    public List<RuleCondition> Conditions { get; set; } = [];

    /// <summary>The actions, run in order.</summary>
    public List<RuleAction> Actions { get; set; } = [];

    /// <summary>Per device: at least this long between two "turns on" (0..3600).</summary>
    public int CooldownSeconds { get; set; } = DefaultCooldownSeconds;

    /// <summary>For the whole rule: at most this many action runs per minute (1..1000).</summary>
    public int MaxRunsPerMinute { get; set; } = DefaultMaxRunsPerMinute;

    /// <summary>A copy.</summary>
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

    /// <summary>The JSON format of rules (camelCase).</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The rule as JSON.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>
    /// Throws <see cref="JsonException"/> for text that is not a rule. JSON nulls of lists and objects read as empty (a null
    /// condition or action is left out); older equals filters stay in <see cref="RuleCondition.Data"/>.
    /// </summary>
    public static RuleDefinition FromJson(string json)
    {
        var rule = JsonSerializer.Deserialize<RuleDefinition>(json, Json) ?? throw new JsonException("Not a rule.");
        rule.Name ??= string.Empty;
        rule.Conditions = [.. (rule.Conditions ?? []).Where(c => c is not null)];
        foreach (var condition in rule.Conditions)
        {
            condition.Normalize();
        }

        rule.Actions = [.. (rule.Actions ?? []).Where(a => a is not null)];
        foreach (var action in rule.Actions)
        {
            action.ActionId ??= string.Empty;
            action.Targets ??= new DeviceSelector { Mode = DeviceSelectorMode.EventDevice };
            action.Targets.DeviceIds ??= [];
            action.Targets.Tags ??= [];
            action.Settings ??= [];
            action.StoredSecretFields ??= [];
        }

        return rule;
    }
}
