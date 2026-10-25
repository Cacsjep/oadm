using System.Globalization;
using System.Text;

using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Pki.Device;

/// <summary>How a device's wired IEEE 802.1X is set (<see cref="Tasks.PkiCompatibility.Dot1xApiOf"/>).</summary>
public enum Dot1xApiKind
{
    /// <summary>network_settings.cgi only: it binds and reports the client and CA certificates (network settings 1.38, AXIS OS 12.11).</summary>
    NetworkSettings,

    /// <summary>
    /// network_settings.cgi for on/off, identity and EAPOL version; the certificates through the SOAP certificate set
    /// <c>EAPTLS_WIRED</c> (network settings before 1.38 answers success to the certificates but does not bind them).
    /// </summary>
    NetworkSettingsAndCertSet,

    /// <summary>
    /// The parameters <c>Network.Interface.I0.dot1x</c> for on/off, identity and EAPOL version (no network settings API:
    /// AXIS OS 8.40 and older); the certificates through the SOAP certificate set.
    /// </summary>
    ParametersAndCertSet,
}

/// <summary>The device's 802.1X state read with <see cref="Dot1xSettings.ReadAsync"/>: the way it is set and the settings (Network.Dot1x null = not offered).</summary>
public sealed record Dot1xState(Dot1xApiKind Api, DeviceNetworkInfo Network)
{
    public Wired8021X? Dot1x => Network.Dot1x;

    /// <summary>How 802.1X is set, for step details.</summary>
    public string Describe() => Api switch
    {
        Dot1xApiKind.NetworkSettings => "802.1X by network settings",
        Dot1xApiKind.NetworkSettingsAndCertSet => "802.1X by network settings, certificates by certificate set",
        _ => "802.1X by parameters, certificates by certificate set",
    };
}

/// <summary>
/// Wired IEEE 802.1X (EAP-TLS) on every AXIS OS from 6.53: reads, enables, sets the RADIUS server CA and disables through the
/// way <see cref="Dot1xApiKind"/> names. On the certificate set ways the certificates are bound first (SOAP
/// <c>SetCertSet EAPTLS_WIRED</c>) and 802.1X is switched on last; the read-back takes them from <c>GetCertSet</c>.
/// </summary>
public static class Dot1xSettings
{
    public const string ParameterGroup = "Network.Interface.I0.dot1x";
    public const string EnabledParameter = ParameterGroup + ".Enabled";
    public const string EapolParameter = ParameterGroup + ".EAPOLVersion";
    public const string IdentityParameter = ParameterGroup + ".EAPTLS.Identity";
    public const string StatusParameter = ParameterGroup + ".Status";
    public const string ParamPath = "axis-cgi/param.cgi";

    /// <summary>The way 802.1X is set on this device and its settings (read-only).</summary>
    public static async Task<Dot1xState> ReadAsync(IVapixClient vapix, IReadOnlyList<DeviceApi> apis, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(apis);
        if (Tasks.PkiCompatibility.HasNetworkSettings(apis))
        {
            var info = await NetworkInfoApi.GetAsync(vapix, apis, ct).ConfigureAwait(false);
            var api = Tasks.PkiCompatibility.Dot1xApiOf(apis, info.Dot1x);
            if (api == Dot1xApiKind.NetworkSettingsAndCertSet && info.Dot1x is { } wired)
            {
                info = info with { Dot1x = await WithCertSetAsync(vapix, wired, ct).ConfigureAwait(false) };
            }

            return new Dot1xState(api, info);
        }

        var parameters = await vapix.ListParametersAsync([ParameterGroup], ct).ConfigureAwait(false);
        var dot1x = FromParameters(parameters);
        if (dot1x is not null)
        {
            dot1x = await WithCertSetAsync(vapix, dot1x, ct).ConfigureAwait(false);
        }

        return new Dot1xState(Dot1xApiKind.ParametersAndCertSet, new DeviceNetworkInfo(null, null, [], dot1x));
    }

    /// <summary>The 802.1X settings of the parameters (no network settings API); null when the device has none.</summary>
    public static Wired8021X? FromParameters(IReadOnlyDictionary<string, string> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        string? Get(string name) => parameters.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
        if (Get(EnabledParameter) is not { } enabled)
        {
            return null;
        }

        var eapol = Get(EapolParameter);
        return new Wired8021X(
            "eth0",
            string.Equals(enabled, "yes", StringComparison.OrdinalIgnoreCase),
            Get(StatusParameter),
            NetworkInfoApi.EapTlsMode, // the only method the parameters set
            Get(IdentityParameter),
            eapol is not null && int.TryParse(eapol, NumberStyles.None, CultureInfo.InvariantCulture, out var version) ? NetworkInfoApi.EapolName(version) : eapol,
            null,
            []);
    }

    /// <summary>Enables EAP-TLS: the certificates first (certificate set ways), then identity, EAPOL version and on.</summary>
    public static async Task EnableAsync(IVapixClient vapix, Dot1xState state, IReadOnlyList<DeviceApi> apis, string identity, int eapolVersion, string certClient, IReadOnlyList<string> certsCa, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(state);
        var deviceName = state.Dot1x?.DeviceName ?? "eth0";
        switch (state.Api)
        {
            case Dot1xApiKind.NetworkSettings:
                await NetworkInfoApi.EnableEapTlsAsync(vapix, apis, deviceName, identity, eapolVersion, certClient, certsCa, ct).ConfigureAwait(false);
                break;
            case Dot1xApiKind.NetworkSettingsAndCertSet:
                await BindAsync(vapix, [certClient], certsCa, "Set 802.1X configuration", ct).ConfigureAwait(false);
                await NetworkInfoApi.EnableEapTlsAsync(vapix, apis, deviceName, identity, eapolVersion, null, null, ct).ConfigureAwait(false);
                break;
            default:
                await BindAsync(vapix, [certClient], certsCa, "Set 802.1X configuration", ct).ConfigureAwait(false);
                await UpdateParametersAsync(vapix,
                [
                    (IdentityParameter, identity),
                    (EapolParameter, eapolVersion.ToString(CultureInfo.InvariantCulture)),
                    (EnabledParameter, "yes"),
                ], "Set 802.1X configuration", ct).ConfigureAwait(false);
                break;
        }
    }

    /// <summary>
    /// Sets only the CA certificates the device checks the RADIUS server with; on, identity, EAPOL version and the client
    /// certificate stay as <paramref name="current"/> has them.
    /// </summary>
    public static async Task SetCaCertificatesAsync(IVapixClient vapix, Dot1xState state, IReadOnlyList<DeviceApi> apis, IReadOnlyList<string> certsCa, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(state);
        var current = state.Dot1x ?? throw new PkiDeviceException("Set the RADIUS server CA: the device does not offer IEEE 802.1X.");
        if (state.Api == Dot1xApiKind.NetworkSettings)
        {
            await NetworkInfoApi.SetCaCertificatesAsync(vapix, apis, current, certsCa, ct).ConfigureAwait(false);
            return;
        }

        await BindAsync(vapix, null, certsCa, "Set the RADIUS server CA", ct).ConfigureAwait(false);
    }

    /// <summary>Turns 802.1X off; certificates and the rest of the configuration stay.</summary>
    public static Task DisableAsync(IVapixClient vapix, Dot1xState state, IReadOnlyList<DeviceApi> apis, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.Api == Dot1xApiKind.ParametersAndCertSet
            ? UpdateParametersAsync(vapix, [(EnabledParameter, "no")], "Set 802.1X off", ct)
            : NetworkInfoApi.DisableAsync(vapix, apis, state.Dot1x?.DeviceName ?? "eth0", ct);
    }

    /// <summary>The <c>param.cgi action=update</c> form body (values escaped, never in the URL).</summary>
    public static string UpdateBody(IReadOnlyList<(string Name, string Value)> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var builder = new StringBuilder("action=update");
        foreach (var (name, value) in values)
        {
            builder.Append('&').Append(Uri.EscapeDataString(name)).Append('=').Append(Uri.EscapeDataString(value));
        }

        return builder.ToString();
    }

    private static async Task<Wired8021X> WithCertSetAsync(IVapixClient vapix, Wired8021X dot1x, CancellationToken ct)
    {
        var set = await CertSoapApi.GetCertSetAsync(vapix, CertSoapApi.WiredDot1xSet, ct).ConfigureAwait(false);
        return dot1x with { CertClient = set.Certificates.Count > 0 ? set.Certificates[0] : null, CertsCa = set.CaCertificates };
    }

    /// <summary>SetCertSet EAPTLS_WIRED: the client certificate (null: as the device has it) and the CA certificates; trusted ones as read.</summary>
    private static async Task BindAsync(IVapixClient vapix, IReadOnlyList<string>? certificates, IReadOnlyList<string> certsCa, string action, CancellationToken ct)
    {
        var current = await CertSoapApi.GetCertSetAsync(vapix, CertSoapApi.WiredDot1xSet, ct).ConfigureAwait(false);
        await CertSoapApi.SetCertSetAsync(vapix, CertSoapApi.WiredDot1xSet, new CertSet(certificates ?? current.Certificates, certsCa, current.TrustedCertificates), action, ct).ConfigureAwait(false);
    }

    private static async Task UpdateParametersAsync(IVapixClient vapix, IReadOnlyList<(string Name, string Value)> values, string action, CancellationToken ct)
    {
        var answer = await DeviceHttp.SendAsync(vapix, HttpMethod.Post, ParamPath, "application/x-www-form-urlencoded", UpdateBody(values), ct).ConfigureAwait(false);
        if ((int)answer.Status is < 200 or >= 300)
        {
            throw new PkiDeviceException($"{action}: {DeviceHttp.DescribeHttp(answer.Status)}") { HttpStatus = (int)answer.Status };
        }

        if (!answer.Body.TrimStart().StartsWith("OK", StringComparison.OrdinalIgnoreCase))
        {
            throw new PkiDeviceException($"{action}: the device refused: {DeviceHttp.FirstLine(answer.Body.Replace("# Error:", string.Empty, StringComparison.Ordinal))}");
        }
    }
}
