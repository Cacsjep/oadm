using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Oadm.Plugins.DeviceConfig;

/// <summary>
/// The input of Configure devices for AI agents (SDK 0.13.0, <c>ITaskPlugin.PayloadType</c>): the dialog's choices without a
/// password. It is read as a <see cref="ConfigurePayload"/> (<see cref="ConfigurePayload.Check"/>), which moves
/// <see cref="Settings"/> and <see cref="ActionRules"/> into the dialog's fields and refuses secret values and device addresses.
/// </summary>
[Description("Sets parameters, action rules and stream profiles on every device of the run, like Configure devices in OADM.")]
public sealed record ConfigureMcpPayload(
    [property: Description("The parameters to set, each by its exact name from the settings read, with its value. Every device gets the same value; a device that does not have a parameter or refuses its value skips it."),
        MaxLength(ConfigureIds.MaxParameters)] IReadOnlyList<ConfigureMcpSetting>? Settings = null,
    [property: Description("Action rules to create. A rule of the same name on a device is replaced."),
        MaxLength(ConfigureIds.MaxRules)] IReadOnlyList<ConfigActionRule>? ActionRules = null,
    [property: Description("The own schedules the action rules use, with the ids their event filters name."),
        MaxLength(ConfigureIds.MaxRules)] IReadOnlyList<ConfigSchedule>? Schedules = null,
    [property: Description("Stream profiles to create. A profile of the same name on a device is replaced."),
        MaxLength(ConfigureIds.MaxStreamProfiles)] IReadOnlyList<ConfigStreamProfile>? StreamProfiles = null);

/// <summary>One parameter of <see cref="ConfigureMcpPayload"/>.</summary>
public sealed record ConfigureMcpSetting(
    [property: Description("The parameter's full name, like ImageSource.I0.Sensor.CaptureFrequency."), MinLength(1), MaxLength(ConfigureMcpSetting.MaxNameLength)] string Name,
    [property: Description("The value as the device writes it, like 50Hz or yes."), MaxLength(ParamRules.MaxTextLength)] string Value)
{
    /// <summary>Longest parameter name the tools take.</summary>
    public const int MaxNameLength = 255;
}

/// <summary>
/// The input of Create configuration file for AI agents: which settings of each device go into its ADM configuration file. The
/// file of each device is the task's result file (a download link for the agent, a file card in the assistant).
/// Passwords and other secret values are never written into it.
/// </summary>
[Description("Saves chosen settings of each device as an ADM configuration file, the task's result.")]
public sealed record CreateConfigFileMcpPayload(
    [property: Description("Parameters to save, by their exact names; a name ending in a dot takes the whole group, like ImageSource.I0."),
        MaxLength(ConfigureIds.MaxParameters)] IReadOnlyList<string>? Parameters = null,
    [property: Description("Action rules to save, by their names on the device. With a rule, every schedule of the device goes into the file."),
        MaxLength(ConfigureIds.MaxRules)] IReadOnlyList<string>? ActionRules = null,
    [property: Description("Stream profiles to save, by their names on the device."),
        MaxLength(ConfigureIds.MaxStreamProfiles)] IReadOnlyList<string>? StreamProfiles = null);

/// <summary>
/// The input of Export configuration for AI agents: no settings. Each device's export is the task's result file (a download
/// link for the agent, a file card in the assistant).
/// </summary>
[Description("Exports the configuration of each device, the task's result.")]
public sealed record ExportConfigMcpPayload;

/// <summary>
/// The input of Import configuration for AI agents: one uploaded configuration file (<c>.json</c> of Export configuration or the
/// camera's web interface) for every device (any model: each takes the settings it has), and the settings areas to import. It is read as an
/// <see cref="ImportPayload"/> without <see cref="ImportPayload.Files"/>: every device of the run gets the file.
/// </summary>
[Description("Imports one configuration file into every device, like Import configuration in OADM; a device of another model takes only the settings it has.")]
public sealed record ImportMcpPayload(
    [property: Description("The file id the upload answered."), MinLength(1), MaxLength(64)] string FileId,
    [property: Description("The file name as uploaded, like lobby.json."), MinLength(1), MaxLength(255)] string FileName,
    [property: Description("The settings areas to import. Connection settings (HTTPS, ports, login method, IEEE 802.1X, IPv6, DNS, network link, web interface) can make the devices unreachable."),
        MinLength(1), MaxLength(8)] IReadOnlyList<ImportArea> Sections);

/// <summary>The settings areas of a configuration file OADM imports (<see cref="ConfigSections.Known"/> and the connection settings).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ImportArea>))]
public enum ImportArea
{
    [JsonStringEnumMemberName(ConfigSections.Param)]
    NetworkAndSystem,

    [JsonStringEnumMemberName(ConfigSections.ActionRules)]
    EventRules,

    [JsonStringEnumMemberName("event-schedules.v2")]
    Schedules,

    [JsonStringEnumMemberName("event-mqtt-bridge.v2")]
    MqttEvents,

    [JsonStringEnumMemberName("time.v2")]
    TimeZone,

    [JsonStringEnumMemberName(ConfigSections.PowerSettings)]
    PowerSettings,

    [JsonStringEnumMemberName(ConfigSections.UserManagement)]
    PasswordPolicy,

    [JsonStringEnumMemberName(ConfigSections.Connection)]
    Connection,
}
