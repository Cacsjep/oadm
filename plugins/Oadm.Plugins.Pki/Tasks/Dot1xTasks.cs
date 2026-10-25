using Oadm.Plugins.DeviceClocks;
using Oadm.Plugins.Pki.Device;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Pki.Tasks;

/// <summary>
/// "Enable IEEE 802.1X" (EAP-TLS with a client certificate from the OADM CA). Safety: fails before any write when the
/// device clock is more than 5 minutes off, the CA chain is incomplete, the policy's EAPOL version or key does not work on
/// the device or 802.1X is not offered; the client asks for confirmation first. Steps: Check compatibility, Check device
/// clock, Install CA certificates, Create key on the device, Get certificate request, Sign certificate, Install certificate,
/// Set 802.1X configuration, Verify 802.1X settings, Remove previous OADM certificate. How 802.1X is set follows the device
/// (<see cref="Dot1xSettings"/>, <see cref="PkiCompatibility.Dot1xApiOf"/>).
/// </summary>
public sealed class Dot1xEnableTask(Func<PkiService?> service) : PkiTaskBase(service), ITaskPluginQuery
{
    public const int MaxIdentityLength = 128;

    /// <summary>The refusal of a device without wired 802.1X.</summary>
    public const string NotOffered = "The device does not offer IEEE 802.1X on a wired interface. Nothing was changed.";

    public override string Id => PkiTaskIds.Dot1xEnable;

    public override string DisplayName => PkiTaskIds.Dot1xEnableName;

    public override bool RequiresDialog => true;

    public override string GetTaskName(string? payloadJson) => "Enable IEEE 802.1X";

    public override async Task ExecuteAsync(ITaskExecutionContext ctx, IDeviceInfo device, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(device);
        ctx.PlanSteps(
            PkiSteps.CheckCompatibility, PkiSteps.CheckClock, PkiSteps.InstallCas, PkiSteps.CreateKey, PkiSteps.GetCsr, PkiSteps.Sign,
            PkiSteps.InstallCertificate, PkiSteps.SetDot1x, PkiSteps.VerifyDot1x, PkiSteps.RemovePrevious);

        var (service, ca, apis, state, radius, policy, support) = await CheckAsync(ctx, device, payloadJson, ct).ConfigureAwait(false);
        await CertificateDeployment.CheckClockAsync(ctx, apis, service.Time, CameraClock.Confirmed(payloadJson), ct).ConfigureAwait(false);
        await DeployAsync(ctx, service, ca, device, apis, state, radius, policy, support, stepPrefix: null, ct).ConfigureAwait(false);
    }

    /// <summary>The dialog's IEEE 802.1X certificate policies and its clock check (<see cref="CameraClock.QueryMethod"/>), read only.</summary>
    public Task<string?> QueryAsync(ITaskQueryContext ctx, IDeviceInfo device, string method, string? payloadJson, CancellationToken ct) =>
        IssuingChoice.QueryAsync(ctx, device, method, Service, Policies.PolicyKinds.Dot1xCertificate, Time, ct);

    /// <summary>
    /// Fresh 802.1X state for Check compatibility: how it is set and its settings; throws "Nothing was changed" when the
    /// device has no wired 802.1X.
    /// </summary>
    internal static async Task<Dot1xState> RequireDot1xAsync(IVapixClient vapix, IReadOnlyList<DeviceApi> apis, CancellationToken ct)
    {
        var state = await Dot1xSettings.ReadAsync(vapix, apis, ct).ConfigureAwait(false);
        return state.Dot1x is null ? throw new DeviceNotCompatibleException(NotOffered) : state;
    }

    /// <summary>Check compatibility: CA (+ complete chain), certificate API, wired 802.1X, RADIUS CA, identity, EAPOL version, key.</summary>
    internal async Task<(PkiService Service, Ca.CaMaterial Ca, IReadOnlyList<DeviceApi> Apis, Dot1xState State, IReadOnlyList<(string Alias, string Pem, string Fingerprint)> RadiusCas, Policies.PkiPolicy Policy, PkiDeviceSupport Support)> CheckAsync(
        ITaskExecutionContext ctx, IDeviceInfo device, string? payloadJson, CancellationToken ct) =>
        await ctx.StepAsync(PkiSteps.CheckCompatibility, async step =>
        {
            var service = CertificateDeployment.RequireService(Service);
            var policy = IssuingChoice.Resolve(service, Policies.PolicyKinds.Dot1xCertificate, payloadJson);
            var ca = CertificateDeployment.RequireCa(service, policy);
            if (!CertificateDeployment.ChainComplete(ca))
            {
                throw new InvalidOperationException(
                    $"The CA chain is incomplete: the root above {Ca.CaCertificates.CommonName(ca.Chain.Count > 0 ? ca.Chain[^1] : ca.Certificate)} is missing. Import the CA with its full chain. Nothing was changed.");
            }

            var radius = CertificateDeployment.RadiusCas(policy, service);
            var support = await PkiCompatibility.RequireAsync(ctx.Vapix, device.Id, service.Time, ct).ConfigureAwait(false);
            PkiCompatibility.RequireEapol(policy, support);
            var apis = await ctx.Vapix.GetApiListAsync(ct).ConfigureAwait(false);
            var state = await RequireDot1xAsync(ctx.Vapix, apis, ct).ConfigureAwait(false);
            var identity = CertificateDeployment.Identity(policy, device, state.Network);
            if (identity.Length is 0 or > MaxIdentityLength)
            {
                throw new InvalidOperationException($"The EAP identity must be 1 to {MaxIdentityLength} characters. Nothing was changed.");
            }

            await CertificateDeployment.RequireKeyAsync(ctx.Vapix, policy, support, ct).ConfigureAwait(false);
            step.Complete($"{support.Describe()} · {state.Describe()} · {state.Dot1x!.DeviceName} · {IssuingChoice.Describe(policy, Ca.CaCertificates.CommonName(ca.Certificate))}");
            return (service, ca, apis, state, radius, policy, support);
        }).ConfigureAwait(false);

    /// <summary>Install CA certificates ... Remove previous OADM certificate (also used by renew).</summary>
    internal static async Task DeployAsync(
        ITaskExecutionContext ctx,
        PkiService service,
        Ca.CaMaterial ca,
        IDeviceInfo device,
        IReadOnlyList<DeviceApi> apis,
        Dot1xState state,
        IReadOnlyList<(string Alias, string Pem, string Fingerprint)> radiusCas,
        Policies.PkiPolicy policy,
        PkiDeviceSupport support,
        string? stepPrefix,
        CancellationToken ct)
    {
        // The RADIUS server CA (certsCA) plus the OADM CA chain (the client certificate's issuers).
        var wanted = radiusCas.Concat(CertificateDeployment.CaChain(ca)).DistinctBy(c => c.Fingerprint, StringComparer.OrdinalIgnoreCase).ToList();
        var aliases = await CertificateDeployment.InstallCaCertificatesAsync(ctx, PkiSteps.Prefixed(stepPrefix, PkiSteps.InstallCas), wanted, support.Certificates, ct).ConfigureAwait(false);
        var certsCa = aliases.Take(radiusCas.Count).ToList();

        var alias = CertificateDeployment.NewAlias(CertificateDeployment.Dot1xAliasPrefix, service.Time);
        var names = CertificateDeployment.NamesFor(device, state.Network, service.InAnyGroup(device));
        var issued = await CertificateDeployment.IssueAsync(ctx, service, ca, device, names, CertificatePurpose.Dot1x, alias, policy, support, stepPrefix, ct).ConfigureAwait(false);
        var configured = false;
        Dot1xCheck check;
        try
        {
            check = await ConfigureAsync(ctx, policy, device, apis, state, issued.Alias, certsCa, stepPrefix, ct, () =>
            {
                // From here on the camera may use it: never revoked as unused (device safety, no lock-out).
                configured = true;
                service.Issued.MarkDelivered(issued.SerialNumber);
            }).ConfigureAwait(false);
        }
        catch (Exception) when (!configured)
        {
            await CertificateDeployment.NotUsedAsync(ctx, service, issued.SerialNumber, evenDelivered: false).ConfigureAwait(false);
            throw;
        }

        switch (check)
        {
            case Dot1xCheck.Confirmed:
                await CertificateDeployment.ConfirmAsync(ctx, service, device, issued, CertificatePurpose.Dot1x).ConfigureAwait(false);
                break;
            case Dot1xCheck.OtherCertificate:
                await CertificateDeployment.NotUsedAsync(ctx, service, issued.SerialNumber, evenDelivered: true).ConfigureAwait(false);
                break;
            default:
                ctx.Log(TaskLogLevel.Info, $"The new certificate \"{issued.Alias}\" stays pending: the camera's 802.1X settings could not confirm it. The previous certificate stays valid.");
                break;
        }

        if (check != Dot1xCheck.Confirmed)
        {
            // The camera may still use a previous certificate for network access: none is deleted (no lock-out).
            ctx.SkipStep(PkiSteps.Prefixed(stepPrefix, PkiSteps.RemovePrevious), "Kept: the camera does not use the new certificate yet");
            return;
        }

        await CertificateDeployment.RemovePreviousAsync(ctx, service, device, CertificatePurpose.Dot1x, [issued.Alias], support.Certificates, PkiSteps.Prefixed(stepPrefix, PkiSteps.RemovePrevious), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Set 802.1X configuration + Verify 802.1X settings (read back the way it was set; a mismatch is a warning).
    /// <paramref name="configuring"/> runs right before the configuration is written. The answer: the read-back confirms the
    /// certificate, names another client certificate, or says something else is off.
    /// </summary>
    internal static async Task<Dot1xCheck> ConfigureAsync(
        ITaskExecutionContext ctx, Policies.PkiPolicy policy, IDeviceInfo device, IReadOnlyList<DeviceApi> apis, Dot1xState state, string certClient, IReadOnlyList<string> certsCa, string? stepPrefix, CancellationToken ct,
        Action? configuring = null)
    {
        var identity = CertificateDeployment.Identity(policy, device, state.Network);
        var eapol = policy.EapolVersion ?? 1;
        var deviceName = state.Dot1x?.DeviceName ?? "eth0";
        var check = Dot1xCheck.Unclear;
        await ctx.StepAsync(PkiSteps.Prefixed(stepPrefix, PkiSteps.SetDot1x), async step =>
        {
            configuring?.Invoke();
            await Dot1xSettings.EnableAsync(ctx.Vapix, state, apis, identity, eapol, certClient, certsCa, ct).ConfigureAwait(false);
            step.Complete($"EAP-TLS · identity {identity} · {NetworkInfoApi.EapolName(eapol)}");
        }).ConfigureAwait(false);
        ctx.Log(TaskLogLevel.Info, $"IEEE 802.1X enabled on {deviceName}: EAP-TLS, identity {identity}, client certificate \"{certClient}\", CA certificates {string.Join(", ", certsCa)}.");

        await ctx.StepAsync(PkiSteps.Prefixed(stepPrefix, PkiSteps.VerifyDot1x), async step =>
        {
            var after = (await Dot1xSettings.ReadAsync(ctx.Vapix, apis, ct).ConfigureAwait(false)).Dot1x;
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
                check = Dot1xCheck.Confirmed;
                step.Complete($"Enabled · status {after!.Status ?? "unknown"}");
            }
            else
            {
                check = after is not null && !string.Equals(after.CertClient, certClient, StringComparison.Ordinal) ? Dot1xCheck.OtherCertificate : Dot1xCheck.Unclear;
                step.Warn("The device reports " + string.Join(", ", problems) + ".");
            }
        }).ConfigureAwait(false);
        return check;
    }
}

/// <summary>What the read-back of "Verify 802.1X settings" says about the new client certificate.</summary>
internal enum Dot1xCheck
{
    /// <summary>Something else is off (or nothing could be read): the certificate may be in use.</summary>
    Unclear,

    /// <summary>802.1X is on with the new certificate and the CA certificates.</summary>
    Confirmed,

    /// <summary>The camera reports another client certificate: the new one is not in use.</summary>
    OtherCertificate,
}

/// <summary>"Disable IEEE 802.1X": 802.1X off, certificates stay. Steps: Check compatibility, Set 802.1X off, Verify.</summary>
public sealed class Dot1xDisableTask(Func<PkiService?> service) : PkiTaskBase(service)
{
    public override string Id => PkiTaskIds.Dot1xDisable;

    public override string DisplayName => PkiTaskIds.Dot1xDisableName;

    public override string? IconKey => "cancel";

    public override string GetTaskName(string? payloadJson) => "Disable IEEE 802.1X";

    public override async Task ExecuteAsync(ITaskExecutionContext ctx, IDeviceInfo device, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(device);
        ctx.PlanSteps(PkiSteps.CheckCompatibility, PkiSteps.SetDot1xOff, PkiSteps.Verify);
        var (apis, state) = await ctx.StepAsync(PkiSteps.CheckCompatibility, async step =>
        {
            var support = await PkiCompatibility.RequireAsync(ctx.Vapix, device.Id, Time, ct).ConfigureAwait(false);
            var list = await ctx.Vapix.GetApiListAsync(ct).ConfigureAwait(false);
            var read = await Dot1xEnableTask.RequireDot1xAsync(ctx.Vapix, list, ct).ConfigureAwait(false);
            step.Complete($"{support.Describe()} · {read.Describe()} · 802.1X {(read.Dot1x!.Enabled ? "on" : "off")}");
            return (list, read);
        }).ConfigureAwait(false);
        var dot1x = state.Dot1x!;

        if (!dot1x.Enabled)
        {
            ctx.SkipStep(PkiSteps.SetDot1xOff, "Already off");
        }
        else
        {
            await ctx.StepAsync(PkiSteps.SetDot1xOff, async step =>
            {
                await Dot1xSettings.DisableAsync(ctx.Vapix, state, apis, ct).ConfigureAwait(false);
                step.Complete("Certificates stay on the device");
            }).ConfigureAwait(false);
            ctx.Log(TaskLogLevel.Info, $"IEEE 802.1X turned off on {dot1x.DeviceName}.");
        }

        await ctx.StepAsync(PkiSteps.Verify, async step =>
        {
            var after = (await Dot1xSettings.ReadAsync(ctx.Vapix, apis, ct).ConfigureAwait(false)).Dot1x;
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
