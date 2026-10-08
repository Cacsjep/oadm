using System.Collections.Concurrent;
using System.Globalization;

using Oadm.Plugins.Pki.Device;
using Oadm.Plugins.Shared;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Pki.Tasks;

/// <summary>
/// Compatibility of the PKI tasks (spec "Compatibility"). <c>getApiList</c> does not list the certificate API, so the tasks
/// read <c>GET /config/discover</c> (fresh in <c>ExecuteAsync</c> before the first write; cached per device for 10 minutes for
/// the read-only dialog queries). <see cref="CanRun"/> uses cached data only: AXIS OS 11.11 or later from the firmware
/// version, plus network-settings 1.x for 802.1X.
/// </summary>
public static class PkiCompatibility
{
    public const string NetworkSettings = "network-settings";

    private static readonly Version NetworkSettingsBase = new(1, 0);
    private static readonly ConcurrentDictionary<Guid, (DateTimeOffset Read, CertApiSupport Support)> DiscoverCache = new();

    /// <summary>How long a <c>config/discover</c> answer is reused by queries.</summary>
    public static readonly TimeSpan DiscoverCacheTime = TimeSpan.FromMinutes(10);

    /// <summary>AXIS OS 11.11 or later ("11.11.135", "12.11.77"); false for unknown or unparsable versions.</summary>
    public static bool FirmwareSupportsCertApi(string? firmware)
    {
        if (string.IsNullOrWhiteSpace(firmware))
        {
            return false;
        }

        var parts = firmware.Trim().Split('.');
        if (parts.Length < 2
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || !int.TryParse(new string([.. parts[1].TakeWhile(char.IsDigit)]), NumberStyles.None, CultureInfo.InvariantCulture, out var minor))
        {
            return false;
        }

        return major > 11 || (major == 11 && minor >= 11);
    }

    /// <summary>network-settings 1.x in the cached API list (802.1X tasks).</summary>
    public static bool HasNetworkSettings(IReadOnlyList<DeviceApi> apis) => CachedApiCheck.Supports(apis, NetworkSettings, NetworkSettingsBase);

    /// <summary>Fresh <c>config/discover</c>; throws <see cref="DeviceNotCompatibleException"/> without released cert v1.</summary>
    public static async Task<CertApiSupport> RequireCertApiAsync(IVapixClient vapix, Guid deviceId, TimeProvider time, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(time);
        var support = await CertApi.DiscoverAsync(vapix, ct).ConfigureAwait(false);
        DiscoverCache[deviceId] = (time.GetUtcNow(), support);
        if (!support.IsSupported)
        {
            throw new DeviceNotCompatibleException(CertApi.NeedsNewerFirmware);
        }

        return support;
    }

    /// <summary><c>config/discover</c> for read-only queries, cached per device for <see cref="DiscoverCacheTime"/>.</summary>
    public static async Task<CertApiSupport> CachedCertApiAsync(IVapixClient vapix, Guid deviceId, TimeProvider time, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(time);
        var now = time.GetUtcNow();
        if (DiscoverCache.TryGetValue(deviceId, out var cached) && now - cached.Read < DiscoverCacheTime)
        {
            return cached.Support;
        }

        var support = await CertApi.DiscoverAsync(vapix, ct).ConfigureAwait(false);
        DiscoverCache[deviceId] = (now, support);
        return support;
    }

    /// <summary>For tests.</summary>
    internal static void ClearCache() => DiscoverCache.Clear();

    /// <summary>
    /// Why a PKI task cannot run on the device (cached data only): "Needs AXIS OS 11.11 or later (this device has
    /// 10.12.338)", for 802.1X also "Needs the network settings API". Null when it can.
    /// </summary>
    public static string? NotSupportedReason(IDeviceInfo device, bool needsNetworkSettings)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (!FirmwareSupportsCertApi(device.FirmwareVersion))
        {
            return TaskSupportReasons.NeedsFirmware("11.11", device);
        }

        return needsNetworkSettings && !HasNetworkSettings(device.Apis) ? TaskSupportReasons.NeedsApi("the network settings API", device) : null;
    }

    /// <summary>Cheap CanRun filter of every PKI task.</summary>
    public static bool CanRunCertificateTask(IDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return FirmwareSupportsCertApi(device.FirmwareVersion);
    }
}
