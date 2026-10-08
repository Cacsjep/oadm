using System.Globalization;
using System.Security.Cryptography;

using Oadm.Plugins.Pki.Device;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Pki.Tasks;

/// <summary>
/// "Install CA certificates" (dialog): installs one or more CA certificates the user chose (PEM / DER files, bundles) on
/// every selected device, so the devices trust certificates those CAs issued (e.g. a RADIUS server or a video management
/// system). Needs no OADM CA. Every certificate is checked again before the first write (a CA certificate, valid now, at most
/// <see cref="CaCertificateFiles.MaxCertificates"/>). Steps: Check compatibility, Read installed CA certificates, Install CA
/// certificate &lt;name&gt; (one each; Skipped "Already installed" when the device has that fingerprint, under any alias),
/// Verify CA certificates. Aliases "OADM CA &lt;8 hex&gt;" like the CA certificates of the enable flows.
/// </summary>
public sealed class InstallCaTask(Func<PkiService?> service) : PkiTaskBase(service)
{
    public const string ReadInstalledCas = "Read installed CA certificates";
    public const string VerifyCas = "Verify CA certificates";

    public override string Id => PkiTaskIds.InstallCa;

    public override string DisplayName => PkiTaskIds.InstallCaName;

    public override string? IconKey => "upload";

    public override bool RequiresDialog => true;

    public override string GetTaskName(string? payloadJson)
    {
        var payload = PkiJson.TryDeserialize<InstallCaPayload>(payloadJson);
        var count = payload?.Certificates.Count ?? 0;
        return count switch
        {
            0 => DisplayName,
            1 => "Install CA certificate " + payload!.Certificates[0].Name,
            _ => string.Create(CultureInfo.InvariantCulture, $"Install {count} CA certificates"),
        };
    }

    /// <summary>"Install CA certificate Acme Root CA", "(2)" for a second certificate with the same name.</summary>
    public static IReadOnlyList<string> StepNames(IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var result = new List<string>(names.Count);
        foreach (var name in names)
        {
            var count = seen[name] = seen.GetValueOrDefault(name) + 1;
            result.Add(PkiSteps.InstallCa + " " + name + (count == 1 ? string.Empty : string.Create(CultureInfo.InvariantCulture, $" ({count})")));
        }

        return result;
    }

    public override async Task ExecuteAsync(ITaskExecutionContext ctx, IDeviceInfo device, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(device);
        var payload = PkiJson.Deserialize<InstallCaPayload>(payloadJson);
        var now = Time.GetUtcNow().UtcDateTime;

        // Validate everything before the first request: a CA certificate each, valid now, one per fingerprint. A problem
        // fails "Check compatibility" before anything is sent.
        var (wanted, problem) = Validate(payload, now);
        var installSteps = StepNames([.. wanted.Select(w => w.Name)]);
        ctx.PlanSteps([PkiSteps.CheckCompatibility, ReadInstalledCas, .. installSteps, VerifyCas]);

        await ctx.StepAsync(PkiSteps.CheckCompatibility, async step =>
        {
            if (problem is not null)
            {
                throw new InvalidOperationException(problem);
            }

            var support = await PkiCompatibility.RequireCertApiAsync(ctx.Vapix, device.Id, Time, ct).ConfigureAwait(false);
            step.Complete("cert " + support.Version);
        }).ConfigureAwait(false);

        var (byFingerprint, aliases) = await ctx.StepAsync(ReadInstalledCas, async step =>
        {
            var installed = await CertApi.ListCaCertificatesAsync(ctx.Vapix, ct).ConfigureAwait(false);
            var known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var certificate in installed)
            {
                try
                {
                    known.TryAdd(CertificateDeployment.Fingerprint(certificate.Pem), certificate.Alias);
                }
                catch (CryptographicException)
                {
                    // an unreadable entry cannot be one of ours
                }
            }

            var present = wanted.Count(w => known.ContainsKey(w.Fingerprint));
            step.Complete(string.Create(CultureInfo.InvariantCulture, $"{installed.Count} CA certificates; {present} of {wanted.Count} already installed"));
            return (known, installed.Select(c => c.Alias).ToHashSet(StringComparer.Ordinal));
        }).ConfigureAwait(false);

        var added = new List<string>();
        for (var i = 0; i < wanted.Count; i++)
        {
            var certificate = wanted[i];
            if (byFingerprint.TryGetValue(certificate.Fingerprint, out var existing))
            {
                ctx.SkipStep(installSteps[i], "Already installed");
                ctx.Log(TaskLogLevel.Info, $"CA certificate \"{certificate.Name}\" is already installed as \"{existing}\".");
                continue;
            }

            await ctx.StepAsync(installSteps[i], async step =>
            {
                var alias = FreeAlias(CertificateDeployment.CaAlias(CertificateDeployment.CaAliasPrefix, certificate.Fingerprint), aliases);
                await CertApi.AddCaCertificateAsync(ctx.Vapix, alias, certificate.Pem, ct).ConfigureAwait(false);
                aliases.Add(alias);
                byFingerprint[certificate.Fingerprint] = alias;
                added.Add(alias);
                step.Complete(alias);
                ctx.Log(TaskLogLevel.Info, $"CA certificate \"{certificate.Name}\" installed as \"{alias}\".");
            }).ConfigureAwait(false);
        }

        await ctx.StepAsync(VerifyCas, async step =>
        {
            var installed = await CertApi.ListCaCertificatesAsync(ctx.Vapix, ct).ConfigureAwait(false);
            var fingerprints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var certificate in installed)
            {
                try
                {
                    fingerprints.Add(CertificateDeployment.Fingerprint(certificate.Pem));
                }
                catch (CryptographicException)
                {
                    // ignored like above
                }
            }

            var missing = wanted.Where(w => !fingerprints.Contains(w.Fingerprint)).Select(w => w.Name).ToList();
            if (missing.Count == 0)
            {
                step.Complete(added.Count == 0
                    ? "All CA certificates were already installed"
                    : string.Create(CultureInfo.InvariantCulture, $"{added.Count} installed, {wanted.Count - added.Count} already there"));
            }
            else
            {
                step.Warn("The device does not list " + string.Join(", ", missing) + ".");
            }
        }).ConfigureAwait(false);
    }

    /// <summary>The distinct certificates of the payload, or the first problem ("&lt;name&gt;: ... Nothing was changed.").</summary>
    public static (IReadOnlyList<CaFileCertificate> Wanted, string? Problem) Validate(InstallCaPayload payload, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Certificates.Count == 0)
        {
            return ([], "No CA certificate was chosen. Nothing was changed.");
        }

        if (payload.Certificates.Count > CaCertificateFiles.MaxCertificates)
        {
            return ([], string.Create(CultureInfo.InvariantCulture, $"At most {CaCertificateFiles.MaxCertificates} CA certificates at once. Nothing was changed."));
        }

        var wanted = new List<CaFileCertificate>();
        var fingerprints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in payload.Certificates)
        {
            CaFileCertificate certificate;
            try
            {
                certificate = CaCertificateFiles.ReadPem(item.Pem ?? string.Empty, nowUtc);
            }
            catch (InvalidOperationException ex)
            {
                return ([], $"{item.Name}: {ex.Message} Nothing was changed.");
            }

            if (certificate.Problem is not null)
            {
                return ([], $"{certificate.Name}: {certificate.Problem} Nothing was changed.");
            }

            if (fingerprints.Add(certificate.Fingerprint))
            {
                wanted.Add(certificate);
            }
        }

        return (wanted, null);
    }

    /// <summary>The alias, or "&lt;alias&gt;-2", "-3" ... when another certificate already uses it.</summary>
    private static string FreeAlias(string alias, HashSet<string> used)
    {
        var candidate = alias;
        for (var n = 2; used.Contains(candidate); n++)
        {
            candidate = string.Create(CultureInfo.InvariantCulture, $"{alias}-{n}");
        }

        return candidate;
    }
}
