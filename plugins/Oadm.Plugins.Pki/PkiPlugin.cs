using Oadm.Plugins.Pki.Ca;
using Oadm.Plugins.Pki.Tasks;
using Oadm.Plugins.Shared;
using Oadm.Sdk.Certificates;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Pki;

/// <summary>
/// Core plugin "PKI" ("PKI" in CLAUDE.md): one active certificate authority that issues device certificates for
/// HTTPS and IEEE 802.1X. Part 1: the CA (generated on first start or imported), previous CAs, export and backup, the
/// server's trust store, trust anchors for the device grid's certificate rating, settings. Part 2: the contributed Security
/// task plugins that deploy certificates (<see cref="TaskPlugins"/>). Page backend methods: <see cref="PkiMethods"/>; live event: state.
/// </summary>
public sealed class PkiPlugin : ICorePlugin, IAsyncDisposable
{
    private readonly PkiOptions _options;
    private volatile PkiService? _service;
    private CancellationTokenSource? _importStop;
    private Task _import = Task.CompletedTask;

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

    public async Task StartAsync(ICorePluginContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        await StopAsync(ct).ConfigureAwait(false);
        var system = ctx.System;
        var service = new PkiService(_options, ctx.Settings, ctx.Secrets, ctx.Events, ctx.TrustAnchors, ctx.Logger, ctx.DeviceScopes, ctx.Records,
            token => system.Devices.ListAsync(token));
        await service.StartAsync(ct).ConfigureAwait(false);
        _service = service;
        ctx.Services?.Offer<IDeviceCertificateAuthority>(new PkiCertificateAuthority(service)); // the OADM CA for EST and others

        // Certificates of the OADM CAs from before the list of issued certificates: one pass over the devices, in the background.
        var stopping = new CancellationTokenSource();
        _importStop = stopping;
        var import = new ExistingCertificateImport(service, system, ctx.Settings, _options.Time, _options.ExistingImportDelay, ctx.Logger);
        _import = Task.Run(() => import.RunAsync(stopping.Token), CancellationToken.None);
    }

    public async Task StopAsync(CancellationToken ct)
    {
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

    private static bool IsRead(string method) => method is PkiMethods.GetState or PkiMethods.GetCertificates or PkiMethods.GetCertificate;

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
                PkiMethods.Revoke => PkiJson.Serialize(await service.RevokeAsync(PkiJson.Deserialize<RevokeRequest>(payloadJson), caller.UserName, ct).ConfigureAwait(false)),
                PkiMethods.RevokeDevice => PkiJson.Serialize(await service.RevokeDeviceAsync(PkiJson.Deserialize<RevokeDeviceRequest>(payloadJson), caller.UserName, ct).ConfigureAwait(false)),
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
                return PkiJson.Serialize(await service.GetCertificateAsync(PkiJson.Deserialize<CertificateDetailsRequest>(payloadJson), view, ct).ConfigureAwait(false));
            }

            default:
            {
                var view = await ViewedSet.OfAsync(caller, ct).ConfigureAwait(false);
                return PkiJson.Serialize(await service.GetStateAsync(view, ct).ConfigureAwait(false));
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
            PkiMethods.SaveSettings => PkiJson.Serialize(await service.SaveSettingsAsync(PkiJson.Deserialize<SaveSettingsRequest>(payloadJson), ct).ConfigureAwait(false)),
            PkiMethods.ImportRadiusCa => PkiJson.Serialize(PkiService.ImportRadiusCa(PkiJson.Deserialize<ImportRadiusCaRequest>(payloadJson))),
            _ => throw new ArgumentException($"Unknown method '{method}'.", nameof(method)),
        };
    }

    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None).ConfigureAwait(false);
}
