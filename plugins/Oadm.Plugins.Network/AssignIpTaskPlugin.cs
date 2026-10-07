using Oadm.Plugins.Network.Model;
using Oadm.Plugins.Network.Vapix;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Network;

/// <summary>
/// "Assign IP address..." (clone of ADM's "Assign IP address to selected devices"): DHCP or a static address from an
/// IP range, subnet mask and default router (optionally DNS servers) for the selected devices, multi-device first.
/// The dialog resolves the range into one address per device; the payload is a <see cref="NetworkPayload"/> with only
/// the IPv4 (and DNS) section. Runs the same per-device task as "Network settings..." with only the steps that apply:
/// Check compatibility, Read current settings, Validate settings, Set DNS, Set IPv4, Wait for the settings to apply,
/// Check reachability, Wait for the device at the new address, Verify device identity, Update OADM device address.
/// </summary>
public sealed class AssignIpTaskPlugin : ITaskPlugin, ITaskPluginQuery
{
    public const string PluginId = "oadm.network.assign-ip";

    private static readonly StepKind[] Sections = [StepKind.Dns, StepKind.Ipv4];

    private readonly NetworkTaskRunner _runner;

    public AssignIpTaskPlugin()
        : this(ReachabilityOptions.Default, TimeProvider.System)
    {
    }

    public AssignIpTaskPlugin(ReachabilityOptions options, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _runner = new NetworkTaskRunner(options, timeProvider);
    }

    public string Id => PluginId;

    public string DisplayName => "Assign IP address";

    public string Group => TaskGroups.Network;

    public string? IconKey => "network";

    public bool ShowInToolbar => true;

    public bool RequiresDialog => true;

    /// <summary>Same requirements as "Network settings...": working credentials and network-settings 1.0 or param.cgi.</summary>
    public bool CanRun(IDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return device.Status is DeviceStatus.Ok or DeviceStatus.Unknown && NetworkApis.CanConfigure(device.Apis);
    }

    public async Task ExecuteAsync(ITaskExecutionContext ctx, IDeviceInfo device, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(device);
        var payload = Validate(NetworkPayload.Parse(payloadJson));
        await _runner.RunAsync(ctx, device, payload, Sections, ct).ConfigureAwait(false);
    }

    /// <summary>The payload must set IPv4 (DHCP or static) and may set DNS; nothing else. Throws before any request.</summary>
    public static NetworkPayload Validate(NetworkPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Ipv4 is null)
        {
            throw new NetworkValidationException(["Assign IP address: choose DHCP or an IP address range."]);
        }

        if (payload.Ipv6 is not null || payload.HostName is not null)
        {
            throw new NetworkValidationException(["Assign IP address only changes IPv4 and DNS; use Network settings for IPv6 and host names."]);
        }

        PayloadValidator.ThrowIfInvalid(payload);
        return payload;
    }

    /// <summary>Read-only: "getNetworkInfo" (prefill of mask, router and DNS) and "checkAddresses" (assignment table).</summary>
    public Task<string?> QueryAsync(ITaskQueryContext ctx, IDeviceInfo device, string method, string? payloadJson, CancellationToken ct) =>
        NetworkSettingsTaskPlugin.RunQueryAsync(ctx, device, method, payloadJson, ct);
}
