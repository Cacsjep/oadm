using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Oadm.Plugins.Pki.Ca;
using Oadm.Plugins.Pki.Device;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Pki.Tasks;

/// <summary>The certificates of one device with what uses them (read-only; the dialogs' query and the delete task).</summary>
public static class CertificateInventory
{
    public const string HttpsUse = "HTTPS";
    public const string Dot1xUse = "802.1X";

    /// <summary>
    /// Reads certificates, CA certificates, the web server certificate (SOAP) and the 802.1X configuration (network-settings,
    /// when <paramref name="apis"/> lists it). Read-only.
    /// </summary>
    public static async Task<IReadOnlyList<InstalledCertificate>> ReadAsync(IVapixClient vapix, IReadOnlyList<DeviceApi> apis, IReadOnlyList<IssuedCertificate> registry, PkiService? service, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(vapix);
        var certificates = await CertApi.ListCertificatesAsync(vapix, ct).ConfigureAwait(false);
        var cas = await CertApi.ListCaCertificatesAsync(vapix, ct).ConfigureAwait(false);
        WebServerTlsConfiguration? webServer = null;
        try
        {
            webServer = await WebServerTls.GetAsync(vapix, ct).ConfigureAwait(false);
        }
        catch (PkiDeviceException)
        {
            // no web server TLS service: nothing is "in use by HTTPS"
        }

        Wired8021X? dot1x = null;
        if (PkiCompatibility.HasNetworkSettings(apis))
        {
            try
            {
                dot1x = (await NetworkInfoApi.GetAsync(vapix, apis, ct).ConfigureAwait(false)).Dot1x;
            }
            catch (PkiDeviceException)
            {
                // unknown: nothing is "in use by 802.1X"
            }
        }

        return Describe(certificates, cas, webServer, dot1x, registry, service);
    }

    public static IReadOnlyList<InstalledCertificate> Describe(
        IReadOnlyList<DeviceCertificate> certificates,
        IReadOnlyList<DeviceCertificate> cas,
        WebServerTlsConfiguration? webServer,
        Wired8021X? dot1x,
        IReadOnlyList<IssuedCertificate> registry,
        PkiService? service)
    {
        ArgumentNullException.ThrowIfNull(certificates);
        ArgumentNullException.ThrowIfNull(cas);
        ArgumentNullException.ThrowIfNull(registry);
        var result = new List<InstalledCertificate>(certificates.Count + cas.Count);
        foreach (var certificate in certificates)
        {
            var uses = new List<string>();
            if (webServer?.Certificates.Contains(certificate.Alias, StringComparer.Ordinal) == true)
            {
                uses.Add(HttpsUse);
            }

            if (string.Equals(dot1x?.CertClient, certificate.Alias, StringComparison.Ordinal))
            {
                uses.Add(Dot1xUse);
            }

            result.Add(Describe(certificate, false, uses, registry, service));
        }

        foreach (var certificate in cas)
        {
            var uses = new List<string>();
            if (webServer is not null && (webServer.CaCertificates.Contains(certificate.Alias, StringComparer.Ordinal) || webServer.TrustedCertificates.Contains(certificate.Alias, StringComparer.Ordinal)))
            {
                uses.Add(HttpsUse);
            }

            if (dot1x?.CertsCa.Contains(certificate.Alias, StringComparer.Ordinal) == true)
            {
                uses.Add(Dot1xUse);
            }

            result.Add(Describe(certificate, true, uses, registry, service));
        }

        return result;
    }

    private static InstalledCertificate Describe(DeviceCertificate certificate, bool ca, IReadOnlyList<string> uses, IReadOnlyList<IssuedCertificate> registry, PkiService? service)
    {
        try
        {
            using var x509 = X509Certificate2.CreateFromPem(certificate.Pem);
            var issuer = x509.GetNameInfo(X509NameType.SimpleName, forIssuer: true);
            var serverAuth = x509.Extensions.OfType<X509EnhancedKeyUsageExtension>()
                .Any(e => e.EnhancedKeyUsages.Cast<Oid>().Any(o => o.Value == DeviceCertificateIssuer.ServerAuthOid));
            var factory = certificate.Alias.Contains("(802.1AR)", StringComparison.Ordinal) || issuer.StartsWith("Axis device ID", StringComparison.Ordinal);
            return new InstalledCertificate
            {
                Alias = certificate.Alias,
                Kind = ca ? CertificateKind.Ca : serverAuth ? CertificateKind.Server : CertificateKind.Client,
                IssuedBy = string.IsNullOrWhiteSpace(issuer) ? x509.Issuer : issuer,
                IssuedTo = CaCertificates.CommonName(x509),
                NotAfterUtc = x509.NotAfter.ToUniversalTime(),
                InUse = uses,
                FromOadm = CertificateDeployment.IsFromOadm(certificate, x509.SerialNumber.ToUpperInvariant(), registry, service)
                    || (ca && (certificate.Alias.StartsWith(CertificateDeployment.CaAliasPrefix, StringComparison.Ordinal) || certificate.Alias.StartsWith(CertificateDeployment.RadiusCaAliasPrefix, StringComparison.Ordinal))),
                Factory = !ca && factory,
                Fingerprint = CaCertificates.Fingerprint(x509),
            };
        }
        catch (CryptographicException)
        {
            return new InstalledCertificate { Alias = certificate.Alias, Kind = ca ? CertificateKind.Ca : CertificateKind.Client, IssuedTo = "(unreadable certificate)", InUse = uses };
        }
    }
}

/// <summary>
/// "View certificates": a read-only dialog (no task). The dialog reads every device through the query
/// <see cref="PkiQueries.ListCertificates"/>, at most 4 devices at a time.
/// </summary>
public sealed class ViewCertificatesTask(Func<PkiService?> service) : PkiTaskBase(service), ITaskPluginQuery
{
    public override string Id => PkiTaskIds.View;

    public override string DisplayName => PkiTaskIds.ViewName;

    public override string? IconKey => "details";

    public override bool RequiresDialog => true;

    public override Task ExecuteAsync(ITaskExecutionContext ctx, IDeviceInfo device, string? payloadJson, CancellationToken ct) =>
        throw new InvalidOperationException("\"View certificates\" only reads; it never runs as a task.");

    public Task<string?> QueryAsync(ITaskQueryContext ctx, IDeviceInfo device, string method, string? payloadJson, CancellationToken ct) =>
        CertificateQuery.QueryAsync(ctx, device, method, Service, Time, ct);
}

/// <summary>The read-only query of the view and delete dialogs.</summary>
public static class CertificateQuery
{
    public static async Task<string?> QueryAsync(ITaskQueryContext ctx, IDeviceInfo device, string method, PkiService? service, TimeProvider time, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(device);
        if (!string.Equals(method, PkiQueries.ListCertificates, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Unknown query '{method}'.", nameof(method));
        }

        var support = await PkiCompatibility.CachedCertApiAsync(ctx.Vapix, device.Id, time, ct).ConfigureAwait(false);
        if (!support.IsSupported)
        {
            return PkiJson.Serialize(new CertificateListReply { Error = "Needs AXIS OS 11.11 or later." });
        }

        var registry = service is null ? [] : await service.Issued.ForDeviceAsync(device.Id, ct).ConfigureAwait(false);
        try
        {
            var list = await CertificateInventory.ReadAsync(ctx.Vapix, device.Apis, registry, service, ct).ConfigureAwait(false);
            return PkiJson.Serialize(new CertificateListReply { Certificates = list });
        }
        catch (PkiDeviceException ex)
        {
            return PkiJson.Serialize(new CertificateListReply { Error = ex.Message });
        }
    }
}

/// <summary>
/// "Delete certificates" (dialog): deletes the chosen certificates per device. Certificates in use by the web server or 802.1X
/// and the Axis factory device ID certificates are refused (checked again on the device before the first delete). Steps:
/// Check compatibility, Read certificates, Delete certificate &lt;alias&gt; (one each), Verify.
/// </summary>
public sealed class DeleteCertificatesTask(Func<PkiService?> service) : PkiTaskBase(service), ITaskPluginQuery
{
    public override string Id => PkiTaskIds.Delete;

    public override string DisplayName => PkiTaskIds.DeleteName;

    public override string? IconKey => "remove";

    public override bool RequiresDialog => true;

    public override string GetTaskName(string? payloadJson)
    {
        var payload = PkiJson.TryDeserialize<DeletePayload>(payloadJson);
        var aliases = payload?.Devices.Values.SelectMany(v => v).Select(r => r.Alias).Distinct(StringComparer.Ordinal).ToList() ?? [];
        return aliases.Count switch
        {
            0 => DisplayName,
            1 => "Delete certificate " + aliases[0],
            _ => string.Create(CultureInfo.InvariantCulture, $"Delete {aliases.Count} certificates"),
        };
    }

    public Task<string?> QueryAsync(ITaskQueryContext ctx, IDeviceInfo device, string method, string? payloadJson, CancellationToken ct) =>
        CertificateQuery.QueryAsync(ctx, device, method, Service, Time, ct);

    public override async Task ExecuteAsync(ITaskExecutionContext ctx, IDeviceInfo device, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(device);
        var payload = PkiJson.Deserialize<DeletePayload>(payloadJson);
        var chosen = payload.Devices.TryGetValue(device.Id, out var list) ? list.DistinctBy(r => (r.Alias, r.Ca)).ToList() : [];
        var deleteSteps = chosen.Select(r => "Delete certificate " + r.Alias).ToArray();
        ctx.PlanSteps([PkiSteps.CheckCompatibility, PkiSteps.ReadCertificates, .. deleteSteps, PkiSteps.Verify]);
        if (chosen.Count == 0)
        {
            ctx.SkipStep(PkiSteps.CheckCompatibility, "No certificate of this device was chosen");
            return;
        }

        var apis = await ctx.StepAsync(PkiSteps.CheckCompatibility, async step =>
        {
            var support = await PkiCompatibility.RequireCertApiAsync(ctx.Vapix, device.Id, Time, ct).ConfigureAwait(false);
            var read = await ctx.Vapix.GetApiListAsync(ct).ConfigureAwait(false);
            step.Complete("cert " + support.Version);
            return read;
        }).ConfigureAwait(false);

        var service = Service;
        var registry = service is null ? [] : await service.Issued.ForDeviceAsync(device.Id, ct).ConfigureAwait(false);
        var installed = await ctx.StepAsync(PkiSteps.ReadCertificates, async step =>
        {
            var read = await CertificateInventory.ReadAsync(ctx.Vapix, apis, registry, service, ct).ConfigureAwait(false);

            // Validate every choice before the first delete.
            foreach (var choice in chosen)
            {
                var match = read.FirstOrDefault(c => string.Equals(c.Alias, choice.Alias, StringComparison.Ordinal) && (c.Kind == CertificateKind.Ca) == choice.Ca)
                    ?? throw new InvalidOperationException($"The device has no certificate \"{choice.Alias}\". Nothing was changed.");
                if (match.Factory)
                {
                    throw new InvalidOperationException($"\"{choice.Alias}\" is an Axis factory certificate and cannot be deleted. Nothing was changed.");
                }

                if (match.InUse.Count > 0)
                {
                    throw new InvalidOperationException($"\"{choice.Alias}\" is in use by {string.Join(" and ", match.InUse)}. Nothing was changed.");
                }
            }

            step.Complete(string.Create(CultureInfo.InvariantCulture, $"{read.Count} certificates; {chosen.Count} to delete"));
            return read;
        }).ConfigureAwait(false);

        var serials = new List<string>();
        foreach (var choice in chosen)
        {
            await ctx.StepAsync("Delete certificate " + choice.Alias, async step =>
            {
                if (choice.Ca)
                {
                    await CertApi.DeleteCaCertificateAsync(ctx.Vapix, choice.Alias, ct).ConfigureAwait(false);
                }
                else
                {
                    await CertApi.DeleteCertificateAsync(ctx.Vapix, choice.Alias, ct).ConfigureAwait(false);
                }

                step.Complete(choice.Ca ? "CA certificate deleted" : "Certificate and key deleted");
            }).ConfigureAwait(false);
            ctx.Log(TaskLogLevel.Info, $"Certificate \"{choice.Alias}\" deleted.");
            if (!choice.Ca && installed.FirstOrDefault(c => c.Alias == choice.Alias && c.Kind != CertificateKind.Ca) is { } deleted)
            {
                serials.AddRange(registry.Where(e => string.Equals(e.Alias, deleted.Alias, StringComparison.Ordinal)).Select(e => e.SerialNumber));
            }
        }

        if (service is not null)
        {
            await service.Issued.RemoveAsync(device.Id, serials, ct).ConfigureAwait(false);
        }

        await ctx.StepAsync(PkiSteps.Verify, async step =>
        {
            var certificates = await CertApi.ListCertificatesAsync(ctx.Vapix, ct).ConfigureAwait(false);
            var cas = await CertApi.ListCaCertificatesAsync(ctx.Vapix, ct).ConfigureAwait(false);
            var left = chosen.Where(c => (c.Ca ? cas : certificates).Any(x => string.Equals(x.Alias, c.Alias, StringComparison.Ordinal))).Select(c => c.Alias).ToList();
            if (left.Count == 0)
            {
                step.Complete("Deleted");
            }
            else
            {
                step.Warn("The device still lists " + string.Join(", ", left) + ".");
            }
        }).ConfigureAwait(false);
    }
}

/// <summary>
/// "Install certificates" (dialog): installs the PKCS#12 file matched to this device (<c>install_from_pkcs12</c>, the
/// password in memory only), then switches HTTPS or 802.1X to it like Enable/Update, or installs the file's certificates as
/// CA certificates only. Steps: Check compatibility, Read certificate file, then HTTPS: Read web server settings, Install
/// certificate, Switch web server to the new certificate, Verify HTTPS; 802.1X: Check device clock, Install CA certificates,
/// Install certificate, Set 802.1X configuration, Verify 802.1X settings; CA only: Install CA certificates.
/// </summary>
public sealed class InstallCertificatesTask(Func<PkiService?> service) : PkiTaskBase(service)
{
    public override string Id => PkiTaskIds.Install;

    public override string DisplayName => PkiTaskIds.InstallName;

    public override string? IconKey => "upload";

    public override bool RequiresDialog => true;

    public override string GetTaskName(string? payloadJson)
    {
        var payload = PkiJson.TryDeserialize<InstallPayload>(payloadJson);
        var files = payload?.Files.Select(f => f.FileName).Distinct(StringComparer.Ordinal).ToList() ?? [];
        return files.Count switch
        {
            0 => DisplayName,
            1 => "Install certificate " + files[0],
            _ => string.Create(CultureInfo.InvariantCulture, $"Install {files.Count} certificates"),
        };
    }

    public override async Task ExecuteAsync(ITaskExecutionContext ctx, IDeviceInfo device, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(device);
        var payload = PkiJson.Deserialize<InstallPayload>(payloadJson);
        var purpose = payload.Purpose;

        // HTTPS / 802.1X: the one file matched to this device. CA only: every file for this device or for all (Guid.Empty).
        var files = purpose == InstallPurpose.CaOnly
            ? payload.Files.Where(f => f.DeviceId == device.Id || f.DeviceId == Guid.Empty).ToList()
            : payload.Files.Where(f => f.DeviceId == device.Id).Take(1).ToList();
        string[] purposeSteps = purpose switch
        {
            InstallPurpose.Https => [PkiSteps.ReadWebServer, PkiSteps.InstallCertificate, PkiSteps.SwitchWebServer, PkiSteps.VerifyHttps],
            InstallPurpose.Dot1x => [PkiSteps.CheckClock, PkiSteps.InstallCas, PkiSteps.InstallCertificate, PkiSteps.SetDot1x, PkiSteps.VerifyDot1x],
            InstallPurpose.CaOnly => [PkiSteps.InstallCas],
            _ => throw new ArgumentException("Unknown purpose: " + purpose, nameof(payloadJson)),
        };
        ctx.PlanSteps([PkiSteps.CheckCompatibility, PkiSteps.ReadFile, .. purposeSteps]);
        if (files.Count == 0)
        {
            ctx.SkipStep(PkiSteps.CheckCompatibility, "No certificate file matches this device");
            ctx.ReportWarning("No certificate file matches this device. Nothing was changed.");
            return;
        }

        var dot1x = purpose == InstallPurpose.Dot1x;
        var (apis, network) = await ctx.StepAsync(PkiSteps.CheckCompatibility, async step =>
        {
            var support = await PkiCompatibility.RequireCertApiAsync(ctx.Vapix, device.Id, Time, ct).ConfigureAwait(false);
            var list = await ctx.Vapix.GetApiListAsync(ct).ConfigureAwait(false);
            DeviceNetworkInfo? info = null;
            if (dot1x)
            {
                list.Require(PkiCompatibility.NetworkSettings, "1.0");
                info = await NetworkInfoApi.GetAsync(ctx.Vapix, list, ct).ConfigureAwait(false);
                if (info.Dot1x is null)
                {
                    throw new DeviceNotCompatibleException("The device does not offer IEEE 802.1X on a wired interface. Nothing was changed.");
                }
            }

            step.Complete("cert " + support.Version);
            return (list, info);
        }).ConfigureAwait(false);

        var read = await ctx.StepAsync(PkiSteps.ReadFile, async step =>
        {
            var result = new List<(InstallFile File, byte[] Data, Pkcs12Contents Contents)>();
            foreach (var file in files)
            {
                var found = await ctx.Files.FindAsync(file.FileId, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"The uploaded file {file.FileName} is no longer on the server. Nothing was changed.");
                if (found.Size > CertificateFiles.MaxBytes)
                {
                    throw new InvalidOperationException($"{file.FileName}: The file is larger than 1 MB. Nothing was changed.");
                }

                await using var stream = await ctx.Files.OpenReadAsync(file.FileId, ct).ConfigureAwait(false);
                using var memory = new MemoryStream();
                await stream.CopyToAsync(memory, ct).ConfigureAwait(false);
                var data = memory.ToArray();
                var contents = CertificateFiles.Read(data, payload.Password, Time.GetUtcNow().UtcDateTime);
                var needsLeaf = purpose != InstallPurpose.CaOnly;
                if (contents.Error is not null || (needsLeaf && !contents.HasLeaf) || contents.CertificatePems.Count == 0)
                {
                    var reason = contents.Error ?? (needsLeaf ? "The file contains no certificate with a private key." : "The file contains no certificate.");
                    throw new InvalidOperationException($"{file.FileName}: {reason} Nothing was changed.");
                }

                result.Add((file, data, contents));
            }

            var first = result[0];
            step.Complete(purpose != InstallPurpose.CaOnly
                ? $"{first.File.FileName} · {first.Contents.LeafSubject}, valid until {first.Contents.LeafNotAfterUtc:yyyy-MM-dd}"
                : $"{string.Join(", ", result.Select(r => r.File.FileName))} · {result.Sum(r => r.Contents.CertificatePems.Count)} certificates");
            return result;
        }).ConfigureAwait(false);
        var (file, bytes, contents) = read[0];

        var service = Service;
        switch (purpose)
        {
            case InstallPurpose.CaOnly:
                await CertificateDeployment.InstallCaCertificatesAsync(ctx, PkiSteps.InstallCas, [.. read.SelectMany(r => r.Contents.CertificatePems).Select(pem =>
                {
                    var fingerprint = CertificateDeployment.Fingerprint(pem);
                    return (CertificateDeployment.CaAlias(CertificateDeployment.CaAliasPrefix, fingerprint), pem, fingerprint);
                }).DistinctBy(c => c.fingerprint, StringComparer.OrdinalIgnoreCase)], ct).ConfigureAwait(false);
                break;

            case InstallPurpose.Https:
            {
                var webServer = await ReadWebServerAsync(ctx, ct).ConfigureAwait(false);
                var alias = await InstallAsync(ctx, bytes, payload.Password, file, ct).ConfigureAwait(false);
                await CertificateDeployment.SwitchWebServerAsync(ctx, webServer, alias, PkiSteps.SwitchWebServer, ct).ConfigureAwait(false);
                await CertificateDeployment.FollowWebServerAsync(ctx, "https", contents.LeafFingerprint, Time, PkiSteps.VerifyHttps, ct).ConfigureAwait(false);
                break;
            }

            case InstallPurpose.Dot1x:
            {
                await CertificateDeployment.CheckClockAsync(ctx, apis, Time, ct).ConfigureAwait(false);
                var (_, ca) = CertificateDeployment.RequireCa(service);
                var radius = CertificateDeployment.RadiusCas(service!.Config, ca);
                var aliases = await CertificateDeployment.InstallCaCertificatesAsync(ctx, PkiSteps.InstallCas, radius, ct).ConfigureAwait(false);
                var alias = await InstallAsync(ctx, bytes, payload.Password, file, ct).ConfigureAwait(false);
                await Dot1xEnableTask.ConfigureAsync(ctx, service.Config.Dot1x, device, apis, network!, alias, aliases, null, ct).ConfigureAwait(false);
                break;
            }
        }
    }

    private Task<string> InstallAsync(ITaskExecutionContext ctx, byte[] pkcs12, string? password, InstallFile file, CancellationToken ct) =>
        ctx.StepAsync(PkiSteps.InstallCertificate, async step =>
        {
            var alias = CertificateDeployment.NewAlias(CertificateDeployment.ImportAliasPrefix, Time);
            await CertApi.InstallFromPkcs12Async(ctx.Vapix, alias, Convert.ToBase64String(pkcs12), password ?? string.Empty, ct).ConfigureAwait(false);
            step.Complete($"{alias} from {file.FileName}");
            ctx.Log(TaskLogLevel.Info, $"Certificate \"{alias}\" installed from {file.FileName}.");
            return alias;
        });
}
