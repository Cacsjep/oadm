using Oadm.Plugins.DeviceClocks;
using Oadm.Plugins.Pki.Device;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Pki.Tasks;

/// <summary>
/// "Enable HTTPS": a new key on the device, a certificate from the OADM CA, the web server switched to it (an
/// HTTP-only policy becomes HTTP and HTTPS), OADM follows the new certificate (scheme https, new pin), the previous OADM
/// certificate is removed. Steps: Check compatibility, Check device clock, Read web server settings, Read network settings, Install CA
/// certificate, Create key on the device, Get certificate request, Sign certificate, Install certificate, Switch web server
/// to the new certificate, Verify HTTPS, Remove previous OADM certificate.
/// </summary>
public sealed class HttpsEnableTask(Func<PkiService?> service) : PkiTaskBase(service), ITaskPluginQuery
{
    public override string Id => PkiTaskIds.HttpsEnable;

    public override string DisplayName => PkiTaskIds.HttpsEnableName;

    /// <summary>The dialog reads the camera clocks first and asks when one is behind (docs/spec/pki.md "Clock check").</summary>
    public override bool RequiresDialog => true;

    /// <summary>The dialog's HTTPS certificate policies and its clock check (<see cref="CameraClock.QueryMethod"/>), read only.</summary>
    public Task<string?> QueryAsync(ITaskQueryContext ctx, IDeviceInfo device, string method, string? payloadJson, CancellationToken ct) =>
        IssuingChoice.QueryAsync(ctx, device, method, Service, Policies.PolicyKinds.HttpsCertificate, Time, ct);

    public override string GetTaskName(string? payloadJson) => "Enable HTTPS";

    public override async Task ExecuteAsync(ITaskExecutionContext ctx, IDeviceInfo device, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(device);
        ctx.PlanSteps(
            PkiSteps.CheckCompatibility, PkiSteps.CheckClock, PkiSteps.ReadWebServer, PkiSteps.ReadNetwork, PkiSteps.InstallCa, PkiSteps.CreateKey,
            PkiSteps.GetCsr, PkiSteps.Sign, PkiSteps.InstallCertificate, PkiSteps.SwitchWebServer, PkiSteps.VerifyHttps, PkiSteps.RemovePrevious);

        var (service, ca, apis, policy, support) = await CheckIssuingTaskAsync(ctx, device, Policies.PolicyKinds.HttpsCertificate, payloadJson, ct).ConfigureAwait(false);
        await CertificateDeployment.CheckClockAsync(ctx, apis, service.Time, CameraClock.Confirmed(payloadJson), ct).ConfigureAwait(false);
        var webServer = await ReadWebServerAsync(ctx, ct).ConfigureAwait(false);
        var network = await ReadNetworkAsync(ctx, apis, ct).ConfigureAwait(false);
        if (webServer.CertificateAlias?.StartsWith(CertificateDeployment.HttpsAliasPrefix, StringComparison.Ordinal) == true)
        {
            ctx.Log(TaskLogLevel.Info, $"The device serves the OADM certificate \"{webServer.CertificateAlias}\"; it is replaced by a new one.");
        }

        await DeployAsync(ctx, service, ca, device, webServer, network, policy, support, stepPrefix: null, ct).ConfigureAwait(false);
    }

    /// <summary>Install CA certificate ... Remove previous OADM certificate (also used by renew and manual install).</summary>
    internal static async Task DeployAsync(
        ITaskExecutionContext ctx, PkiService service, Ca.CaMaterial ca, IDeviceInfo device, WebServerTlsConfiguration webServer, DeviceNetworkInfo? network, Policies.PkiPolicy policy, PkiDeviceSupport support, string? stepPrefix, CancellationToken ct)
    {
        await CertificateDeployment.InstallCaCertificatesAsync(ctx, PkiSteps.Prefixed(stepPrefix, PkiSteps.InstallCa), CertificateDeployment.CaChain(ca), support.Certificates, ct).ConfigureAwait(false);
        var alias = CertificateDeployment.NewAlias(CertificateDeployment.HttpsAliasPrefix, service.Time);
        var names = CertificateDeployment.NamesFor(device, network, service.InAnyGroup(device));
        var issued = await CertificateDeployment.IssueAsync(ctx, service, ca, device, names, CertificatePurpose.Https, alias, policy, support, stepPrefix, ct).ConfigureAwait(false);
        try
        {
            await CertificateDeployment.SwitchWebServerAsync(ctx, webServer, issued.Alias, support.WebServerAllRoles, PkiSteps.Prefixed(stepPrefix, PkiSteps.SwitchWebServer), ct).ConfigureAwait(false);
            await CertificateDeployment.FollowWebServerAsync(ctx, "https", issued.Fingerprint, service.Time, PkiSteps.Prefixed(stepPrefix, PkiSteps.VerifyHttps), CertificateDeployment.SwitchedTo(issued.Alias), TimeSpan.Zero, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Not verified: the previous certificate stays current, the new one is revoked as unused.
            await CertificateDeployment.NotUsedAsync(ctx, service, issued.SerialNumber, evenDelivered: false).ConfigureAwait(false);
            throw;
        }

        await CertificateDeployment.ConfirmAsync(ctx, service, device, issued, CertificatePurpose.Https).ConfigureAwait(false);
        await CertificateDeployment.RemovePreviousAsync(ctx, service, device, CertificatePurpose.Https, [issued.Alias], support.Certificates, PkiSteps.Prefixed(stepPrefix, PkiSteps.RemovePrevious), ct).ConfigureAwait(false);
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

        var support = await ctx.StepAsync(PkiSteps.CheckCompatibility, async step =>
        {
            var support = await PkiCompatibility.RequireAsync(ctx.Vapix, device.Id, Time, ct).ConfigureAwait(false);
            var apis = await ctx.Vapix.GetApiListAsync(ct).ConfigureAwait(false);
            apis.Require("param-cgi", "1.0");
            var parameters = await ctx.Vapix.ListParametersAsync([AuthenticationPolicyParameter], ct).ConfigureAwait(false);
            var policy = parameters.TryGetValue(AuthenticationPolicyParameter, out var value) ? value.Trim() : null;
            if (string.Equals(policy, "basic", StringComparison.OrdinalIgnoreCase))
            {
                throw new DeviceNotCompatibleException(
                    "The device accepts only Basic authentication over HTTP, which OADM never sends unencrypted. Change the HTTP authentication policy first. Nothing was changed.");
            }

            step.Complete($"{support.Describe()} · HTTP authentication {policy ?? "unknown"}");
            return support;
        }).ConfigureAwait(false);

        var webServer = await ReadWebServerAsync(ctx, ct).ConfigureAwait(false);
        var changed = "Nothing was changed on the device.";
        if (ConnectionPolicy.IsHttpOnly(webServer.Policy))
        {
            ctx.SkipStep(PkiSteps.SetHttpOnly, "Already HTTP only");
        }
        else
        {
            await ctx.StepAsync(PkiSteps.SetHttpOnly, async step =>
            {
                var current = support.WebServerAllRoles ? webServer.WithRoles() : webServer;
                await WebServerTls.SetAsync(ctx.Vapix, current.WithPolicies(_ => ConnectionPolicy.Http), ct).ConfigureAwait(false);
                step.Complete("Connection policy HTTP only; certificates stay");
            }).ConfigureAwait(false);
            ctx.Log(TaskLogLevel.Info, $"Connection policy changed from {ConnectionPolicy.Describe(webServer.Policy)} to HTTP only.");
            changed = "The device was set to HTTP only; its certificates stay.";
        }

        await CertificateDeployment.FollowWebServerAsync(ctx, "http", null, Time, PkiSteps.Verify, changed, Service?.WebServerSettle ?? CertificateDeployment.DefaultWebServerSettle, ct).ConfigureAwait(false);
    }
}
