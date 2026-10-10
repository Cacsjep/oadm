using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Oadm.Plugins.DeviceTriggers;

/// <summary>Ids, limits and timings shared by the server part, the page, the fake mode and the tests.</summary>
public static class DeviceTriggersPluginInfo
{
    public const string PluginId = "oadm.device-triggers";
    public const string DisplayName = "Device triggers";
    public const string IconKey = "bell";

    /// <summary>Group of the trigger events in the rule dialog.</summary>
    public const string EventGroup = "Device triggers";

    /// <summary>The device's event stream over WebSocket; only devices that list it can be chosen.</summary>
    public const string ApiId = "event-streaming-over-websocket";

    public const string ApiVersion = "1.0";

    /// <summary>The event stream (the session token is added by the host).</summary>
    public const string StreamPath = "/vapix/ws-data-stream?sources=events";

    public const int MaxTriggers = 200;
    public const int MaxNameLength = 64;
    public const int MaxDevicesPerTrigger = 5_000;

    /// <summary>Second event of every trigger (user decision 2026-10-10): "&lt;Trigger&gt;: connection lost".</summary>
    public const string LostSuffix = ": connection lost";

    /// <summary>"Connection lost" turns on after the trigger received nothing usable from a device for this long.</summary>
    public static readonly TimeSpan LostDelay = TimeSpan.FromSeconds(30);

    /// <summary>The event list of a connected device is read again this often (an app stopped or removed).</summary>
    public static readonly TimeSpan RecheckInterval = TimeSpan.FromMinutes(5);

    /// <summary>WebSocket ping; no pong within the same time = connection lost.</summary>
    public static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(10);

    public const string NotSupportedReason = "Cannot send events to OADM (update AXIS OS)";

    public static string TypeId(Guid triggerId) => string.Create(CultureInfo.InvariantCulture, $"{PluginId}.{triggerId:N}");

    public static string LostTypeId(Guid triggerId) => TypeId(triggerId) + ".lost";
}

/// <summary>Page backend methods and the live event topic.</summary>
public static class DeviceTriggersMethods
{
    /// <summary>-> <see cref="ListReply"/>: every trigger with its counts.</summary>
    public const string List = "list";

    /// <summary><see cref="IdRequest"/> -> <see cref="GetReply"/>: one trigger with the state per device.</summary>
    public const string Get = "get";

    /// <summary><see cref="StartReadRequest"/> -> <see cref="ReadStatusReply"/>: reads the event lists of devices live (editor).</summary>
    public const string StartRead = "startRead";

    /// <summary><see cref="ReadStatusRequest"/> -> <see cref="ReadStatusReply"/>: progress, and the result once finished.</summary>
    public const string ReadStatus = "readStatus";

    /// <summary><see cref="SaveRequest"/> -> <see cref="SaveReply"/> (Admin).</summary>
    public const string Save = "save";

    /// <summary><see cref="IdRequest"/> (Admin).</summary>
    public const string Delete = "delete";

    /// <summary><see cref="SetEnabledRequest"/> (Admin).</summary>
    public const string SetEnabled = "setEnabled";

    /// <summary><see cref="IdRequest"/> -> <see cref="SaveReply"/> (Admin): "&lt;name&gt; (copy)", turned off.</summary>
    public const string Duplicate = "duplicate";

    /// <summary>Event: <see cref="ChangedEvent"/> when triggers or their device states changed (at most every 500 ms).</summary>
    public const string ChangedTopic = "changed";

    public static bool IsAdminMethod(string method) => method is Save or Delete or SetEnabled or Duplicate;
}

/// <summary>A value an event item can have, with the device's display name for it.</summary>
public sealed record EventValue(string Value, string? DisplayName);

/// <summary>A source or data item of an event ("port", "Port number", values 0..63).</summary>
public sealed record EventItem(string Name, string? DisplayName, IReadOnlyList<EventValue> Values)
{
    public string Label => string.IsNullOrWhiteSpace(DisplayName) ? Name : DisplayName;
}

/// <summary>
/// An event a device offers (from its event list). <see cref="Topic"/> as the stream names it
/// ("tns1:Device/tnsaxis:IO/VirtualInput"), <see cref="Name"/> the device's display path ("Device › Input ports ›
/// Virtual input"). Stateful events are on or off: <see cref="StateItem"/> is the data item holding the state.
/// </summary>
public sealed record DeviceEvent(string Topic, string Name, bool Stateful, string? StateItem, IReadOnlyList<EventItem> Sources, IReadOnlyList<EventItem> Data);

/// <summary>One source item narrowed to one value (missing items mean "Any").</summary>
public sealed record TriggerFilter(string Name, string Value);

/// <summary>A device trigger as stored and edited.</summary>
public sealed record TriggerDefinition
{
    public Guid Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public bool Enabled { get; init; } = true;

    public string Topic { get; init; } = string.Empty;

    /// <summary>The device's name of the event ("Device › Input ports › Virtual input").</summary>
    public string EventName { get; init; } = string.Empty;

    public bool Stateful { get; init; }

    public string? StateItem { get; init; }

    public IReadOnlyList<TriggerFilter> Filters { get; init; } = [];

    /// <summary>Display names of the narrowed values, by item name ("port" = "Port number: 3").</summary>
    public string FilterText { get; init; } = string.Empty;

    /// <summary>Names of the event's source and data items: the placeholders ({port}) rules can use.</summary>
    public IReadOnlyList<string> Values { get; init; } = [];

    public IReadOnlyList<Guid> DeviceIds { get; init; } = [];
}

/// <summary>A trigger in the list with its live counts.</summary>
public sealed record TriggerSummary(
    Guid Id,
    string Name,
    bool Enabled,
    string EventName,
    string FilterText,
    bool Stateful,
    int Devices,
    int On,
    int Lost,
    int Waiting,
    DateTimeOffset? LastEventUtc);

/// <summary>How OADM receives a trigger's events from one device.</summary>
public enum TriggerConnection
{
    /// <summary>The trigger is turned off.</summary>
    Off = 0,

    /// <summary>Connecting, or reading the device's event list.</summary>
    Connecting = 1,

    /// <summary>Events arrive.</summary>
    Connected = 2,

    /// <summary>A problem (see the problem text); "connection lost" turns on after <see cref="DeviceTriggersPluginInfo.LostDelay"/>.</summary>
    Problem = 3,

    /// <summary>The problem lasts longer than <see cref="DeviceTriggersPluginInfo.LostDelay"/>: "connection lost" is on.</summary>
    Lost = 4,
}

/// <summary>The state of a trigger on one device.</summary>
public sealed record TriggerDeviceState(
    Guid DeviceId,
    TriggerConnection Connection,
    string? Problem,
    bool IsOn,
    DateTimeOffset? LastEventUtc,
    string? LastEventText);

public sealed record ListReply(IReadOnlyList<TriggerSummary> Triggers);

public sealed record IdRequest(Guid Id);

public sealed record GetReply(TriggerDefinition? Trigger, IReadOnlyList<TriggerDeviceState> Devices);

public sealed record StartReadRequest(IReadOnlyList<Guid> DeviceIds);

public sealed record ReadStatusRequest(string JobId);

/// <summary>A device's read: the indexes of its events in <see cref="ReadStatusReply.Events"/>, or why it failed.</summary>
public sealed record DeviceReadResult(Guid DeviceId, string? Error, IReadOnlyList<int> Events);

/// <summary>
/// A live read of event lists. While it runs only the counts; once <see cref="Finished"/> every event any device offers
/// (once per topic, values of all devices merged) and per device its events or the error.
/// </summary>
public sealed record ReadStatusReply(string JobId, int Total, int Done, bool Finished, IReadOnlyList<DeviceEvent> Events, IReadOnlyList<DeviceReadResult> Devices);

public sealed record SaveRequest(TriggerDefinition Trigger);

/// <summary><see cref="Errors"/>: field problems (Name, Event, Devices); <see cref="Error"/>: anything else.</summary>
public sealed record SaveReply(TriggerDefinition? Trigger, string? Error, IReadOnlyDictionary<string, string>? Errors);

public sealed record SetEnabledRequest(Guid Id, bool Enabled);

public sealed record ChangedEvent(IReadOnlyList<Guid> TriggerIds);

/// <summary>Checks shared by the server and the editor (messages appear below the fields).</summary>
public static class TriggerValidation
{
    public const string NameField = "Name";
    public const string EventField = "Event";
    public const string DevicesField = "Devices";

    public static string? NameProblem(string? name, IEnumerable<string> otherNames)
    {
        var text = name?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return "Enter a name.";
        }

        if (text.Length > DeviceTriggersPluginInfo.MaxNameLength)
        {
            return $"At most {DeviceTriggersPluginInfo.MaxNameLength} characters.";
        }

        if (text.Any(char.IsControl))
        {
            return "Use letters, digits and punctuation only.";
        }

        return otherNames.Any(o => string.Equals(o.Trim(), text, StringComparison.OrdinalIgnoreCase)) ? "Another trigger has this name." : null;
    }

    public static string? DevicesProblem(int count) => count switch
    {
        0 => "Choose at least one device.",
        > DeviceTriggersPluginInfo.MaxDevicesPerTrigger => $"At most {DeviceTriggersPluginInfo.MaxDevicesPerTrigger:N0} devices.",
        _ => null,
    };

    public static IReadOnlyDictionary<string, string> Check(TriggerDefinition trigger, IEnumerable<string> otherNames)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        var errors = new Dictionary<string, string>();
        if (NameProblem(trigger.Name, otherNames) is { } name)
        {
            errors[NameField] = name;
        }

        if (string.IsNullOrWhiteSpace(trigger.Topic))
        {
            errors[EventField] = "Choose an event.";
        }

        if (DevicesProblem(trigger.DeviceIds.Distinct().Count()) is { } devices)
        {
            errors[DevicesField] = devices;
        }

        return errors;
    }
}

/// <summary>JSON of the page contract (camelCase, enums as text).</summary>
public static class TriggerJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Deserialize<T>(string? json) =>
        JsonSerializer.Deserialize<T>(string.IsNullOrWhiteSpace(json) ? "{}" : json, Options)
        ?? throw new ArgumentException("The request is empty.");
}
