using Oadm.Plugins.Pki.Device;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Pki.Tasks;

/// <summary>
/// Common part of the Security tasks the PKI core plugin contributes: group Security, no toolbar, CanRun on cached data
/// (AXIS OS 11.11+), the PKI service through the owning plugin.
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

    /// <summary>"Needs AXIS OS 11.11 or later (this device has 10.12.338)" when <see cref="CanRun"/> is false.</summary>
    public virtual string? NotSupportedReason(IDeviceInfo device) => PkiCompatibility.NotSupportedReason(device, needsNetworkSettings: false);

    public virtual string GetTaskName(string? payloadJson) => DisplayName;

    public abstract Task ExecuteAsync(ITaskExecutionContext ctx, IDeviceInfo device, string? payloadJson, CancellationToken ct);

    /// <summary>"Check compatibility" of the certificate tasks: the CA, a fresh config/discover (cert v1) and the API list.</summary>
    protected async Task<(PkiService Service, Ca.CaMaterial Ca, IReadOnlyList<DeviceApi> Apis)> CheckCertificateTaskAsync(
        ITaskExecutionContext ctx, IDeviceInfo device, CancellationToken ct, Action<IReadOnlyList<DeviceApi>>? more = null)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(device);
        return await ctx.StepAsync(PkiSteps.CheckCompatibility, async step =>
        {
            var (service, ca) = CertificateDeployment.RequireCa(Service);
            var support = await PkiCompatibility.RequireCertApiAsync(ctx.Vapix, device.Id, service.Time, ct).ConfigureAwait(false);
            var apis = await ctx.Vapix.GetApiListAsync(ct).ConfigureAwait(false);
            more?.Invoke(apis);
            step.Complete("cert " + support.Version);
            return (service, ca, apis);
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
