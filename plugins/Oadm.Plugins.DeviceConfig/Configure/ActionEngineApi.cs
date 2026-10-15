using System.Xml.Linq;

using Oadm.Plugins.ActionEngine;
using Oadm.Sdk.Vapix;

using EngineValue = Oadm.Plugins.ActionEngine.NameValue;

namespace Oadm.Plugins.DeviceConfig.Configure;

/// <summary>
/// The device's action rules, their action configurations and its schedules in the models of Configure devices and ADM
/// files. The device access is the shared <see cref="SoapActionEngine"/> (the same code as the Action Rule Editor); this
/// class only maps its models. A rule on the device points to an action configuration by id; ADM copies the configuration
/// into the rule, and so does OADM.
/// </summary>
public static class ActionEngineApi
{
    public const string ServicePath = SoapActionEngine.ServicePath;

    /// <summary>The rules with their actions copied in. Rules whose configuration is missing are left out.</summary>
    public static Task<IReadOnlyList<ConfigActionRule>> ReadRulesAsync(IVapixClient vapix, CancellationToken ct) =>
        DeviceAsync<IReadOnlyList<ConfigActionRule>>(async () => [.. (await SoapActionEngine.ReadRulesAsync(vapix, ct).ConfigureAwait(false)).Select(ToConfig)]);

    public static Task<IReadOnlyList<ConfigSchedule>> ReadSchedulesAsync(IVapixClient vapix, CancellationToken ct) =>
        DeviceAsync<IReadOnlyList<ConfigSchedule>>(async () => [.. (await SoapActionEngine.ReadSchedulesAsync(vapix, ct).ConfigureAwait(false)).Select(ToConfig)]);

    /// <summary>The device's rules as (rule id, name, configuration id), for replacing a rule of the same name.</summary>
    public static Task<IReadOnlyList<(string RuleId, string Name, string? ConfigurationId)>> ListRuleIdsAsync(IVapixClient vapix, CancellationToken ct) =>
        DeviceAsync<IReadOnlyList<(string RuleId, string Name, string? ConfigurationId)>>(async () =>
            [.. (await SoapActionEngine.ListRulesAsync(vapix, ct).ConfigureAwait(false)).Select(r => (r.Id, r.Name, r.ConfigurationId))]);

    /// <summary>Creates a schedule; returns the id the device gave it.</summary>
    public static Task<string> AddScheduleAsync(IVapixClient vapix, ConfigSchedule schedule, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        return DeviceAsync(() => SoapActionEngine.AddScheduleAsync(vapix, new DeviceSchedule(string.Empty, schedule.Name, schedule.Definition), ct));
    }

    /// <summary>Creates an action configuration; returns its id.</summary>
    public static Task<string> AddConfigurationAsync(IVapixClient vapix, ConfigAction action, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(action);
        return DeviceAsync(() => SoapActionEngine.AddConfigurationAsync(vapix, ToDevice(action), action.Name, ct));
    }

    /// <summary>Creates a rule pointing to <paramref name="configurationId"/>; returns its id.</summary>
    public static Task<string> AddRuleAsync(IVapixClient vapix, ConfigActionRule rule, string configurationId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var device = new DeviceRule(
            string.Empty,
            rule.Name,
            rule.Enabled,
            rule.StartEvent is { } start ? ToDevice(start) : null,
            [.. rule.Conditions.Select(ToDevice)],
            rule.ActivationTimeout,
            ToDevice(rule.Action));
        return DeviceAsync(() => SoapActionEngine.AddRuleAsync(vapix, device, configurationId, ct));
    }

    public static Task RemoveRuleAsync(IVapixClient vapix, string ruleId, CancellationToken ct) =>
        DeviceAsync(() => SoapActionEngine.RemoveRuleAsync(vapix, ruleId, ct));

    public static Task RemoveConfigurationAsync(IVapixClient vapix, string configurationId, CancellationToken ct) =>
        DeviceAsync(() => SoapActionEngine.RemoveConfigurationAsync(vapix, configurationId, ct));

    /// <summary>Removes an own schedule (the hardware write test cleans up with it).</summary>
    public static Task RemoveScheduleAsync(IVapixClient vapix, string eventId, CancellationToken ct) =>
        DeviceAsync(() => SoapActionEngine.RemoveScheduleAsync(vapix, eventId, ct));

    /// <summary>The topic without the <c>//.</c> AXIS OS 10 appends to condition topics.</summary>
    public static string Topic(string topic) => SoapActionEngine.Topic(topic);

    public static IReadOnlyDictionary<string, ConfigAction> ParseConfigurations(XDocument doc) =>
        SoapActionEngine.ParseConfigurations(doc).ToDictionary(p => p.Key, p => ToConfig(p.Value), StringComparer.Ordinal);

    public static IReadOnlyList<ConfigActionRule> ParseRules(XDocument doc, IReadOnlyDictionary<string, ConfigAction> configurations)
    {
        ArgumentNullException.ThrowIfNull(configurations);
        var device = configurations.ToDictionary(p => p.Key, p => ToDevice(p.Value) with { ConfigurationId = p.Key }, StringComparer.Ordinal);
        return [.. SoapActionEngine.ParseRules(doc, device).Select(ToConfig)];
    }

    public static IReadOnlyList<ConfigSchedule> ParseSchedules(XDocument doc) => [.. SoapActionEngine.ParseSchedules(doc).Select(ToConfig)];

    private static ConfigActionRule ToConfig(DeviceRule rule) =>
        new(rule.Name, rule.Enabled, rule.StartEvent is { } start ? ToConfig(start) : null, [.. rule.Conditions.Select(ToConfig)], ToConfig(rule.Action))
        {
            ActivationTimeout = rule.Timeout,
        };

    private static ConfigAction ToConfig(RuleAction action) =>
        new(action.Name ?? string.Empty, action.Template, [.. action.Parameters.Select(p => new NameValue(p.Name, p.Value))])
        {
            Id = action.ConfigurationId,
        };

    private static ConfigEventFilter ToConfig(EventFilter filter) => new(filter.Topic, filter.Message);

    private static ConfigSchedule ToConfig(DeviceSchedule schedule) => new(schedule.Id, schedule.Name, schedule.Definition);

    private static RuleAction ToDevice(ConfigAction action) =>
        new(action.Template, [.. action.Parameters.Select(p => new EngineValue(p.Name, p.Value))]) { Name = action.Name };

    private static EventFilter ToDevice(ConfigEventFilter filter) => new(filter.Topic, filter.Message);

    /// <summary>A refusal of the shared engine becomes this plugin's <see cref="ConfigDeviceException"/>.</summary>
    private static async Task<T> DeviceAsync<T>(Func<Task<T>> call)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (ActionEngineException ex)
        {
            throw new ConfigDeviceException(ex.Message, ex);
        }
    }

    private static async Task DeviceAsync(Func<Task> call)
    {
        try
        {
            await call().ConfigureAwait(false);
        }
        catch (ActionEngineException ex)
        {
            throw new ConfigDeviceException(ex.Message, ex);
        }
    }
}
