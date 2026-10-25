using Oadm.Plugins.DeviceClocks;
using Oadm.Plugins.Pki.Device;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Pki.Tasks;

/// <summary>
/// "Renew certificates": renews what OADM issued on the device. Per purpose (the certificate the web server presents, the
/// 802.1X client certificate) when it is an OADM certificate: a new key and certificate on a new alias, switch, verify, remove
/// the old one ("HTTPS: ..." / "IEEE 802.1X: ..." steps). Purposes that do not apply are one Skipped step with the reason.
/// Steps before: Check compatibility, Read certificates, Read web server settings, Read network settings. The dialog chooses
/// the IEEE 802.1X certificate policy (<see cref="RenewPayload"/>); HTTPS renews with the first HTTPS certificate policy.
/// </summary>
public sealed class RenewTask(Func<PkiService?> service) : PkiTaskBase(service), ITaskPluginQuery
{
    public const string HttpsPrefix = "HTTPS";
    public const string Dot1xPrefix = "IEEE 802.1X";
    public const string RenewHttpsStep = "Renew HTTPS certificate";
    public const string RenewDot1xStep = "Renew IEEE 802.1X certificate";

    public override string Id => PkiTaskIds.Renew;

    public override string DisplayName => PkiTaskIds.RenewName;

    public override string? IconKey => "refresh";

    public override bool RequiresDialog => true;

    public override string GetTaskName(string? payloadJson) => "Renew certificates";

    /// <summary>The dialog's IEEE 802.1X certificate policies and its clock check (<see cref="CameraClock.QueryMethod"/>), read only.</summary>
    public Task<string?> QueryAsync(ITaskQueryContext ctx, IDeviceInfo device, string method, string? payloadJson, CancellationToken ct) =>
        IssuingChoice.QueryAsync(ctx, device, method, Service, Policies.PolicyKinds.Dot1xCertificate, Time, ct);

    public override async Task ExecuteAsync(ITaskExecutionContext ctx, IDeviceInfo device, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(device);
        var payload = PkiJson.TryDeserialize<RenewPayload>(payloadJson) ?? new RenewPayload();
        ctx.PlanSteps(PkiSteps.CheckCompatibility, PkiSteps.ReadCertificates, PkiSteps.ReadWebServer, PkiSteps.ReadNetwork);
        // A chosen IEEE 802.1X policy deleted after the dialog fails the run before anything is written (also HTTPS).
        var (service, apis) = await CheckCertificateTaskAsync(ctx, device, ct,
            _ => IssuingChoice.Chosen(CertificateDeployment.RequireService(Service), Policies.PolicyKinds.Dot1xCertificate, payload.Dot1xPolicyId)).ConfigureAwait(false);

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
        // HTTPS: the first policy of the kind; IEEE 802.1X: the dialog's (older payloads: the first). Checked before the first
        // write like a run of Enable.
        IReadOnlyList<(string Alias, string Pem, string Fingerprint)>? radius = null;
        Policies.PkiPolicy? httpsPolicy = null;
        Policies.PkiPolicy? dot1xPolicy = null;
        Ca.CaMaterial? httpsCa = null;
        Ca.CaMaterial? dot1xCa = null;
        if (httpsReason is null)
        {
            (httpsPolicy, httpsCa, httpsReason) = await PolicyOrReasonAsync(ctx, service, Policies.PolicyKinds.HttpsCertificate, null, ct).ConfigureAwait(false);
        }

        if (dot1xReason is null)
        {
            (dot1xPolicy, dot1xCa, dot1xReason) = await PolicyOrReasonAsync(ctx, service, Policies.PolicyKinds.Dot1xCertificate, payload.Dot1xPolicyId, ct).ConfigureAwait(false);
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
            await CertificateDeployment.CheckClockAsync(ctx, apis, service.Time, payload.ClockConfirmed, ct).ConfigureAwait(false);
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
                await CertificateDeployment.CheckClockAsync(ctx, apis, service.Time, payload.ClockConfirmed, ct).ConfigureAwait(false); // checked once
            }

            await Dot1xEnableTask.DeployAsync(ctx, service, dot1xCa!, device, apis, network!, radius!, dot1xPolicy!, Dot1xPrefix, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The policy <paramref name="policyId"/> of <paramref name="kind"/> (null: the first), the CA it names and the device's key
    /// check; or why this purpose is skipped (nothing written for it).
    /// </summary>
    private static async Task<(Policies.PkiPolicy? Policy, Ca.CaMaterial? Ca, string? Reason)> PolicyOrReasonAsync(ITaskExecutionContext ctx, PkiService service, string kind, Guid? policyId, CancellationToken ct)
    {
        try
        {
            var policy = IssuingChoice.Resolve(service, kind, policyId);
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
