using System.Collections.Concurrent;
using System.Globalization;

using Oadm.Plugins.Pki.Device;
using Oadm.Plugins.Shared;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Pki.Tasks;

/// <summary>
/// What a device offers the PKI tasks (<see cref="PkiCompatibility.Decide"/>): the certificate API, the AXIS OS version and
/// what follows from it. Read fresh in Check compatibility before the first write.
/// </summary>
/// <param name="Firmware">The AXIS OS version read from the device (null on the REST path when not needed).</param>
/// <param name="Certificates">REST cert v1 (released) or the SOAP certificate service.</param>
/// <param name="CertApiVersion">The REST cert API version ("1.1.2"); null on SOAP.</param>
public sealed record PkiDeviceSupport(string? Firmware, CertificateApiKind Certificates, string? CertApiVersion)
{
    /// <summary>
    /// The web server set request must name the Operator and Viewer policies too (AXIS OS before 7.10, research on 6.53:
    /// Admin alone is refused with <c>ter:InvalidArgs</c>).
    /// </summary>
    public bool WebServerAllRoles =>
        Certificates == CertificateApiKind.Soap && PkiCompatibility.ParseFirmware(Firmware) is { } v && v < PkiCompatibility.AllRolesBefore;

    /// <summary>The highest EAPOL version the device's 802.1X client works with: 3 from AXIS OS 10.1 (release notes), else 2.</summary>
    public int MaxEapolVersion =>
        Certificates == CertificateApiKind.Rest || (PkiCompatibility.ParseFirmware(Firmware) is { } v && v >= PkiCompatibility.Eapol3From) ? 3 : 2;

    /// <summary>"cert 1.1.2" or "AXIS OS 9.80.3 · SOAP certificates" for the Check compatibility step.</summary>
    public string Describe() => Certificates == CertificateApiKind.Rest ? "cert " + CertApiVersion : $"AXIS OS {Firmware} · SOAP certificates";
}

/// <summary>
/// Compatibility of the PKI tasks (docs/spec/pki.md "Older AXIS OS"), decided in one place. Certificates: the REST API
/// <c>cert</c> v1 when <c>GET /config/discover</c> lists it released (AXIS OS 11.11.135 and later), else the SOAP certificate
/// service on AXIS OS 6.53 and later (the version read from the device; unknown or unreadable refuses). IEEE 802.1X:
/// network_settings.cgi alone where it binds and reports the certificates (<see cref="Dot1xApiOf"/>), else the SOAP
/// certificate set for the certificates. <see cref="CanRun"/>-style checks use cached data only (the firmware version).
/// </summary>
public static class PkiCompatibility
{
    public const string NetworkSettings = "network-settings";

    /// <summary>The oldest AXIS OS the PKI tasks run on (researched on hardware down to 6.53).</summary>
    public const string MinimumFirmware = "6.53";

    /// <summary>"Needs AXIS OS 6.53 or later. Nothing was changed."</summary>
    public const string NeedsNewerFirmware = "Needs AXIS OS " + MinimumFirmware + " or later. Nothing was changed.";

    /// <summary>The device's AXIS OS version could not be read: no write.</summary>
    public const string UnknownFirmware = "The device's AXIS OS version cannot be read. Nothing was changed.";

    public static readonly Version Minimum = new(6, 53);

    /// <summary>Before this AXIS OS the web server needs the Operator and Viewer policies in the set request.</summary>
    public static readonly Version AllRolesBefore = new(7, 10);

    /// <summary>EAPOL version 3 (IEEE 802.1X-2010) from AXIS OS 10.1.0 (release notes; 6.53 breaks with it).</summary>
    public static readonly Version Eapol3From = new(10, 1);

    /// <summary>network-settings from this version binds and reports the 802.1X certificates (AXIS OS 12.11).</summary>
    public static readonly Version NetworkSettingsCertificatesFrom = new(1, 38);

    private static readonly Version NetworkSettingsBase = new(1, 0);
    private static readonly ConcurrentDictionary<Guid, (DateTimeOffset Read, CertApiSupport Support)> DiscoverCache = new();

    /// <summary>How long a <c>config/discover</c> answer is reused by queries.</summary>
    public static readonly TimeSpan DiscoverCacheTime = TimeSpan.FromMinutes(10);

    /// <summary>"6.53.4" -> 6.53, "11.11.135" -> 11.11; null for unknown or unparsable versions.</summary>
    public static Version? ParseFirmware(string? firmware)
    {
        if (string.IsNullOrWhiteSpace(firmware))
        {
            return null;
        }

        var parts = firmware.Trim().Split('.');
        if (parts.Length < 2
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || !int.TryParse(new string([.. parts[1].TakeWhile(char.IsDigit)]), NumberStyles.None, CultureInfo.InvariantCulture, out var minor))
        {
            return null;
        }

        return new Version(major, minor);
    }

    /// <summary>AXIS OS 6.53 or later; false for unknown or unparsable versions (cached data, the menus).</summary>
    public static bool FirmwareSupported(string? firmware) => ParseFirmware(firmware) is { } v && v >= Minimum;

    /// <summary>network-settings 1.x in the cached API list.</summary>
    public static bool HasNetworkSettings(IReadOnlyList<DeviceApi> apis) => CachedApiCheck.Supports(apis, NetworkSettings, NetworkSettingsBase);

    /// <summary>
    /// The certificate API for the device: REST when <c>config/discover</c> lists cert v1 released, else SOAP from AXIS OS
    /// 6.53. Throws <see cref="DeviceNotCompatibleException"/> ("Nothing was changed") for an older, unknown or unreadable version.
    /// </summary>
    public static PkiDeviceSupport Decide(string? firmware, CertApiSupport cert)
    {
        ArgumentNullException.ThrowIfNull(cert);
        if (cert.IsSupported)
        {
            return new PkiDeviceSupport(firmware, CertificateApiKind.Rest, cert.Version);
        }

        var version = ParseFirmware(firmware) ?? throw new DeviceNotCompatibleException(UnknownFirmware);
        return version < Minimum
            ? throw new DeviceNotCompatibleException(NeedsNewerFirmware)
            : new PkiDeviceSupport(firmware!.Trim(), CertificateApiKind.Soap, null);
    }

    /// <summary>
    /// How 802.1X is set: network_settings.cgi alone when it reports the certificates in <c>getNetworkInfo</c> or lists 1.38 or
    /// later (before, it answers success but does not bind them); the SOAP certificate set with network_settings.cgi below
    /// that; with the parameters when the API list has no network settings.
    /// </summary>
    public static Dot1xApiKind Dot1xApiOf(IReadOnlyList<DeviceApi> apis, Wired8021X? reported)
    {
        ArgumentNullException.ThrowIfNull(apis);
        if (!HasNetworkSettings(apis))
        {
            return Dot1xApiKind.ParametersAndCertSet;
        }

        var listed = apis.FindApi(NetworkSettings, 1)?.Version;
        var binds = reported?.ReportsCertificates == true
            || (Version.TryParse(listed, out var version) && version >= NetworkSettingsCertificatesFrom);
        return binds ? Dot1xApiKind.NetworkSettings : Dot1xApiKind.NetworkSettingsAndCertSet;
    }

    /// <summary>The policy's EAPOL version works on the device; else "Nothing was changed".</summary>
    public static void RequireEapol(Policies.PkiPolicy policy, PkiDeviceSupport support)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(support);
        if ((policy.EapolVersion ?? 1) > support.MaxEapolVersion)
        {
            throw new DeviceNotCompatibleException(
                string.Create(CultureInfo.InvariantCulture, $"EAPOL version {policy.EapolVersion} needs AXIS OS {Eapol3From.Major}.{Eapol3From.Minor} or later. Choose a policy with EAPOL version 1 or 2. Nothing was changed."));
        }
    }

    /// <summary>
    /// Fresh: <c>config/discover</c> and, without the released REST API, the AXIS OS version of the device; then
    /// <see cref="Decide"/>. Throws <see cref="DeviceNotCompatibleException"/> before any write.
    /// </summary>
    public static async Task<PkiDeviceSupport> RequireAsync(IVapixClient vapix, Guid deviceId, TimeProvider time, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(time);
        var cert = await CertApi.DiscoverAsync(vapix, ct).ConfigureAwait(false);
        DiscoverCache[deviceId] = (time.GetUtcNow(), cert);
        return Decide(cert.IsSupported ? null : await ReadFirmwareAsync(vapix, ct).ConfigureAwait(false), cert);
    }

    /// <summary>
    /// For read-only queries and background reads: <c>config/discover</c> cached per device for <see cref="DiscoverCacheTime"/>
    /// and the cached AXIS OS version of <paramref name="device"/>.
    /// </summary>
    public static async Task<PkiDeviceSupport> CachedAsync(IVapixClient vapix, IDeviceInfo device, TimeProvider time, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(time);
        var now = time.GetUtcNow();
        if (!DiscoverCache.TryGetValue(device.Id, out var cached) || now - cached.Read >= DiscoverCacheTime)
        {
            cached = (now, await CertApi.DiscoverAsync(vapix, ct).ConfigureAwait(false));
            DiscoverCache[device.Id] = cached;
        }

        return Decide(device.FirmwareVersion, cached.Support);
    }

    /// <summary>
    /// The AXIS OS version from <c>basicdeviceinfo.cgi</c>, else the parameter <c>Properties.Firmware.Version</c> (older AXIS
    /// OS); null when neither answers.
    /// </summary>
    public static async Task<string?> ReadFirmwareAsync(IVapixClient vapix, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(vapix);
        try
        {
            if ((await vapix.GetBasicDeviceInfoAsync(ct).ConfigureAwait(false)).Version is { Length: > 0 } version)
            {
                return version;
            }
        }
#pragma warning disable CA1031 // Any failure of the first source falls back to the parameter; cancellation passes.
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
#pragma warning restore CA1031
        {
            // older AXIS OS without basicdeviceinfo.cgi
        }

        try
        {
            var parameters = await vapix.ListParametersAsync([FirmwareParameter], ct).ConfigureAwait(false);
            return parameters.TryGetValue(FirmwareParameter, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
        }
#pragma warning disable CA1031 // Unreadable: the caller refuses ("Nothing was changed").
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
#pragma warning restore CA1031
        {
            return null;
        }
    }

    /// <summary>The AXIS OS version as a parameter (every AXIS OS).</summary>
    public const string FirmwareParameter = "Properties.Firmware.Version";

    /// <summary>For tests.</summary>
    internal static void ClearCache() => DiscoverCache.Clear();

    /// <summary>
    /// Why a PKI task cannot run on the device (cached data only): "Needs AXIS OS 6.53 or later (this device has 6.50.5)".
    /// Null when it can.
    /// </summary>
    public static string? NotSupportedReason(IDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return FirmwareSupported(device.FirmwareVersion) ? null : TaskSupportReasons.NeedsFirmware(MinimumFirmware, device);
    }

    /// <summary>Cheap CanRun filter of every PKI task.</summary>
    public static bool CanRunCertificateTask(IDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return FirmwareSupported(device.FirmwareVersion);
    }
}
