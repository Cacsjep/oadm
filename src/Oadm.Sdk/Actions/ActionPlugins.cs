using System.Text.Json;

using Microsoft.Extensions.Logging;

using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

namespace Oadm.Sdk.Actions;

/// <summary>Why an action runs: a stateless rule fired, or a stateful rule turned on or off.</summary>
public enum ActionEdge
{
    Fired = 0,
    TurnedOn = 1,
    TurnedOff = 2,
}

/// <summary>Input kinds of a declarative action setting; the client draws the form from them.</summary>
public enum ActionFieldType
{
    /// <summary>One line of text. Placeholders such as {device} are filled by <see cref="IActionContext.Text"/>.</summary>
    Text = 0,

    /// <summary>Several lines of text (an email body).</summary>
    Multiline = 1,

    /// <summary>A whole number between <see cref="ActionField.Min"/> and <see cref="ActionField.Max"/>.</summary>
    Number = 2,

    /// <summary>A check box; the value is true or false.</summary>
    Bool = 3,

    /// <summary>One of <see cref="ActionField.Choices"/>.</summary>
    Choice = 4,

    /// <summary>
    /// One of a list the action plugin returns at edit time (<see cref="IActionPlugin.ListChoicesAsync"/>), e.g. the
    /// saved VAPIX Commander commands.
    /// </summary>
    DynamicChoice = 5,

    /// <summary>A secret: stored encrypted on the server, never sent back to a client, decrypted only for the run.</summary>
    Password = 6,
}

/// <summary>A choice of a <see cref="ActionFieldType.Choice"/> or <see cref="ActionFieldType.DynamicChoice"/> field.</summary>
public sealed record ActionChoice(string Value, string Label);

/// <summary>
/// One setting of an action. <see cref="Key"/> is the JSON property in the settings; <see cref="Label"/> the form label.
/// <see cref="OffEdge"/> marks a setting that belongs to "When the rule turns off" (shown only for stateful rules).
/// </summary>
public sealed record ActionField(string Key, string Label, ActionFieldType Type)
{
    public bool Required { get; init; }

    /// <summary>Value of a new action (string, number or bool as JSON text, e.g. "5" or "true"); null = empty.</summary>
    public string? Default { get; init; }

    public string? Hint { get; init; }

    public IReadOnlyList<ActionChoice> Choices { get; init; } = [];

    public int Min { get; init; }

    public int Max { get; init; } = int.MaxValue;

    /// <summary>Longest text (Text, Multiline, Password).</summary>
    public int MaxLength { get; init; } = 1000;

    /// <summary>The setting of the "When the rule turns off" part.</summary>
    public bool OffEdge { get; init; }

    /// <summary>Shown only while the Choice / Bool field <see cref="VisibleWhenKey"/> has the value <see cref="VisibleWhenValue"/>.</summary>
    public string? VisibleWhenKey { get; init; }

    public string? VisibleWhenValue { get; init; }

    public static ActionField Text(string key, string label, bool required = false, string? defaultValue = null) =>
        new(key, label, ActionFieldType.Text) { Required = required, Default = defaultValue };

    public static ActionField Multiline(string key, string label, bool required = false) =>
        new(key, label, ActionFieldType.Multiline) { Required = required, MaxLength = 10_000 };

    public static ActionField Number(string key, string label, int min, int max, int defaultValue) =>
        new(key, label, ActionFieldType.Number) { Required = true, Min = min, Max = max, Default = defaultValue.ToString(System.Globalization.CultureInfo.InvariantCulture) };

    public static ActionField Bool(string key, string label, bool defaultValue = false) =>
        new(key, label, ActionFieldType.Bool) { Default = defaultValue ? "true" : "false" };

    public static ActionField Choice(string key, string label, IReadOnlyList<ActionChoice> choices, string? defaultValue = null) =>
        new(key, label, ActionFieldType.Choice) { Required = true, Choices = choices, Default = defaultValue ?? (choices.Count > 0 ? choices[0].Value : null) };

    public static ActionField DynamicChoice(string key, string label, bool required = true) =>
        new(key, label, ActionFieldType.DynamicChoice) { Required = required };

    public static ActionField Password(string key, string label, bool required = false) =>
        new(key, label, ActionFieldType.Password) { Required = required, MaxLength = 256 };
}

/// <summary>What happened, handed to an action run.</summary>
public sealed record ActionTrigger(
    string EventTypeId,
    string EventName,
    Guid? DeviceId,
    string? DeviceAddress,
    string? DeviceModel,
    bool? State,
    DateTimeOffset TimeUtc,
    IReadOnlyDictionary<string, string> Data);

/// <summary>
/// One run of an action on one target (a device, or none for actions without <see cref="IActionPlugin.NeedsDevice"/>).
/// <paramref name="Settings"/> are the action's settings with password fields decrypted.
/// </summary>
public sealed record ActionInvocation(
    Guid RuleId,
    string RuleName,
    ActionEdge Edge,
    JsonElement Settings,
    IDeviceInfo? Device,
    ActionTrigger Trigger)
{
    /// <summary>A setting as text (null when missing or null).</summary>
    public string? Get(string key) =>
        Settings.ValueKind == JsonValueKind.Object && Settings.TryGetProperty(key, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                _ => value.GetRawText(),
            }
            : null;

    public bool GetBool(string key) => string.Equals(Get(key), "true", StringComparison.OrdinalIgnoreCase);

    public int GetInt(string key, int fallback) =>
        int.TryParse(Get(key), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : fallback;
}

/// <summary>Server services for one action run.</summary>
public interface IActionContext
{
    /// <summary>Pre-authenticated for <see cref="ActionInvocation.Device"/> with the stored credentials; null without a device.</summary>
    IVapixClient? Vapix { get; }

    ILogger Logger { get; }

    /// <summary>The core plugin that contributed the action (VAPIX Commander for "Run VAPIX command"), else null.</summary>
    ICorePlugin? Owner { get; }

    /// <summary>The settings of the plugin package (e.g. an SMTP server), namespaced like a core plugin's.</summary>
    IPluginSettings Settings { get; }

    /// <summary>Extra line for the event log entry of this run. Never log secrets.</summary>
    void Log(string message);

    /// <summary>
    /// Fills the placeholders {rule}, {event}, {device} (address), {address}, {model}, {serial}, {state} (on / off) and
    /// {time} (server local time) in a text of the settings, then every value of the event by its name ({port},
    /// {total}; <see cref="ActionTrigger.Data"/>). The fixed names win over event values of the same name.
    /// </summary>
    string Text(string? text);
}

/// <summary>Services for <see cref="IActionPlugin.ListChoicesAsync"/> (read-only, while a rule is edited).</summary>
public interface IActionQueryContext
{
    ILogger Logger { get; }

    ICorePlugin? Owner { get; }

    IPluginSettings Settings { get; }

    IDeviceRepository Devices { get; }
}

/// <summary>
/// An action of the event rules ("Set output", "Run VAPIX command", "Send email"). Registered by the loader like task
/// plugins: a public class with a parameterless constructor in a plugin's *.Server.dll, or contributed by a core plugin
/// (<see cref="IActionSource.ActionPlugins"/>). Settings are declared with <see cref="Fields"/>; the client draws the
/// form, so most actions need no client part.
/// </summary>
public interface IActionPlugin : IPlugin
{
    /// <summary>Group of the action picker, e.g. "Devices", "Notifications". Default "General".</summary>
    string Group => "General";

    /// <summary>One short sentence for the action picker (optional).</summary>
    string? Description => null;

    /// <summary>
    /// True: the action acts on devices; the rule picks the targets (the device of the event, chosen devices, tags, all)
    /// and the action runs once per target with a pre-authenticated <see cref="IActionContext.Vapix"/>.
    /// False: it runs once per rule edge without a device (email, webhook).
    /// </summary>
    bool NeedsDevice { get; }

    /// <summary>
    /// True: the action has a "When the rule turns off" part (fields with <see cref="ActionField.OffEdge"/>, or the
    /// action undoes itself, e.g. "Follow the rule"), so it also runs on <see cref="ActionEdge.TurnedOff"/>.
    /// </summary>
    bool SupportsTurnOff => false;

    IReadOnlyList<ActionField> Fields { get; }

    /// <summary>
    /// Checks the settings before a rule is saved; returns errors per field key ("" for the whole action). The host has
    /// already checked required fields, numbers and choices. Default: no further errors.
    /// </summary>
    IReadOnlyDictionary<string, string> Validate(JsonElement settings, bool statefulRule) => new Dictionary<string, string>();

    /// <summary>One line for the rule list: "Pulse output 1 for 5 s". Default: the display name.</summary>
    string Describe(JsonElement settings) => DisplayName;

    /// <summary>Whether this edge needs a run with these settings (e.g. no "off" command chosen). Default: Fired and TurnedOn always, TurnedOff when <see cref="SupportsTurnOff"/>.</summary>
    bool RunsOn(ActionEdge edge, JsonElement settings) => edge != ActionEdge.TurnedOff || SupportsTurnOff;

    /// <summary>
    /// How long one run may take with these settings when it needs longer than the host's default (30 s), e.g. a pulse the
    /// device only answers after it ended. Null: the default. The host never uses less than its default.
    /// </summary>
    TimeSpan? MaxRunTime(JsonElement settings) => null;

    /// <summary>The choices of a <see cref="ActionFieldType.DynamicChoice"/> field (read-only). Default: none.</summary>
    Task<IReadOnlyList<ActionChoice>> ListChoicesAsync(IActionQueryContext ctx, string fieldKey, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ActionChoice>>([]);

    /// <summary>
    /// Runs the action. Return normally for success; throw for a failure (the message goes into the event log). The
    /// host cancels <paramref name="ct"/> after the action timeout (30 s) or when the server stops.
    /// </summary>
    Task ExecuteAsync(IActionContext ctx, ActionInvocation invocation, CancellationToken ct);
}

/// <summary>A core plugin that contributes actions implements this next to <c>ICorePlugin</c>.</summary>
public interface IActionSource
{
    IReadOnlyList<IActionPlugin> ActionPlugins { get; }
}
