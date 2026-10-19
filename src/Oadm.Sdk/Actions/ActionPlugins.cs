using System.Text.Json;

using Microsoft.Extensions.Logging;

using Oadm.Sdk.Devices;
using Oadm.Sdk.Licensing;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

namespace Oadm.Sdk.Actions;

/// <summary>Why an action runs: a stateless rule fired, or a stateful rule turned on or off.</summary>
public enum ActionEdge
{
    /// <summary>A one-shot rule fired.</summary>
    Fired = 0,
    /// <summary>A rule that turns on and off turned on.</summary>
    TurnedOn = 1,
    /// <summary>A rule that turns on and off turned off.</summary>
    TurnedOff = 2,
}

/// <summary>Input kinds of a declarative action setting; the client draws the form from them.</summary>
public enum ActionFieldType
{
    /// <summary>One line of text. Placeholders such as {device} are filled by <see cref="IActionContext.Text(string?)"/>.</summary>
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

    /// <summary>
    /// A body with a format (JSON, XML, HTML, text), drawn as a code editor colored by <see cref="ActionField.Language"/>
    /// or by the value of the field <see cref="ActionField.LanguageKey"/> ("json", "xml", "html", "text", "form").
    /// </summary>
    Code = 7,

    /// <summary>Rows of name and value (HTTP headers, query parameters); stored as a JSON array of {"name", "value"}.</summary>
    KeyValues = 8,

    /// <summary>
    /// A file the user chooses (a configuration file of a compliance check); stored as {"name", "data"} with the content in
    /// base64, at most <see cref="ActionField.MaxLength"/> bytes. Read it with <see cref="ActionFile.From"/>. Compliance checks
    /// only; the rule dialog does not offer it.
    /// </summary>
    File = 9,
}

/// <summary>The value of a <see cref="ActionFieldType.File"/> setting.</summary>
/// <param name="Name">The file name as chosen ("base-config.cfg").</param>
/// <param name="Data">The content.</param>
public sealed record ActionFile(string Name, byte[] Data)
{
    /// <summary>The file of a setting value {"name", "data"}; null when it is not one or the content is not base64.</summary>
    public static ActionFile? From(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object
            || !value.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var name = value.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? string.Empty : string.Empty;
        try
        {
            return new ActionFile(name, Convert.FromBase64String(data.GetString() ?? string.Empty));
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

/// <summary>A choice of a <see cref="ActionFieldType.Choice"/> or <see cref="ActionFieldType.DynamicChoice"/> field.</summary>
/// <param name="Value">The value stored in the settings.</param>
/// <param name="Label">What the select shows.</param>
public sealed record ActionChoice(string Value, string Label);

/// <summary>
/// One setting of an action. <see cref="Key"/> is the JSON property in the settings; <see cref="Label"/> the form label.
/// <see cref="OffEdge"/> marks a setting that belongs to "When the rule turns off" (shown only for stateful rules).
/// </summary>
/// <param name="Key">The JSON property of the setting.</param>
/// <param name="Label">The form label.</param>
/// <param name="Type">The kind of input.</param>
public sealed record ActionField(string Key, string Label, ActionFieldType Type)
{
    /// <summary>The rule cannot be saved without a value.</summary>
    public bool Required { get; init; }

    /// <summary>Value of a new action (string, number or bool as JSON text, e.g. "5" or "true"); null = empty.</summary>
    public string? Default { get; init; }

    /// <summary>Help text below the input.</summary>
    public string? Hint { get; init; }

    /// <summary>The choices of a <see cref="ActionFieldType.Choice"/> field.</summary>
    public IReadOnlyList<ActionChoice> Choices { get; init; } = [];

    /// <summary>The smallest value of a <see cref="ActionFieldType.Number"/> field.</summary>
    public int Min { get; init; }

    /// <summary>The largest value of a <see cref="ActionFieldType.Number"/> field.</summary>
    public int Max { get; init; } = int.MaxValue;

    /// <summary>Longest text (Text, Multiline, Password).</summary>
    public int MaxLength { get; init; } = 1000;

    /// <summary>The setting of the "When the rule turns off" part.</summary>
    public bool OffEdge { get; init; }

    /// <summary>Shown only while the Choice / Bool field <see cref="VisibleWhenKey"/> has the value <see cref="VisibleWhenValue"/>.</summary>
    public string? VisibleWhenKey { get; init; }

    /// <summary>One value, or several separated by "|" ("basic|digest").</summary>
    public string? VisibleWhenValue { get; init; }

    /// <summary>Fixed language of a <see cref="ActionFieldType.Code"/> field: "json", "xml", "html", "text".</summary>
    public string? Language { get; init; }

    /// <summary>The field whose value is the language of this <see cref="ActionFieldType.Code"/> field.</summary>
    public string? LanguageKey { get; init; }

    /// <summary>
    /// A <see cref="ActionFieldType.DynamicChoice"/> whose choices are read from one device the user picks ("Read from
    /// device"), e.g. the applications installed on a camera. Compliance checks only.
    /// </summary>
    public bool ChoicesFromDevice { get; init; }

    /// <summary>File name endings a <see cref="ActionFieldType.File"/> field takes (".cfg"); empty = any.</summary>
    public IReadOnlyList<string> Accept { get; init; } = [];

    /// <summary>One line of text.</summary>
    public static ActionField Text(string key, string label, bool required = false, string? defaultValue = null) =>
        new(key, label, ActionFieldType.Text) { Required = required, Default = defaultValue };

    /// <summary>Several lines of text.</summary>
    public static ActionField Multiline(string key, string label, bool required = false) =>
        new(key, label, ActionFieldType.Multiline) { Required = required, MaxLength = 10_000 };

    /// <summary>A whole number from <paramref name="min"/> to <paramref name="max"/>.</summary>
    public static ActionField Number(string key, string label, int min, int max, int defaultValue) =>
        new(key, label, ActionFieldType.Number) { Required = true, Min = min, Max = max, Default = defaultValue.ToString(System.Globalization.CultureInfo.InvariantCulture) };

    /// <summary>A check box.</summary>
    public static ActionField Bool(string key, string label, bool defaultValue = false) =>
        new(key, label, ActionFieldType.Bool) { Default = defaultValue ? "true" : "false" };

    /// <summary>A select with fixed choices.</summary>
    public static ActionField Choice(string key, string label, IReadOnlyList<ActionChoice> choices, string? defaultValue = null) =>
        new(key, label, ActionFieldType.Choice) { Required = true, Choices = choices, Default = defaultValue ?? (choices.Count > 0 ? choices[0].Value : null) };

    /// <summary>A select whose choices come from <see cref="IActionPlugin.ListChoicesAsync"/> while the rule is edited.</summary>
    public static ActionField DynamicChoice(string key, string label, bool required = true) =>
        new(key, label, ActionFieldType.DynamicChoice) { Required = required };

    /// <summary>A password: stored encrypted and never sent back to a client.</summary>
    public static ActionField Password(string key, string label, bool required = false) =>
        new(key, label, ActionFieldType.Password) { Required = required, MaxLength = 256 };

    /// <summary>A body in the code editor; <paramref name="languageKey"/> names the field whose value is its language.</summary>
    public static ActionField Code(string key, string label, string? languageKey = null, bool required = false) =>
        new(key, label, ActionFieldType.Code) { Required = required, MaxLength = 50_000, LanguageKey = languageKey };

    /// <summary>A file of at most <paramref name="maxBytes"/> bytes with one of the <paramref name="accept"/> endings.</summary>
    public static ActionField File(string key, string label, IReadOnlyList<string> accept, int maxBytes, bool required = true) =>
        new(key, label, ActionFieldType.File) { Required = required, Accept = accept, MaxLength = maxBytes };

    /// <summary>Name and value rows (headers, query parameters); read them with <see cref="ActionInvocation.GetPairs"/>.</summary>
    public static ActionField KeyValues(string key, string label) =>
        new(key, label, ActionFieldType.KeyValues) { MaxLength = 20_000 };
}

/// <summary>What happened, handed to an action run.</summary>
/// <param name="EventTypeId">The event type.</param>
/// <param name="EventName">Its name ("Input 1 active").</param>
/// <param name="DeviceId">The device of the event; null for server events.</param>
/// <param name="DeviceAddress">Its address.</param>
/// <param name="DeviceModel">Its model.</param>
/// <param name="State">On or off for events that turn on and off; null for one-shot events.</param>
/// <param name="TimeUtc">When it happened.</param>
/// <param name="Data">The event's values by name; also placeholders ({port}).</param>
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
/// <param name="RuleId">The rule.</param>
/// <param name="RuleName">Its name.</param>
/// <param name="Edge">Why the action runs.</param>
/// <param name="Settings">The settings of the action, passwords decrypted.</param>
/// <param name="Device">The target device; null for actions without devices.</param>
/// <param name="Trigger">What happened.</param>
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

    /// <summary>A check box setting.</summary>
    public bool GetBool(string key) => string.Equals(Get(key), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>A number setting; <paramref name="fallback"/> when missing or not a number.</summary>
    public int GetInt(string key, int fallback) =>
        int.TryParse(Get(key), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : fallback;

    /// <summary>The rows of a <see cref="ActionFieldType.KeyValues"/> setting, in order; rows without a name are left out.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> GetPairs(string key) =>
        Settings.ValueKind == JsonValueKind.Object && Settings.TryGetProperty(key, out var value) ? ActionSettings.Pairs(value) : [];
}

/// <summary>Server services for one action run.</summary>
public interface IActionContext
{
    /// <summary>Pre-authenticated for <see cref="ActionInvocation.Device"/> with the stored credentials; null without a device.</summary>
    IVapixClient? Vapix { get; }

    /// <summary>The server log. For the event log, use <see cref="Log"/>.</summary>
    ILogger Logger { get; }

    /// <summary>The core plugin that contributed the action (VAPIX Commander for "Run VAPIX command"), else null.</summary>
    ICorePlugin? Owner { get; }

    /// <summary>The settings of the plugin package (e.g. an SMTP server), namespaced like a core plugin's.</summary>
    IPluginSettings Settings { get; }

    /// <summary>Extra line for the event log entry of this run. Never log secrets.</summary>
    void Log(string message);

    /// <summary>
    /// Fills the placeholders of <see cref="ActionPlaceholders.Fixed"/> ({rule}, {event}, {state}, {time}, {device},
    /// {serial}, {mac}, ...) in a text of the settings, then every value of the event by its name ({port}, {total};
    /// <see cref="ActionTrigger.Data"/>). The fixed names win over event values of the same name; unknown names stay.
    /// Device values come from the action's device, else from the device of the event.
    /// </summary>
    string Text(string? text);

    /// <summary>
    /// <see cref="Text(string?)"/> for a text with a format: each value is escaped (<see cref="PlaceholderEscape"/>), so
    /// "objectId": "{objectId}" stays valid JSON whatever the value holds. The text around the placeholders is unchanged.
    /// </summary>
    string Text(string? text, PlaceholderEscape escape) => escape == PlaceholderEscape.None ? Text(text) : Text(text);

    /// <summary>The license of the action's plugin package (plugin.json <c>license</c>); null on hosts without licenses.</summary>
    IPluginLicense? License => null;
}

/// <summary>Services for <see cref="IActionPlugin.ListChoicesAsync"/> (read-only, while a rule is edited).</summary>
public interface IActionQueryContext
{
    /// <summary>The server log.</summary>
    ILogger Logger { get; }

    /// <summary>The core plugin that contributed the action; null for a standalone action.</summary>
    ICorePlugin? Owner { get; }

    /// <summary>The settings of the plugin package (or of the owning core plugin).</summary>
    IPluginSettings Settings { get; }

    /// <summary>Every managed device.</summary>
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

    /// <summary>
    /// True: the action's texts are written for placeholders ({device}, {event}, the event's values), so the rule dialog
    /// shows the Placeholders list next to it (email, webhook). False hides the list for this action.
    /// </summary>
    bool UsesPlaceholders => false;

    /// <summary>The settings form, drawn by the rule dialog and checked on the server.</summary>
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
    /// <summary>The actions this core plugin contributes; available while it is on.</summary>
    IReadOnlyList<IActionPlugin> ActionPlugins { get; }
}
