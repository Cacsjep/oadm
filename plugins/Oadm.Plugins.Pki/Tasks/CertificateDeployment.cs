using Oadm.Plugins.DeviceClocks;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Oadm.Plugins.Pki.Ca;
using Oadm.Plugins.Pki.Device;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Pki.Tasks;

/// <summary>Step names of the PKI tasks (one device request or wait per step).</summary>
public static class PkiSteps
{
    public const string CheckCompatibility = "Check compatibility";
    public const string ReadWebServer = "Read web server settings";
    public const string ReadNetwork = "Read network settings";
    public const string InstallCa = "Install CA certificate";
    public const string InstallCas = "Install CA certificates";
    public const string CreateKey = "Create key on the device";
    public const string GetCsr = "Get certificate request";
    public const string Sign = "Sign certificate";
    public const string InstallCertificate = "Install certificate";
    public const string SwitchWebServer = "Switch web server to the new certificate";
    public const string VerifyHttps = "Verify HTTPS";
    public const string RemovePrevious = "Remove previous OADM certificate";
    public const string SetHttpOnly = "Set HTTP only";
    public const string Verify = "Verify";
    public const string CheckClock = "Check device clock";
    public const string SetDot1x = "Set 802.1X configuration";
    public const string VerifyDot1x = "Verify 802.1X settings";
    public const string SetDot1xOff = "Set 802.1X off";
    public const string ReadCertificates = "Read certificates";
    public const string ReadFile = "Read certificate file";

    /// <summary>"HTTPS: Create key on the device" (renewal of several purposes).</summary>
    public static string Prefixed(string? prefix, string step) => prefix is null ? step : prefix + ": " + step;
}

/// <summary>A certificate OADM signed and installed on a device.</summary>
public sealed record IssuedOnDevice(string Alias, string Fingerprint, string SerialNumber, DateTime NotAfterUtc);

/// <summary>Device state the HTTPS / 802.1X switches need.</summary>
public sealed record DeviceCertificateState(
    IReadOnlyList<DeviceCertificate> Certificates,
    WebServerTlsConfiguration? WebServer,
    DeviceNetworkInfo? Network);

/// <summary>
/// The shared parts of the certificate tasks: the CA check, names, the key-on-device issuing flow (create key -> CSR ->
/// sign -> install), CA certificates, the web server switch with OADM following the new certificate, the 802.1X
/// configuration and the removal of previous OADM certificates. Every device request is its own step.
/// </summary>
public static class CertificateDeployment
{
    public const string HttpsAliasPrefix = "OADM HTTPS ";
    public const string Dot1xAliasPrefix = "OADM 802.1X ";
    public const string CaAliasPrefix = "OADM CA ";
    public const string RadiusCaAliasPrefix = "OADM RADIUS CA ";
    public const string ImportAliasPrefix = "OADM import ";


    /// <summary>How long OADM waits for the web server to answer with the new setting.</summary>
    public static readonly TimeSpan SwitchTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Wait between tries while the web server restarts.</summary>
    public static readonly TimeSpan SwitchRetryDelay = TimeSpan.FromSeconds(3);

    /// <summary>The running service; throws with the user text otherwise.</summary>
    public static PkiService RequireService(PkiService? service) =>
        service ?? throw new InvalidOperationException("The PKI plugin is not running. Nothing was changed.");

    /// <summary>The running service and the OADM CA with its key; throws with the user text otherwise.</summary>
    public static (PkiService Service, CaMaterial Ca) RequireCa(PkiService? service) => (RequireService(service), RequireCa(RequireService(service), null));

    /// <summary>
    /// The CA with its key that issues for <paramref name="policy"/> ("Issued by", decision 2026-10-25: the OADM CA or an
    /// imported CA with its key; no policy: the OADM CA), valid now; throws with the user text otherwise (before any write).
    /// </summary>
    public static CaMaterial RequireCa(PkiService service, Policies.PkiPolicy? policy)
    {
        ArgumentNullException.ThrowIfNull(service);
        var oadm = policy?.CaId is null || string.Equals(policy.CaId, Oadm.Sdk.Certificates.CertificateAuthorityInfo.OadmId, StringComparison.OrdinalIgnoreCase);
        var ca = service.IssuingCa(policy?.CaId)
            ?? throw new InvalidOperationException(oadm
                ? "The PKI has no usable certificate authority (see the PKI page). Nothing was changed."
                : $"The CA of the policy {policy!.Name} cannot issue certificates. Choose another CA in the policy on the PKI page. Nothing was changed.");
        if (service.Time.GetUtcNow().UtcDateTime > ca.Certificate.NotAfter.ToUniversalTime())
        {
            throw new InvalidOperationException(oadm
                ? "The certificate authority has expired. Use New OADM CA on the PKI page. Nothing was changed."
                : $"The CA of the policy {policy!.Name} has expired. Choose another CA in the policy on the PKI page. Nothing was changed.");
        }

        return ca;
    }

    /// <summary>"OADM HTTPS 20261007-120000".</summary>
    public static string NewAlias(string prefix, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        return prefix + time.GetUtcNow().UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
    }

    /// <summary>"OADM CA 3FA2B4C1" (first 8 hex characters of the fingerprint).</summary>
    public static string CaAlias(string prefix, string fingerprint) => prefix + fingerprint[..Math.Min(8, fingerprint.Length)];

    public static string Fingerprint(string pem)
    {
        using var certificate = X509Certificate2.CreateFromPem(pem);
        return CaCertificates.Fingerprint(certificate);
    }

    /// <summary>
    /// Names for the device certificate (docs/spec/device-scope.md "Host names in certificates"): a name address only for a
    /// device a user with all devices added; the host name, FQDN and IP addresses the device reports only when, in addition
    /// (names) or alone (addresses), no device group covers the device (<paramref name="inAnyGroup"/>: a user of the group
    /// could change them on the camera). A covered device gets its stored address and <c>axis-&lt;serial&gt;.local</c>.
    /// </summary>
    public static CertificateNames NamesFor(IDeviceInfo device, DeviceNetworkInfo? network, bool inAnyGroup)
    {
        ArgumentNullException.ThrowIfNull(device);
        return inAnyGroup
            ? CertificateNames.For(device.Address, device.Serial, null, null, [], device.AddedByAllDevices)
            : CertificateNames.For(device.Address, device.Serial, network?.HostName ?? device.HostName, network?.Fqdn, network?.Addresses ?? [], device.AddedByAllDevices);
    }

    /// <summary>The CA certificate and the issuers above it, with OADM aliases.</summary>
    public static IReadOnlyList<(string Alias, string Pem, string Fingerprint)> CaChain(CaMaterial ca)
    {
        ArgumentNullException.ThrowIfNull(ca);
        return [.. new[] { ca.Certificate }.Concat(ca.Chain).Select(c =>
        {
            var fingerprint = CaCertificates.Fingerprint(c);
            return (CaAlias(CaAliasPrefix, fingerprint), CaCertificates.ToPem(c), fingerprint);
        })];
    }

    /// <summary>True when the CA is a self-signed root or its chain ends in one (802.1X needs the full chain).</summary>
    public static bool ChainComplete(CaMaterial ca)
    {
        ArgumentNullException.ThrowIfNull(ca);
        var top = ca.Chain.Count > 0 ? ca.Chain[^1] : ca.Certificate;
        return CaCertificates.IsSelfIssued(top);
    }

    /// <summary>
    /// Installs the CA certificates the device does not have yet (matched by fingerprint, so an existing alias is reused) in
    /// one step; Skipped "Already installed" when nothing was missing. Returns the device aliases of all of them.
    /// </summary>
    public static async Task<IReadOnlyList<string>> InstallCaCertificatesAsync(
        ITaskExecutionContext ctx, string stepName, IReadOnlyList<(string Alias, string Pem, string Fingerprint)> wanted, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        using var step = ctx.BeginStep(stepName);
        var installed = await CertApi.ListCaCertificatesAsync(ctx.Vapix, ct).ConfigureAwait(false);
        var byFingerprint = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var certificate in installed)
        {
            try
            {
                byFingerprint.TryAdd(Fingerprint(certificate.Pem), certificate.Alias);
            }
            catch (CryptographicException)
            {
                // an unreadable entry cannot be the one we need
            }
        }

        var aliases = new List<string>();
        var added = new List<string>();
        foreach (var (alias, pem, fingerprint) in wanted)
        {
            if (byFingerprint.TryGetValue(fingerprint, out var existing))
            {
                aliases.Add(existing);
                continue;
            }

            await CertApi.AddCaCertificateAsync(ctx.Vapix, alias, pem, ct).ConfigureAwait(false);
            byFingerprint[fingerprint] = alias;
            aliases.Add(alias);
            added.Add(alias);
        }

        if (added.Count == 0)
        {
            step.Skip("Already installed");
        }
        else
        {
            step.Complete(string.Join(", ", added));
            ctx.Log(TaskLogLevel.Info, "CA certificates installed: " + string.Join(", ", added) + ".");
        }

        return aliases;
    }

    /// <summary>
    /// Create key on the device, Get certificate request, Sign certificate, Install certificate. The key never leaves the
    /// device. The signed certificate is added to the issued registry. When a step after the key creation fails, the
    /// unfinished key is removed again (best effort).
    /// </summary>
    public static async Task<IssuedOnDevice> IssueAsync(
        ITaskExecutionContext ctx,
        PkiService service,
        CaMaterial ca,
        IDeviceInfo device,
        CertificateNames names,
        string purpose,
        string alias,
        Policies.PkiPolicy policy,
        string? stepPrefix,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(policy);
        var sans = names.DeviceSubjectAltNames;
        var keyType = CertApi.KeyTypeOf(policy.Key);
        await ctx.StepAsync(PkiSteps.Prefixed(stepPrefix, PkiSteps.CreateKey), async step =>
        {
            await CertApi.CreateCertificateAsync(ctx.Vapix, alias, names.Subject, sans, keyType, ct).ConfigureAwait(false);
            step.Complete($"{keyType} key \"{alias}\"");
        }).ConfigureAwait(false);

        var installed = false;
        string? pendingSerial = null;
        try
        {
            var csr = await ctx.StepAsync(PkiSteps.Prefixed(stepPrefix, PkiSteps.GetCsr), async step =>
            {
                var pem = await CertApi.GetCsrAsync(ctx.Vapix, alias, names.Subject, sans, ct).ConfigureAwait(false);
                step.Complete("Received");
                return pem;
            }).ConfigureAwait(false);

            using var signed = await ctx.StepAsync(PkiSteps.Prefixed(stepPrefix, PkiSteps.Sign), step =>
            {
                var certificate = DeviceCertificateIssuer.Sign(ca, csr, names, purpose, service.Time.GetUtcNow(), service.DeviceCertificateValidity(policy.ValidDays, ca));
                step.Complete($"{names.CommonName}, valid until {certificate.NotAfter.ToUniversalTime():yyyy-MM-dd}");
                return Task.FromResult(certificate);
            }).ConfigureAwait(false);

            var (fingerprint, serial, notAfter) = DeviceCertificateIssuer.Describe(signed);

            // Pending until the task's verify step confirms it (docs/spec/pki.md "Current and pending").
            await service.Issued.AddPendingAsync(new IssuedCertificate
            {
                SerialNumber = serial,
                DeviceId = device.Id,
                DeviceSerial = device.Serial,
                Purpose = purpose,
                CaId = ca.Id,
                NotBeforeUtc = signed.NotBefore.ToUniversalTime(),
                NotAfterUtc = notAfter,
                IssuedUtc = service.Time.GetUtcNow().UtcDateTime,
                Alias = alias,
            }, signed.RawData, ct).ConfigureAwait(false);
            pendingSerial = serial;
            await ctx.StepAsync(PkiSteps.Prefixed(stepPrefix, PkiSteps.InstallCertificate), async step =>
            {
                await CertApi.PatchCertificateAsync(ctx.Vapix, alias, CaCertificates.ToPem(signed), ct).ConfigureAwait(false);
                installed = true;
                step.Complete(alias);
            }).ConfigureAwait(false);

            ctx.Log(TaskLogLevel.Info, $"Certificate \"{alias}\" issued by {CaCertificates.CommonName(ca.Certificate)} with the policy {policy.Name} (serial {serial}, valid until {notAfter:yyyy-MM-dd}).");
            return new IssuedOnDevice(alias, fingerprint, serial, notAfter);
        }
        catch (Exception) when (!installed)
        {
            if (pendingSerial is not null)
            {
                await NotUsedAsync(ctx, service, pendingSerial, evenDelivered: false).ConfigureAwait(false);
            }

            try
            {
                await CertApi.DeleteCertificateAsync(ctx.Vapix, alias, CancellationToken.None).ConfigureAwait(false);
                ctx.Log(TaskLogLevel.Info, $"The unfinished key \"{alias}\" was removed again.");
            }
#pragma warning disable CA1031 // Best effort cleanup; the original error is what the user sees.
            catch (Exception)
#pragma warning restore CA1031
            {
                ctx.Log(TaskLogLevel.Warning, $"The unfinished key \"{alias}\" could not be removed; delete it with \"Delete certificates\".");
            }

            throw;
        }
    }

    /// <summary>
    /// The verify step passed: the new certificate becomes the camera's current one; the previous one of the same use is
    /// revoked (replaced) and leaves the store. Written to the task log.
    /// </summary>
    public static async Task ConfirmAsync(ITaskExecutionContext ctx, PkiService service, IDeviceInfo device, IssuedOnDevice issued, string purpose)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(issued);
        var previous = service.Issued.ForDevice(device)
            .Where(e => !e.Pending && string.Equals(e.Purpose, purpose, StringComparison.Ordinal) && service.Issued.IsCurrent(e.SerialNumber))
            .Select(e => e.SerialNumber).FirstOrDefault();
        await service.Issued.ConfirmAsync(issued.SerialNumber, CancellationToken.None).ConfigureAwait(false);
        if (previous is not null && service.Issued.IsRevoked(previous))
        {
            ctx.Log(TaskLogLevel.Info, $"The previous certificate (serial {previous}) was revoked: replaced by \"{issued.Alias}\".");
        }
    }

    /// <summary>
    /// The new certificate is not in use (an install or verify failed): revoked as unused and removed from the store. An IEEE
    /// 802.1X certificate a task configured stays unless <paramref name="evenDelivered"/> (device safety: no lock-out).
    /// </summary>
    public static async Task NotUsedAsync(ITaskExecutionContext ctx, PkiService service, string serialNumber, bool evenDelivered)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(service);
        try
        {
            if (await service.Issued.DiscardAsync(serialNumber, evenDelivered, CancellationToken.None).ConfigureAwait(false))
            {
                ctx.Log(TaskLogLevel.Info, $"The new certificate (serial {serialNumber}) is not in use and was revoked.");
            }
        }
#pragma warning disable CA1031 // The task's own error is what the user sees; an unused pending certificate is never accepted as current.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ctx.Log(TaskLogLevel.Warning, "The unused certificate could not be revoked: " + ex.Message);
        }
    }

    /// <summary>
    /// Switches the web server to <paramref name="alias"/> (SetWebServerTlsConfiguration, everything else as read; an HTTP-only
    /// policy becomes HTTP and HTTPS, because Enable must enable).
    /// </summary>
    public static async Task SwitchWebServerAsync(ITaskExecutionContext ctx, WebServerTlsConfiguration current, string alias, string stepName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(current);
        await ctx.StepAsync(stepName, async step =>
        {
            var policy = ConnectionPolicy.IsHttpOnly(current.Policy) || string.IsNullOrEmpty(current.Policy) ? ConnectionPolicy.HttpAndHttps : current.Policy!;
            await WebServerTls.SetAsync(ctx.Vapix, current with { Tls = true, Policy = policy, Certificates = [alias] }, ct).ConfigureAwait(false);
            step.Complete($"{alias} · {ConnectionPolicy.Describe(policy)}");
        }).ConfigureAwait(false);
        ctx.Log(TaskLogLevel.Info, $"The web server presents \"{alias}\" (previously \"{current.CertificateAlias ?? "none"}\").");
    }

    /// <summary>
    /// OADM connects with <paramref name="scheme"/> and, for https, trusts exactly <paramref name="fingerprint"/>
    /// (<c>ITaskExecutionContext.UpdateDeviceTlsAsync</c>), retrying while the web server restarts.
    /// </summary>
    public static async Task FollowWebServerAsync(ITaskExecutionContext ctx, string scheme, string? fingerprint, TimeProvider time, string stepName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(time);
        await ctx.StepAsync(stepName, async step =>
        {
            var https = string.Equals(scheme, "https", StringComparison.OrdinalIgnoreCase);
            var end = time.GetUtcNow() + SwitchTimeout;
            while (true)
            {
                try
                {
                    await ctx.UpdateDeviceTlsAsync(scheme, fingerprint, ct).ConfigureAwait(false);
                    break;
                }
                catch (DeviceIdentityException ex) when (time.GetUtcNow() + SwitchRetryDelay < end)
                {
                    step.ReportProgress(0, "Waiting for the web server: " + ex.Message);
                    await Task.Delay(SwitchRetryDelay, time, ct).ConfigureAwait(false);
                }
                catch (DeviceIdentityException ex)
                {
                    throw new InvalidOperationException(
                        (https ? "The device does not present the new certificate: " : "The device does not answer over HTTP: ") + ex.Message, ex);
                }
            }

            step.Complete(https ? "The device presents the new certificate; OADM trusts it and connects over HTTPS" : "HTTP answers; OADM connects over HTTP");
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes earlier OADM certificates of the same purpose (alias prefix and a registry entry for this device, or the alias
    /// prefix and an issuer among the OADM CAs) that nothing uses any more; never certificates OADM did not issue.
    /// </summary>
    public static async Task RemovePreviousAsync(
        ITaskExecutionContext ctx,
        PkiService service,
        IDeviceInfo device,
        string purpose,
        IReadOnlyCollection<string> inUse,
        string stepName,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(device);
        using var step = ctx.BeginStep(stepName);
        var certificates = await CertApi.ListCertificatesAsync(ctx.Vapix, ct).ConfigureAwait(false);
        var registry = await service.Issued.ForDeviceAsync(device, ct).ConfigureAwait(false);
        var prefix = string.Equals(purpose, CertificatePurpose.Dot1x, StringComparison.Ordinal) ? Dot1xAliasPrefix : HttpsAliasPrefix;
        var removed = new List<string>();
        foreach (var certificate in certificates)
        {
            if (inUse.Contains(certificate.Alias, StringComparer.Ordinal) || !certificate.Alias.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var serial = SerialOf(certificate.Pem);
            if (!IsFromOadm(certificate, serial, registry, service))
            {
                continue;
            }

            // Deleted from the device: a replaced one is revoked already; any other stays in the store (not listed) until it
            // ends, so it can still be revoked.
            await CertApi.DeleteCertificateAsync(ctx.Vapix, certificate.Alias, ct).ConfigureAwait(false);
            removed.Add(certificate.Alias);
        }

        if (removed.Count == 0)
        {
            step.Skip("No previous OADM certificate");
        }
        else
        {
            step.Complete(string.Join(", ", removed));
            ctx.Log(TaskLogLevel.Info, "Previous OADM certificates removed: " + string.Join(", ", removed) + ".");
        }
    }

    /// <summary>OADM issued it: its serial is in the device's registry entries, or OADM's alias with an OADM CA as issuer.</summary>
    public static bool IsFromOadm(DeviceCertificate certificate, string? serial, IReadOnlyList<IssuedCertificate> registry, PkiService? service)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        ArgumentNullException.ThrowIfNull(registry);
        if (serial is not null && registry.Any(e => string.Equals(Oadm.Sdk.Certificates.CertificateSerials.Normalize(e.SerialNumber), Oadm.Sdk.Certificates.CertificateSerials.Normalize(serial), StringComparison.Ordinal)))
        {
            return true;
        }

        // A replaced one left the store but stays on the revocation list until it ends.
        if (serial is not null && service is not null && service.Issued.IsRevoked(serial)
            && (certificate.Alias.StartsWith(HttpsAliasPrefix, StringComparison.Ordinal) || certificate.Alias.StartsWith(Dot1xAliasPrefix, StringComparison.Ordinal)))
        {
            return true;
        }

        if (!certificate.Alias.StartsWith(HttpsAliasPrefix, StringComparison.Ordinal)
            && !certificate.Alias.StartsWith(Dot1xAliasPrefix, StringComparison.Ordinal)
            && !certificate.Alias.StartsWith(ImportAliasPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        return service is not null && IssuedByKnownCa(certificate.Pem, service);
    }

    /// <summary>The certificate's issuer is a CA OADM issues with: the OADM CA or an imported CA with its key (name).</summary>
    public static bool IssuedByKnownCa(string pem, PkiService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        var cas = service.IssuingCas();
        if (cas.Count == 0)
        {
            return false;
        }

        try
        {
            using var certificate = X509Certificate2.CreateFromPem(pem);
            return cas.Any(ca => certificate.IssuerName.RawData.AsSpan().SequenceEqual(ca.Certificate.SubjectName.RawData));
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>Serial number (upper hex) of a PEM certificate, or null.</summary>
    public static string? SerialOf(string pem)
    {
        try
        {
            using var certificate = X509Certificate2.CreateFromPem(pem);
            return certificate.SerialNumber.ToUpperInvariant();
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>
    /// The device clock against the server clock (docs/spec/pki.md "Clock check"): a device ahead is fine; one still behind the
    /// certificate's start (5 min back) by more than 5 s fails with "Nothing was changed" unless the user answered the
    /// dialog's question with Yes (<paramref name="confirmed"/>, the payload's <c>clockConfirmed</c>): then it goes on and
    /// says when the certificate becomes valid on the device.
    /// </summary>
    public static async Task CheckClockAsync(ITaskExecutionContext ctx, IReadOnlyList<DeviceApi> apis, TimeProvider time, bool confirmed, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(time);
        await ctx.StepAsync(PkiSteps.CheckClock, async step =>
        {
            var deviceNow = await DeviceClock.ReadAsync(ctx.Vapix, apis, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException(CameraClock.Unreadable);
            var behind = time.GetUtcNow() - deviceNow;
            if (CameraClock.NeedsQuestion(behind))
            {
                if (!confirmed)
                {
                    throw new InvalidOperationException(CameraClock.Refusal(behind));
                }

                ctx.Log(TaskLogLevel.Warning, CameraClock.Late(behind) + " The user chose to continue.");
                step.Complete($"{CameraClock.Describe(behind)} behind, valid on the device in {CameraClock.Describe(CameraClock.ValidIn(behind))}");
                return;
            }

            step.Complete(behind.Duration() < TimeSpan.FromSeconds(1) ? "In sync with the server"
                : behind < TimeSpan.Zero ? $"{CameraClock.Describe(behind)} ahead of the server clock" : $"{CameraClock.Describe(behind)} behind the server clock");
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// The EAP identity of an IEEE 802.1X certificate policy: MAC (= serial), host name, IP address, the last octet of the IP
    /// address, or custom with {serial} / {hostName}.
    /// </summary>
    public static string Identity(Policies.PkiPolicy policy, IDeviceInfo device, DeviceNetworkInfo? network)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(device);
        var hostName = network?.HostName ?? device.HostName ?? device.Serial;
        return policy.Identity switch
        {
            Dot1xIdentity.HostName => hostName,
            Dot1xIdentity.IpAddress => device.Address,
            Dot1xIdentity.IpLastOctet => LastOctet(device.Address),
            Dot1xIdentity.Custom when !string.IsNullOrWhiteSpace(policy.CustomIdentity) =>
                policy.CustomIdentity.Replace("{serial}", device.Serial, StringComparison.OrdinalIgnoreCase).Replace("{hostName}", hostName, StringComparison.OrdinalIgnoreCase),
            _ => device.Serial,
        };
    }

    /// <summary>"48" of 10.0.0.48; the last group of an IPv6 address; the address itself when it is no IP address.</summary>
    internal static string LastOctet(string address)
    {
        if (!System.Net.IPAddress.TryParse(address, out var ip))
        {
            return address;
        }

        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return ip.GetAddressBytes()[3].ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        var text = ip.ToString();
        return text[(text.LastIndexOf(':') + 1)..] is { Length: > 0 } last ? last : text;
    }

    /// <summary>
    /// The CA certificates the device uses to check the RADIUS server: the CA of the list the policy names (decision
    /// 2026-10-25) and the certificates above it that PKI knows.
    /// </summary>
    public static IReadOnlyList<(string Alias, string Pem, string Fingerprint)> RadiusCas(Policies.PkiPolicy policy, PkiService service)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(service);
        var chain = service.Authorities().FirstOrDefault(a => a.Kind != Oadm.Sdk.Certificates.CertificateAuthorityKind.PreviousOadm
            && string.Equals(a.Id, policy.RadiusCaId, StringComparison.OrdinalIgnoreCase))?.Certificates ?? [];
        if (chain.Count == 0)
        {
            throw new InvalidOperationException($"The policy {policy.Name} names no CA of the RADIUS server that is in the CA list. Choose one in the policy. Nothing was changed.");
        }

        List<(string Alias, string Pem, string Fingerprint)> cas = [.. chain.Select(der =>
        {
            using var certificate = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadCertificate(der);
            var fingerprint = CaCertificates.Fingerprint(certificate);
            return (CaAlias(CaAliasPrefix, fingerprint), CaCertificates.ToPem(certificate), fingerprint);
        })];
        RequireServerIssuer(policy, service, cas);
        return cas;
    }

    /// <summary>
    /// Device safety (no lock-out): when OADM's 802.1X server has a certificate, the cameras must get the CA that issued it,
    /// else they refuse OADM's own 802.1X server (after New OADM CA the server keeps its certificate from the previous CA).
    /// Fails closed when PKI does not know that CA.
    /// </summary>
    private static void RequireServerIssuer(Policies.PkiPolicy policy, PkiService service, List<(string Alias, string Pem, string Fingerprint)> cas)
    {
        if (service.Policies.FirstOf(Policies.PolicyKinds.Dot1xServer)?.CertificateId is null)
        {
            return;
        }

        var issuer = service.ServerIssuerChain(Policies.PolicyKinds.Dot1xServer);
        if (issuer.Count == 0)
        {
            throw new InvalidOperationException(
                "The CA that issued the 802.1X server's certificate is not in the CA list. Choose another certificate in the 802.1X server policy. Nothing was changed.");
        }

        var fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(issuer[0]));
        if (!cas.Any(c => string.Equals(c.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase)))
        {
            using var certificate = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadCertificate(issuer[0]);
            var choosable = service.Authorities().Any(a => a.Kind != Oadm.Sdk.Certificates.CertificateAuthorityKind.PreviousOadm
                && a.Certificates.Count > 0 && a.Certificates[0].AsSpan().SequenceEqual(issuer[0]));
            throw new InvalidOperationException(
                $"The 802.1X server's certificate is issued by {CaCertificates.CommonName(certificate)}, but the policy {policy.Name} gives the cameras another RADIUS server CA. "
                + (choosable ? "Choose that CA in the policy, or choose" : "Choose")
                + " a certificate of the policy's RADIUS server CA in the 802.1X server policy. Nothing was changed.");
        }
    }
}
