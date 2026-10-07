using Oadm.Sdk.Devices;

namespace Oadm.Core.Vapix;

/// <summary>Network related device properties shown in the device grid.</summary>
/// <param name="DhcpEnabled">Network.BootProto == dhcp.</param>
/// <param name="HttpsEnabled">HTTPS.Enabled, null when the device does not report it.</param>
/// <param name="Dot1xEnabled">Network.Interface.I0.dot1x.Enabled, null when not reported.</param>
/// <param name="UpnpFriendlyName">Network.UPnP.FriendlyName, null when not reported.</param>
public sealed record NetworkInfo(bool? DhcpEnabled, bool? HttpsEnabled, bool? Dot1xEnabled, string? UpnpFriendlyName);

/// <summary>Result of systemready.cgi (anonymous).</summary>
/// <param name="SystemReady">Device finished booting.</param>
/// <param name="NeedSetup">True when no admin user exists yet (factory default), see <c>needsetup</c>.</param>
/// <param name="PassphrasePolicy">"none", "length" or "complex"; null on firmware that does not report it.</param>
/// <param name="UptimeSeconds">Uptime in seconds, when reported.</param>
/// <param name="BootId">Changes on every boot; useful to detect a completed restart.</param>
public sealed record SystemReadyInfo(bool SystemReady, bool? NeedSetup, string? PassphrasePolicy, long? UptimeSeconds, string? BootId);

/// <summary>Outcome of an anonymous probe against one address.</summary>
/// <param name="Address">Host or IP that was probed.</param>
/// <param name="Scheme">"https" or "http", whichever answered.</param>
/// <param name="Serial">Serial number (MAC, upper hex, no separators).</param>
/// <param name="Model">ProdNbr when the device reveals it anonymously.</param>
/// <param name="FirmwareVersion">Version when the device reveals it anonymously.</param>
/// <param name="IsFactoryDefault">True when the device has no admin password yet (systemready needsetup).</param>
/// <param name="AuthenticationRequired">True when getAllProperties answered 401.</param>
/// <param name="CertificateFingerprint">SHA-256 of the TLS certificate, null for HTTP.</param>
/// <param name="Status">Status derived from the probe (Ok, CredentialsRequired, PasswordNotSet).</param>
public sealed record VapixProbeResult(
    string Address,
    string Scheme,
    string Serial,
    string? Model,
    string? FirmwareVersion,
    bool IsFactoryDefault,
    bool AuthenticationRequired,
    string? CertificateFingerprint,
    DeviceStatus Status)
{
    public Uri BaseAddress => VapixClient.BuildBaseAddress(Scheme, Address);
}
