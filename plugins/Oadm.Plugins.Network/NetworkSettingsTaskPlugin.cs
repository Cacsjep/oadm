using Oadm.Plugins.Network.Model;
using Oadm.Plugins.Network.Vapix;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Network;

/// <summary>Timing of the reachability check after the connection-relevant write.</summary>
/// <param name="SettleDelay">Wait before the first probe: the device answers the write before it applies it.</param>
/// <param name="ProbeInterval">Pause between probes.</param>
/// <param name="ProbeTimeout">How long to watch the old address.</param>
/// <param name="RequestTimeout">Timeout of one probe request.</param>
public sealed record ReachabilityOptions(TimeSpan SettleDelay, TimeSpan ProbeInterval, TimeSpan ProbeTimeout, TimeSpan RequestTimeout)
{
    public static ReachabilityOptions Default { get; } = new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(5));
}

/// <summary>
/// "Network settings..." task: IPv4 (DHCP/static), IPv6, DNS and host name for the selected devices.
/// Compatibility is checked against the cached API list in <see cref="CanRun"/> and again against a fresh list
/// (with <c>Require</c> per method) before the first write; the payload is validated before anything is written;
/// the change that affects OADM's connection is written last and its effect is reported.
/// Steps: Check compatibility, Read current settings, Validate settings, Set host name, Set DNS, Set IPv6,
/// Set IPv4 (IPv4 before IPv6 when OADM connects over IPv6; one step per device request, sections kept
/// unchanged are Skipped), Wait for the settings to apply, Check reachability.
/// </summary>
public sealed class NetworkSettingsTaskPlugin : ITaskPlugin, ITaskPluginQuery
{
    public const string PluginId = "oadm.network";
    public const string QueryGetNetworkInfo = "getNetworkInfo";

    internal const string StepCheckCompatibility = "Check compatibility";
    internal const string StepReadSettings = "Read current settings";
    internal const string StepReadIpv6Mode = "Read IPv6 address mode";
    internal const string StepValidate = "Validate settings";
    internal const string StepSetHostName = "Set host name";
    internal const string StepSetDns = "Set DNS";
    internal const string StepSetIpv6 = "Set IPv6";
    internal const string StepSetIpv4 = "Set IPv4";
    internal const string StepWaitApply = "Wait for the settings to apply";
    internal const string StepCheckReachability = "Check reachability";
    internal const string KeepUnchanged = "Keep unchanged";

    private readonly ReachabilityOptions _options;
    private readonly TimeProvider _time;

    public NetworkSettingsTaskPlugin()
        : this(ReachabilityOptions.Default, TimeProvider.System)
    {
    }

    public NetworkSettingsTaskPlugin(ReachabilityOptions options, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _options = options;
        _time = timeProvider;
    }

    public string Id => PluginId;

    public string DisplayName => "Network settings...";

    public string? IconKey => "network";

    public bool ShowInToolbar => false;

    public bool RequiresDialog => true;

    /// <summary>Working credentials and network-settings 1.0 or param.cgi in the cached API list.</summary>
    public bool CanRun(IDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return device.Status is DeviceStatus.Ok or DeviceStatus.Unknown && NetworkApis.CanConfigure(device.Apis);
    }

    public async Task ExecuteAsync(ITaskExecutionContext ctx, IDeviceInfo device, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(device);

        // 1. Validate the input (whole batch) before touching the device.
        var payload = NetworkPayload.Parse(payloadJson);
        PayloadValidator.ThrowIfInvalid(payload);

        // Sections in their default write order; BeginStep moves a section up when the plan writes it earlier.
        ctx.PlanSteps(StepCheckCompatibility, StepReadSettings, StepReadIpv6Mode, StepValidate, StepSetHostName, StepSetDns, StepSetIpv6, StepSetIpv4, StepWaitApply, StepCheckReachability);

        // 2. Compatibility against a fresh API list, then read the current state (read-only).
        var apis = await ctx.StepAsync(StepCheckCompatibility, async step =>
        {
            var list = await ctx.Vapix.GetApiListAsync(ct).ConfigureAwait(false);
            if (!NetworkApis.CanConfigure(list))
            {
                list.Require(NetworkApis.NetworkSettings, NetworkApis.NetworkSettingsBase);
            }

            step.Complete(NetworkApis.UseJsonApi(list) && list.FindApi(NetworkApis.NetworkSettings, 1) is { } ns
                ? $"network-settings {ns.Version}"
                : "param.cgi");
            return list;
        }).ConfigureAwait(false);

        // One step per request: getNetworkInfo (or the legacy param.cgi group), then the IPv6 address mode.
        var current = await ctx.StepAsync(StepReadSettings, async step =>
        {
            var read = NetworkApis.UseJsonApi(apis)
                ? await NetworkSettingsClient.ReadNetworkInfoAsync(ctx.Vapix, apis, device.Address, ct).ConfigureAwait(false)
                : await NetworkSettingsClient.ReadParametersAsync(ctx.Vapix, apis, ct).ConfigureAwait(false);
            step.Complete($"IPv4 {Describe(read.Ipv4)}");
            return read;
        }).ConfigureAwait(false);
        if (NetworkSettingsClient.ReadsIpv6ModeSeparately(apis))
        {
            current = await ctx.StepAsync(StepReadIpv6Mode, async step =>
            {
                var read = await NetworkSettingsClient.ReadIpv6ModeAsync(ctx.Vapix, current, ct).ConfigureAwait(false);
                step.Complete("param.cgi Network.IPv6");
                return read;
            }).ConfigureAwait(false);
        }
        else
        {
            ctx.SkipStep(StepReadIpv6Mode, NetworkApis.UseJsonApi(apis) ? "param.cgi is not available" : "Read with the current settings");
        }

        ctx.Log(TaskLogLevel.Info, $"Current settings read via {current.Source}: IPv4 {Describe(current.Ipv4)}.");

        // 3. Plan: Require(...) per method happens here, so an unsupported method fails before any write.
        var plan = await ctx.StepAsync(StepValidate, step =>
        {
            var built = NetworkPlanner.Build(payload, device.Id, apis, current, device.Address);
            step.Complete(built.Steps.Count == 0
                ? "Nothing to change"
                : built.Steps.Count == 1 ? "1 section to change" : $"{built.Steps.Count} sections to change");
            return Task.FromResult(built);
        }).ConfigureAwait(false);

        if (plan.Steps.Count > 0)
        {
            ctx.Log(TaskLogLevel.Info, "Plan: " + string.Join("; ", plan.Steps.Select(s => s.Description)) + ".");
        }

        // 4. Write in order, one step per request; the connection-relevant section is last.
        var done = new List<string>();
        foreach (var kind in SectionOrder(plan))
        {
            var index = plan.Steps.ToList().FindIndex(s => s.Kind == kind);
            if (index < 0)
            {
                ctx.SkipStep(SectionStepName(kind), KeepUnchanged);
                continue;
            }

            var section = plan.Steps[index];
            if (index == plan.Steps.Count - 1 && plan.Impact is { Readdressed: true } readdress)
            {
                var message = readdress.NewAddress is { } next
                    ? $"The device will be re-addressed from {readdress.OldAddress} to {next}."
                    : $"The device may get a new address; it is reached at {readdress.OldAddress} now.";
                ctx.Log(TaskLogLevel.Warning, message);
            }

            for (var r = 0; r < section.Requests.Count; r++)
            {
                using var step = ctx.BeginStep(RequestStepName(section, r));
                try
                {
                    await NetworkSettingsClient.SendAsync(ctx.Vapix, section.Requests[r], ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    var applied = done.Count == 0 ? "Nothing was changed." : "Already applied: " + string.Join("; ", done) + ".";
                    ctx.Log(TaskLogLevel.Error, $"{section.Description} failed: {ex.Message}");
                    throw new InvalidOperationException($"{section.Description} failed: {ex.Message} {applied}", ex);
                }

                step.Complete(r == 0 ? section.Description : null);
            }

            done.Add(section.Description);
            ctx.Log(TaskLogLevel.Info, section.Description + ": applied.");
        }

        // 5. Report what the change did to OADM's connection.
        if (plan.Impact.Affected)
        {
            await CheckReachabilityAsync(ctx, plan.Impact, ct).ConfigureAwait(false);
        }
        else
        {
            var reason = plan.Steps.Count == 0 ? "Nothing was changed." : "The change does not affect how OADM reaches the device.";
            ctx.SkipStep(StepWaitApply, reason);
            ctx.SkipStep(StepCheckReachability, reason);
        }
    }

    /// <summary>Host name, DNS, then the address family OADM does not use, and the one it connects with last (as the planner writes them).</summary>
    private static IEnumerable<StepKind> SectionOrder(NetworkPlan plan)
    {
        yield return StepKind.HostName;
        yield return StepKind.Dns;
        var families = plan.Steps.Select(s => s.Kind).Where(k => k is StepKind.Ipv4 or StepKind.Ipv6).ToList();
        foreach (var kind in families.Count == 2 ? families : [StepKind.Ipv6, StepKind.Ipv4])
        {
            yield return kind;
        }
    }

    internal static string SectionStepName(StepKind kind) => kind switch
    {
        StepKind.HostName => StepSetHostName,
        StepKind.Dns => StepSetDns,
        StepKind.Ipv6 => StepSetIpv6,
        _ => StepSetIpv4,
    };

    /// <summary>The first request of a section is the section step; IPv6 may follow with enabling the interface.</summary>
    internal static string RequestStepName(PlannedStep section, int index)
    {
        if (index == 0)
        {
            return SectionStepName(section.Kind);
        }

        return section.Requests[index] is JsonMethodRequest { Method: "setIPv6AddressConfiguration" }
            ? "Enable IPv6"
            : $"{SectionStepName(section.Kind)} ({index + 1} of {section.Requests.Count})";
    }

    /// <summary>Read-only: "getNetworkInfo" returns <see cref="CurrentNetworkSettings"/> as JSON for the dialog prefill.</summary>
    public async Task<string?> QueryAsync(ITaskQueryContext ctx, IDeviceInfo device, string method, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(device);
        if (!string.Equals(method, QueryGetNetworkInfo, StringComparison.Ordinal))
        {
            throw new NotSupportedException($"Unknown query \"{method}\".");
        }

        var apis = await ctx.Vapix.GetApiListAsync(ct).ConfigureAwait(false);
        var current = await NetworkSettingsClient.ReadAsync(ctx.Vapix, apis, device.Address, ct).ConfigureAwait(false);
        return current.ToJson();
    }

    private async Task CheckReachabilityAsync(ITaskExecutionContext ctx, ConnectionImpact impact, CancellationToken ct)
    {
        // The device answers the write before it applies it.
        using (var wait = ctx.BeginStep(StepWaitApply))
        {
            await Task.Delay(_options.SettleDelay, _time, ct).ConfigureAwait(false);
            wait.Complete();
        }

        using var step = ctx.BeginStep(StepCheckReachability);
        step.ReportProgress(0, $"Probing {impact.OldAddress}");
        var start = _time.GetTimestamp();
        while (true)
        {
            var answered = await TryPingAsync(ctx, ct).ConfigureAwait(false);
            if (!answered)
            {
                if (impact.Readdressed)
                {
                    step.Warn($"The device no longer answers at {impact.OldAddress}. {WhereNow(impact)} " +
                        "OADM still has the old address: remove the device and add it again with its new address.");
                }
                else
                {
                    step.Warn($"The device does not answer at {impact.OldAddress} after the change. Check the subnet mask and gateway; " +
                        "the device keeps the new settings.");
                }

                return;
            }

            var elapsed = _time.GetElapsedTime(start);
            if (elapsed >= _options.ProbeTimeout)
            {
                break;
            }

            step.ReportProgress((int)(100 * elapsed.TotalMilliseconds / _options.ProbeTimeout.TotalMilliseconds), $"Probing {impact.OldAddress}");
            await Task.Delay(_options.ProbeInterval, _time, ct).ConfigureAwait(false);
        }

        if (impact.Readdressed && impact.NewAddress is not null)
        {
            step.Warn($"The device still answers at {impact.OldAddress}, so the new address {impact.NewAddress} may not be active. " +
                "The device returns to its previous settings when a change fails; check its system log.");
        }
        else
        {
            ctx.Log(TaskLogLevel.Info, $"The device still answers at {impact.OldAddress}.");
            step.Complete($"The device still answers at {impact.OldAddress}");
        }
    }

    private async Task<bool> TryPingAsync(ITaskExecutionContext ctx, CancellationToken ct)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
        attempt.CancelAfter(_options.RequestTimeout);
        try
        {
            await ctx.Vapix.GetBasicDeviceInfoAsync(attempt.Token).ConfigureAwait(false);
            return true;
        }
#pragma warning disable CA1031 // Any error or timeout while the device reconfigures means "not answering".
        catch (Exception)
#pragma warning restore CA1031
        {
            ct.ThrowIfCancellationRequested();
            return false;
        }
    }

    private static string WhereNow(ConnectionImpact impact) => impact.NewAddress is { } next
        ? $"It now uses {next}."
        : "It now uses an address assigned by DHCP or router advertisement.";

    private static string Describe(CurrentIpv4 v4) =>
        v4.Supported ? $"{v4.Mode ?? "unknown"} {v4.Address ?? "-"}/{v4.PrefixLength?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"}" : "not supported";
}
