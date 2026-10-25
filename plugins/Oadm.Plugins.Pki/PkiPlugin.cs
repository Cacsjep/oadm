using Oadm.Plugins.Pki.Ca;
using Oadm.Plugins.Pki.Certificates;
using Oadm.Plugins.Pki.Device;
using Oadm.Plugins.Pki.Policies;
using Oadm.Plugins.Pki.Tasks;
using Oadm.Plugins.Shared;
using Oadm.Sdk.Certificates;
using Oadm.Sdk.Events;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Pki;

/// <summary>
/// Core plugin "PKI" ("PKI" in CLAUDE.md): one active certificate authority that issues device certificates for
/// HTTPS and IEEE 802.1X. Part 1: the CA (generated on first start or imported), previous CAs, export and backup, the
/// server's trust store, trust anchors for the device grid's certificate rating, settings. Part 2: the contributed Security
/// task plugins that deploy certificates (<see cref="TaskPlugins"/>). Page backend methods: <see cref="PkiMethods"/>; live event: state.
/// </summary>
public sealed class PkiPlugin : ICorePlugin, ICorePluginDownloads, IEventSource, IAsyncDisposable
{
    private readonly PkiOptions _options;
    private volatile PkiService? _service;
    private ExpiryEvents? _expiry;
    private CancellationTokenSource? _importStop;
    private Task _import = Task.CompletedTask;
    private Oadm.Sdk.Licensing.IProLicense? _pro;
    private EventHandler? _onPro;

    /// <summary>Who revokes in calls without a user (in-process callers, tests).</summary>
    public const string InProcessUser = "OADM";

    public PkiPlugin()
        : this(new PkiOptions())
    {
    }

    public PkiPlugin(PkiOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        PkiService? Current() => _service;
        TaskPlugins =
        [
            new HttpsEnableTask(Current),
            new HttpsDisableTask(Current),
            new Dot1xEnableTask(Current),
            new Dot1xDisableTask(Current),
            new ViewCertificatesTask(Current),
            new DeleteCertificatesTask(Current),
            new InstallCertificatesTask(Current),
            new InstallCaTask(Current),
            new RenewTask(Current),
        ];
    }

    public string Id => PkiPluginInfo.PluginId;

    public string DisplayName => PkiPluginInfo.DisplayName;

    public string? IconKey => PkiPluginInfo.IconKey;
    public CorePluginGroup Group => CorePluginGroup.Security;

    /// <summary>The Security tasks (part 2): HTTPS and IEEE 802.1X enable / disable, view, delete, manual install, install CA certificates, renew.</summary>
    public IReadOnlyList<ITaskPlugin> TaskPlugins { get; }

    /// <summary>The running service (null while stopped).</summary>
    public PkiService? Service => _service;

    /// <summary>"Certificate expires soon", "Certificate expired" and their Any device twins (group "Certificates"; the ids of 4.1's built-in events).</summary>
    public IReadOnlyList<EventTypeInfo> EventTypes => ExpiryEvents.Types;

    /// <summary>The certificate expiry events (null while stopped or without the host's event rules).</summary>
    public ExpiryEvents? Expiry => _expiry;

    public async Task StartAsync(ICorePluginContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        await StopAsync(ct).ConfigureAwait(false);
        var system = ctx.System;
        var service = new PkiService(_options, ctx.Settings, ctx.Secrets, ctx.Events, ctx.TrustAnchors, ctx.Logger, ctx.DeviceScopes, ctx.Records,
            token => system.Devices.ListAsync(token), ctx.Sites, ctx.ProtectedTags);
        await service.StartAsync(ct).ConfigureAwait(false);
        service.IsPro = () => ctx.Pro?.IsActive == true; // the PRO servers' policies show only with OADM PRO
        service.DeviceCertificateReader = async (device, token) =>
        {
            var vapix = await system.Vapix.CreateAsync(device.Id, token).ConfigureAwait(false);
            var support = await PkiCompatibility.CachedAsync(vapix, device, _options.Time, token).ConfigureAwait(false);
            var list = await DeviceCertificates.ListAsync(vapix, support.Certificates, token).ConfigureAwait(false);
            return [.. list.Select(c => (c.Alias, c.Pem))];
        };
        _service = service;
        ctx.Services?.Offer<IDeviceCertificateAuthority>(new PkiCertificateAuthority(service)); // the OADM CA for EST and others
        ctx.Services?.Offer(service.Policies); // the policies for the 802.1X server inside PKI (in process, no SDK contract)
        if (ctx.EventPublisher is { } publisher)
        {
            var expiry = new ExpiryEvents(service.Policies, service.SystemDevicesAsync, publisher, _options.Time, ctx.Logger);
            await expiry.StartAsync(ct).ConfigureAwait(false);
            _expiry = expiry;
        }

        // Certificates of the OADM CAs from before the list of issued certificates: one pass over the devices, in the background.
        var stopping = new CancellationTokenSource();
        _importStop = stopping;
        var import = new ExistingCertificateImport(service, system, ctx.Settings, _options.Time, _options.ExistingImportDelay, ctx.Logger) { RetryInterval = _options.ExistingImportRetry };
        _import = Task.Run(() => import.RunAsync(stopping.Token), CancellationToken.None);

        // OADM PRO active (now, or later): one certificate for the 802.1X and EST server policies that have none (decision
        // 2026-10-25), once both server plugins started (they tell their names) and none of them is turned on (the one that is
        // makes it at its start with its names; the 802.1X server first takes over the certificate it kept).
        if (ctx.Pro is { } pro)
        {
            _pro = pro;
            _onPro = (_, _) => EnsureServerCertificates(service, pro);
            pro.Changed += _onPro;
            service.Policies.ServerPluginsChanged += _onPro;
            EnsureServerCertificates(service, pro);
        }
    }

    /// <summary>OADM PRO is active, both server plugins started and neither server is turned on (its own start makes the certificate).</summary>
    internal static bool MakesServerCertificates(PkiService service, Oadm.Sdk.Licensing.IProLicense pro) =>
        pro.IsActive && service.Policies.ServerPluginsKnown && !service.Policies.AnyServerTurnedOn;

    /// <summary>In the background: never holds up the start or the license change; errors are logged by the service.</summary>
    private static void EnsureServerCertificates(PkiService service, Oadm.Sdk.Licensing.IProLicense pro)
    {
        if (MakesServerCertificates(service, pro))
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await service.EnsureServerCertificatesAsync(null, CancellationToken.None).ConfigureAwait(false);
                }
#pragma warning disable CA1031 // A failure only means the servers make it on Turn on.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    service.LogServerCertificatesFailed(ex);
                }
            });
        }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_pro is { } pro && _onPro is { } onPro)
        {
            pro.Changed -= onPro;
            if (_service is { } running)
            {
                running.Policies.ServerPluginsChanged -= onPro;
            }

            _pro = null;
            _onPro = null;
        }

        if (_expiry is { } expiry)
        {
            _expiry = null;
            await expiry.DisposeAsync().ConfigureAwait(false);
        }

        if (_importStop is { } stop)
        {
            _importStop = null;
            await stop.CancelAsync().ConfigureAwait(false);
            await _import.ConfigureAwait(false); // never throws: the pass logs its own errors
            stop.Dispose();
        }

        if (_service is { } service)
        {
            _service = null;
            await service.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>The one pass over the devices for certificates from before the list (tests wait for it).</summary>
    public Task ExistingImport => _import;

    /// <summary>
    /// Reading the state and the issued certificates is for everyone; generate, import, export, backup, install in the server
    /// root store, the settings and revoking are Admin only (and audited). The Security tasks are task plugins and need no
    /// more than Operator.
    /// </summary>
    public UserRole RequiredRole(string method) => IsRead(method) ? UserRole.Operator : UserRole.Admin;

    private static bool IsRead(string method) => method is PkiMethods.GetState or PkiMethods.GetCertificates or PkiMethods.GetCertificate or PolicyMethods.GetPolicies;

    /// <summary>Admin methods (the Certificates card's Create, Sign request, Import, Delete and Export too) are audited by the host; the count of a revoke policy only reads.</summary>
    public bool IsAudited(string method) => RequiredRole(method) == UserRole.Admin && method != PolicyMethods.CountRevocations;

    /// <summary>The certificate authority ("manage") and the server's trust in it ("trustServer") are rights of their own.</summary>
    public IReadOnlyList<PermissionDefinition> Permissions { get; } =
    [
        new(PermissionKeys.Manage, "Manage the certificate authority", "Create, import, back up and export the certificate authority and change its settings.") { OperatorDefault = false },
        new(PkiPermissions.TrustServer, "Trust it on the server", "Install the certificate authority in the trusted root store of the server computer.") { OperatorDefault = false },
    ];

    /// <summary>Installing the CA in the server's root store needs "trustServer"; the other Admin methods "manage".</summary>
    public string? RequiredPermission(string method) => method == PkiMethods.InstallServerTrust ? PkiPermissions.TrustServer : null;

    /// <summary>
    /// The CA and the trust in it are server-wide (docs/spec/device-scope.md "Server-wide services"): users limited to a
    /// device group may read the state (counts of their own devices), every other method is for users with all devices.
    /// </summary>
    public bool RequiresAllDevices(string method) => !IsRead(method);

    /// <summary>
    /// The page backend with the user's call: the state of a user limited to a group counts only that group's devices, the
    /// issued certificates are only those of the group's devices; revoking names the caller.
    /// </summary>
    public async Task<string?> InvokeAsync(ICallContext caller, string method, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var service = _service ?? throw new InvalidOperationException("The PKI plugin is not running.");
        if (!IsRead(method))
        {
            if (!caller.HasAllDevices)
            {
                throw new DeviceAccessRefusedException(); // the host refuses these first (RequiresAllDevices); fail closed
            }

            return method switch
            {
                PolicyMethods.SavePolicy => PkiJson.Serialize(await service.SavePolicyAsync(PkiJson.Deserialize<PkiPolicy>(payloadJson), caller.UserName, ct).ConfigureAwait(false)),
                PolicyMethods.DeletePolicy => PkiJson.Serialize(await service.DeletePolicyAsync(PkiJson.Deserialize<PolicyRef>(payloadJson).Id, caller.UserName, ct).ConfigureAwait(false)),
                PkiMethods.Revoke => PkiJson.Serialize(await service.RevokeAsync(PkiJson.Deserialize<RevokeRequest>(payloadJson), caller.UserName, ct).ConfigureAwait(false)),
                PkiMethods.RevokeDevice => PkiJson.Serialize(await service.RevokeDeviceAsync(PkiJson.Deserialize<RevokeDeviceRequest>(payloadJson), caller.UserName, ct).ConfigureAwait(false)),
                CertificateMethods.Create or CertificateMethods.Sign or CertificateMethods.Import or CertificateMethods.Delete =>
                    await CertificateActionAsync(service, method, payloadJson, caller.UserName, ct).ConfigureAwait(false),
                _ => await InvokeAsync(method, payloadJson, ct).ConfigureAwait(false),
            };
        }

        service.Viewers.Seen(caller);
        switch (method)
        {
            case PkiMethods.GetCertificates:
            {
                // The caller's devices: every device for a user with all devices (rows of removed devices too), else the group's.
                var devices = await caller.Devices.ListAsync(ct).ConfigureAwait(false);
                return PkiJson.Serialize(service.GetCertificates(PkiJson.Deserialize<CertificatesRequest>(payloadJson), devices, onlyListed: !caller.HasAllDevices));
            }

            case PkiMethods.GetCertificate:
            {
                var view = await ViewedSet.OfAsync(caller, ct).ConfigureAwait(false);
                var devices = view is null ? null : await caller.Devices.ListAsync(ct).ConfigureAwait(false); // the group's: a camera by its serial
                return PkiJson.Serialize(await service.GetCertificateAsync(PkiJson.Deserialize<CertificateDetailsRequest>(payloadJson), view, ct, devices).ConfigureAwait(false));
            }

            case PolicyMethods.GetPolicies:
                // Server-wide: a user limited to a device group gets none; changing needs "Manage the certificate authority".
                return PkiJson.Serialize(await service.PolicyListAsync(caller.HasAllDevices, caller.HasAllDevices && caller.Has(PermissionKeys.Of(Id, PermissionKeys.Manage)), ct)
                    .ConfigureAwait(false));

            default:
            {
                var view = await ViewedSet.OfAsync(caller, ct).ConfigureAwait(false);
                var devices = caller.HasAllDevices ? null : await caller.Devices.ListAsync(ct).ConfigureAwait(false);
                return PkiJson.Serialize(await service.GetStateAsync(view, ct, devices).ConfigureAwait(false));
            }
        }
    }

    /// <summary>In-process callers and tests: the view of a user with all devices (revocations by "OADM").</summary>
    public async Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct)
    {
        var service = _service ?? throw new InvalidOperationException("The PKI plugin is not running.");
        return method switch
        {
            PkiMethods.GetState => PkiJson.Serialize(await service.GetStateAsync(ct).ConfigureAwait(false)),
            PolicyMethods.GetPolicies => PkiJson.Serialize(await service.PolicyListAsync(hasAllDevices: true, mayManage: true, ct).ConfigureAwait(false)),
            PolicyMethods.SavePolicy => PkiJson.Serialize(await service.SavePolicyAsync(PkiJson.Deserialize<PkiPolicy>(payloadJson), InProcessUser, ct).ConfigureAwait(false)),
            PolicyMethods.DeletePolicy => PkiJson.Serialize(await service.DeletePolicyAsync(PkiJson.Deserialize<PolicyRef>(payloadJson).Id, InProcessUser, ct).ConfigureAwait(false)),
            PolicyMethods.CountRevocations => PkiJson.Serialize(await service.CountRevocationsAsync(PkiJson.Deserialize<PolicyDevices>(payloadJson), ct).ConfigureAwait(false)),
            PkiMethods.GetCertificates => PkiJson.Serialize(await service.GetCertificatesForEveryDeviceAsync(PkiJson.Deserialize<CertificatesRequest>(payloadJson), ct).ConfigureAwait(false)),
            PkiMethods.GetCertificate => PkiJson.Serialize(await service.GetCertificateAsync(PkiJson.Deserialize<CertificateDetailsRequest>(payloadJson), null, ct).ConfigureAwait(false)),
            PkiMethods.Revoke => PkiJson.Serialize(await service.RevokeAsync(PkiJson.Deserialize<RevokeRequest>(payloadJson), InProcessUser, ct).ConfigureAwait(false)),
            PkiMethods.RevokeDevice => PkiJson.Serialize(await service.RevokeDeviceAsync(PkiJson.Deserialize<RevokeDeviceRequest>(payloadJson), InProcessUser, ct).ConfigureAwait(false)),
            PkiMethods.Generate => PkiJson.Serialize(await service.GenerateAsync(PkiJson.Deserialize<GenerateRequest>(payloadJson), ct).ConfigureAwait(false)),
            PkiMethods.Import => PkiJson.Serialize(await service.ImportAsync(PkiJson.Deserialize<ImportRequest>(payloadJson), ct).ConfigureAwait(false)),
            PkiMethods.PreviewReplace => PkiJson.Serialize(await service.PreviewReplaceAsync(ct).ConfigureAwait(false)),
            PkiMethods.ExportPublic => PkiJson.Serialize(service.ExportPublic(PkiJson.Deserialize<ExportRequest>(payloadJson))),
            PkiMethods.ExportPrevious => PkiJson.Serialize(service.ExportPrevious(PkiJson.Deserialize<ExportPreviousRequest>(payloadJson))),
            PkiMethods.RemovePrevious => PkiJson.Serialize(await service.RemovePreviousAsync(PkiJson.Deserialize<RemovePreviousRequest>(payloadJson), ct).ConfigureAwait(false)),
            PkiMethods.Backup => PkiJson.Serialize(service.Backup(PkiJson.Deserialize<BackupRequest>(payloadJson))),
            PkiMethods.InstallServerTrust => PkiJson.Serialize(await service.InstallServerTrustAsync(ct).ConfigureAwait(false)),
            PkiMethods.ImportAuthority => PkiJson.Serialize(await service.ImportAuthorityAsync(PkiJson.Deserialize<ImportAuthorityRequest>(payloadJson), ct).ConfigureAwait(false)),
            PkiMethods.RemoveAuthority => PkiJson.Serialize(await service.RemoveAuthorityAsync(PkiJson.Deserialize<RemoveAuthorityRequest>(payloadJson), ct).ConfigureAwait(false)),
            CertificateMethods.Create or CertificateMethods.Sign or CertificateMethods.Import or CertificateMethods.Delete =>
                await CertificateActionAsync(service, method, payloadJson, InProcessUser, ct).ConfigureAwait(false),
            _ => throw new ArgumentException($"Unknown method '{method}'.", nameof(method)),
        };
    }

    /// <summary>Create, Sign request, Import and Delete of the Certificates card in the name of <paramref name="userName"/>.</summary>
    private static async Task<string?> CertificateActionAsync(PkiService service, string method, string? payloadJson, string userName, CancellationToken ct) => method switch
    {
        CertificateMethods.Create => PkiJson.Serialize(await service.CreateCertificateAsync(PkiJson.Deserialize<CreateCertificateRequest>(payloadJson), userName, ct).ConfigureAwait(false)),
        CertificateMethods.Sign => PkiJson.Serialize(await service.SignCertificateAsync(PkiJson.Deserialize<SignCertificateRequest>(payloadJson), userName, ct).ConfigureAwait(false)),
        CertificateMethods.Import => PkiJson.Serialize(await service.ImportCertificateAsync(PkiJson.Deserialize<ImportCertificateRequest>(payloadJson), userName, ct).ConfigureAwait(false)),
        _ => PkiJson.Serialize(await service.DeleteCertificatesAsync(PkiJson.Deserialize<DeleteCertificatesRequest>(payloadJson), userName, ct).ConfigureAwait(false)),
    };

    /// <summary>Export of the Certificates card (users with all devices; the host checks "Manage the certificate authority" and audits).</summary>
    public Task<PluginDownload> OpenDownloadAsync(ICallContext caller, string method, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (!caller.HasAllDevices)
        {
            throw new DeviceAccessRefusedException(); // the host refuses it first (RequiresAllDevices); fail closed
        }

        return OpenDownloadAsync(method, payloadJson, caller.UserName, ct);
    }

    /// <summary>In-process callers and tests: Export in the name of "OADM".</summary>
    public Task<PluginDownload> OpenDownloadAsync(string method, string? payloadJson, CancellationToken ct) => OpenDownloadAsync(method, payloadJson, InProcessUser, ct);

    private Task<PluginDownload> OpenDownloadAsync(string method, string? payloadJson, string userName, CancellationToken ct)
    {
        var service = _service ?? throw new InvalidOperationException("The PKI plugin is not running.");
        return method == CertificateMethods.Download
            ? service.OpenCertificateDownloadAsync(PkiJson.Deserialize<DownloadCertificateRequest>(payloadJson), userName, ct)
            : throw new ArgumentException($"Unknown download '{method}'.", nameof(method));
    }

    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None).ConfigureAwait(false);
}
