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

    /// <summary>Largest allowed difference between the device clock and the server clock before 802.1X.</summary>
    public static readonly TimeSpan MaxClockOffset = TimeSpan.FromMinutes(5);

    /// <summary>How long OADM waits for the web server to answer with the new setting.</summary>
    public static readonly TimeSpan SwitchTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Wait between tries while the web server restarts.</summary>
    public static readonly TimeSpan SwitchRetryDelay = TimeSpan.FromSeconds(3);

    /// <summary>The running service and its CA with key; throws with the user text otherwise.</summary>
    public static (PkiService Service, CaMaterial Ca) RequireCa(PkiService? service)
    {
        if (service is null)
        {
            throw new InvalidOperationException("The PKI plugin is not running. Nothing was changed.");
        }

        var ca = service.Ca;
        if (ca is null || !ca.HasKey)
        {
            throw new InvalidOperationException("The PKI has no usable certificate authority (see the PKI page). Nothing was changed.");
        }

        if (service.Time.GetUtcNow().UtcDateTime > ca.Certificate.NotAfter.ToUniversalTime())
        {
            throw new InvalidOperationException("The certificate authority has expired. Generate or import a new CA on the PKI page. Nothing was changed.");
        }

        return (service, ca);
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

    /// <summary>Names for the device certificate from the OADM address and the device's network info.</summary>
    public static CertificateNames NamesFor(IDeviceInfo device, DeviceNetworkInfo? network)
    {
        ArgumentNullException.ThrowIfNull(device);
        return CertificateNames.For(device.Address, device.Serial, network?.HostName ?? device.HostName, network?.Fqdn, network?.Addresses ?? []);
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
        string? stepPrefix,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(names);
        var sans = names.DeviceSubjectAltNames;
        await ctx.StepAsync(PkiSteps.Prefixed(stepPrefix, PkiSteps.CreateKey), async step =>
        {
            await CertApi.CreateCertificateAsync(ctx.Vapix, alias, names.Subject, sans, ct).ConfigureAwait(false);
            step.Complete($"{CertApi.KeyType} key \"{alias}\"");
        }).ConfigureAwait(false);

        var installed = false;
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
                var certificate = DeviceCertificateIssuer.Sign(ca, csr, names, purpose, service.Time.GetUtcNow(), service.DeviceCertificateValidity());
                step.Complete($"{names.CommonName}, valid until {certificate.NotAfter.ToUniversalTime():yyyy-MM-dd}");
                return Task.FromResult(certificate);
            }).ConfigureAwait(false);

            var (fingerprint, serial, notAfter) = DeviceCertificateIssuer.Describe(signed);
            await ctx.StepAsync(PkiSteps.Prefixed(stepPrefix, PkiSteps.InstallCertificate), async step =>
            {
                await CertApi.PatchCertificateAsync(ctx.Vapix, alias, CaCertificates.ToPem(signed), ct).ConfigureAwait(false);
                installed = true;
                step.Complete(alias);
            }).ConfigureAwait(false);

            await service.Issued.AddAsync(new IssuedCertificate
            {
                SerialNumber = serial,
                DeviceId = device.Id,
                Purpose = purpose,
                CaId = ca.Id,
                NotAfterUtc = notAfter,
                IssuedUtc = service.Time.GetUtcNow().UtcDateTime,
                Alias = alias,
            }, ct).ConfigureAwait(false);
            ctx.Log(TaskLogLevel.Info, $"Certificate \"{alias}\" issued by {CaCertificates.CommonName(ca.Certificate)} (serial {serial}, valid until {notAfter:yyyy-MM-dd}).");
            return new IssuedOnDevice(alias, fingerprint, serial, notAfter);
        }
        catch (Exception) when (!installed)
        {
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
        var registry = await service.Issued.ForDeviceAsync(device.Id, ct).ConfigureAwait(false);
        var prefix = string.Equals(purpose, CertificatePurpose.Dot1x, StringComparison.Ordinal) ? Dot1xAliasPrefix : HttpsAliasPrefix;
        var removed = new List<string>();
        var serials = new List<string>();
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

            await CertApi.DeleteCertificateAsync(ctx.Vapix, certificate.Alias, ct).ConfigureAwait(false);
            removed.Add(certificate.Alias);
            if (serial is not null)
            {
                serials.Add(serial);
            }
        }

        await service.Issued.RemoveAsync(device.Id, serials, ct).ConfigureAwait(false);
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
        if (serial is not null && registry.Any(e => string.Equals(e.SerialNumber, serial, StringComparison.OrdinalIgnoreCase)))
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

    /// <summary>The certificate's issuer is the active or a previous OADM CA (name and key identifier).</summary>
    public static bool IssuedByKnownCa(string pem, PkiService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        if (service.Ca is not { } ca)
        {
            return false;
        }

        try
        {
            using var certificate = X509Certificate2.CreateFromPem(pem);
            return certificate.IssuerName.RawData.AsSpan().SequenceEqual(ca.Certificate.SubjectName.RawData);
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

    /// <summary>The device clock against the server clock; throws beyond <see cref="MaxClockOffset"/> (802.1X safety).</summary>
    public static async Task CheckClockAsync(ITaskExecutionContext ctx, IReadOnlyList<DeviceApi> apis, TimeProvider time, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(time);
        await ctx.StepAsync(PkiSteps.CheckClock, async step =>
        {
            var deviceNow = await DeviceClock.ReadAsync(ctx.Vapix, apis, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The device clock cannot be read. Nothing was changed.");
            var offset = deviceNow - time.GetUtcNow();
            var text = DescribeOffset(offset);
            if (offset.Duration() > MaxClockOffset)
            {
                throw new InvalidOperationException($"The device clock is {text} off. Set the date and time first. Nothing was changed.");
            }

            step.Complete(offset.Duration() < TimeSpan.FromSeconds(1) ? "In sync with the server" : $"{text} off the server clock");
        }).ConfigureAwait(false);
    }

    /// <summary>"12 min", "3 s", "2 h".</summary>
    public static string DescribeOffset(TimeSpan offset)
    {
        var d = offset.Duration();
        return d.TotalHours >= 1 ? string.Create(CultureInfo.InvariantCulture, $"{(int)d.TotalHours} h")
            : d.TotalMinutes >= 1 ? string.Create(CultureInfo.InvariantCulture, $"{(int)d.TotalMinutes} min")
            : string.Create(CultureInfo.InvariantCulture, $"{(int)d.TotalSeconds} s");
    }

    /// <summary>The EAP identity from the page setting: MAC (= serial), host name, or custom with {serial} / {hostName}.</summary>
    public static string Identity(Dot1xConfig config, IDeviceInfo device, DeviceNetworkInfo? network)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(device);
        var hostName = network?.HostName ?? device.HostName ?? device.Serial;
        return config.Identity switch
        {
            Dot1xIdentity.HostName => hostName,
            Dot1xIdentity.Custom when !string.IsNullOrWhiteSpace(config.CustomIdentity) =>
                config.CustomIdentity.Replace("{serial}", device.Serial, StringComparison.OrdinalIgnoreCase).Replace("{hostName}", hostName, StringComparison.OrdinalIgnoreCase),
            _ => device.Serial,
        };
    }

    /// <summary>The CA certificates the device uses to check the RADIUS server: the imported one, or the OADM CA chain.</summary>
    public static IReadOnlyList<(string Alias, string Pem, string Fingerprint)> RadiusCas(PkiConfig config, CaMaterial ca)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (string.Equals(config.Dot1x.RadiusCa, RadiusCaSource.Imported, StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(config.Dot1x.RadiusCaPem))
            {
                throw new InvalidOperationException("The RADIUS server CA is set to an imported certificate, but none is imported (see the PKI page). Nothing was changed.");
            }

            var fingerprint = Fingerprint(config.Dot1x.RadiusCaPem);
            return [(CaAlias(RadiusCaAliasPrefix, fingerprint), config.Dot1x.RadiusCaPem, fingerprint)];
        }

        return CaChain(ca);
    }
}
