using Oadm.Plugins.Est.Serving;
using Oadm.Plugins.Shared;
using Oadm.Sdk.Certificates;
using Oadm.Sdk.Events;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Est;

/// <summary>
/// Core plugin "EST server", the first OADM PRO plugin (docs/spec/est-server.md): an RFC 7030 EST server for the cameras'
/// own EST clients, signing with the OADM CA of the PKI plugin (<see cref="IDeviceCertificateAuthority"/> through the plugin
/// services). The host runs it only while OADM PRO is active; it checks the license again on every request.
/// Page backend methods: <see cref="EstMethods"/>; events: Certificate enrolled, Enrollment refused.
/// </summary>
public sealed class EstPlugin : ICorePlugin, IEventSource, IAsyncDisposable
{
    private volatile EstService? _service;

    public string Id => EstPluginInfo.PluginId;

    public string DisplayName => EstPluginInfo.DisplayName;

    public string? IconKey => EstPluginInfo.IconKey;

    public CorePluginGroup Group => CorePluginGroup.Security;

    public EstPlugin()
    {
        TaskPlugins = [new Tasks.EstSetupTask(() => _service)];
    }

    /// <summary>"Certificate enrollment" (device context menu, Security): set up, or remove profiles.</summary>
    public IReadOnlyList<ITaskPlugin> TaskPlugins { get; }

    /// <summary>The running server (null while stopped).</summary>
    public EstService? Service => _service;

    public IReadOnlyList<EventTypeInfo> EventTypes { get; } =
    [
        new(EstPluginInfo.EnrolledEvent, "Certificate enrolled", EstPluginInfo.DisplayName, EventKind.Stateless, EventScope.Device,
        [
            new EventField("profile", "Profile"),
            new EventField("kind", "New or renewal", EventFieldType.Choice, ["New", "Renewal"]),
        ])
        {
            Description = "A camera got a certificate from the EST server.",
        },
        new(EstPluginInfo.RefusedEvent, "Enrollment refused", EstPluginInfo.DisplayName, EventKind.Stateless, EventScope.Device,
        [
            new EventField("profile", "Profile"),
            new EventField("reason", "Reason") { Filter = false },
        ])
        {
            Description = "The EST server refused a camera OADM manages.",
        },
        new(EstPluginInfo.NotRenewedEvent, "Certificate not renewed", EstPluginInfo.DisplayName, EventKind.Stateless, EventScope.Device,
        [
            new EventField("profile", "Profile"),
            new EventField("validUntil", "Valid until") { Filter = false },
        ])
        {
            Description = "A camera did not renew a certificate from the EST server in time.",
        },
    ];

    public async Task StartAsync(ICorePluginContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        await StopAsync(ct).ConfigureAwait(false);
        var service = new EstService(new EstHost(
            ctx.Settings,
            ctx.Devices,
            ctx.Network,
            () => ctx.Services?.Find<IDeviceCertificateAuthority>(),
            () => ctx.Pro?.IsActive == true)
        {
            Events = ctx.Events,
            Records = ctx.Records ?? throw new InvalidOperationException("The EST server needs OADM 4.1 or later."),
            Sites = ctx.Sites,
            Publisher = ctx.EventPublisher,
            Scopes = ctx.DeviceScopes,
            Logger = ctx.Logger,
        });
        await service.StartAsync(ct).ConfigureAwait(false);
        _service = service;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_service is { } service)
        {
            _service = null;
            await service.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Reading is for everyone; Save and Delete all change the server (Admin, audited).</summary>
    public UserRole RequiredRole(string method) =>
        method is EstMethods.GetState or EstMethods.GetEnrollments or EstMethods.GetNotRenewed or EstMethods.GetCertificate ? UserRole.Operator : UserRole.Admin;

    public IReadOnlyList<PermissionDefinition> Permissions { get; } =
    [
        new(PermissionKeys.Manage, "Change the EST server", "Change the interface, the port and the profiles, and delete the enrollments.") { OperatorDefault = false },
    ];

    /// <summary>
    /// The EST server is server-wide (docs/spec/device-scope.md "Server-wide services"): Save and Delete all are for users
    /// with all devices; users limited to a device group read the page with the rows of their own devices.
    /// </summary>
    public bool RequiresAllDevices(string method) => method is EstMethods.Save or EstMethods.ClearEnrollments;

    /// <summary>The page backend with the user's call: a user limited to a group sees the enrollments, certificates and listeners of that group's devices.</summary>
    public async Task<string?> InvokeAsync(ICallContext caller, string method, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var service = _service ?? throw new InvalidOperationException("The EST server plugin is not running.");
        if (RequiresAllDevices(method) && !caller.HasAllDevices)
        {
            throw new DeviceAccessRefusedException(); // the host refuses these first; fail closed
        }

        service.Viewers.Seen(caller);
        var view = await ViewedSet.OfAsync(caller, ct).ConfigureAwait(false);
        return await InvokeAsync(service, view, method, payloadJson, ct).ConfigureAwait(false);
    }

    /// <summary>In-process callers and tests: the view of a user with all devices.</summary>
    public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) =>
        InvokeAsync(_service ?? throw new InvalidOperationException("The EST server plugin is not running."), null, method, payloadJson, ct);

    private static async Task<string?> InvokeAsync(EstService service, ViewedSet? view, string method, string? payloadJson, CancellationToken ct) =>
        method switch
        {
            EstMethods.GetState => EstJson.Serialize(service.GetState(view)),
            EstMethods.GetEnrollments => EstJson.Serialize(new EnrollmentList(service.Enrollments(view))),
            EstMethods.GetNotRenewed => EstJson.Serialize(service.Store.NotRenewed(EstPluginInfo.CertificatesPageSize, view is null ? null : view.Contains)),
            EstMethods.Save => EstJson.Serialize(await service.SaveAsync(EstJson.Deserialize<SaveRequest>(payloadJson), ct).ConfigureAwait(false)),
            EstMethods.ClearEnrollments => ClearEnrollments(service),
            EstMethods.GetCertificate => EstJson.Serialize(CertificateDescriber.Describe(
                await service.IssuedAsync(EstJson.Deserialize<CertificateQuery>(payloadJson).SerialNumber, view, ct).ConfigureAwait(false)
                ?? throw new ArgumentException(EstMethods.CertificateGone))),
            _ => throw new ArgumentException($"Unknown method '{method}'.", nameof(method)),
        };

    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None).ConfigureAwait(false);

    private static string? ClearEnrollments(EstService service)
    {
        service.ClearEnrollments();
        return null;
    }
}
