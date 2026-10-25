using Oadm.Plugins.Pki.Device;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Pki.Tasks;

/// <summary>
/// "Renew certificates": renews what OADM issued on the device. Per purpose (the certificate the web server presents, the
/// 802.1X client certificate) when it is an OADM certificate: a new key and certificate on a new alias, switch, verify, remove
/// the old one ("HTTPS: ..." / "IEEE 802.1X: ..." steps). Purposes that do not apply are one Skipped step with the reason.
/// Steps before: Check compatibility, Read certificates, Read web server settings, Read network settings.
/// </summary>
public sealed class RenewTask(Func<PkiService?> service) : PkiTaskBase(service)
{
    public const string HttpsPrefix = "HTTPS";
    public const string Dot1xPrefix = "IEEE 802.1X";
    public const string RenewHttpsStep = "Renew HTTPS certificate";
    public const string RenewDot1xStep = "Renew IEEE 802.1X certificate";

    public override string Id => PkiTaskIds.Renew;

    public override string DisplayName => PkiTaskIds.RenewName;

    public override string? IconKey => "refresh";

    public override string GetTaskName(string? payloadJson) => "Renew certificates";

    public override async Task ExecuteAsync(ITaskExecutionContext ctx, IDeviceInfo device, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(device);
        ctx.PlanSteps(PkiSteps.CheckCompatibility, PkiSteps.ReadCertificates, PkiSteps.ReadWebServer, PkiSteps.ReadNetwork);
        var (service, apis) = await CheckCertificateTaskAsync(ctx, device, ct).ConfigureAwait(false);

        var certificates = await ctx.StepAsync(PkiSteps.ReadCertificates, async step =>
        {
            var list = await CertApi.ListCertificatesAsync(ctx.Vapix, ct).ConfigureAwait(false);
            await service.ImportFoundAsync(device, list.Select(c => (c.Alias, c.Pem)), ct).ConfigureAwait(false); // from before the list
            step.Complete($"{list.Count} certificates");
            return list;
        }).ConfigureAwait(false);

        WebServerTlsConfiguration? webServer;
        try
        {
            webServer = await ReadWebServerAsync(ctx, ct).ConfigureAwait(false);
        }
        catch (DeviceNotCompatibleException ex)
        {
            ctx.Log(TaskLogLevel.Warning, ex.Message);
            webServer = null;
        }

        var network = await HttpsEnableTask.ReadNetworkAsync(ctx, apis, ct).ConfigureAwait(false);
        var registry = await service.Issued.ForDeviceAsync(device, ct).ConfigureAwait(false);
        bool IsOadm(string? alias) =>
            alias is not null
            && certificates.FirstOrDefault(c => string.Equals(c.Alias, alias, StringComparison.Ordinal)) is { } certificate
            && CertificateDeployment.IsFromOadm(certificate, CertificateDeployment.SerialOf(certificate.Pem), registry, service);

        // HTTPS
        var httpsReason = webServer is null ? "The web server settings cannot be read"
            : !IsOadm(webServer.CertificateAlias) ? "No OADM HTTPS certificate on this device"
            : null;

        // 802.1X
        var dot1x = network?.Dot1x;
        var dot1xReason = dot1x is null ? "No OADM 802.1X certificate on this device"
            : !IsOadm(dot1x.CertClient) ? "No OADM 802.1X certificate on this device"
            : !dot1x.Enabled ? "IEEE 802.1X is off on this device"
            : null;
        // The first policy of each kind (a renewal has no dialog): its values, checked before the first write like a run of Enable.
        IReadOnlyList<(string Alias, string Pem, string Fingerprint)>? radius = null;
        Policies.PkiPolicy? httpsPolicy = null;
        Policies.PkiPolicy? dot1xPolicy = null;
        Ca.CaMaterial? httpsCa = null;
        Ca.CaMaterial? dot1xCa = null;
        if (httpsReason is null)
        {
            (httpsPolicy, httpsCa, httpsReason) = await PolicyOrReasonAsync(ctx, service, Policies.PolicyKinds.HttpsCertificate, ct).ConfigureAwait(false);
        }

        if (dot1xReason is null)
        {
            (dot1xPolicy, dot1xCa, dot1xReason) = await PolicyOrReasonAsync(ctx, service, Policies.PolicyKinds.Dot1xCertificate, ct).ConfigureAwait(false);
        }

        if (dot1xReason is null && !CertificateDeployment.ChainComplete(dot1xCa!))
        {
            dot1xReason = "The CA chain is incomplete; import the CA with its full chain";
        }

        if (dot1xReason is null)
        {
            try
            {
                radius = CertificateDeployment.RadiusCas(dot1xPolicy!, service);
            }
            catch (InvalidOperationException ex)
            {
                dot1xReason = Reason(ex);
            }
        }

        if (httpsReason is not null)
        {
            ctx.SkipStep(RenewHttpsStep, httpsReason);
        }
        else
        {
            await CertificateDeployment.CheckClockAsync(ctx, apis, service.Time, confirmed: false, ct).ConfigureAwait(false);
            await HttpsEnableTask.DeployAsync(ctx, service, httpsCa!, device, webServer!, network, httpsPolicy!, HttpsPrefix, ct).ConfigureAwait(false);
        }

        if (dot1xReason is not null)
        {
            ctx.SkipStep(RenewDot1xStep, dot1xReason);
        }
        else
        {
            if (httpsReason is not null)
            {
                await CertificateDeployment.CheckClockAsync(ctx, apis, service.Time, confirmed: false, ct).ConfigureAwait(false); // checked once
            }

            await Dot1xEnableTask.DeployAsync(ctx, service, dot1xCa!, device, apis, network!, radius!, dot1xPolicy!, Dot1xPrefix, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The first policy of <paramref name="kind"/>, the CA it names and the device's key check; or why this purpose is skipped
    /// (nothing written for it).
    /// </summary>
    private static async Task<(Policies.PkiPolicy? Policy, Ca.CaMaterial? Ca, string? Reason)> PolicyOrReasonAsync(ITaskExecutionContext ctx, PkiService service, string kind, CancellationToken ct)
    {
        try
        {
            var policy = IssuingChoice.Resolve(service, kind, null);
            var ca = CertificateDeployment.RequireCa(service, policy);
            await IssuingChoice.RequireKeyAsync(ctx.Vapix, policy, ct).ConfigureAwait(false);
            return (policy, ca, null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or DeviceNotCompatibleException)
        {
            return (null, null, Reason(ex));
        }
    }

    private static string Reason(Exception ex) => ex.Message.Replace(" Nothing was changed.", string.Empty, StringComparison.Ordinal).TrimEnd('.');
}
