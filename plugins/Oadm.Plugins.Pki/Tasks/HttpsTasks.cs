using Oadm.Plugins.Pki.Device;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Pki.Tasks;

/// <summary>
/// "Enable HTTPS": a new key on the device, a certificate from the OADM CA, the web server switched to it (an
/// HTTP-only policy becomes HTTP and HTTPS), OADM follows the new certificate (scheme https, new pin), the previous OADM
/// certificate is removed. Steps: Check compatibility, Read web server settings, Read network settings, Install CA
/// certificate, Create key on the device, Get certificate request, Sign certificate, Install certificate, Switch web server
/// to the new certificate, Verify HTTPS, Remove previous OADM certificate.
/// </summary>
public sealed class HttpsEnableTask(Func<PkiService?> service) : PkiTaskBase(service)
{
    public override string Id => PkiTaskIds.HttpsEnable;

    public override string DisplayName => PkiTaskIds.HttpsEnableName;

    public override string GetTaskName(string? payloadJson) => "Enable HTTPS";

    public override async Task ExecuteAsync(ITaskExecutionContext ctx, IDeviceInfo device, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(device);
        ctx.PlanSteps(
            PkiSteps.CheckCompatibility, PkiSteps.ReadWebServer, PkiSteps.ReadNetwork, PkiSteps.InstallCa, PkiSteps.CreateKey, PkiSteps.GetCsr,
            PkiSteps.Sign, PkiSteps.InstallCertificate, PkiSteps.SwitchWebServer, PkiSteps.VerifyHttps, PkiSteps.RemovePrevious);

        var (service, ca, apis) = await CheckCertificateTaskAsync(ctx, device, ct).ConfigureAwait(false);
        var webServer = await ReadWebServerAsync(ctx, ct).ConfigureAwait(false);
        var network = await ReadNetworkAsync(ctx, apis, ct).ConfigureAwait(false);
        if (webServer.CertificateAlias?.StartsWith(CertificateDeployment.HttpsAliasPrefix, StringComparison.Ordinal) == true)
        {
            ctx.Log(TaskLogLevel.Info, $"The device serves the OADM certificate \"{webServer.CertificateAlias}\"; it is replaced by a new one.");
        }

        await DeployAsync(ctx, service, ca, device, webServer, network, stepPrefix: null, ct).ConfigureAwait(false);
    }

    /// <summary>Install CA certificate ... Remove previous OADM certificate (also used by renew and manual install).</summary>
    internal static async Task DeployAsync(
        ITaskExecutionContext ctx, PkiService service, Ca.CaMaterial ca, IDeviceInfo device, WebServerTlsConfiguration webServer, DeviceNetworkInfo? network, string? stepPrefix, CancellationToken ct)
    {
        await CertificateDeployment.InstallCaCertificatesAsync(ctx, PkiSteps.Prefixed(stepPrefix, PkiSteps.InstallCa), CertificateDeployment.CaChain(ca), ct).ConfigureAwait(false);
        var alias = CertificateDeployment.NewAlias(CertificateDeployment.HttpsAliasPrefix, service.Time);
        var names = CertificateDeployment.NamesFor(device, network);
        var issued = await CertificateDeployment.IssueAsync(ctx, service, ca, device, names, CertificatePurpose.Https, alias, stepPrefix, ct).ConfigureAwait(false);
        await CertificateDeployment.SwitchWebServerAsync(ctx, webServer, issued.Alias, PkiSteps.Prefixed(stepPrefix, PkiSteps.SwitchWebServer), ct).ConfigureAwait(false);
        await CertificateDeployment.FollowWebServerAsync(ctx, "https", issued.Fingerprint, service.Time, PkiSteps.Prefixed(stepPrefix, PkiSteps.VerifyHttps), ct).ConfigureAwait(false);
        await CertificateDeployment.RemovePreviousAsync(ctx, service, device, CertificatePurpose.Https, [issued.Alias], PkiSteps.Prefixed(stepPrefix, PkiSteps.RemovePrevious), ct).ConfigureAwait(false);
    }

    /// <summary>Host name, domain and addresses for the certificate (network-settings 1.x); Skipped without that API.</summary>
    internal static async Task<DeviceNetworkInfo?> ReadNetworkAsync(ITaskExecutionContext ctx, IReadOnlyList<DeviceApi> apis, CancellationToken ct)
    {
        if (!PkiCompatibility.HasNetworkSettings(apis))
        {
            ctx.SkipStep(PkiSteps.ReadNetwork, "No network settings API; the certificate names the OADM address");
            return null;
        }

        return await ctx.StepAsync(PkiSteps.ReadNetwork, async step =>
        {
            var info = await NetworkInfoApi.GetAsync(ctx.Vapix, apis, ct).ConfigureAwait(false);
            step.Complete(Describe(info));
            return info;
        }).ConfigureAwait(false);
    }

    internal static string Describe(DeviceNetworkInfo info) =>
        $"Host name {info.HostName ?? "unknown"}{(info.Fqdn is { } fqdn ? $" ({fqdn})" : string.Empty)} · {string.Join(", ", info.Addresses)}";
}

/// <summary>
/// "Disable HTTPS": connection policy HTTP only (like ADM), certificates stay, OADM switches its own connection to HTTP.
/// Steps: Check compatibility (the device must accept Digest over HTTP, else OADM would lock itself out), Read web server
/// settings, Set HTTP only, Verify.
/// </summary>
public sealed class HttpsDisableTask(Func<PkiService?> service) : PkiTaskBase(service)
{
    public const string AuthenticationPolicyParameter = "Network.HTTP.AuthenticationPolicy";

    public override string Id => PkiTaskIds.HttpsDisable;

    public override string DisplayName => PkiTaskIds.HttpsDisableName;

    public override string? IconKey => "cancel";

    /// <summary>The client asks for confirmation first (the dialog only shows the confirmation).</summary>
    public override bool RequiresDialog => true;

    public override string GetTaskName(string? payloadJson) => "Disable HTTPS";

    public override async Task ExecuteAsync(ITaskExecutionContext ctx, IDeviceInfo device, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(device);
        ctx.PlanSteps(PkiSteps.CheckCompatibility, PkiSteps.ReadWebServer, PkiSteps.SetHttpOnly, PkiSteps.Verify);

        await ctx.StepAsync(PkiSteps.CheckCompatibility, async step =>
        {
            var apis = await ctx.Vapix.GetApiListAsync(ct).ConfigureAwait(false);
            apis.Require("param-cgi", "1.0");
            var parameters = await ctx.Vapix.ListParametersAsync([AuthenticationPolicyParameter], ct).ConfigureAwait(false);
            var policy = parameters.TryGetValue(AuthenticationPolicyParameter, out var value) ? value.Trim() : null;
            if (string.Equals(policy, "basic", StringComparison.OrdinalIgnoreCase))
            {
                throw new DeviceNotCompatibleException(
                    "The device accepts only Basic authentication over HTTP, which OADM never sends unencrypted. Change the HTTP authentication policy first. Nothing was changed.");
            }

            step.Complete("HTTP authentication " + (policy ?? "unknown"));
        }).ConfigureAwait(false);

        var webServer = await ReadWebServerAsync(ctx, ct).ConfigureAwait(false);
        if (ConnectionPolicy.IsHttpOnly(webServer.Policy))
        {
            ctx.SkipStep(PkiSteps.SetHttpOnly, "Already HTTP only");
        }
        else
        {
            await ctx.StepAsync(PkiSteps.SetHttpOnly, async step =>
            {
                await WebServerTls.SetAsync(ctx.Vapix, webServer with { Policy = ConnectionPolicy.Http }, ct).ConfigureAwait(false);
                step.Complete("Connection policy HTTP only; certificates stay");
            }).ConfigureAwait(false);
            ctx.Log(TaskLogLevel.Info, $"Connection policy changed from {ConnectionPolicy.Describe(webServer.Policy)} to HTTP only.");
        }

        await CertificateDeployment.FollowWebServerAsync(ctx, "http", null, Time, PkiSteps.Verify, ct).ConfigureAwait(false);
    }
}
