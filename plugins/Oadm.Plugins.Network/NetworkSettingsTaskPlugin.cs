using Oadm.Plugins.Network.Model;
using Oadm.Plugins.Network.Vapix;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Network;

/// <summary>
/// "Network settings..." task: IPv4 (DHCP/static), IPv6, DNS and host name for the selected devices.
/// Compatibility is checked against the cached API list in <see cref="CanRun"/> and again against a fresh list
/// (with <c>Require</c> per method) before the first write; the payload is validated before anything is written;
/// the change that affects OADM's connection is written last and OADM follows a new static address.
/// Steps: Check compatibility, Read current settings, Read IPv6 address mode, Validate settings, Check address is free, Set host name,
/// Set DNS, Set IPv6, Set IPv4 (IPv4 before IPv6 when OADM connects over IPv6; one step per device request,
/// sections kept unchanged are Skipped), Wait for the settings to apply, Check reachability, Wait for the device
/// at the new address, Verify device identity, Update OADM device address (see <see cref="NetworkTaskRunner"/>).
/// </summary>
public sealed class NetworkSettingsTaskPlugin : ITaskPlugin, ITaskPluginQuery
{
    public const string PluginId = "oadm.network";
    public const string QueryGetNetworkInfo = "getNetworkInfo";

    private static readonly StepKind[] AllSections = [StepKind.HostName, StepKind.Dns, StepKind.Ipv6, StepKind.Ipv4];

    private readonly NetworkTaskRunner _runner;
    private readonly IAddressProbe _probe;

    public NetworkSettingsTaskPlugin()
        : this(ReachabilityOptions.Default, TimeProvider.System, NetworkAddressProbe.Instance)
    {
    }

    /// <param name="options">Timing of the reachability checks.</param>
    /// <param name="timeProvider">Clock for the waits.</param>
    /// <param name="probe">Is an address taken (ping, TCP 80/443)? Tests pass a fake: no network in unit tests.</param>
    public NetworkSettingsTaskPlugin(ReachabilityOptions options, TimeProvider timeProvider, IAddressProbe probe)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(probe);
        _probe = probe;
        _runner = new NetworkTaskRunner(options, timeProvider, probe);
    }

    public string Id => PluginId;

    public string DisplayName => "Network settings";

    public string Group => TaskGroups.Network;

    public string? IconKey => "network";

    public bool ShowInToolbar => false;

    public bool RequiresDialog => true;

    /// <summary>Working credentials and network-settings 1.0 or param.cgi in the cached API list.</summary>
    public bool CanRun(IDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return device.Status is DeviceStatus.Ok or DeviceStatus.Unknown && NetworkApis.CanConfigure(device.Apis);
    }

    /// <summary>Plain-language reason for the greyed menu entry when <see cref="CanRun"/> is false (cached data only).</summary>
    public string? NotSupportedReason(IDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return TaskSupportReasons.ForStatus(device.Status) ?? TaskSupportReasons.NeedsApi("the network settings API", device);
    }

    public async Task ExecuteAsync(ITaskExecutionContext ctx, IDeviceInfo device, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(device);

        // Validate the input (whole batch) before touching the device.
        var payload = NetworkPayload.Parse(payloadJson);
        PayloadValidator.ThrowIfInvalid(payload);
        await _runner.RunAsync(ctx, device, payload, AllSections, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Read-only: "getNetworkInfo" returns <see cref="CurrentNetworkSettings"/> as JSON for the dialog prefill;
    /// "checkAddresses" flags candidate addresses used by managed devices or answering ping or TCP 80/443.
    /// </summary>
    public Task<string?> QueryAsync(ITaskQueryContext ctx, IDeviceInfo device, string method, string? payloadJson, CancellationToken ct) =>
        RunQueryAsync(ctx, device, method, payloadJson, _probe, ct);

    /// <summary>
    /// "Set static IP 10.0.0.60", "Switch to DHCP", "Set DNS servers", "Set host name cam-1", "Change IPv6 settings"
    /// when only one section changes, else "Change network settings".
    /// </summary>
    public string GetTaskName(string? payloadJson) => NetworkTaskNames.ForNetworkSettings(payloadJson) ?? DisplayName;

    /// <summary>The queries of both network plugins ("getNetworkInfo", "checkAddresses").</summary>
    internal static async Task<string?> RunQueryAsync(ITaskQueryContext ctx, IDeviceInfo device, string method, string? payloadJson, IAddressProbe probe, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(device);
        if (string.Equals(method, AddressCheck.QueryMethod, StringComparison.Ordinal))
        {
            return await AddressCheck.QueryAsync(ctx, payloadJson, probe, ct).ConfigureAwait(false);
        }

        if (!string.Equals(method, QueryGetNetworkInfo, StringComparison.Ordinal))
        {
            throw new NotSupportedException($"Unknown query \"{method}\".");
        }

        var apis = await ctx.Vapix.GetApiListAsync(ct).ConfigureAwait(false);
        var current = await NetworkSettingsClient.ReadAsync(ctx.Vapix, apis, device.Address, ct).ConfigureAwait(false);
        return current.ToJson();
    }
}
