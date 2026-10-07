using System.Globalization;
using System.Net;

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

    /// <summary>How long to wait for the device at its new static address. Default 90 s.</summary>
    public TimeSpan NewAddressTimeout { get; init; } = TimeSpan.FromSeconds(90);
}

/// <summary>
/// The per-device task of both network plugins ("Network settings..." and "Assign IP address..."): compatibility,
/// read, validate/plan, one step per write request (the address family OADM connects with last), then OADM follows
/// the device: a new static address is polled, the serial number verified and the OADM device record moved
/// (<see cref="ITaskExecutionContext.UpdateDeviceAddressAsync"/>); a DHCP change keeps the record, the server's
/// periodic mDNS browse finds the device again.
/// </summary>
internal sealed class NetworkTaskRunner(ReachabilityOptions options, TimeProvider time)
{
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
    internal const string StepWaitNewAddress = "Wait for the device at the new address";
    internal const string StepVerifyIdentity = "Verify device identity";
    internal const string StepUpdateAddress = "Update OADM device address";
    internal const string KeepUnchanged = "Keep unchanged";
    internal const string DhcpReason = "DHCP: address assigned by the network, the device will be found again by the next scan";

    /// <summary>Runs the task. <paramref name="sections"/> limits the planned write steps (Assign IP address: DNS and IPv4 only).</summary>
    public async Task RunAsync(ITaskExecutionContext ctx, IDeviceInfo device, NetworkPayload payload, IReadOnlyList<StepKind> sections, CancellationToken ct)
    {
        var readIpv6Mode = sections.Contains(StepKind.Ipv6);
        var planned = new List<string> { StepCheckCompatibility, StepReadSettings };
        if (readIpv6Mode)
        {
            planned.Add(StepReadIpv6Mode);
        }

        planned.Add(StepValidate);
        planned.AddRange(sections.Order().Select(SectionStepName));
        planned.AddRange([StepWaitApply, StepCheckReachability, StepWaitNewAddress, StepVerifyIdentity, StepUpdateAddress]);
        ctx.PlanSteps([.. planned]);

        // Compatibility against a fresh API list, then read the current state (read-only).
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
        if (readIpv6Mode)
        {
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
        }

        ctx.Log(TaskLogLevel.Info, $"Current settings read via {current.Source}: IPv4 {Describe(current.Ipv4)}.");

        // Plan: Require(...) per method happens here, so an unsupported method fails before any write.
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

        // Write in order, one step per request; the connection-relevant section is last.
        var done = new List<string>();
        foreach (var kind in SectionOrder(plan, sections))
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

        // What the change did to OADM's connection, and OADM following the device.
        if (!plan.Impact.Affected)
        {
            var reason = plan.Steps.Count == 0 ? "Nothing was changed." : "The change does not affect how OADM reaches the device.";
            SkipAll(ctx, reason, StepWaitApply, StepCheckReachability, StepWaitNewAddress, StepVerifyIdentity, StepUpdateAddress);
            return;
        }

        // The device answers the write before it applies it.
        using (var wait = ctx.BeginStep(StepWaitApply))
        {
            await Task.Delay(options.SettleDelay, time, ct).ConfigureAwait(false);
            wait.Complete();
        }

        var impact = plan.Impact;
        if (impact is { Readdressed: true, NewAddress: { } newAddress })
        {
            ctx.SkipStep(StepCheckReachability, $"The device moves to {newAddress}.");
            await FollowAsync(ctx, device, impact.OldAddress, newAddress, ct).ConfigureAwait(false);
            return;
        }

        await CheckReachabilityAsync(ctx, impact, ct).ConfigureAwait(false);
        var followReason = !impact.Readdressed
            ? "The address does not change."
            : payload.Ipv4 is { Mode: Ipv4Mode.Dhcp } && IsIpv4(impact.OldAddress)
                ? DhcpReason
                : "The new address is assigned by the network; the device will be found again by the next scan";
        SkipAll(ctx, followReason, StepWaitNewAddress, StepVerifyIdentity, StepUpdateAddress);
    }

    /// <summary>Host name, DNS, then the address family OADM does not use, and the one it connects with last (as the planner writes them).</summary>
    private static IEnumerable<StepKind> SectionOrder(NetworkPlan plan, IReadOnlyList<StepKind> sections)
    {
        var order = new List<StepKind> { StepKind.HostName, StepKind.Dns };
        var families = plan.Steps.Select(s => s.Kind).Where(k => k is StepKind.Ipv4 or StepKind.Ipv6).ToList();
        order.AddRange(families.Count == 2 ? families : [StepKind.Ipv6, StepKind.Ipv4]);
        return order.Where(sections.Contains);
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

    /// <summary>Wait for the device at its new static address, verify its serial number, move the OADM record.</summary>
    private async Task FollowAsync(ITaskExecutionContext ctx, IDeviceInfo device, string oldAddress, string newAddress, CancellationToken ct)
    {
        BasicDeviceInfo? info;
        using (var step = ctx.BeginStep(StepWaitNewAddress))
        {
            IVapixClient client;
            try
            {
                client = await ctx.CreateClientForAsync(newAddress, ct).ConfigureAwait(false);
            }
            catch (NotSupportedException)
            {
                step.Warn($"This server cannot follow the device to {newAddress}. Remove the device and add it again with its new address.");
                SkipAll(ctx, "Not supported by this server.", StepVerifyIdentity, StepUpdateAddress);
                return;
            }

            try
            {
                info = await WaitForAsync(step, client, newAddress, ct).ConfigureAwait(false);
            }
            finally
            {
                (client as IDisposable)?.Dispose();
            }

            if (info is null)
            {
                var stillOld = await TryPingAsync(ctx.Vapix, ct).ConfigureAwait(false) is not null;
                var seconds = options.NewAddressTimeout.TotalSeconds.ToString("0", CultureInfo.InvariantCulture);
                step.Warn(stillOld
                    ? $"The device does not answer at {newAddress} within {seconds} s but still answers at {oldAddress}, so the new address may not be active. " +
                      "The device returns to its previous settings when a change fails; check its system log. The OADM device record is unchanged."
                    : $"The device does not answer at {newAddress} within {seconds} s, nor at {oldAddress}. Check the subnet mask and default router. " +
                      $"The OADM device record keeps {oldAddress}.");
                SkipAll(ctx, "The device was not found at the new address.", StepVerifyIdentity, StepUpdateAddress);
                return;
            }

            step.Complete($"The device answers at {newAddress}");
        }

        using (var verify = ctx.BeginStep(StepVerifyIdentity))
        {
            if (!SameSerial(info.SerialNumber, device.Serial))
            {
                verify.Warn($"Another device answers at {newAddress} (serial number {info.SerialNumber}, expected {device.Serial}). " +
                    "The address may be in use. The OADM device record is unchanged.");
                ctx.SkipStep(StepUpdateAddress, "The device at the new address is not this device.");
                return;
            }

            verify.Complete($"Serial number {device.Serial}");
        }

        if (!IPAddress.TryParse(device.Address.Trim('[', ']'), out _))
        {
            ctx.SkipStep(StepUpdateAddress, $"OADM reaches the device by host name {device.Address}; the host name is kept.");
            return;
        }

        using var update = ctx.BeginStep(StepUpdateAddress);
        try
        {
            var moved = await ctx.UpdateDeviceAddressAsync(newAddress, ct).ConfigureAwait(false);
            update.Complete(moved ? $"{oldAddress} -> {newAddress}" : "Unchanged: the record already has this address or uses the host name");
        }
        catch (DeviceIdentityException ex)
        {
            update.Warn(ex.Message);
        }
        catch (NotSupportedException)
        {
            update.Warn($"This server cannot change the device address. Remove the device and add it again with {newAddress}.");
        }
    }

    private async Task<BasicDeviceInfo?> WaitForAsync(ITaskStep step, IVapixClient client, string address, CancellationToken ct)
    {
        var start = time.GetTimestamp();
        step.ReportProgress(0, $"Probing {address}");
        while (true)
        {
            if (await TryPingAsync(client, ct).ConfigureAwait(false) is { } info)
            {
                return info;
            }

            var elapsed = time.GetElapsedTime(start);
            if (elapsed >= options.NewAddressTimeout)
            {
                return null;
            }

            step.ReportProgress((int)(100 * elapsed.TotalMilliseconds / options.NewAddressTimeout.TotalMilliseconds), $"Probing {address}");
            await Task.Delay(options.ProbeInterval, time, ct).ConfigureAwait(false);
        }
    }

    private async Task CheckReachabilityAsync(ITaskExecutionContext ctx, ConnectionImpact impact, CancellationToken ct)
    {
        using var step = ctx.BeginStep(StepCheckReachability);
        step.ReportProgress(0, $"Probing {impact.OldAddress}");
        var start = time.GetTimestamp();
        while (true)
        {
            var answered = await TryPingAsync(ctx.Vapix, ct).ConfigureAwait(false) is not null;
            if (!answered)
            {
                if (impact.Readdressed)
                {
                    step.Warn($"The device no longer answers at {impact.OldAddress}. It now uses an address assigned by DHCP or router advertisement. " +
                        "OADM keeps the old address until the next mDNS scan finds the device again (only while it is unreachable).");
                }
                else
                {
                    step.Warn($"The device does not answer at {impact.OldAddress} after the change. Check the subnet mask and gateway; " +
                        "the device keeps the new settings.");
                }

                return;
            }

            var elapsed = time.GetElapsedTime(start);
            if (elapsed >= options.ProbeTimeout)
            {
                break;
            }

            step.ReportProgress((int)(100 * elapsed.TotalMilliseconds / options.ProbeTimeout.TotalMilliseconds), $"Probing {impact.OldAddress}");
            await Task.Delay(options.ProbeInterval, time, ct).ConfigureAwait(false);
        }

        ctx.Log(TaskLogLevel.Info, $"The device still answers at {impact.OldAddress}.");
        step.Complete($"The device still answers at {impact.OldAddress}");
    }

    private async Task<BasicDeviceInfo?> TryPingAsync(IVapixClient client, CancellationToken ct)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
        attempt.CancelAfter(options.RequestTimeout);
        try
        {
            return await client.GetBasicDeviceInfoAsync(attempt.Token).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Any error or timeout while the device reconfigures means "not answering".
        catch (Exception)
#pragma warning restore CA1031
        {
            ct.ThrowIfCancellationRequested();
            return null;
        }
    }

    private static void SkipAll(ITaskExecutionContext ctx, string reason, params string[] steps)
    {
        foreach (var name in steps)
        {
            ctx.SkipStep(name, reason);
        }
    }

    private static bool SameSerial(string? a, string? b) => Normalize(a) is { Length: > 0 } x && x == Normalize(b);

    private static string Normalize(string? serial) =>
        new((serial ?? string.Empty).Where(char.IsAsciiHexDigit).Select(char.ToUpperInvariant).ToArray());

    private static bool IsIpv4(string address) => Ipv4.TryParse(address, out _) || !address.Contains(':', StringComparison.Ordinal);

    internal static string Describe(CurrentIpv4 v4) =>
        v4.Supported ? $"{v4.Mode ?? "unknown"} {v4.Address ?? "-"}/{v4.PrefixLength?.ToString(CultureInfo.InvariantCulture) ?? "-"}" : "not supported";
}
