using Oadm.Plugins.Pki.Device;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Pki.Tasks;

/// <summary>
/// "Renew certificates now": renews what OADM issued on the device. Per purpose (the certificate the web server presents, the
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
        var (service, ca, apis) = await CheckCertificateTaskAsync(ctx, device, ct).ConfigureAwait(false);

        var certificates = await ctx.StepAsync(PkiSteps.ReadCertificates, async step =>
        {
            var list = await CertApi.ListCertificatesAsync(ctx.Vapix, ct).ConfigureAwait(false);
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
        var registry = await service.Issued.ForDeviceAsync(device.Id, ct).ConfigureAwait(false);
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
            : !CertificateDeployment.ChainComplete(ca) ? "The CA chain is incomplete; import the CA with its full chain"
            : null;
        IReadOnlyList<(string Alias, string Pem, string Fingerprint)>? radius = null;
        if (dot1xReason is null)
        {
            try
            {
                radius = CertificateDeployment.RadiusCas(service.Config, ca);
            }
            catch (InvalidOperationException ex)
            {
                dot1xReason = ex.Message.Replace(" Nothing was changed.", string.Empty, StringComparison.Ordinal);
            }
        }

        if (httpsReason is not null)
        {
            ctx.SkipStep(RenewHttpsStep, httpsReason);
        }
        else
        {
            await HttpsEnableTask.DeployAsync(ctx, service, ca, device, webServer!, network, HttpsPrefix, ct).ConfigureAwait(false);
        }

        if (dot1xReason is not null)
        {
            ctx.SkipStep(RenewDot1xStep, dot1xReason);
        }
        else
        {
            await CertificateDeployment.CheckClockAsync(ctx, apis, service.Time, ct).ConfigureAwait(false);
            await Dot1xEnableTask.DeployAsync(ctx, service, ca, device, apis, network!, radius!, Dot1xPrefix, ct).ConfigureAwait(false);
        }
    }
}
