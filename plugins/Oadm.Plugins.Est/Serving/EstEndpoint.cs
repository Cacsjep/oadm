using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Oadm.Plugins.Est.DeviceId;
using Oadm.Plugins.Est.Protocol;
using Oadm.Sdk.Certificates;
using Oadm.Sdk.Devices;

namespace Oadm.Plugins.Est.Serving;

/// <summary>The client certificate of the TLS connection and the intermediates the camera sent; null certificate = none.</summary>
public sealed record ClientIdentity(X509Certificate2? Certificate, IReadOnlyList<X509Certificate2> Presented)
{
    public static ClientIdentity None { get; } = new(null, []);
}

/// <summary>Where a request came from: the site (null = this server's network) and the camera's address.</summary>
public sealed record RequestOrigin(Guid? SiteId, IPAddress? Remote);

/// <summary>The answer and what the page lists of it.</summary>
public sealed record EstResult(HttpResponse Response, string Operation, string Profile, Enrollment? Enrollment = null, IssuedCertificate? Issued = null);

/// <summary>Texts of refusals, the same in the HTTP answer, the log and the Enrollments list.</summary>
public static class EstRefusals
{
    public const string NotPro = "OADM PRO is not active.";
    public const string NoCa = "The PKI has no certificate authority.";
    public const string NoIdentity = "The camera did not sign in with its Axis device ID.";
    public const string NotDeviceId = "The camera's certificate is not a valid Axis device ID or an EST certificate.";
    public const string NotManaged = "OADM does not manage this camera.";
    public const string OtherSite = "The camera belongs to another site.";
    public const string Revoked = "The certificate was revoked. Set up the camera again.";
    public const string BadRequest = "The certificate request cannot be read.";
}

/// <summary>
/// The EST operations (RFC 7030) of one request: <c>GET /.well-known/est/&lt;profile&gt;/cacerts</c>,
/// <c>POST .../simpleenroll</c> and <c>POST .../simplereenroll</c>; everything else and unknown or disabled profiles 404.
/// Who may enroll (docs/spec/est-server.md): the camera signs in with its Axis device ID, or with a certificate EST issued
/// it that is not revoked, and its serial number is a device OADM manages (at a site: a device of that site).
/// </summary>
public sealed class EstEndpoint(
    EstStore store,
    AxisDeviceId deviceIds,
    Func<string, CancellationToken, Task<IDeviceInfo?>> findBySerial,
    Func<IDeviceCertificateAuthority?> authority,
    Func<bool> proActive,
    TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public async Task<EstResult> HandleAsync(HttpRequest request, ClientIdentity identity, RequestOrigin origin, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(origin);
        if (!request.Path.StartsWith(EstPluginInfo.PathPrefix, StringComparison.Ordinal))
        {
            return new EstResult(NotFound(), "-", "-");
        }

        var rest = request.Path[EstPluginInfo.PathPrefix.Length..].Split('/');
        if (rest.Length != 2)
        {
            return new EstResult(NotFound(), "-", "-");
        }

        var (profileName, operation) = (rest[0], rest[1]);
        var profile = store.Config.Profiles.FirstOrDefault(p => p.Enabled && string.Equals(p.Name, profileName, StringComparison.OrdinalIgnoreCase));
        if (profile is null || operation is not ("cacerts" or "simpleenroll" or "simplereenroll"))
        {
            return new EstResult(NotFound(), operation, profileName);
        }

        if (!proActive())
        {
            return new EstResult(Unavailable(EstRefusals.NotPro), operation, profile.Name);
        }

        if (authority() is not { IsReady: true } ca)
        {
            return new EstResult(Unavailable(EstRefusals.NoCa), operation, profile.Name);
        }

        if (operation == "cacerts")
        {
            return request.Method == "GET"
                ? new EstResult(Certificates(EstMessages.CaCertsContentType, ca.Chain), operation, profile.Name)
                : new EstResult(HttpResponse.Text(405, "Use GET."), operation, profile.Name);
        }

        if (request.Method != "POST")
        {
            return new EstResult(HttpResponse.Text(405, "Use POST."), operation, profile.Name);
        }

        var kind = operation == "simplereenroll" ? EnrollmentKind.Renewal : EnrollmentKind.New;
        return await EnrollAsync(request, identity, origin, profile, kind, ca, ct).ConfigureAwait(false);
    }

    private async Task<EstResult> EnrollAsync(HttpRequest request, ClientIdentity identity, RequestOrigin origin, EstProfile profile, EnrollmentKind kind,
        IDeviceCertificateAuthority ca, CancellationToken ct)
    {
        var operation = kind == EnrollmentKind.Renewal ? "simplereenroll" : "simpleenroll";
        EstResult Refuse(int status, string reason, IDeviceInfo? device = null, string? serial = null) =>
            new(HttpResponse.Text(status, reason), operation, profile.Name, Record(device, serial ?? origin.Remote?.ToString() ?? "-", profile, kind, ok: false, reason));

        if (identity.Certificate is not { } client)
        {
            return Refuse(401, EstRefusals.NoIdentity);
        }

        var serial = SerialOf(client, identity.Presented, ca);
        if (serial is null)
        {
            return Refuse(401, EstRefusals.NotDeviceId);
        }

        var device = await findBySerial(serial, ct).ConfigureAwait(false);
        if (device is null)
        {
            return Refuse(403, EstRefusals.NotManaged, serial: serial);
        }

        if (device.SiteId != origin.SiteId)
        {
            return Refuse(403, EstRefusals.OtherSite, device);
        }

        if (store.IsRevoked(device.Id, profile.Name))
        {
            return Refuse(403, EstRefusals.Revoked, device);
        }

        // A certificate EST issued signs in only for its own device and while it is not revoked.
        if (store.FindCertificate(client.SerialNumber) is { } own && (own.DeviceId != device.Id || own.Revoked))
        {
            return Refuse(403, EstRefusals.Revoked, device);
        }

        byte[] csr;
        try
        {
            csr = EstMessages.ReadRequest(request.Body);
        }
        catch (FormatException)
        {
            return Refuse(400, EstRefusals.BadRequest, device);
        }

        IssuedDeviceCertificate issued;
        try
        {
            issued = await ca.SignAsync(
                new DeviceCertificateRequest(csr, CommonName(device), IpAddresses(device, origin), DnsNames(device), profile.CertificateUse)
                {
                    DeviceId = device.Id,
                    Validity = TimeSpan.FromDays(profile.ValidityDays),
                    Source = "EST",
                },
                ct).ConfigureAwait(false);
        }
        catch (CertificateRequestRefusedException ex)
        {
            return Refuse(400, ex.Message, device);
        }

        var now = _time.GetUtcNow().UtcDateTime;
        var certificate = new IssuedCertificate
        {
            SerialNumber = issued.SerialNumber,
            DeviceId = device.Id,
            Device = device.Address,
            Profile = profile.Name,
            IssuedUtc = now,
            NotAfterUtc = issued.NotAfterUtc,
        };
        store.AddCertificate(certificate);
        var reason = "Valid until " + issued.NotAfterUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var response = Certificates(EstMessages.CertsOnlyContentType, [issued.Certificate, .. issued.Chain]);
        return new EstResult(response, operation, profile.Name, Record(device, device.Address, profile, kind, ok: true, reason), certificate);
    }

    /// <summary>The camera's serial: from its Axis device ID, or from the EST certificate it signs in with.</summary>
    private string? SerialOf(X509Certificate2 client, IReadOnlyList<X509Certificate2> presented, IDeviceCertificateAuthority ca)
    {
        var now = _time.GetUtcNow();
        if (deviceIds.Verify(client, presented, now) is { } serial)
        {
            return serial;
        }

        if (store.FindCertificate(client.SerialNumber) is not { } own || !IssuedByCa(client, ca.Chain, now))
        {
            return null;
        }

        // The serial of the device it was issued for: its axis-<serial>.local name.
        var san = client.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault();
        var name = san?.EnumerateDnsNames().FirstOrDefault(n => n.StartsWith("axis-", StringComparison.OrdinalIgnoreCase) && n.EndsWith(".local", StringComparison.OrdinalIgnoreCase));
        return own.DeviceId != Guid.Empty ? AxisDeviceId.Normalize(name) : null;
    }

    private static bool IssuedByCa(X509Certificate2 certificate, IReadOnlyList<byte[]> chainDer, DateTimeOffset now)
    {
        if (chainDer.Count == 0)
        {
            return false;
        }

        var authorities = new X509Certificate2Collection();
        try
        {
            foreach (var der in chainDer)
            {
                authorities.Add(X509CertificateLoader.LoadCertificate(der));
            }

            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.AddRange(authorities);
            chain.ChainPolicy.ExtraStore.AddRange(authorities);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.DisableCertificateDownloads = true;
            chain.ChainPolicy.VerificationTime = now.UtcDateTime;
            var ok = chain.Build(certificate);
            foreach (var element in chain.ChainElements)
            {
                if (!ReferenceEquals(element.Certificate, certificate))
                {
                    element.Certificate.Dispose();
                }
            }

            return ok;
        }
        catch (CryptographicException)
        {
            return false;
        }
        finally
        {
            foreach (var authority in authorities)
            {
                authority.Dispose();
            }
        }
    }

    /// <summary>The device's address (host part) like PKI's certificates.</summary>
    internal static string CommonName(IDeviceInfo device) => HostPart(device.Address);

    internal static IReadOnlyList<string> IpAddresses(IDeviceInfo device, RequestOrigin origin)
    {
        var addresses = new List<string>();
        if (IPAddress.TryParse(HostPart(device.Address), out var stored))
        {
            addresses.Add(stored.ToString());
        }

        // The address the camera connected from, when it reached the server directly (behind a site the node's NAT may differ).
        if (origin.SiteId is null && origin.Remote is { } remote)
        {
            var text = (remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote).ToString();
            if (!addresses.Contains(text))
            {
                addresses.Add(text);
            }
        }

        return addresses;
    }

    internal static IReadOnlyList<string> DnsNames(IDeviceInfo device)
    {
        var names = new List<string>();
        if (!string.IsNullOrWhiteSpace(device.HostName) && !IPAddress.TryParse(device.HostName, out _))
        {
            names.Add(device.HostName.Trim());
        }

        if (!IPAddress.TryParse(HostPart(device.Address), out _) && !names.Contains(HostPart(device.Address), StringComparer.OrdinalIgnoreCase))
        {
            names.Add(HostPart(device.Address));
        }

        names.Add("axis-" + device.Serial.ToLowerInvariant() + ".local");
        return names;
    }

    private static string HostPart(string address)
    {
        var trimmed = address.Trim();
        if (IPAddress.TryParse(trimmed.Trim('[', ']'), out var ip))
        {
            return ip.ToString();
        }

        var text = trimmed.Contains("://", StringComparison.Ordinal) ? trimmed : "http://" + trimmed;
        return Uri.TryCreate(text, UriKind.Absolute, out var uri) ? uri.Host.Trim('[', ']') : trimmed;
    }

    private Enrollment Record(IDeviceInfo? device, string who, EstProfile profile, EnrollmentKind kind, bool ok, string reason) => new()
    {
        TimeUtc = _time.GetUtcNow().UtcDateTime,
        DeviceId = device?.Id,
        Device = device?.Address ?? who,
        Profile = profile.Name,
        Kind = kind,
        Ok = ok,
        Reason = reason,
    };

    private static HttpResponse Certificates(string contentType, IEnumerable<byte[]> certificates) =>
        new(200, contentType, EstMessages.Base64(EstMessages.CertsOnly(certificates)))
        {
            Headers = [new("Content-Transfer-Encoding", "base64")],
        };

    private static HttpResponse NotFound() => HttpResponse.Text(404, "Not found.");

    private static HttpResponse Unavailable(string reason) =>
        HttpResponse.Text(503, reason) with { Headers = [new("Retry-After", "600")] };
}
