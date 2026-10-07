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
/// </summary>
public sealed class NetworkSettingsTaskPlugin : ITaskPlugin, ITaskPluginQuery
{
    public const string PluginId = "oadm.network";
    public const string QueryGetNetworkInfo = "getNetworkInfo";

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

        // 2. Compatibility against a fresh API list, then read the current state (read-only).
        ctx.ReportProgress(0, "Checking device compatibility");
        var apis = await ctx.Vapix.GetApiListAsync(ct).ConfigureAwait(false);
        if (!NetworkApis.CanConfigure(apis))
        {
            apis.Require(NetworkApis.NetworkSettings, NetworkApis.NetworkSettingsBase);
        }

        ctx.ReportProgress(5, "Reading current network settings");
        var current = await NetworkSettingsClient.ReadAsync(ctx.Vapix, apis, device.Address, ct).ConfigureAwait(false);
        ctx.Log(TaskLogLevel.Info, $"Current settings read via {current.Source}: IPv4 {Describe(current.Ipv4)}.");

        // 3. Plan: Require(...) per method happens here, so an unsupported method fails before any write.
        var plan = NetworkPlanner.Build(payload, device.Id, apis, current, device.Address);
        if (plan.Steps.Count == 0)
        {
            ctx.ReportProgress(100, "Nothing to change");
            return;
        }

        ctx.Log(TaskLogLevel.Info, "Plan: " + string.Join("; ", plan.Steps.Select(s => s.Description)) + ".");

        // 4. Write in order; the connection-relevant section is last.
        var done = new List<string>();
        for (var i = 0; i < plan.Steps.Count; i++)
        {
            var step = plan.Steps[i];
            var isLast = i == plan.Steps.Count - 1;
            ctx.ReportProgress(10 + (70 * i / plan.Steps.Count), step.Description);
            if (isLast && plan.Impact is { Readdressed: true } readdress)
            {
                var message = readdress.NewAddress is { } next
                    ? $"The device will be re-addressed from {readdress.OldAddress} to {next}."
                    : $"The device may get a new address; it is reached at {readdress.OldAddress} now.";
                ctx.Log(TaskLogLevel.Warning, message);
            }

            try
            {
                foreach (var request in step.Requests)
                {
                    await NetworkSettingsClient.SendAsync(ctx.Vapix, request, ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var applied = done.Count == 0 ? "Nothing was changed." : "Already applied: " + string.Join("; ", done) + ".";
                ctx.Log(TaskLogLevel.Error, $"{step.Description} failed: {ex.Message}");
                throw new InvalidOperationException($"{step.Description} failed: {ex.Message} {applied}", ex);
            }

            done.Add(step.Description);
            ctx.Log(TaskLogLevel.Info, step.Description + ": applied.");
        }

        // 5. Report what the change did to OADM's connection.
        if (plan.Impact.Affected)
        {
            await CheckReachabilityAsync(ctx, plan.Impact, ct).ConfigureAwait(false);
        }

        ctx.ReportProgress(100, plan.Impact.Readdressed ? ReaddressedSummary(plan.Impact) : "Network settings applied");
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
        ctx.ReportProgress(85, $"Checking whether the device still answers at {impact.OldAddress}");
        await Task.Delay(_options.SettleDelay, _time, ct).ConfigureAwait(false);
        var start = _time.GetTimestamp();
        while (true)
        {
            var answered = await TryPingAsync(ctx, ct).ConfigureAwait(false);
            if (!answered)
            {
                if (impact.Readdressed)
                {
                    ctx.ReportWarning($"The device no longer answers at {impact.OldAddress}. {WhereNow(impact)} " +
                        "OADM still has the old address: remove the device and add it again with its new address.");
                }
                else
                {
                    ctx.ReportWarning($"The device does not answer at {impact.OldAddress} after the change. Check the subnet mask and gateway; " +
                        "the device keeps the new settings.");
                }

                return;
            }

            if (_time.GetElapsedTime(start) >= _options.ProbeTimeout)
            {
                break;
            }

            await Task.Delay(_options.ProbeInterval, _time, ct).ConfigureAwait(false);
        }

        if (impact.Readdressed && impact.NewAddress is not null)
        {
            ctx.ReportWarning($"The device still answers at {impact.OldAddress}, so the new address {impact.NewAddress} may not be active. " +
                "The device returns to its previous settings when a change fails; check its system log.");
        }
        else
        {
            ctx.Log(TaskLogLevel.Info, $"The device still answers at {impact.OldAddress}.");
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

    private static string ReaddressedSummary(ConnectionImpact impact) => impact.NewAddress is { } next
        ? $"Applied; device re-addressed to {next}"
        : "Applied; device address assigned by the network";

    private static string Describe(CurrentIpv4 v4) =>
        v4.Supported ? $"{v4.Mode ?? "unknown"} {v4.Address ?? "-"}/{v4.PrefixLength?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"}" : "not supported";
}
