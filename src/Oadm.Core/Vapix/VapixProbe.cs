using System.Net;
using System.Text;

using Oadm.Sdk.Devices;

namespace Oadm.Core.Vapix;

/// <summary>
/// Anonymous probe used by the IP range scan and the add wizard. Tries HTTPS first and falls
/// back to HTTP when 443 does not answer. Never sends credentials.
/// </summary>
public sealed class VapixProbe
{
    /// <summary>Default per-request timeout from the spec (Scan.TimeoutMs).</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMilliseconds(1500);

    private static readonly string[] Schemes = [Uri.UriSchemeHttps, Uri.UriSchemeHttp];

    private readonly Func<Uri, CertificatePinning, HttpMessageHandler> _handlerFactory;
    private readonly TimeSpan _timeout;

    /// <param name="timeout">Per-request timeout, default 1500 ms.</param>
    /// <param name="handlerFactory">Creates the HTTP handler per scheme; tests inject fakes. Default: anonymous <see cref="HttpClientHandler"/>.</param>
    public VapixProbe(TimeSpan? timeout = null, Func<Uri, CertificatePinning, HttpMessageHandler>? handlerFactory = null)
    {
        _timeout = timeout ?? DefaultTimeout;
        _handlerFactory = handlerFactory ?? (static (uri, pinning) => VapixClient.CreateHandler(uri, null, pinning));
    }

    /// <summary>
    /// Probes one address. Returns null when nothing answers or the answer is not an Axis device.
    /// Steps: basicdeviceinfo getAllProperties (401 realm AXIS_&lt;serial&gt; or anonymous 200),
    /// then best effort getAllUnrestrictedProperties (model, firmware) and systemready (needsetup).
    /// </summary>
    public async Task<VapixProbeResult?> ProbeAsync(string address, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);

        foreach (var scheme in Schemes)
        {
            ct.ThrowIfCancellationRequested();
            var baseAddress = VapixClient.BuildBaseAddress(scheme, address);
            var pinning = new CertificatePinning();
            using var client = new VapixClient(baseAddress, _handlerFactory(baseAddress, pinning), pinning, _timeout);

            HttpResponseMessage response;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, VapixClient.BasicDeviceInfoPath)
                {
                    Content = new StringContent("""{"apiVersion":"1.0","method":"getAllProperties"}""", Encoding.UTF8, "application/json"),
                };
                response = await client.SendAsync(request, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && IsTransportFailure(ex))
            {
                continue; // closed port, timeout or TLS failure: try the next scheme
            }

            using (response)
            {
                return await InterpretAsync(client, address, scheme, response, ct).ConfigureAwait(false);
            }
        }

        return null;
    }

    private static async Task<VapixProbeResult?> InterpretAsync(VapixClient client, string address, string scheme, HttpResponseMessage response, CancellationToken ct)
    {
        string? serial;
        string? model = null;
        string? firmware = null;
        bool authRequired;

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            serial = VapixParsers.ParseSerialFromRealm(response.Headers.WwwAuthenticate.Select(h => h.ToString()));
            if (serial is null)
            {
                return null; // some other device asking for credentials
            }

            authRequired = true;
        }
        else if (response.IsSuccessStatusCode)
        {
            try
            {
                var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var info = VapixParsers.ParseBasicDeviceInfo(json);
                serial = info.SerialNumber;
                model = info.ProdNbr;
                firmware = info.Version;
            }
            catch (VapixException)
            {
                return null;
            }

            authRequired = false;
        }
        else
        {
            return null;
        }

        if (model is null)
        {
            try
            {
                var props = await client.GetUnrestrictedPropertiesAsync(ct).ConfigureAwait(false);
                model = props.GetValueOrDefault("ProdNbr");
                firmware = props.GetValueOrDefault("Version");
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && IsBestEffortFailure(ex))
            {
                // older firmware: model stays unknown until credentials are known
            }
        }

        var factoryDefault = false;
        try
        {
            var ready = await client.GetSystemReadyAsync(ct).ConfigureAwait(false);
            factoryDefault = ready.NeedSetup == true;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && IsBestEffortFailure(ex))
        {
            // systemready missing on old firmware
        }

        var status = factoryDefault
            ? DeviceStatus.PasswordNotSet
            : authRequired ? DeviceStatus.CredentialsRequired : DeviceStatus.Ok;

        return new VapixProbeResult(
            Address: address,
            Scheme: scheme,
            Serial: VapixParsers.NormalizeSerial(serial),
            Model: string.IsNullOrEmpty(model) ? null : model,
            FirmwareVersion: string.IsNullOrEmpty(firmware) ? null : firmware,
            IsFactoryDefault: factoryDefault,
            AuthenticationRequired: authRequired,
            CertificateFingerprint: client.CertificateFingerprint,
            Status: status);
    }

    private static bool IsTransportFailure(Exception ex)
    {
        return ex is HttpRequestException or TaskCanceledException or TimeoutException or IOException;
    }

    private static bool IsBestEffortFailure(Exception ex)
    {
        return IsTransportFailure(ex) || ex is VapixException;
    }
}
