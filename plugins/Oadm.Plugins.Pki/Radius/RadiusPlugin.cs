using Oadm.Plugins.Pki.Servers;
using Oadm.Plugins.Radius.Api;
using Oadm.Plugins.Radius.Server;
using Oadm.Plugins.Shared;
using Oadm.Sdk.Certificates;
using Oadm.Sdk.Events;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Radius;

/// <summary>
/// Core plugin "802.1X server" of the PKI package (docs/spec/radius-server.md): OADM answers the switches as their RADIUS
/// server and authenticates the cameras on the switch ports with EAP-TLS. The protocol layer is in <c>Protocol/</c> (no
/// sockets, no host), the server part in <c>Server/</c> (<see cref="RadiusService"/>), the page contract in <c>Api/</c>.
/// A PRO part of PKI (docs/spec/pki.md "One plugin"): the core plugin always runs, the server only while it is turned on and
/// OADM PRO is active (<see cref="ServerSwitch{TService}"/>); the server checks the license again on every request.
/// Events: "Device authenticated", "Authentication failed".
/// </summary>
public sealed class RadiusPlugin : ICorePlugin, IEventSource, IAsyncDisposable
{
    /// <summary>The plugin id, equal to the id in plugin.json.</summary>
    public const string PluginId = "oadm.radius";

    /// <summary>The server's state for the page.</summary>
    public const string GetStateMethod = RadiusMethods.GetState;

    private readonly Func<RadiusHost, RadiusHost>? _customize;
    private volatile ServerSwitch<RadiusService>? _server;

    public RadiusPlugin()
    {
    }

    /// <summary>Tests: changes the host the plugin builds from its context (time, interfaces, the CA's device ID roots, ...).</summary>
    internal RadiusPlugin(Func<RadiusHost, RadiusHost> customize)
    {
        _customize = customize;
    }

    public string Id => PluginId;

    public string DisplayName => "802.1X server";

    public string? IconKey => "lock";

    public CorePluginGroup Group => CorePluginGroup.Security;

    /// <summary>The page is in the client part of PKI (<c>Oadm.Plugins.Pki.Client</c>, <c>Radius/</c>).</summary>
    public bool HasPage => true;

    public IReadOnlyList<ITaskPlugin> TaskPlugins { get; } = [];

    /// <summary>The running server (null while stopped, turned off or without OADM PRO).</summary>
    public RadiusService? Service => _server?.Service;

    /// <summary>Turn on / Turn off of the server (null while the core plugin is stopped).</summary>
    public ServerSwitch<RadiusService>? Server => _server;

    public IReadOnlyList<EventTypeInfo> EventTypes { get; } =
    [
        new(RadiusService.AuthenticatedEvent, "Device authenticated", "802.1X server", EventKind.Stateless, EventScope.Device,
        [
            new EventField("switch", "Switch"),
            new EventField("port", "Port"),
            new EventField("vlan", "VLAN", EventFieldType.Number) { Kind = EventValueKind.Number },
        ])
        {
            Description = "A device passed the 802.1X server.",
        },
        new(RadiusService.FailedEvent, "Authentication failed", "802.1X server", EventKind.Stateless, EventScope.Device,
        [
            new EventField("switch", "Switch"),
            new EventField("port", "Port"),
            new EventField("reason", "Reason") { Filter = false },
        ])
        {
            Description = "The 802.1X server refused a device or a device did not finish.",
        },
    ];

    public async Task StartAsync(ICorePluginContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        await StopAsync(ct).ConfigureAwait(false);
        var server = new ServerSwitch<RadiusService>(DisplayName, ctx.Settings, ctx.Pro, async token =>
        {
            var host = new RadiusHost(
                ctx.Settings,
                ctx.Devices,
                ctx.Network,
                () => ctx.Services?.Find<IDeviceCertificateAuthority>(),
                () => ctx.Pro?.IsActive == true)
            {
                Sites = ctx.Sites,
                Events = ctx.Events,
                Records = ctx.Records ?? throw new InvalidOperationException("The 802.1X server needs OADM 4.1 or later."),
                Publisher = ctx.EventPublisher,
                Scopes = ctx.DeviceScopes,
                ProtectedTags = ctx.ProtectedTags,
                Secrets = ctx.Secrets,
                Logger = ctx.Logger,
            };
            var service = new RadiusService(_customize?.Invoke(host) ?? host);
            await service.StartAsync(token).ConfigureAwait(false);
            return service;
        }, ctx.Events, ctx.Logger);
        _server = server;
        await server.StartAsync(ct).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_server is { } server)
        {
            _server = null;
            await server.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None).ConfigureAwait(false);

    /// <summary>
    /// Reading is for everyone with the page; every change is Admin (permission "Change the 802.1X server"), audited. A new
    /// or changed revoke policy needs PKI's "Manage the certificate authority" too (checked with the call).
    /// </summary>
    public UserRole RequiredRole(string method) => method is ServerSwitchMethods.Get or RadiusMethods.GetState or RadiusMethods.ListSwitches or RadiusMethods.GetPolicies
        or RadiusMethods.ListSessions or RadiusMethods.ListLog or RadiusMethods.GetLogCertificate or RadiusMethods.ListSites or RadiusMethods.GetTrustedCas
        ? UserRole.Operator
        : UserRole.Admin;

    /// <summary>Generate only makes a random text and the count only reads: not audited.</summary>
    public bool IsAudited(string method) => RequiredRole(method) == UserRole.Admin && method is not (RadiusMethods.GenerateSecret or RadiusMethods.CountRevocations);

    public IReadOnlyList<PermissionDefinition> Permissions { get; } =
    [
        new(PermissionKeys.Manage, "Change the 802.1X server", "Turn the server on and off, change the settings, the switches, the trusted CAs and the policies, and delete the log.") { OperatorDefault = false },
    ];

    /// <summary>
    /// The 802.1X server is server-wide (docs/spec/device-scope.md "Server-wide services"): Turn on, Turn off, the settings, Switches, Trusted
    /// CAs, Policies and Delete all are for users with all devices; users limited to a group read the page with the Sessions
    /// and Log rows of their devices.
    /// </summary>
    public bool RequiresAllDevices(string method) => ServerSwitchMethods.IsChange(method) || method is RadiusMethods.SaveSettings or RadiusMethods.SaveSwitch or RadiusMethods.GenerateSecret
        or RadiusMethods.DeleteSwitch or RadiusMethods.SaveTrustedCas or RadiusMethods.SavePolicies or RadiusMethods.CountRevocations or RadiusMethods.DeleteLog;

    /// <summary>The page backend with the user's call: rows of devices are filtered to the caller's devices.</summary>
    public async Task<string?> InvokeAsync(ICallContext caller, string method, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (RequiresAllDevices(method) && !caller.HasAllDevices)
        {
            throw new DeviceAccessRefusedException(); // the host refuses these first; fail closed
        }

        if (ServerSwitchMethods.IsSwitchMethod(method))
        {
            var server = _server ?? throw new InvalidOperationException(RadiusTexts.NotRunning);
            return ServerSwitchMethods.Serialize(method switch
            {
                ServerSwitchMethods.TurnOn => await server.TurnOnAsync(ct).ConfigureAwait(false),
                ServerSwitchMethods.TurnOff => await server.TurnOffAsync(ct).ConfigureAwait(false),
                _ => server.State,
            });
        }

        var service = Service ?? throw new InvalidOperationException(RadiusTexts.NotRunning);

        service.Viewers.Seen(caller);
        switch (method)
        {
            case RadiusMethods.GetState:
                return RadiusJson.Serialize(service.GetState(caller.HasAllDevices));
            case RadiusMethods.SaveSettings:
                return RadiusJson.Serialize(await service.SaveSettingsAsync(RadiusJson.Deserialize<RadiusSettings>(payloadJson), ct).ConfigureAwait(false));
            case RadiusMethods.ListSwitches:
                return RadiusJson.Serialize(caller.HasAllDevices ? service.ListSwitches() : service.ListSwitches(caller.CoversRoute));
            case RadiusMethods.SaveSwitch:
                return RadiusJson.Serialize(await service.SaveSwitchAsync(RadiusJson.Deserialize<SaveSwitchRequest>(payloadJson), ct).ConfigureAwait(false));
            case RadiusMethods.GenerateSecret:
                return RadiusJson.Serialize(new GeneratedSecret(RadiusService.GenerateSecret()));
            case RadiusMethods.DeleteSwitch:
                await service.DeleteSwitchAsync(RadiusJson.Deserialize<SwitchRef>(payloadJson).Id, ct).ConfigureAwait(false);
                return null;
            case RadiusMethods.GetTrustedCas:
                return RadiusJson.Serialize(service.GetTrustedCas(caller.HasAllDevices));
            case RadiusMethods.SaveTrustedCas:
                return RadiusJson.Serialize(await service.SaveTrustedCasAsync(RadiusJson.Deserialize<TrustedCaSettings>(payloadJson), ct).ConfigureAwait(false));
            case RadiusMethods.GetPolicies:
                return RadiusJson.Serialize(service.GetPolicies(caller.HasAllDevices));
            case RadiusMethods.SavePolicies:
                var mayRevoke = caller.Has(PermissionKeys.Of(RadiusTexts.PkiPluginId, PermissionKeys.Manage));
                return RadiusJson.Serialize(await service.SavePoliciesAsync(RadiusJson.Deserialize<PolicySettings>(payloadJson), caller.UserName, mayRevoke, ct).ConfigureAwait(false));
            case RadiusMethods.CountRevocations:
                return RadiusJson.Serialize(await service.CountRevocationsAsync(RadiusJson.Deserialize<PolicyDevices>(payloadJson), ct).ConfigureAwait(false));
            case RadiusMethods.ListSessions:
                return RadiusJson.Serialize(new SessionList(service.ListSessions(await ViewedSet.OfAsync(caller, ct).ConfigureAwait(false))));
            case RadiusMethods.ListLog:
                var query = string.IsNullOrWhiteSpace(payloadJson) ? new LogQuery(null) : RadiusJson.Deserialize<LogQuery>(payloadJson);
                return RadiusJson.Serialize(service.ListLog(query, await ViewedSet.OfAsync(caller, ct).ConfigureAwait(false)));
            case RadiusMethods.DeleteLog:
                service.DeleteLog(caller.UserName);
                return null;
            case RadiusMethods.GetLogCertificate:
                var entry = RadiusJson.Deserialize<LogEntryRef>(payloadJson);
                return RadiusJson.Serialize(await service.LogCertificateAsync(entry.Id, await ViewedSet.OfAsync(caller, ct).ConfigureAwait(false), ct).ConfigureAwait(false));
            case RadiusMethods.ListSites:
                return RadiusJson.Serialize(service.ListSites(await ViewedSet.OfAsync(caller, ct).ConfigureAwait(false)));
            default:
                throw new ArgumentException($"Unknown method '{method}'.", nameof(method));
        }
    }

    /// <summary>In-process callers without a user (the host, tests): the view of a user with all devices.</summary>
    public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) =>
        InvokeAsync(SystemCall.Instance, method, payloadJson, ct);

    /// <summary>The call of the old overload: all devices, user "OADM", no device access needed by any method there.</summary>
    private sealed class SystemCall : ICallContext
    {
        public static readonly SystemCall Instance = new();

        public string UserName => "OADM";

        public string SessionId => "oadm";

        public string DeviceScopeKey => ICallContext.AllDevicesScopeKey;

        public bool HasAllDevices => true;

        public Sdk.Devices.IDeviceRepository Devices => throw new NotSupportedException();

        public Sdk.Vapix.IVapixClientFactory Vapix => throw new NotSupportedException();

        public Sdk.Tasks.ITaskRunner Tasks => throw new NotSupportedException();

        public Sdk.Devices.IDeviceEventStreams? EventStreams => null;

        public bool Has(string permission) => true;

        public bool Contains(Sdk.Devices.IDeviceInfo device) => true;
    }
}
