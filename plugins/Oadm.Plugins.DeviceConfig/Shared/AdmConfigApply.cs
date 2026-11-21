using Oadm.Sdk.Tasks;

namespace Oadm.Plugins.DeviceConfig;

/// <summary>The areas of a configuration file (.cfg) that Configure devices applies.</summary>
public sealed record AdmConfigAreas(bool Parameters, bool ActionRules, bool StreamProfiles)
{
    public static AdmConfigAreas All { get; } = new(true, true, true);
}

/// <summary>
/// What Configure devices applies of a configuration file to every camera: the payload, the parameters of the file it never
/// writes (<see cref="LeftOutParameters"/>) and the payload's problems (<see cref="ConfigurePayload.Check(string?)"/>; empty
/// when it can run).
/// </summary>
public sealed record AdmConfigPlan(ConfigurePayload Payload, int LeftOutParameters, IReadOnlyList<PayloadError> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// The rules of Configure devices for the settings of a configuration file, shared by its dialog and by Provisioning's
/// Configuration node: which parameters are offered, the values of a file, the own schedules a rule needs, and the payload
/// for "all settings of the file" on devices OADM has not read (each device checks the values against its own parameters
/// when the task runs).
/// </summary>
public static class AdmConfigApply
{
    /// <summary>
    /// A parameter Configure devices offers: never the device's address (<see cref="DangerousParameters.IsAddress"/>), the
    /// dangerous ones only with the right, the per-device addresses and names only for one device.
    /// </summary>
    public static bool IsOffered(string name, bool allowDangerous, bool severalDevices)
    {
        ArgumentNullException.ThrowIfNull(name);
        return (allowDangerous || !DangerousParameters.IsDangerous(name))
            && !DangerousParameters.IsAddress(name)
            && (!severalDevices || !DangerousParameters.IsPerDevice(name));
    }

    /// <summary>The file's parameter values by name; a name listed twice keeps its last value, like ADM.</summary>
    public static IReadOnlyDictionary<string, string> Values(AdmConfig file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var parameter in file.Parameters)
        {
            values[parameter.Name] = parameter.Value;
        }

        return values;
    }

    /// <summary>The own schedules the rules use, in the order of <paramref name="schedules"/> (built-in ones every device has).</summary>
    public static IReadOnlyList<ConfigSchedule> UsedBy(IEnumerable<ConfigActionRule> rules, IEnumerable<ConfigSchedule> schedules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(schedules);
        var ids = rules.SelectMany(r => r.ScheduleIds).ToHashSet(StringComparer.Ordinal);
        return [.. schedules.Where(s => ids.Contains(s.Id))];
    }

    /// <summary>
    /// The payload of Configure devices for every setting of the chosen areas of <paramref name="file"/>, as the dialog builds
    /// it for several devices of any model with nothing unchecked and without the dangerous right: no address, no per-device
    /// name, no identity or connection parameter, no empty password (the dialog asks for one); the action rules with the own
    /// schedules they use; the stream profiles.
    /// </summary>
    public static AdmConfigPlan ForEveryDevice(AdmConfig file, AdmConfigAreas areas, string? source)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(areas);
        List<NameValue> parameters = [];
        var leftOut = 0;
        if (areas.Parameters)
        {
            foreach (var (name, value) in Values(file))
            {
                if (IsOffered(name, allowDangerous: false, severalDevices: true) && !(value.Length == 0 && SecretNames.IsSecret(name)))
                {
                    parameters.Add(new NameValue(name, value));
                }
                else
                {
                    leftOut++;
                }
            }
        }

        IReadOnlyList<ConfigActionRule> rules = areas.ActionRules ? file.Rules : [];
        var payload = new ConfigurePayload
        {
            Parameters = parameters,
            Rules = rules,
            Schedules = UsedBy(rules, file.Schedules),
            StreamProfiles = areas.StreamProfiles ? file.StreamProfiles : [],
            Source = source,
        };
        var (_, errors) = ConfigurePayload.Check(payload.ToJson());
        return new AdmConfigPlan(payload, leftOut, errors);
    }
}
