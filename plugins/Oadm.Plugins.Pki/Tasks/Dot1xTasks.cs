using Oadm.Plugins.Pki.Device;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Pki.Tasks;

/// <summary>
/// "IEEE 802.1X: Enable/Update" (EAP-TLS with a client certificate from the OADM CA). Safety: fails before any write when the
/// device clock is more than 5 minutes off, the CA chain is incomplete or 802.1X is not offered; the client asks for
/// confirmation first. Steps: Check compatibility, Check device clock, Install CA certificates, Create key on the device, Get
/// certificate request, Sign certificate, Install certificate, Set 802.1X configuration, Verify 802.1X settings, Remove
/// previous OADM certificate.
/// </summary>
public sealed class Dot1xEnableTask(Func<PkiService?> service) : PkiTaskBase(service)
{
    public const int MaxIdentityLength = 128;

    public override string Id => PkiTaskIds.Dot1xEnable;

    public override string DisplayName => PkiTaskIds.Dot1xEnableName;

    public override bool RequiresDialog => true;

    public override bool CanRun(IDeviceInfo device) => base.CanRun(device) && PkiCompatibility.HasNetworkSettings(device.Apis);

    public override string GetTaskName(string? payloadJson) => "Enable IEEE 802.1X";

    public override async Task ExecuteAsync(ITaskExecutionContext ctx, IDeviceInfo device, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(device);
        ctx.PlanSteps(
            PkiSteps.CheckCompatibility, PkiSteps.CheckClock, PkiSteps.InstallCas, PkiSteps.CreateKey, PkiSteps.GetCsr, PkiSteps.Sign,
            PkiSteps.InstallCertificate, PkiSteps.SetDot1x, PkiSteps.VerifyDot1x, PkiSteps.RemovePrevious);

        var (service, ca, apis, network, radius) = await CheckAsync(ctx, device, ct).ConfigureAwait(false);
        await CertificateDeployment.CheckClockAsync(ctx, apis, service.Time, ct).ConfigureAwait(false);
        await DeployAsync(ctx, service, ca, device, apis, network, radius, stepPrefix: null, ct).ConfigureAwait(false);
    }

    /// <summary>Check compatibility: CA (+ complete chain), cert v1, network-settings 1.x with wired 802.1X, RADIUS CA, identity.</summary>
    internal async Task<(PkiService Service, Ca.CaMaterial Ca, IReadOnlyList<DeviceApi> Apis, DeviceNetworkInfo Network, IReadOnlyList<(string Alias, string Pem, string Fingerprint)> RadiusCas)> CheckAsync(
        ITaskExecutionContext ctx, IDeviceInfo device, CancellationToken ct) =>
        await ctx.StepAsync(PkiSteps.CheckCompatibility, async step =>
        {
            var (service, ca) = CertificateDeployment.RequireCa(Service);
            if (!CertificateDeployment.ChainComplete(ca))
            {
                throw new InvalidOperationException(
                    $"The CA chain is incomplete: the root above {Ca.CaCertificates.CommonName(ca.Chain.Count > 0 ? ca.Chain[^1] : ca.Certificate)} is missing. Import the CA with its full chain. Nothing was changed.");
            }

            var radius = CertificateDeployment.RadiusCas(service.Config, ca);
            var support = await PkiCompatibility.RequireCertApiAsync(ctx.Vapix, device.Id, service.Time, ct).ConfigureAwait(false);
            var apis = await ctx.Vapix.GetApiListAsync(ct).ConfigureAwait(false);
            apis.Require(PkiCompatibility.NetworkSettings, "1.0");
            var network = await NetworkInfoApi.GetAsync(ctx.Vapix, apis, ct).ConfigureAwait(false);
            if (network.Dot1x is null)
            {
                throw new DeviceNotCompatibleException("The device does not offer IEEE 802.1X on a wired interface. Nothing was changed.");
            }

            var identity = CertificateDeployment.Identity(service.Config.Dot1x, device, network);
            if (identity.Length is 0 or > MaxIdentityLength)
            {
                throw new InvalidOperationException($"The EAP identity must be 1 to {MaxIdentityLength} characters. Nothing was changed.");
            }

            step.Complete($"cert {support.Version} · network-settings {apis.FindApi(PkiCompatibility.NetworkSettings, 1)?.Version} · {network.Dot1x.DeviceName}");
            return (service, ca, apis, network, radius);
        }).ConfigureAwait(false);

    /// <summary>Install CA certificates ... Remove previous OADM certificate (also used by renew).</summary>
    internal static async Task DeployAsync(
        ITaskExecutionContext ctx,
        PkiService service,
        Ca.CaMaterial ca,
        IDeviceInfo device,
        IReadOnlyList<DeviceApi> apis,
        DeviceNetworkInfo network,
        IReadOnlyList<(string Alias, string Pem, string Fingerprint)> radiusCas,
        string? stepPrefix,
        CancellationToken ct)
    {
        // The RADIUS server CA (certsCA) plus the OADM CA chain (the client certificate's issuers).
        var wanted = radiusCas.Concat(CertificateDeployment.CaChain(ca)).DistinctBy(c => c.Fingerprint, StringComparer.OrdinalIgnoreCase).ToList();
        var aliases = await CertificateDeployment.InstallCaCertificatesAsync(ctx, PkiSteps.Prefixed(stepPrefix, PkiSteps.InstallCas), wanted, ct).ConfigureAwait(false);
        var certsCa = aliases.Take(radiusCas.Count).ToList();

        var alias = CertificateDeployment.NewAlias(CertificateDeployment.Dot1xAliasPrefix, service.Time);
        var names = CertificateDeployment.NamesFor(device, network);
        var issued = await CertificateDeployment.IssueAsync(ctx, service, ca, device, names, CertificatePurpose.Dot1x, alias, stepPrefix, ct).ConfigureAwait(false);
        await ConfigureAsync(ctx, service.Config.Dot1x, device, apis, network, issued.Alias, certsCa, stepPrefix, ct).ConfigureAwait(false);
        await CertificateDeployment.RemovePreviousAsync(ctx, service, device, CertificatePurpose.Dot1x, [issued.Alias], PkiSteps.Prefixed(stepPrefix, PkiSteps.RemovePrevious), ct).ConfigureAwait(false);
    }

    /// <summary>Set 802.1X configuration + Verify 802.1X settings (read back; a mismatch is a warning).</summary>
    internal static async Task ConfigureAsync(
        ITaskExecutionContext ctx, Dot1xConfig config, IDeviceInfo device, IReadOnlyList<DeviceApi> apis, DeviceNetworkInfo network, string certClient, IReadOnlyList<string> certsCa, string? stepPrefix, CancellationToken ct)
    {
        var identity = CertificateDeployment.Identity(config, device, network);
        var deviceName = network.Dot1x?.DeviceName ?? "eth0";
        await ctx.StepAsync(PkiSteps.Prefixed(stepPrefix, PkiSteps.SetDot1x), async step =>
        {
            await NetworkInfoApi.EnableEapTlsAsync(ctx.Vapix, apis, deviceName, identity, config.EapolVersion, certClient, certsCa, ct).ConfigureAwait(false);
            step.Complete($"EAP-TLS · identity {identity} · {NetworkInfoApi.EapolName(config.EapolVersion)}");
        }).ConfigureAwait(false);
        ctx.Log(TaskLogLevel.Info, $"IEEE 802.1X enabled on {deviceName}: EAP-TLS, identity {identity}, client certificate \"{certClient}\", CA certificates {string.Join(", ", certsCa)}.");

        await ctx.StepAsync(PkiSteps.Prefixed(stepPrefix, PkiSteps.VerifyDot1x), async step =>
        {
            var after = (await NetworkInfoApi.GetAsync(ctx.Vapix, apis, ct).ConfigureAwait(false)).Dot1x;
            var problems = new List<string>();
            if (after is null || !after.Enabled)
            {
                problems.Add("802.1X is not enabled");
            }

            if (after is not null && !string.Equals(after.Mode, NetworkInfoApi.EapTlsMode, StringComparison.Ordinal))
            {
                problems.Add("mode " + (after.Mode ?? "unknown"));
            }

            if (after is not null && !string.Equals(after.CertClient, certClient, StringComparison.Ordinal))
            {
                problems.Add("client certificate " + (after.CertClient ?? "none"));
            }

            if (after is not null && certsCa.Any(c => !after.CertsCa.Contains(c, StringComparer.Ordinal)))
            {
                problems.Add("CA certificates " + string.Join(", ", after.CertsCa));
            }

            if (problems.Count == 0)
            {
                step.Complete($"Enabled · status {after!.Status ?? "unknown"}");
            }
            else
            {
                step.Warn("The device reports " + string.Join(", ", problems) + ".");
            }
        }).ConfigureAwait(false);
    }
}

/// <summary>"IEEE 802.1X: Disable": 802.1X off, certificates stay. Steps: Check compatibility, Set 802.1X off, Verify.</summary>
public sealed class Dot1xDisableTask(Func<PkiService?> service) : PkiTaskBase(service)
{
    public override string Id => PkiTaskIds.Dot1xDisable;

    public override string DisplayName => PkiTaskIds.Dot1xDisableName;

    public override string? IconKey => "cancel";

    public override bool CanRun(IDeviceInfo device) => base.CanRun(device) && PkiCompatibility.HasNetworkSettings(device.Apis);

    public override string GetTaskName(string? payloadJson) => "Disable IEEE 802.1X";

    public override async Task ExecuteAsync(ITaskExecutionContext ctx, IDeviceInfo device, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(device);
        ctx.PlanSteps(PkiSteps.CheckCompatibility, PkiSteps.SetDot1xOff, PkiSteps.Verify);
        var (apis, dot1x) = await ctx.StepAsync(PkiSteps.CheckCompatibility, async step =>
        {
            var list = await ctx.Vapix.GetApiListAsync(ct).ConfigureAwait(false);
            list.Require(PkiCompatibility.NetworkSettings, "1.0");
            var network = await NetworkInfoApi.GetAsync(ctx.Vapix, list, ct).ConfigureAwait(false);
            var wired = network.Dot1x ?? throw new DeviceNotCompatibleException("The device does not offer IEEE 802.1X on a wired interface. Nothing was changed.");
            step.Complete($"network-settings {list.FindApi(PkiCompatibility.NetworkSettings, 1)?.Version} · 802.1X {(wired.Enabled ? "on" : "off")}");
            return (list, wired);
        }).ConfigureAwait(false);

        if (!dot1x.Enabled)
        {
            ctx.SkipStep(PkiSteps.SetDot1xOff, "Already off");
        }
        else
        {
            await ctx.StepAsync(PkiSteps.SetDot1xOff, async step =>
            {
                await NetworkInfoApi.DisableAsync(ctx.Vapix, apis, dot1x.DeviceName, ct).ConfigureAwait(false);
                step.Complete("Certificates stay on the device");
            }).ConfigureAwait(false);
            ctx.Log(TaskLogLevel.Info, $"IEEE 802.1X turned off on {dot1x.DeviceName}.");
        }

        await ctx.StepAsync(PkiSteps.Verify, async step =>
        {
            var after = (await NetworkInfoApi.GetAsync(ctx.Vapix, apis, ct).ConfigureAwait(false)).Dot1x;
            if (after is { Enabled: true })
            {
                step.Warn("The device still reports 802.1X as enabled.");
            }
            else
            {
                step.Complete("802.1X is off");
            }
        }).ConfigureAwait(false);
    }
}
