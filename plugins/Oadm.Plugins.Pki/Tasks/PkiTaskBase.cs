using Oadm.Plugins.Pki.Device;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Pki.Tasks;

/// <summary>
/// Common part of the Security tasks the PKI core plugin contributes: group Security, no toolbar, CanRun on cached data
/// (AXIS OS 6.53+), the PKI service through the owning plugin.
/// </summary>
public abstract class PkiTaskBase : ITaskPlugin
{
    private readonly Func<PkiService?> _service;

    protected PkiTaskBase(Func<PkiService?> service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
    }

    public abstract string Id { get; }

    public abstract string DisplayName { get; }

    public virtual string? IconKey => PkiPluginInfo.IconKey;

    public string Group => TaskGroups.Security;

    public bool ShowInToolbar => false;

    public virtual bool RequiresDialog => false;

    /// <summary>Certificate work on many devices at once loads the server (signing) and the network little; 8 is fine.</summary>
    public virtual int? MaxParallelDevices => null;

    /// <summary>The PKI service (null while the core plugin is stopped).</summary>
    protected PkiService? Service => _service();

    /// <summary>The server clock (the PKI options' clock when running).</summary>
    protected TimeProvider Time => _service()?.Time ?? TimeProvider.System;

    public virtual bool CanRun(IDeviceInfo device) => PkiCompatibility.CanRunCertificateTask(device);

    /// <summary>"Needs AXIS OS 6.53 or later (this device has 6.50.5)" when <see cref="CanRun"/> is false.</summary>
    public virtual string? NotSupportedReason(IDeviceInfo device) => PkiCompatibility.NotSupportedReason(device);

    public virtual string GetTaskName(string? payloadJson) => DisplayName;

    public abstract Task ExecuteAsync(ITaskExecutionContext ctx, IDeviceInfo device, string? payloadJson, CancellationToken ct);

    /// <summary>
    /// "Check compatibility" of the certificate tasks: PKI runs, the certificate API from a fresh config/discover (else the
    /// AXIS OS version for the SOAP path, <see cref="PkiCompatibility.RequireAsync"/>) and the API list (the CA of each policy
    /// is checked where it issues: decision 2026-10-25, several CAs issue).
    /// </summary>
    protected async Task<(PkiService Service, IReadOnlyList<DeviceApi> Apis, PkiDeviceSupport Support)> CheckCertificateTaskAsync(
        ITaskExecutionContext ctx, IDeviceInfo device, CancellationToken ct, Action<IReadOnlyList<DeviceApi>>? more = null)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(device);
        return await ctx.StepAsync(PkiSteps.CheckCompatibility, async step =>
        {
            var service = CertificateDeployment.RequireService(Service);
            var support = await PkiCompatibility.RequireAsync(ctx.Vapix, device.Id, service.Time, ct).ConfigureAwait(false);
            var apis = await ctx.Vapix.GetApiListAsync(ct).ConfigureAwait(false);
            more?.Invoke(apis);
            step.Complete(support.Describe());
            return (service, apis, support);
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// "Check compatibility" of a task that issues with a policy of <paramref name="kind"/>: the policy of the run with the
    /// dialog's values and the CA it names ("Issued by") (<see cref="IssuingChoice.Resolve"/>), the certificate API
    /// (<see cref="PkiCompatibility.RequireAsync"/>), the API list and the key type the device can make. Everything before
    /// the first write.
    /// </summary>
    protected async Task<(PkiService Service, Ca.CaMaterial Ca, IReadOnlyList<DeviceApi> Apis, Policies.PkiPolicy Policy, PkiDeviceSupport Support)> CheckIssuingTaskAsync(
        ITaskExecutionContext ctx, IDeviceInfo device, string kind, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(device);
        return await ctx.StepAsync(PkiSteps.CheckCompatibility, async step =>
        {
            var service = CertificateDeployment.RequireService(Service);
            var policy = IssuingChoice.Resolve(service, kind, payloadJson);
            var ca = CertificateDeployment.RequireCa(service, policy);
            var support = await PkiCompatibility.RequireAsync(ctx.Vapix, device.Id, service.Time, ct).ConfigureAwait(false);
            var apis = await ctx.Vapix.GetApiListAsync(ct).ConfigureAwait(false);
            await CertificateDeployment.RequireKeyAsync(ctx.Vapix, policy, support, ct).ConfigureAwait(false);
            step.Complete($"{support.Describe()} · {IssuingChoice.Describe(policy, Ca.CaCertificates.CommonName(ca.Certificate))}");
            return (service, ca, apis, policy, support);
        }).ConfigureAwait(false);
    }

    /// <summary>The web server settings; a missing web server TLS service fails with "Nothing was changed".</summary>
    protected static async Task<WebServerTlsConfiguration> ReadWebServerAsync(ITaskExecutionContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return await ctx.StepAsync(PkiSteps.ReadWebServer, async step =>
        {
            WebServerTlsConfiguration configuration;
            try
            {
                configuration = await WebServerTls.GetAsync(ctx.Vapix, ct).ConfigureAwait(false);
            }
            catch (PkiDeviceException ex)
            {
                throw new DeviceNotCompatibleException("The device's web server settings cannot be read (" + ex.Message + "). Nothing was changed.", ex);
            }

            step.Complete($"{ConnectionPolicy.Describe(configuration.Policy)} · certificate {configuration.CertificateAlias ?? "none"}");
            return configuration;
        }).ConfigureAwait(false);
    }
}
