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
/// Steps: Check compatibility, Read current settings, Read IPv6 address mode, Validate settings, Set host name,
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

    public NetworkSettingsTaskPlugin()
        : this(ReachabilityOptions.Default, TimeProvider.System)
    {
    }

    public NetworkSettingsTaskPlugin(ReachabilityOptions options, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _runner = new NetworkTaskRunner(options, timeProvider);
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

        // Validate the input (whole batch) before touching the device.
        var payload = NetworkPayload.Parse(payloadJson);
        PayloadValidator.ThrowIfInvalid(payload);
        await _runner.RunAsync(ctx, device, payload, AllSections, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Read-only: "getNetworkInfo" returns <see cref="CurrentNetworkSettings"/> as JSON for the dialog prefill;
    /// "checkAddresses" flags candidate addresses used by managed devices or answering on TCP 80/443.
    /// </summary>
    public Task<string?> QueryAsync(ITaskQueryContext ctx, IDeviceInfo device, string method, string? payloadJson, CancellationToken ct) =>
        RunQueryAsync(ctx, device, method, payloadJson, ct);

    /// <summary>The queries of both network plugins ("getNetworkInfo", "checkAddresses").</summary>
    internal static async Task<string?> RunQueryAsync(ITaskQueryContext ctx, IDeviceInfo device, string method, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(device);
        if (string.Equals(method, AddressCheck.QueryMethod, StringComparison.Ordinal))
        {
            return await AddressCheck.QueryAsync(ctx, payloadJson, ct).ConfigureAwait(false);
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
