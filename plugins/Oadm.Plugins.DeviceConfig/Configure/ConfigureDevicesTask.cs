using System.Globalization;

using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.DeviceConfig.Configure;

/// <summary>
/// "Configure devices" (Maintenance, dialog; ADM's Configure Devices > Configure): sets the chosen parameters, action rules and
/// stream profiles on every selected device, one task per device. Each device checks the values against its own parameter
/// definitions first; parameters it does not have are skipped (Done with warnings). Steps: Check compatibility, Set
/// parameters, Create schedules, Create action rules, Set stream profiles, Set connection parameters (the dangerous ones,
/// always last), Verify parameters.
/// </summary>
public sealed class ConfigureDevicesTask : ITaskPlugin, ITaskPluginQuery
{
    public static class Steps
    {
        public const string CheckCompatibility = "Check compatibility";
        public const string SetParameters = "Set parameters";
        public const string CreateSchedules = "Create schedules";
        public const string CreateRules = "Create action rules";
        public const string SetStreamProfiles = "Set stream profiles";
        public const string SetConnection = "Set connection parameters";
        public const string Verify = "Verify parameters";

        /// <summary>Why the source device skips its own rules, schedules and stream profiles.</summary>
        public const string IsSource = "This is the source device";
    }

    /// <summary>Most skipped or differing parameters a step names; the task log lists all.</summary>
    public const int MaxNamed = 5;

    public const string ParamApi = "param-cgi";

    public string Id => ConfigureIds.Configure;

    public string DisplayName => ConfigureIds.ConfigureName;

    public string? IconKey => "sliders";

    public string Group => TaskGroups.Maintenance;

    public bool ShowInToolbar => false;

    public bool RequiresDialog => true;

    public bool CanRun(IDeviceInfo device) => ConfigureSupport.CanRun(device);

    public string? NotSupportedReason(IDeviceInfo device) => ConfigureSupport.NotSupportedReason(device);

    public IReadOnlyList<PermissionDefinition> Permissions { get; } =
    [
        new(ConfigurePermissions.Configure, "Configure devices", "Set parameters, action rules and stream profiles on many devices."),
        new(ConfigurePermissions.Dangerous, "Configure dangerous parameters", "Also set addresses, host names, HTTPS, ports, the login method and IEEE 802.1X; devices can become unreachable.")
        {
            OperatorDefault = false,
            FullDeviceControl = true,
        },
    ];

    public IReadOnlyList<string>? RequiredPermissions(string? payloadJson) =>
        ConfigurePayload.Parse(payloadJson).Parameters.Any(p => DangerousParameters.IsDangerous(p.Name))
            ? [ConfigurePermissions.Configure, ConfigurePermissions.Dangerous]
            : [ConfigurePermissions.Configure];

    public string GetTaskName(string? payloadJson)
    {
        try
        {
            return ConfigurePayload.Parse(payloadJson).TaskName();
        }
        catch (ArgumentException)
        {
            return DisplayName;
        }
    }

    public async Task<string?> QueryAsync(ITaskQueryContext ctx, IDeviceInfo device, string method, string? payloadJson, CancellationToken ct) => method switch
    {
        ConfigureIds.ReadQuery => (await DeviceSettingsReader.ReadAsync(ctx, device, full: true, ct).ConfigureAwait(false)).ToJson(),
        ConfigureIds.DefinitionsQuery => (await DeviceSettingsReader.ReadAsync(ctx, device, full: false, ct).ConfigureAwait(false)).ToJson(),
        _ => throw new ArgumentException($"Unknown query '{method}'.", nameof(method)),
    };

    public async Task ExecuteAsync(ITaskExecutionContext ctx, IDeviceInfo device, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(device);
        var payload = ConfigurePayload.Parse(payloadJson);
        var normal = payload.Parameters.Where(p => !DangerousParameters.IsDangerous(p.Name)).ToList();
        var dangerous = payload.Parameters.Where(p => DangerousParameters.IsDangerous(p.Name)).ToList();

        var plan = new List<string> { Steps.CheckCompatibility };
        if (normal.Count > 0)
        {
            plan.Add(Steps.SetParameters);
        }

        var scheduleIds = payload.Rules.SelectMany(r => r.ScheduleIds).ToHashSet(StringComparer.Ordinal);
        var schedules = payload.Schedules.Where(s => scheduleIds.Contains(s.Id)).ToList();
        if (schedules.Count > 0)
        {
            plan.Add(Steps.CreateSchedules);
        }

        if (payload.Rules.Count > 0)
        {
            plan.Add(Steps.CreateRules);
        }

        if (payload.StreamProfiles.Count > 0)
        {
            plan.Add(Steps.SetStreamProfiles);
        }

        if (dangerous.Count > 0)
        {
            plan.Add(Steps.SetConnection);
        }

        if (payload.Parameters.Count > 0)
        {
            plan.Add(Steps.Verify);
        }

        ctx.PlanSteps([.. plan]);

        Dictionary<string, ParamDefinition> definitions;
        using (var step = ctx.BeginStep(Steps.CheckCompatibility))
        {
            if (TaskSupportReasons.ForStatus(device) is { } refused)
            {
                throw new ConfigDeviceException(refused + ". Nothing was changed.");
            }

            var apis = await ctx.Vapix.GetApiListAsync(ct).ConfigureAwait(false);
            if (apis.Count > 0 && !apis.Any(a => string.Equals(a.Id, ParamApi, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ConfigDeviceException("The device has no parameter API. Nothing was changed.");
            }

            var info = await ctx.Vapix.GetBasicDeviceInfoAsync(ct).ConfigureAwait(false);
            definitions = payload.Parameters.Count == 0
                ? []
                : (await ParamDefinitionsReader.ReadAsync(ctx.Vapix, ct, [.. payload.Parameters.Select(p => p.Name)]).ConfigureAwait(false))
                    .ToDictionary(d => d.Name, StringComparer.Ordinal);
            step.Complete($"{ModelNames.Display(info.ProdNbr)}, AXIS OS {info.Version}");
        }

        var secrets = Secrets(payload, definitions);
        var failures = new List<string>();
        var written = new List<NameValue>();
        if (normal.Count > 0)
        {
            await SetParametersAsync(ctx, Steps.SetParameters, normal, definitions, secrets, written, failures, ct).ConfigureAwait(false);
        }

        // The source device keeps its own rules, schedules and profiles: writing them back would only recreate them.
        var isSource = payload.SourceDeviceId == device.Id;
        if (isSource)
        {
            foreach (var name in new[] { Steps.CreateSchedules, Steps.CreateRules, Steps.SetStreamProfiles }.Where(plan.Contains))
            {
                ctx.SkipStep(name, Steps.IsSource);
            }
        }

        var newScheduleIds = new Dictionary<string, string>(StringComparer.Ordinal);
        if (schedules.Count > 0 && !isSource)
        {
            await Isolated(ctx, Steps.CreateSchedules, failures, secrets, async step =>
            {
                var existing = await ActionEngineApi.ReadSchedulesAsync(ctx.Vapix, ct).ConfigureAwait(false);
                int created = 0, reused = 0;
                foreach (var schedule in schedules)
                {
                    // The same schedule (name and recurrence) is used again: running twice never adds copies.
                    if (existing.FirstOrDefault(s => s.Name == schedule.Name && s.Definition == schedule.Definition) is { } same)
                    {
                        newScheduleIds[schedule.Id] = same.Id;
                        reused++;
                        continue;
                    }

                    newScheduleIds[schedule.Id] = await ActionEngineApi.AddScheduleAsync(ctx.Vapix, schedule, ct).ConfigureAwait(false);
                    created++;
                }

                step.Complete(Joined(created > 0 ? ConfigureIds.Count(created, "schedule") + " created" : null, reused > 0 ? reused + " already there" : null));
            }, ct).ConfigureAwait(false);
        }

        if (payload.Rules.Count > 0 && !isSource)
        {
            await Isolated(ctx, Steps.CreateRules, failures, secrets, async step =>
            {
                // Only rules that were on the device before this run are replaced (decision Q4).
                var existing = (await ActionEngineApi.ListRuleIdsAsync(ctx.Vapix, ct).ConfigureAwait(false)).ToList();
                int created = 0, replaced = 0;
                var skipped = new List<string>();
                var failed = new List<string>();
                foreach (var rule in payload.Rules)
                {
                    // A rule is never written with a schedule id this device does not have.
                    if (rule.ScheduleIds.FirstOrDefault(id => !newScheduleIds.ContainsKey(id)) is { } missing)
                    {
                        skipped.Add($"{rule.Name} (its schedule could not be created)");
                        continue;
                    }

                    try
                    {
                        var same = existing.Where(r => r.Name == rule.Name).ToList();
                        await ReplaceRuleAsync(ctx, rule, newScheduleIds, same, existing, ct).ConfigureAwait(false);
                        if (same.Count > 0)
                        {
                            replaced++;
                        }
                        else
                        {
                            created++;
                        }
                    }
                    catch (Exception ex) when (!DeviceErrors.IsCancelled(ex, ct) && !DeviceErrors.IsCertificateChanged(ex))
                    {
                        // One rule the device refuses (an action it does not have) does not stop the others.
                        var message = ParamWriter.Scrub(DeviceErrors.Describe(ex, timeout: null), secrets);
                        failed.Add($"{rule.Name} ({message.TrimEnd('.')})");
                        ctx.Log(TaskLogLevel.Error, $"Action rule {rule.Name}: {message}");
                    }
                }

                foreach (var item in skipped)
                {
                    ctx.Log(TaskLogLevel.Warning, "Skipped action rule " + item);
                }

                var done = Joined(created > 0 ? ConfigureIds.Count(created, "action rule") + " created" : null, replaced > 0 ? replaced + " replaced" : null);
                if (failed.Count > 0)
                {
                    throw new ConfigDeviceException(Joined(done, ConfigureIds.Count(failed.Count, "action rule") + " failed: " + Summary(failed)));
                }

                if (skipped.Count > 0)
                {
                    step.Warn(Joined(done, skipped.Count + " skipped: " + Summary(skipped)));
                }
                else
                {
                    step.Complete(done);
                }
            }, ct).ConfigureAwait(false);
        }

        if (payload.StreamProfiles.Count > 0 && !isSource)
        {
            await Isolated(ctx, Steps.SetStreamProfiles, failures, secrets, async step =>
            {
                var list = await StreamProfileApi.ListAsync(ctx.Vapix, ct).ConfigureAwait(false);
                var names = list.Profiles.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
                var added = payload.StreamProfiles.Count(p => !names.Contains(p.Name));
                if (list.MaxProfiles is { } max && names.Count + added > max)
                {
                    throw new ConfigDeviceException(string.Create(CultureInfo.InvariantCulture,
                        $"The device has room for {max} stream profiles; it has {names.Count} and {added} would be added. No stream profile was changed."));
                }

                int created = 0, updated = 0;
                foreach (var profile in payload.StreamProfiles)
                {
                    if (names.Contains(profile.Name))
                    {
                        await StreamProfileApi.UpdateAsync(ctx.Vapix, profile, ct).ConfigureAwait(false);
                        updated++;
                    }
                    else
                    {
                        await StreamProfileApi.CreateAsync(ctx.Vapix, profile, ct).ConfigureAwait(false);
                        created++;
                    }
                }

                step.Complete(Joined(created > 0 ? ConfigureIds.Count(created, "stream profile") + " created" : null, updated > 0 ? updated + " replaced" : null));
            }, ct).ConfigureAwait(false);
        }

        if (dangerous.Count > 0)
        {
            await SetParametersAsync(ctx, Steps.SetConnection, dangerous, definitions, secrets, written, failures, ct).ConfigureAwait(false);
        }

        if (payload.Parameters.Count > 0)
        {
            await VerifyAsync(ctx, written, definitions, dangerous.Count > 0, ct).ConfigureAwait(false);
        }

        if (failures.Count > 0)
        {
            throw new ConfigDeviceException(failures.Count == 1
                ? failures[0]
                : string.Create(CultureInfo.InvariantCulture, $"{failures[0]} (+{failures.Count - 1} more failed)"));
        }
    }

    /// <summary>
    /// Creates the rule (its action first), and only then removes the rules of the same name that were on the device before
    /// (and their actions when no other rule uses them): a rule the device refuses never leaves it without the old one. When
    /// the rule is refused, the action just created is removed again.
    /// </summary>
    private static async Task ReplaceRuleAsync(ITaskExecutionContext ctx, ConfigActionRule rule, IReadOnlyDictionary<string, string> newScheduleIds,
        List<(string RuleId, string Name, string? ConfigurationId)> same, List<(string RuleId, string Name, string? ConfigurationId)> existing, CancellationToken ct)
    {
        var configurationId = await ActionEngineApi.AddConfigurationAsync(ctx.Vapix, rule.Action, ct).ConfigureAwait(false);
        var pointed = rule with
        {
            StartEvent = rule.StartEvent is { } start ? start with { Message = ScheduleRefs.Rewrite(start.Message, newScheduleIds) } : null,
            Conditions = [.. rule.Conditions.Select(c => c with { Message = ScheduleRefs.Rewrite(c.Message, newScheduleIds) })],
        };
        string ruleId;
        try
        {
            ruleId = await ActionEngineApi.AddRuleAsync(ctx.Vapix, pointed, configurationId, ct).ConfigureAwait(false);
        }
        catch
        {
            await ActionEngineApi.RemoveConfigurationAsync(ctx.Vapix, configurationId, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        foreach (var old in same)
        {
            await ActionEngineApi.RemoveRuleAsync(ctx.Vapix, old.RuleId, ct).ConfigureAwait(false);
            existing.Remove(old);
            if (old.ConfigurationId is { } configuration && configuration != configurationId && !existing.Any(r => r.ConfigurationId == configuration))
            {
                await ActionEngineApi.RemoveConfigurationAsync(ctx.Vapix, configuration, ct).ConfigureAwait(false);
            }

            ctx.Log(TaskLogLevel.Info, $"Replaced the action rule {rule.Name}");
        }

        existing.Add((ruleId, rule.Name, configurationId));
    }

    /// <summary>Most retries when the device refuses single parameters of an update.</summary>
    public const int MaxRefusedRetries = 20;

    /// <summary>
    /// Checks each parameter against this device's definition and writes the ones that fit in one request; the others are
    /// named in the step (Done with warnings) and in the log. A parameter the device refuses is skipped by name and the rest
    /// sent again.
    /// </summary>
    private static Task SetParametersAsync(ITaskExecutionContext ctx, string name, List<NameValue> parameters, Dictionary<string, ParamDefinition> definitions,
        IReadOnlyCollection<string> secrets, List<NameValue> written, List<string> failures, CancellationToken ct) =>
        Isolated(ctx, name, failures, secrets, async step =>
        {
            var apply = new List<NameValue>();
            var skipped = new List<string>();
            foreach (var parameter in parameters)
            {
                if (DangerousParameters.IsAddress(parameter.Name))
                {
                    skipped.Add($"{parameter.Name} ({DangerousParameters.AddressNote.TrimEnd('.')})");
                }
                else if (!definitions.TryGetValue(parameter.Name, out var definition))
                {
                    skipped.Add(parameter.Name + " (not on this device)");
                }
                else if (ParamRules.Problem(definition, parameter.Value) is { } problem)
                {
                    skipped.Add($"{parameter.Name} ({problem.TrimEnd('.')})");
                }
                else
                {
                    // Checked trimmed, written trimmed (numbers, addresses, names, choices); text and passwords exactly as typed.
                    apply.Add(definition.Kind is ParamKind.Text or ParamKind.Password ? parameter : parameter with { Value = parameter.Value?.Trim() ?? string.Empty });
                }
            }

            foreach (var item in skipped)
            {
                ctx.Log(TaskLogLevel.Warning, "Skipped " + item);
            }

            if (apply.Count == 0)
            {
                step.Warn("Nothing set: " + Summary(skipped));
                return;
            }

            for (var retry = 0; ; retry++)
            {
                try
                {
                    await ParamWriter.UpdateAsync(ctx.Vapix, apply, secrets, ct).ConfigureAwait(false);
                    break;
                }
                catch (ParamRefusedException refused) when (refused.Parameter is { } bad && retry < MaxRefusedRetries && apply.Any(p => p.Name == bad))
                {
                    apply.RemoveAll(p => p.Name == bad);
                    skipped.Add($"{bad} (the device refused it)");
                    ctx.Log(TaskLogLevel.Warning, "Skipped " + bad + ": " + refused.Message);
                    if (apply.Count == 0)
                    {
                        step.Warn("Nothing set: " + Summary(skipped));
                        return;
                    }
                }
            }

            written.AddRange(apply);
            var done = ConfigureIds.Count(apply.Count, "parameter") + " set";
            if (skipped.Count > 0)
            {
                step.Warn($"{done}, {skipped.Count} skipped: {Summary(skipped)}");
            }
            else
            {
                step.Complete(done);
            }
        }, ct);

    /// <summary>Reads the written parameters back; differences are a warning (passwords are not read back).</summary>
    private static async Task VerifyAsync(ITaskExecutionContext ctx, List<NameValue> written, Dictionary<string, ParamDefinition> definitions, bool connectionChanged, CancellationToken ct)
    {
        var check = written.Where(p => definitions.GetValueOrDefault(p.Name)?.Kind != ParamKind.Password).ToList();
        if (check.Count == 0)
        {
            ctx.SkipStep(Steps.Verify, written.Count == 0 ? "Nothing was set" : "Passwords are not read back");
            return;
        }

        using var step = ctx.BeginStep(Steps.Verify);
        var now = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            // The names go into the URL: read in chunks that stay far below the web server's request line limit.
            foreach (var chunk in Chunks(check.Select(p => p.Name), MaxVerifyUrlCharacters))
            {
                foreach (var (key, value) in await ctx.Vapix.ListParametersAsync(chunk, ct).ConfigureAwait(false))
                {
                    now[key] = value;
                }
            }
        }
        catch (Exception ex) when (!DeviceErrors.IsCancelled(ex, ct))
        {
            // After new connection parameters the device may be gone for OADM: expected, a warning.
            var message = DeviceErrors.IsCertificateChanged(ex) ? DeviceMessages.CertificateChanged : DeviceErrors.Describe(ex, timeout: null);
            if (connectionChanged)
            {
                step.Warn("The device could not be read after the connection parameters changed: " + message);
                return;
            }

            throw new ConfigDeviceException(message, ex);
        }

        var differing = check.Where(p => !now.TryGetValue(p.Name, out var value) || !string.Equals(value, p.Value, StringComparison.Ordinal)).Select(p => p.Name).ToList();
        foreach (var name in differing)
        {
            ctx.Log(TaskLogLevel.Warning, "Differs after setting: " + name);
        }

        if (differing.Count == 0)
        {
            step.Complete("All set values match");
        }
        else
        {
            step.Warn($"{(differing.Count == 1 ? "1 parameter differs" : ConfigureIds.Count(differing.Count, "parameter") + " differ")}: {Summary(differing)}");
        }
    }

    /// <summary>Characters of parameter names per read-back request.</summary>
    public const int MaxVerifyUrlCharacters = 2000;

    /// <summary>The names in groups whose joined length stays below <paramref name="max"/>.</summary>
    internal static IEnumerable<List<string>> Chunks(IEnumerable<string> names, int max)
    {
        var chunk = new List<string>();
        var length = 0;
        foreach (var name in names)
        {
            if (chunk.Count > 0 && length + name.Length + 1 > max)
            {
                yield return chunk;
                chunk = [];
                length = 0;
            }

            chunk.Add(name);
            length += name.Length + 1;
        }

        if (chunk.Count > 0)
        {
            yield return chunk;
        }
    }

    /// <summary>
    /// Runs one step; a failure fails that step only (the others still run) and is collected, with every secret value masked.
    /// A changed certificate stops the task: every later request fails the same way.
    /// </summary>
    private static async Task Isolated(ITaskExecutionContext ctx, string name, List<string> failures, IReadOnlyCollection<string> secrets, Func<ITaskStep, Task> run, CancellationToken ct)
    {
        using var step = ctx.BeginStep(name);
        try
        {
            await run(step).ConfigureAwait(false);
        }
        catch (Exception ex) when (!DeviceErrors.IsCancelled(ex, ct))
        {
            if (DeviceErrors.IsCertificateChanged(ex))
            {
                throw new ConfigDeviceException(DeviceMessages.CertificateChanged, ex);
            }

            var message = ParamWriter.Scrub(DeviceErrors.Describe(ex, timeout: null), secrets);
            step.Fail(message);
            ctx.Log(TaskLogLevel.Error, $"{name}: {message}");
            failures.Add($"{name}: {message}");
        }
    }

    /// <summary>
    /// Password parameter values, values whose name says password, secret or community (SNMP community strings are plain
    /// text parameters) and such action values: never in a log or an error.
    /// </summary>
    private static IReadOnlyCollection<string> Secrets(ConfigurePayload payload, Dictionary<string, ParamDefinition> definitions) =>
    [
        .. payload.Parameters.Where(p => definitions.GetValueOrDefault(p.Name)?.Kind == ParamKind.Password || IsSecretName(p.Name)).Select(p => p.Value),
        .. payload.Rules.SelectMany(r => r.Action.Parameters).Where(p => IsSecretName(p.Name)).Select(p => p.Value),
    ];

    private static bool IsSecretName(string name) =>
        name.Contains("pass", StringComparison.OrdinalIgnoreCase) || name.Contains("secret", StringComparison.OrdinalIgnoreCase)
        || name.Contains("community", StringComparison.OrdinalIgnoreCase);

    private static string Joined(params string?[] parts) => string.Join(", ", parts.Where(p => !string.IsNullOrEmpty(p)));

    /// <summary>"a; b; c (+4 more)".</summary>
    private static string Summary(List<string> items) =>
        string.Join("; ", items.Take(MaxNamed)) + (items.Count > MaxNamed ? string.Create(CultureInfo.InvariantCulture, $" (+{items.Count - MaxNamed} more)") : string.Empty);
}

/// <summary>"Create configuration file" (Maintenance, one device): a read-only dialog that saves an ADM file, never a task.</summary>
public sealed class CreateConfigFileTask : ITaskPlugin, ITaskPluginQuery
{
    public string Id => ConfigureIds.Create;

    public string DisplayName => ConfigureIds.CreateName;

    public string? IconKey => "save";

    public string Group => TaskGroups.Maintenance;

    public bool ShowInToolbar => false;

    public bool RequiresDialog => true;

    public bool CanRun(IDeviceInfo device) => ConfigureSupport.CanRun(device);

    public string? NotSupportedReason(IDeviceInfo device) => ConfigureSupport.NotSupportedReason(device);

    public Task ExecuteAsync(ITaskExecutionContext ctx, IDeviceInfo device, string? payloadJson, CancellationToken ct) =>
        throw new InvalidOperationException("\"Create configuration file\" only reads; it never runs as a task.");

    public async Task<string?> QueryAsync(ITaskQueryContext ctx, IDeviceInfo device, string method, string? payloadJson, CancellationToken ct) =>
        method == ConfigureIds.ReadQuery
            ? (await DeviceSettingsReader.ReadAsync(ctx, device, full: true, ct).ConfigureAwait(false)).ToJson()
            : throw new ArgumentException($"Unknown query '{method}'.", nameof(method));
}

/// <summary>Which devices Configure devices runs on (cached data only): every device OADM is logged in to with the parameter API.</summary>
public static class ConfigureSupport
{
    public static bool CanRun(IDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return device.Status == DeviceStatus.Ok && (device.Apis.Count == 0 || device.Apis.Any(a => string.Equals(a.Id, ConfigureDevicesTask.ParamApi, StringComparison.OrdinalIgnoreCase)));
    }

    public static string? NotSupportedReason(IDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return TaskSupportReasons.ForStatus(device, requireOk: true)
            ?? (CanRun(device) ? null : "The device has no parameter API");
    }
}
