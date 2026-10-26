using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Oadm.Plugins.DeviceIds;
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
/// <param name="IssuedDer">The certificate issued (DER): kept with its enrollment for the page's certificate details.</param>
public sealed record EstResult(HttpResponse Response, string Operation, string Profile, Enrollment? Enrollment = null, IssuedCertificate? Issued = null, byte[]? IssuedDer = null);

/// <summary>Texts of refusals, the same in the HTTP answer, the log and the Enrollments list.</summary>
public static class EstRefusals
{
    public const string NotPro = "OADM PRO is not active.";
    public const string NoCa = "The PKI has no certificate authority.";

    /// <summary>The CA of the profile's issuing policy cannot issue (no key, gone from the list).</summary>
    public const string CaCannotIssue = "The CA of this profile cannot issue certificates.";

    /// <summary>The server in a sentence of its status ("The EST server has no certificate: ...").</summary>
    public const string ServerName = "The EST server";
    public const string NoIdentity = "The camera did not sign in with its Axis device ID.";
    public const string NotDeviceId = "The camera's certificate is not a valid Axis device ID or an EST certificate.";
    public const string NotManaged = "OADM does not manage this camera.";
    public const string OtherSite = "The camera belongs to another site.";
    public const string OtherCamera = "The camera signed in with the certificate of another camera.";
    public const string BadRequest = "The certificate request cannot be read.";
    public const string NoPolicy = EstTexts.NoPolicy;

    /// <summary>No Enrollment policy of PKI holds the camera (docs/spec/pki.md "Policies").</summary>
    public const string NoEnrollmentPolicy = "No enrollment policy for this device.";
}

/// <summary>
/// The EST operations (RFC 7030) of one request: <c>GET /.well-known/est/&lt;profile&gt;/cacerts</c>,
/// <c>POST .../simpleenroll</c> and <c>POST .../simplereenroll</c>; everything else and unknown or disabled profiles 404.
/// Who may enroll (docs/spec/est-server.md, docs/spec/pki.md "Policies"): the camera signs in with a certificate of a CA a
/// Sign-in CA policy names (its Axis device ID, or a certificate the OADM CA issued it), its serial number is a device OADM
/// manages (at a site: a device of that site), and an Enrollment policy holds it and allows the profile.
/// </summary>
public sealed partial class EstEndpoint(
    EstStore store,
    AxisDeviceId deviceIds,
    Func<string, CancellationToken, Task<IDeviceInfo?>> findBySerial,
    Func<IDeviceCertificateAuthority?> authority,
    Func<bool> proActive,
    TimeProvider? time = null,
    Func<Guid, CancellationToken, Task<bool>>? isManaged = null,
    Func<IDeviceInfo, bool>? inAnyGroup = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>What a profile issues with (its policy's valid days); null: the policy cannot be read, the request is refused (fail closed).</summary>
    public Func<EstProfile, ProfileIssuing?> IssuingOf { get; init; } = p => new ProfileIssuing(p.ValidityDays, p.Key, null);

    /// <summary>PKI's EST policies now (Sign-in CA, Enrollment); without them nothing signs in and no device enrolls.</summary>
    public Func<EstRules> Rules { get; init; } = () => EstRules.Closed(Oadm.Plugins.Pki.Policies.PolicyTexts.NotRunning);

    // Without the host's groups every device counts as covered (fail closed): no host name the camera reports.
    private readonly Func<IDeviceInfo, bool> _inAnyGroup = inAnyGroup ?? (_ => true);

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

        // The CA of the profile's issuing policy (decision 2026-10-25: any CA with its key); its chain is what cacerts answers.
        if (authority() is not { } ca)
        {
            return new EstResult(Unavailable(EstRefusals.NoCa), operation, profile.Name);
        }

        if (Oadm.Plugins.Pki.Ca.IssuerChains.Of(ca, IssuingOf(profile)?.CaId) is not { Count: > 0 } issuerChain)
        {
            return new EstResult(Unavailable(EstRefusals.CaCannotIssue), operation, profile.Name);
        }

        if (operation == "cacerts")
        {
            return request.Method == "GET"
                ? new EstResult(Certificates(EstMessages.CaCertsContentType, issuerChain), operation, profile.Name)
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
        // A 401 (no or no valid identity) is not recorded: anything on the network can cause it, it would push out the real
        // enrollments.
        EstResult Refuse(int status, string reason, IDeviceInfo? device = null, string? serial = null) =>
            new(HttpResponse.Text(status, reason), operation, profile.Name,
                status == 401 ? null : Record(device, serial ?? origin.Remote?.ToString() ?? "-", profile, kind, ok: false, reason));

        if (identity.Certificate is not { } client)
        {
            return Refuse(401, EstRefusals.NoIdentity);
        }

        var rules = Rules();
        var serial = await SerialOfAsync(client, identity.Presented, ca, rules.SignInCas(kind), kind, ct).ConfigureAwait(false);
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

        // PKI's Enrollment policies: none holds the device (403), or one that holds it does not allow the profile: the
        // answer of an unknown profile (no probing), listed in Enrollments with the policy that refused it.
        if (rules.Check(device, profile.Name) is { Allowed: false } check)
        {
            return check.Status == 404
                ? new EstResult(NotFound(), operation, profile.Name, Record(device, device.Address, profile, kind, ok: false, check.Reason ?? string.Empty))
                : Refuse(check.Status, check.Reason ?? EstRefusals.NoEnrollmentPolicy, device);
        }

        // A certificate on PKI's revocation list renews nothing (docs/spec/radius-server.md "Certificates and revocation"):
        // the Axis device ID is never on it (OADM did not issue it), so Set up still gives the camera a new certificate.
        if (ca.IsRevoked(client.SerialNumber))
        {
            return Refuse(403, Oadm.Plugins.Pki.Ca.NotAcceptedTexts.Of(ca.FindRevocation(client.SerialNumber)), device);
        }

        // A certificate EST issued signs in only for its own device. A record of a device OADM no longer manages (the camera
        // was removed and added again: a new device id) is forgotten.
        if (store.FindCertificate(client.SerialNumber) is { } own && own.DeviceId != device.Id)
        {
            if (isManaged is null || await isManaged(own.DeviceId, ct).ConfigureAwait(false))
            {
                return Refuse(403, EstRefusals.OtherCamera, device);
            }

            store.ForgetDevice(own.DeviceId);
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

        if (IssuingOf(profile) is not { } issuing)
        {
            return Refuse(503, EstRefusals.NoPolicy, device);
        }

        IssuedDeviceCertificate issued;
        try
        {
            issued = await ca.SignAsync(
                new DeviceCertificateRequest(csr, CommonName(device), IpAddresses(device, origin), DnsNames(device, _inAnyGroup(device)), profile.CertificateUse)
                {
                    DeviceId = device.Id,
                    Validity = TimeSpan.FromDays(issuing.ValidDays),
                    Source = IssuedCertificateSources.Est,
                    Purpose = profile.Purpose,
                    IssuerId = issuing.CaId,
                },
                ct).ConfigureAwait(false);
        }
        catch (CertificateRequestRefusedException ex)
        {
            return Refuse(400, ex.Message, device);
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException or FormatException)
        {
            return Refuse(400, EstRefusals.BadRequest, device); // e.g. an unsupported key: an answer, never a dropped connection
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
            Fingerprint = Convert.ToHexString(SHA256.HashData(issued.Certificate)),
        };

        // Into the store only once the answer is written (EstService): an undelivered one would push out the camera's older one.
        var reason = "Valid until " + issued.NotAfterUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var response = Certificates(EstMessages.CertsOnlyContentType, [issued.Certificate, .. issued.Chain]);
        return new EstResult(response, operation, profile.Name, Record(device, device.Address, profile, kind, ok: true, reason) with { SerialNumber = issued.SerialNumber },
            certificate, issued.Certificate);
    }

    /// <summary>
    /// The camera's serial, in this order: its Axis device ID (an EST sign-in CA policy names it for this request); for a
    /// renewal its current certificate when OADM issued exactly that one (decision 2026-10-25: PKI's store gives the camera,
    /// whatever CA of PKI issued it, also an imported one; <see cref="Oadm.Plugins.Pki.Ca.ICurrentCertificates"/>); else a
    /// certificate of an OADM CA the EST sign-in CA policies for this request (<paramref name="chosen"/>) name, PKI still
    /// holds (fail closed), by its axis name. All lookups O(1).
    /// </summary>
    private async Task<string?> SerialOfAsync(X509Certificate2 client, IReadOnlyList<X509Certificate2> presented, IDeviceCertificateAuthority ca, IReadOnlySet<string> chosen,
        EnrollmentKind kind, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        var authorities = ca.Authorities;
        var legacy = authorities.Count == 0; // a PKI without the list: the Axis device ID and the OADM CA as before
        if (chosen.Contains(CertificateAuthorityInfo.AxisDeviceIdId) && (legacy || authorities.Any(a => a.Kind == CertificateAuthorityKind.AxisDeviceId))
            && deviceIds.Verify(client, presented, now) is { } serial)
        {
            return serial;
        }

        // A renewal with the camera's current certificate (valid now): the store entry names the camera, not the certificate.
        if (kind == EnrollmentKind.Renewal && ca is Oadm.Plugins.Pki.Ca.ICurrentCertificates current
            && client.NotBefore.ToUniversalTime() <= now.UtcDateTime && now.UtcDateTime <= client.NotAfter.ToUniversalTime()
            && await current.CurrentCameraAsync(client.RawData, ct).ConfigureAwait(false) is { } camera)
        {
            return camera;
        }

        var issuers = legacy
            ? chosen.Contains(CertificateAuthorityInfo.OadmId) ? [ca.Chain] : []
            : authorities.Where(a => a.Kind != CertificateAuthorityKind.AxisDeviceId && chosen.Contains(a.Id)).Select(a => a.Certificates).ToList(); // the sign-in CA policies name only CAs made in OADM
        // A certificate created, signed or imported on the PKI page is never a camera's, whatever it names (fail closed).
        return issuers.Any(chain => IssuedByCa(client, chain, now)) && ca.PurposeOf(client.SerialNumber) != Oadm.Plugins.Pki.Ca.PkiCertificateAuthority.NotDevicePurpose
            ? SerialFromAxisName(client)
            : null;
    }

    /// <summary>The one camera serial of the certificate's <c>axis-&lt;serial&gt;.local</c> names; null for none or several.</summary>
    private static string? SerialFromAxisName(X509Certificate2 client)
    {

        // Any valid certificate the OADM CA itself issued for a camera, also one whose record is gone (the store keeps the
        // newest two per camera and profile; a camera whose answers got lost renews with an older one). Its serial: the one
        // axis-<serial>.local name OADM put in. A certificate naming two serials (a camera that reported another camera's
        // axis name as its host name) names none. The caller still checks that the camera is managed and, with a record,
        // that the record names the same device.
        var san = client.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault();
        var serials = (san?.EnumerateDnsNames() ?? []).Select(AxisLocalSerial).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return serials.Count == 1 ? serials[0] : null;
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
            // Issued by the OADM CA itself (element 1), never by another CA below a root above an imported OADM CA.
            var ok = chain.Build(certificate) && chain.ChainElements.Count >= 2
                && chain.ChainElements[1].Certificate.RawData.AsSpan().SequenceEqual(chainDer[0]);
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

    /// <summary>
    /// The device's address (host part) like PKI's certificates. For a device a user limited to a group added, a name
    /// address is never taken (docs/spec/device-scope.md "Host names in certificates"): its <c>axis-&lt;serial&gt;.local</c>.
    /// </summary>
    internal static string CommonName(IDeviceInfo device)
    {
        var host = HostPart(device.Address);
        return device.AddedByAllDevices || IPAddress.TryParse(host, out _) ? host : AxisLocalNameOf(device);
    }

    private static string AxisLocalNameOf(IDeviceInfo device) => "axis-" + device.Serial.ToLowerInvariant() + ".local";

    internal static IReadOnlyList<string> IpAddresses(IDeviceInfo device, RequestOrigin origin)
    {
        var addresses = new List<string>();
        if (IPAddress.TryParse(HostPart(device.Address), out var stored))
        {
            addresses.Add(stored.ToString());
        }

        // Only the address OADM stores for the device: never the address a connection came from (a camera could take
        // another camera's address for a moment and get a certificate naming it).

        return addresses;
    }

    /// <summary>The serial of an <c>axis-&lt;12 hex&gt;.local</c> name (upper case), else null.</summary>
    internal static string? AxisLocalSerial(string name) => AxisLocalName().Match(name) is { Success: true } match ? match.Groups[1].Value.ToUpperInvariant() : null;

    /// <summary>A name that looks like another camera's (<c>axis-&lt;12 hex&gt;</c>, any domain) is never taken from the camera.</summary>
    internal static bool NamesOtherCamera(string name, string serial) =>
        AxisNamePrefix().Match(name) is { Success: true } match && !string.Equals(match.Groups[1].Value, serial, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The DNS names of the device's certificate (docs/spec/device-scope.md "Host names in certificates"): names only for a
    /// device a user with all devices added; the host name the camera reports only when no device group covers it
    /// (<paramref name="inAnyGroup"/>: a user of the group could change it on the camera), else only the name address.
    /// </summary>
    internal static IReadOnlyList<string> DnsNames(IDeviceInfo device, bool inAnyGroup)
    {
        var names = new List<string>();
        if (!device.AddedByAllDevices)
        {
            // Added by a user limited to a group, who could control the DNS at a site: no host name, only the axis name.
            names.Add(AxisLocalNameOf(device));
            return names;
        }

        // The camera reports its host name itself: only plain names (no wildcard, no odd characters) go into a certificate
        // every OADM device trusts, never another camera's axis name.
        if (!inAnyGroup && IsPlainHostName(device.HostName) && !NamesOtherCamera(device.HostName!.Trim(), device.Serial))
        {
            names.Add(device.HostName!.Trim());
        }

        var host = HostPart(device.Address);
        if (!IPAddress.TryParse(host, out _) && IsPlainHostName(host) && !NamesOtherCamera(host, device.Serial) && !names.Contains(host, StringComparer.OrdinalIgnoreCase))
        {
            names.Add(host);
        }

        names.Add(AxisLocalNameOf(device));
        return names;
    }

    /// <summary>Letters, digits and hyphens in labels of 1 to 63 characters, dot separated, at most 253: no "*", no spaces.</summary>
    internal static bool IsPlainHostName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var trimmed = name.Trim().TrimEnd('.');
        return trimmed.Length <= 253 && !IPAddress.TryParse(trimmed, out _)
            && trimmed.Split('.').All(label => label.Length is >= 1 and <= 63 && !label.StartsWith('-') && !label.EndsWith('-')
                && label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'));
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

    [System.Text.RegularExpressions.GeneratedRegex("^axis-([0-9a-fA-F]{12})\\.local\\.?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex AxisLocalName();

    [System.Text.RegularExpressions.GeneratedRegex("^axis-([0-9a-fA-F]{12})(\\.|$)", System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex AxisNamePrefix();
}
