using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Oadm.Sdk.Actions;
using Oadm.Sdk.Compliance;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.DeviceConfig.Configure;

/// <summary>
/// Compliance check "Matches configuration file": every parameter, action rule and stream profile of an ADM configuration
/// file (Create configuration file, ADM) is on the device as the file has it. Values the device does not have are skipped
/// (another model), per-device values (addresses, names) and passwords are never compared, extra rules and profiles on the
/// device are fine (like a merge). Read only: param.cgi lists, the action rules and the stream profiles.
/// </summary>
public sealed class ConfigMatchCheck : IComplianceCheckType
{
    public const string TypeId = "oadm.device-config.matches-file";

    /// <summary>How many differences the detail names.</summary>
    public const int MaxNamed = 20;

    private static readonly ConcurrentDictionary<string, AdmConfig> Parsed = new(StringComparer.Ordinal);

    public string Id => TypeId;

    public string DisplayName => "Matches configuration file";

    public string? IconKey => "settings";

    public string Group => "Configuration";

    public string? Description => "The device has the settings of a configuration file (parameters, action rules, stream profiles).";

    public IReadOnlyList<ActionField> Fields =>
    [
        ActionField.File("file", "Configuration file", ["." + AdmConfigFile.Extension], (int)AdmConfigFile.MaxFileBytes)
            with { Hint = "Made with Create configuration file or AXIS Device Manager." },
    ];

    public IReadOnlyDictionary<string, string> Validate(JsonElement settings)
    {
        var file = new ComplianceInvocation(Guid.Empty, string.Empty, settings).GetFile("file");
        if (file is null)
        {
            return new Dictionary<string, string> { ["file"] = "Choose a file." };
        }

        try
        {
            var config = Read(file);
            return Comparable(config).Count + config.Rules.Count + config.StreamProfiles.Count == 0
                ? new Dictionary<string, string> { ["file"] = "The file has no settings to compare." }
                : new Dictionary<string, string>();
        }
        catch (FormatException ex)
        {
            return new Dictionary<string, string> { ["file"] = ex.Message };
        }
    }

    public string Describe(JsonElement settings)
    {
        var file = new ComplianceInvocation(Guid.Empty, string.Empty, settings).GetFile("file");
        if (file is null)
        {
            return DisplayName;
        }

        try
        {
            var config = Read(file);
            var count = Comparable(config).Count + config.Rules.Count + config.StreamProfiles.Count;
            return string.Create(CultureInfo.InvariantCulture, $"Matches {file.Name} ({count:N0} {(count == 1 ? "setting" : "settings")})");
        }
        catch (FormatException)
        {
            return "Matches " + file.Name;
        }
    }

    public async Task<ComplianceResult> EvaluateAsync(IComplianceContext ctx, ComplianceInvocation invocation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(invocation);
        var file = invocation.GetFile("file") ?? throw new InvalidOperationException("The check has no configuration file.");
        var config = Read(file);
        var vapix = ctx.Vapix!;
        var differences = new List<string>();
        var compared = 0;
        var missing = 0;

        var parameters = Comparable(config);
        if (parameters.Count > 0)
        {
            var now = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var chunk in ConfigureDevicesTask.Chunks(parameters.Select(p => p.Name), ConfigureDevicesTask.MaxVerifyUrlCharacters))
            {
                foreach (var (key, value) in await vapix.ListParametersAsync(chunk, ct).ConfigureAwait(false))
                {
                    now[key] = value;
                }
            }

            foreach (var parameter in parameters)
            {
                if (!now.TryGetValue(parameter.Name, out var value))
                {
                    missing++;
                    continue;
                }

                compared++;
                if (!string.Equals(value.Trim(), parameter.Value.Trim(), StringComparison.Ordinal))
                {
                    differences.Add($"{parameter.Name}: {Show(value)} (file: {Show(parameter.Value)})");
                }
            }
        }

        if (config.Rules.Count > 0)
        {
            var rules = await ActionEngineApi.ReadRulesAsync(vapix, ct).ConfigureAwait(false);
            var deviceSchedules = config.Rules.Any(r => r.ScheduleIds.Any())
                ? (await ActionEngineApi.ReadSchedulesAsync(vapix, ct).ConfigureAwait(false)).ToDictionary(s => s.Id, s => s.Name, StringComparer.Ordinal)
                : [];
            var fileSchedules = config.Schedules.ToDictionary(s => s.Id, s => s.Name, StringComparer.Ordinal);
            foreach (var rule in config.Rules)
            {
                compared++;
                var onDevice = rules.Where(r => string.Equals(r.Name, rule.Name, StringComparison.Ordinal)).ToList();
                if (onDevice.Count == 0)
                {
                    differences.Add($"Action rule {rule.Name}: missing");
                }
                else if (!onDevice.Any(r => SameRule(rule, fileSchedules, r, deviceSchedules)))
                {
                    differences.Add($"Action rule {rule.Name}: other settings");
                }
            }
        }

        if (config.StreamProfiles.Count > 0)
        {
            var profiles = (await StreamProfileApi.ListAsync(vapix, ct).ConfigureAwait(false)).Profiles;
            foreach (var profile in config.StreamProfiles)
            {
                compared++;
                var onDevice = profiles.FirstOrDefault(p => string.Equals(p.Name, profile.Name, StringComparison.Ordinal));
                if (onDevice is null)
                {
                    differences.Add($"Stream profile {profile.Name}: missing");
                }
                else if (!SameProfile(profile, onDevice))
                {
                    differences.Add($"Stream profile {profile.Name}: other settings");
                }
            }
        }

        var skipped = missing == 0 ? string.Empty : string.Create(CultureInfo.InvariantCulture, $" ({missing:N0} not on this model)");
        if (differences.Count == 0)
        {
            return compared == 0
                ? ComplianceResult.NotApplicable("None of the file's settings are on this model")
                : ComplianceResult.Passed(compared == 1
                    ? "1 setting matches" + skipped
                    : string.Create(CultureInfo.InvariantCulture, $"{compared:N0} settings match{skipped}"));
        }

        var head = differences.Count == 1 ? "1 setting differs" : string.Create(CultureInfo.InvariantCulture, $"{differences.Count:N0} settings differ");
        var named = string.Join("\n", differences.Take(MaxNamed));
        var more = differences.Count > MaxNamed ? string.Create(CultureInfo.InvariantCulture, $"\n+{differences.Count - MaxNamed:N0} more") : string.Empty;
        return ComplianceResult.Failed(head + skipped + "\n" + named + more);
    }

    /// <summary>The parameters of the file that are compared: not per-device values (addresses, names) and not passwords.</summary>
    internal static List<NameValue> Comparable(AdmConfig config) =>
    [
        .. config.Parameters.Where(p => !DangerousParameters.IsAddress(p.Name) && !DangerousParameters.IsPerDevice(p.Name) && !IsSecretName(p.Name)),
    ];

    private static bool SameRule(ConfigActionRule file, Dictionary<string, string> fileSchedules, ConfigActionRule device, Dictionary<string, string> deviceSchedules)
    {
        if (file.Enabled != device.Enabled
            || !SameFilter(file.StartEvent, fileSchedules, device.StartEvent, deviceSchedules)
            || file.Conditions.Count != device.Conditions.Count
            || !string.Equals(file.Action.Template, device.Action.Template, StringComparison.Ordinal)
            || (file.ActivationTimeout is not null && !string.Equals(file.ActivationTimeout, device.ActivationTimeout, StringComparison.Ordinal)))
        {
            return false;
        }

        var deviceConditions = device.Conditions.Select(c => Key(c, deviceSchedules)).Order(StringComparer.Ordinal);
        if (!file.Conditions.Select(c => Key(c, fileSchedules)).Order(StringComparer.Ordinal).SequenceEqual(deviceConditions, StringComparer.Ordinal))
        {
            return false;
        }

        var values = device.Action.Parameters.ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
        return file.Action.Parameters
            .Where(p => !IsSecretName(p.Name))
            .All(p => values.TryGetValue(p.Name, out var value) && string.Equals(value, p.Value, StringComparison.Ordinal));
    }

    private static bool SameFilter(ConfigEventFilter? a, Dictionary<string, string> aSchedules, ConfigEventFilter? b, Dictionary<string, string> bSchedules) =>
        (a is null && b is null) || (a is not null && b is not null && Key(a, aSchedules) == Key(b, bSchedules));

    /// <summary>A filter as text with its schedules by name (each device gives a schedule its own id).</summary>
    private static string Key(ConfigEventFilter filter, Dictionary<string, string> schedules)
    {
        var message = filter.Message ?? string.Empty;
        foreach (var id in ScheduleRefs.Find(message).Where(schedules.ContainsKey))
        {
            message = message.Replace(id, "schedule:" + schedules[id], StringComparison.Ordinal);
        }

        return ActionEngineApi.Topic(filter.Topic) + "|" + message;
    }

    private static bool SameProfile(ConfigStreamProfile file, ConfigStreamProfile device) =>
        string.Equals(file.Description ?? string.Empty, device.Description ?? string.Empty, StringComparison.Ordinal)
        && Pairs(file.Parameters).SetEquals(Pairs(device.Parameters));

    private static HashSet<string> Pairs(string parameters) =>
        [.. parameters.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    private static bool IsSecretName(string name) =>
        name.Contains("pass", StringComparison.OrdinalIgnoreCase) || name.Contains("secret", StringComparison.OrdinalIgnoreCase)
        || name.Contains("community", StringComparison.OrdinalIgnoreCase);

    private static string Show(string value) => value.Length == 0 ? "empty" : value.Length > 60 ? value[..60] + "…" : value;

    /// <summary>The file parsed once per content (a check runs on many devices).</summary>
    private static AdmConfig Read(ActionFile file)
    {
        var key = Convert.ToHexString(SHA256.HashData(file.Data));
        if (Parsed.TryGetValue(key, out var config))
        {
            return config;
        }

        config = AdmConfigFile.Read(Encoding.UTF8.GetString(file.Data));
        if (Parsed.Count > 16)
        {
            Parsed.Clear();
        }

        Parsed[key] = config;
        return config;
    }
}
