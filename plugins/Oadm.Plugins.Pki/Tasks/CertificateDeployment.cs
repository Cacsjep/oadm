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

    /// <summary>How long a device must keep answering over HTTP after a change to HTTP only (<see cref="FollowWebServerAsync"/>).</summary>
    public static readonly TimeSpan DefaultWebServerSettle = TimeSpan.FromSeconds(10);

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
                ? "The certificate authority has expired. Choose another CA in the policy on the PKI page. Nothing was changed."
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

    /// <summary>
    /// The chain a certificate of <paramref name="ca"/> goes to the camera with (decision 2026-10-26): an intermediate CA and
    /// the intermediates above it (PEM, never a root); a root CA: none (the certificate alone, as before).
    /// </summary>
    public static IReadOnlyList<string> IssuerChainPems(CaMaterial ca)
    {
        ArgumentNullException.ThrowIfNull(ca);
        return CaCertificates.IsSelfIssued(ca.Certificate)
            ? []
            : [.. new[] { ca.Certificate }.Concat(ca.Chain).Where(c => !CaCertificates.IsSelfIssued(c)).Select(CaCertificates.ToPem)];
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
        ITaskExecutionContext ctx, string stepName, IReadOnlyList<(string Alias, string Pem, string Fingerprint)> wanted, CertificateApiKind api, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        using var step = ctx.BeginStep(stepName);
        var installed = await DeviceCertificates.ListCaAsync(ctx.Vapix, api, ct).ConfigureAwait(false);
        var used = installed.Select(c => c.Alias).ToHashSet(StringComparer.Ordinal);
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
        foreach (var (wantedAlias, pem, fingerprint) in wanted)
        {
            if (byFingerprint.TryGetValue(fingerprint, out var existing))
            {
                aliases.Add(existing);
                continue;
            }

            // Another CA certificate may hold the alias already (the same first 8 hex characters of the fingerprint).
            var alias = FreeAlias(wantedAlias, used);
            await DeviceCertificates.AddCaAsync(ctx.Vapix, api, alias, pem, ct).ConfigureAwait(false);
            used.Add(alias);
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

    /// <summary>The alias, or "&lt;alias&gt;-2", "-3" ... when another certificate already uses it.</summary>
    public static string FreeAlias(string alias, IReadOnlySet<string> used)
    {
        ArgumentNullException.ThrowIfNull(used);
        var candidate = alias;
        for (var n = 2; used.Contains(candidate); n++)
        {
            candidate = string.Create(CultureInfo.InvariantCulture, $"{alias}-{n}");
        }

        return candidate;
    }

    /// <summary>
    /// The device can make the policy's key: on REST its default keystore lists the key type
    /// (<see cref="IssuingChoice.RequireKeyAsync"/>); the SOAP certificate service makes RSA 2048 only. Else "Nothing was changed".
    /// </summary>
    public static async Task RequireKeyAsync(Oadm.Sdk.Vapix.IVapixClient vapix, Policies.PkiPolicy policy, PkiDeviceSupport support, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(support);
        if (support.Certificates == CertificateApiKind.Rest)
        {
            await IssuingChoice.RequireKeyAsync(vapix, policy, ct).ConfigureAwait(false);
        }
        else if (CertApi.KeyTypeOf(policy.Key) != CertApi.KeyType)
        {
            throw new DeviceNotCompatibleException("The device cannot create an ECC key. Choose a policy with RSA. Nothing was changed.");
        }
    }

    /// <summary>
    /// Create key on the device, Get certificate request, Sign certificate, Install certificate. The key never leaves the
    /// device. The signed certificate is added to the issued registry. When a step after the key creation fails, the
    /// unfinished key is removed again (best effort). REST: the key and the certificate share the alias; SOAP: the key's
    /// placeholder is "&lt;alias&gt; key" until the signed certificate is loaded under the alias (<see cref="DeviceCertificates"/>).
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
        PkiDeviceSupport support,
        string? stepPrefix,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(support);
        var api = support.Certificates;
        var keyType = CertApi.KeyTypeOf(policy.Key);
        await ctx.StepAsync(PkiSteps.Prefixed(stepPrefix, PkiSteps.CreateKey), async step =>
        {
            await DeviceCertificates.CreateKeyAsync(ctx.Vapix, api, alias, names, keyType, service.Time.GetUtcNow(), ct).ConfigureAwait(false);
            step.Complete($"{keyType} key \"{DeviceCertificates.KeyAlias(api, alias)}\"");
        }).ConfigureAwait(false);

        var installed = false;
        string? pendingSerial = null;
        try
        {
            var csr = await ctx.StepAsync(PkiSteps.Prefixed(stepPrefix, PkiSteps.GetCsr), async step =>
            {
                var pem = await DeviceCertificates.GetCsrAsync(ctx.Vapix, api, alias, names, ct).ConfigureAwait(false);
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
                var chain = IssuerChainPems(ca);
                var result = await DeviceCertificates.InstallSignedAsync(ctx.Vapix, api, alias, CaCertificates.ToPem(signed), chain, ct).ConfigureAwait(false);
                installed = true;
                if (result.WithChain)
                {
                    ctx.Log(TaskLogLevel.Info, chain.Count == 1
                        ? $"Certificate \"{alias}\" installed with its chain (1 CA certificate)."
                        : string.Create(CultureInfo.InvariantCulture, $"Certificate \"{alias}\" installed with its chain ({chain.Count} CA certificates)."));
                }
                else if (result.ChainRefusal is { } refusal)
                {
                    ctx.Log(TaskLogLevel.Warning, $"The device refused the certificate with its chain ({refusal}); the certificate was installed alone.");
                }

                if (!result.Tidy)
                {
                    ctx.Log(TaskLogLevel.Warning, $"The key placeholder \"{DeviceCertificates.KeyAlias(api, alias)}\" could not be removed; delete it with \"Delete certificates\".");
                }

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
                await DeviceCertificates.DeleteUnfinishedKeyAsync(ctx.Vapix, api, alias, CancellationToken.None).ConfigureAwait(false);
                ctx.Log(TaskLogLevel.Info, $"The unfinished key \"{DeviceCertificates.KeyAlias(api, alias)}\" was removed again.");
            }
#pragma warning disable CA1031 // Best effort cleanup; the original error is what the user sees.
            catch (Exception)
#pragma warning restore CA1031
            {
                ctx.Log(TaskLogLevel.Warning, $"The unfinished key \"{DeviceCertificates.KeyAlias(api, alias)}\" could not be removed; delete it with \"Delete certificates\".");
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
    /// Enable HTTPS or Renew did not verify the new certificate. Before the switch request was sent
    /// (<paramref name="switching"/> false) it is revoked as unused. After it, the web server may present it after a slow
    /// restart or an answer lost after the device applied it: it is revoked only when a read of the web server settings shows
    /// another certificate; otherwise it stays pending (delivered) and the previous one stays valid. Never throws.
    /// </summary>
    public static async Task HttpsNotVerifiedAsync(ITaskExecutionContext ctx, PkiService service, IssuedOnDevice issued, bool switching)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(issued);
        if (!switching)
        {
            await NotUsedAsync(ctx, service, issued.SerialNumber, evenDelivered: false).ConfigureAwait(false);
            return;
        }

        WebServerTlsConfiguration? read = null;
        try
        {
            read = await WebServerTls.GetAsync(ctx.Vapix, CancellationToken.None).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Unreadable: the certificate may be in use, so it stays; the task's own error is what the user sees.
        catch (Exception)
#pragma warning restore CA1031
        {
            // kept below
        }

        if (read is not null && !read.Certificates.Contains(issued.Alias, StringComparer.Ordinal))
        {
            await NotUsedAsync(ctx, service, issued.SerialNumber, evenDelivered: true).ConfigureAwait(false);
            return;
        }

        ctx.Log(TaskLogLevel.Info, $"The new certificate \"{issued.Alias}\" stays pending: the web server may present it. The previous certificate stays valid.");
    }

    /// <summary>
    /// Switches the web server to <paramref name="alias"/> (SetWebServerTlsConfiguration, everything else as read; an HTTP-only
    /// policy becomes HTTP and HTTPS, because Enable must enable). <paramref name="allRoles"/> (AXIS OS before 7.10): the
    /// Operator and Viewer policies are sent too, each switched the same way.
    /// </summary>
    public static async Task SwitchWebServerAsync(ITaskExecutionContext ctx, WebServerTlsConfiguration current, string alias, bool allRoles, string stepName, CancellationToken ct, Action? switching = null)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(current);
        await ctx.StepAsync(stepName, async step =>
        {
            var wanted = (allRoles ? current.WithRoles() : current).WithPolicies(ConnectionPolicy.Enabled) with { Tls = true, Certificates = [alias] };
            switching?.Invoke(); // right before the request: the device may apply it even when its answer is lost
            await WebServerTls.SetAsync(ctx.Vapix, wanted, ct).ConfigureAwait(false);
            step.Complete($"{alias} · {ConnectionPolicy.Describe(wanted.Policy)}");
        }).ConfigureAwait(false);
        ctx.Log(TaskLogLevel.Info, $"The web server presents \"{alias}\" (previously \"{current.CertificateAlias ?? "none"}\").");
    }

    /// <summary>The step text while the web server restarts after a change.</summary>
    public const string WaitingForRestart = "Waiting for the web server to restart";

    /// <summary>What Enable HTTPS, Renew and Install changed when Verify HTTPS fails: "The web server was switched to ...".</summary>
    public static string SwitchedTo(string alias) => $"The web server was switched to the certificate \"{alias}\".";

    /// <summary>
    /// OADM connects with <paramref name="scheme"/> and, for https, trusts exactly <paramref name="fingerprint"/>
    /// (<c>ITaskExecutionContext.UpdateDeviceTlsAsync</c>), retrying while the web server restarts (at most
    /// <see cref="SwitchTimeout"/>, step text <see cref="WaitingForRestart"/>). HTTPS: the new certificate in the handshake
    /// shows that the restart happened. HTTP: the device answers right after the change and restarts its web server a few
    /// seconds later (AXIS OS 9.80 refuses connections meanwhile), so it must keep answering for <paramref name="settle"/>
    /// (ignored for https; a read every <see cref="SwitchRetryDelay"/>; a refused one starts the wait again). A device that does not come back
    /// fails the step with <paramref name="changed"/> (what the task changed on it) and how OADM connects now.
    /// </summary>
    public static async Task FollowWebServerAsync(ITaskExecutionContext ctx, string scheme, string? fingerprint, TimeProvider time, string stepName, string changed, TimeSpan settle, CancellationToken ct)
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
                catch (DeviceIdentityException) when (time.GetUtcNow() + SwitchRetryDelay < end)
                {
                    step.ReportProgress(0, WaitingForRestart);
                    await Task.Delay(SwitchRetryDelay, time, ct).ConfigureAwait(false);
                }
                catch (DeviceIdentityException ex)
                {
                    throw new InvalidOperationException(
                        (https ? "The device did not come back with the new certificate within 1 minute. " : "The device did not answer over HTTP within 1 minute. ")
                        + changed + " OADM still connects as before.", ex);
                }
            }

            if (!https && settle > TimeSpan.Zero)
            {
                DateTimeOffset? answering = time.GetUtcNow();
                while (answering is null || time.GetUtcNow() - answering < settle)
                {
                    if (time.GetUtcNow() + SwitchRetryDelay > end)
                    {
                        if (answering is not null)
                        {
                            break; // it answers again, only not for the whole settle time yet
                        }

                        throw new InvalidOperationException(
                            "The device stopped answering when its web server restarted and did not come back within 1 minute. " + changed + " OADM connects over HTTP.");
                    }

                    await Task.Delay(SwitchRetryDelay, time, ct).ConfigureAwait(false);
                    if (await AnswersAsync(ctx.Vapix, ct).ConfigureAwait(false))
                    {
                        answering ??= time.GetUtcNow();
                    }
                    else
                    {
                        answering = null;
                        step.ReportProgress(0, WaitingForRestart);
                    }
                }
            }

            step.Complete(https ? "The device presents the new certificate; OADM trusts it and connects over HTTPS" : "HTTP answers; OADM connects over HTTP");
        }).ConfigureAwait(false);
    }

    /// <summary>One cheap read (every AXIS OS has the parameter); false when the device does not answer.</summary>
    private static async Task<bool> AnswersAsync(IVapixClient vapix, CancellationToken ct)
    {
        try
        {
            _ = await vapix.ListParametersAsync([PkiCompatibility.FirmwareParameter], ct).ConfigureAwait(false);
            return true;
        }
#pragma warning disable CA1031 // Any failure while the web server restarts counts as "not answering"; cancellation passes.
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
#pragma warning restore CA1031
        {
            return false;
        }
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
        CertificateApiKind api,
        string stepName,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(device);
        using var step = ctx.BeginStep(stepName);
        var certificates = await DeviceCertificates.ListAsync(ctx.Vapix, api, ct).ConfigureAwait(false);
        var registry = await service.Issued.ForDeviceAsync(device, ct).ConfigureAwait(false);
        var prefix = string.Equals(purpose, CertificatePurpose.Dot1x, StringComparison.Ordinal) ? Dot1xAliasPrefix : HttpsAliasPrefix;
        var removed = new List<string>();
        var kept = new List<string>();
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
            // ends, so it can still be revoked. The new certificate is switched and confirmed already: a refused delete is a
            // warning (Done with warnings), never a failed task.
            try
            {
                await DeviceCertificates.DeleteAsync(ctx.Vapix, api, certificate.Alias, ct).ConfigureAwait(false);
                removed.Add(certificate.Alias);
            }
            catch (Exception ex) when (ex is PkiDeviceException or HttpRequestException)
            {
                kept.Add(certificate.Alias);
                ctx.Log(TaskLogLevel.Warning, $"The previous OADM certificate \"{certificate.Alias}\" could not be removed: {ex.Message} Delete it with \"Delete certificates\".");
            }
        }

        if (removed.Count > 0)
        {
            ctx.Log(TaskLogLevel.Info, "Previous OADM certificates removed: " + string.Join(", ", removed) + ".");
        }

        if (kept.Count > 0)
        {
            step.Warn((removed.Count > 0 ? "Removed " + string.Join(", ", removed) + ". " : string.Empty)
                + "Could not remove " + string.Join(", ", kept) + "; delete it with \"Delete certificates\".");
        }
        else if (removed.Count == 0)
        {
            step.Skip("No previous OADM certificate");
        }
        else
        {
            step.Complete(string.Join(", ", removed));
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
        var chain = service.Authorities().FirstOrDefault(a => string.Equals(a.Id, policy.RadiusCaId, StringComparison.OrdinalIgnoreCase))?.Certificates ?? [];
        if (chain.Count == 0)
        {
            throw new InvalidOperationException($"The policy {policy.Name} names no CA of the RADIUS server that is in the CA list. Choose one in the policy. Nothing was changed.");
        }

        return [.. chain.Select(der =>
        {
            using var certificate = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadCertificate(der);
            var fingerprint = CaCertificates.Fingerprint(certificate);
            return (CaAlias(CaAliasPrefix, fingerprint), CaCertificates.ToPem(certificate), fingerprint);
        })];
    }
}
