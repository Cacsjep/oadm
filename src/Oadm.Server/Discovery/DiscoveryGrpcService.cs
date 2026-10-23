using System.Collections.Concurrent;
using System.Globalization;
using System.Threading.Channels;

using Grpc.Core;

using Oadm.Core.DeviceGroups;
using Oadm.Core.Discovery;
using Oadm.Server.AddDevices;
using Oadm.Server.Auth;
using Oadm.Server.Common;
using Oadm.Server.Mapping;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Discovery;

/// <summary>
/// gRPC DiscoveryService. WatchDiscovered sends one message per new or changed device
/// (<c>already_managed</c> from the device table) and again whenever the automatic login of the
/// <see cref="DiscoveryAuthenticator"/> changes its <c>auth_state</c>. Range scans and address
/// probes also send progress-only messages (empty <c>discovered_id</c>) and one with
/// <c>scan_finished</c> = true; their stream ends when every login of the session finished.
/// <para>Through the caller (docs/spec/device-scope.md, Decisions "New devices", "Add and discovery sessions"): a session
/// belongs to the user who started it (another user's id answers like an unknown one); a user with a device group looks only
/// at the sites of the group (the server's own network only with "This server"; a group of tags or single devices only is
/// refused with <see cref="AddDeviceTexts.CannotAdd"/>) and logs in only with what was typed, never with the credential
/// list; a managed device outside the caller's devices is sent only as "already added", without its details.</para>
/// </summary>
public sealed class DiscoveryGrpcService(
    IDiscoveryBackend discovery,
    DeviceAdder adder,
    DeviceViewFactory views,
    DiscoverySessionOwners owners,
    DiscoveryAuthenticator authenticator,
    IHostApplicationLifetime lifetime,
    Core.Sites.ISiteNetwork? sites = null,
    Core.Sites.ISiteStates? states = null) : Proto.DiscoveryService.DiscoveryServiceBase
{
    /// <summary>Longest user name of a probe credential (like the credential list).</summary>
    public const int MaxUserNameLength = 64;

    /// <summary>Longest password of a probe credential.</summary>
    public const int MaxPasswordLength = 256;

    public override async Task<Proto.DiscoverySession> StartZeroConf(Proto.Empty request, ServerCallContext context)
    {
        var reach = await ReachAsync(context).ConfigureAwait(false);
        var session = await discovery.StartZeroConfAsync(context.CancellationToken, reach.Routes).ConfigureAwait(false);
        return new() { SessionId = Own(session.Id, reach, context) };
    }

    public override async Task<Proto.DiscoverySession> StartRangeScan(Proto.RangeScanRequest request, ServerCallContext context)
    {
        var reach = await ReachAsync(context).ConfigureAwait(false);
        try
        {
            var session = await discovery.StartRangeScanAsync(request.From, request.To, context.CancellationToken, reach.Routes).ConfigureAwait(false);
            return new Proto.DiscoverySession { SessionId = Own(session.Id, reach, context) };
        }
        catch (ArgumentException ex)
        {
            throw GrpcGuard.InvalidArgument(ex.Message);
        }
    }

    public override async Task<Proto.DiscoverySession> ProbeAddress(Proto.ProbeAddressRequest request, ServerCallContext context)
    {
        // Credentials of an imported line: both or none, checked before anything is probed.
        var userName = request.UserName.Trim();
        var hasCredentials = userName.Length > 0 || request.Password.Length > 0;
        if (hasCredentials && (userName.Length is 0 or > MaxUserNameLength || request.Password.Length is 0 or > MaxPasswordLength))
        {
            throw GrpcGuard.InvalidArgument(string.Create(CultureInfo.InvariantCulture,
                $"Enter both a user name (1-{MaxUserNameLength} characters) and a password (1-{MaxPasswordLength} characters)."));
        }

        var expectedSerial = request.ExpectedSerial.Replace(":", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal).Trim().ToUpperInvariant();
        if (expectedSerial.Length > 0 && (expectedSerial.Length != 12 || !expectedSerial.All(Uri.IsHexDigit)))
        {
            throw GrpcGuard.InvalidArgument("The MAC address must be 12 hexadecimal digits.");
        }

        var reach = await ReachAsync(context).ConfigureAwait(false);
        Guid? siteId = null;
        if (!string.IsNullOrWhiteSpace(request.Site))
        {
            // A site outside the caller's device group answers like one that does not exist.
            siteId = sites?.FindSite(request.Site.Trim()) is { } found && reach.Routes.Allows(found)
                ? found
                : throw GrpcGuard.InvalidArgument($"There is no site named {request.Site.Trim()}.");
        }

        try
        {
            var session = await discovery.StartAddressProbeAsync(request.Address, request.ImportLine, context.CancellationToken, siteId,
                expectedSerial.Length > 0 ? expectedSerial : null, reach.Routes).ConfigureAwait(false);
            Own(session.Id, reach, context);
            if (expectedSerial.Length > 0)
            {
                // Before the watch starts the automatic login: another device at this address gets no password.
                authenticator.SetExpectedSerial(session.Id, expectedSerial);
            }

            if (hasCredentials)
            {
                // Before the watch starts the automatic login, so the credential is the first one tried.
                authenticator.AddSessionCredential(session.Id, userName, request.Password);
            }

            return new Proto.DiscoverySession { SessionId = session.Id, SiteId = siteId?.ToString() ?? string.Empty };
        }
        catch (ArgumentException ex)
        {
            throw GrpcGuard.InvalidArgument(ex.Message);
        }
    }

    public override async Task WatchDiscovered(Proto.DiscoverySession request, IServerStreamWriter<Proto.DiscoveredDevice> responseStream, ServerCallContext context)
    {
        using var linked = GrpcGuard.LinkWithShutdown(context, lifetime.ApplicationStopping);
        var ct = linked.Token;
        var sessionId = request.SessionId;
        var caller = AuthInterceptor.CallerOf(context);
        // Held while the watch is open: the add page may stay open for hours and its Add and Retry still find the session.
        using var hold = owners.Hold(sessionId, caller)
            ?? throw GrpcGuard.NotFound($"Discovery session '{sessionId}' not found."); // another user's session: like an unknown one

        var view = await views.ForAsync(context).ConfigureAwait(false);
        IAsyncEnumerable<DiscoveryEvent> events;
        try
        {
            events = discovery.WatchAsync(sessionId, ct);
        }
        catch (KeyNotFoundException)
        {
            throw GrpcGuard.NotFound($"Discovery session '{sessionId}' not found.");
        }

        // Discovery events and login results both produce messages; one writer sends them in order.
        var output = Channel.CreateUnbounded<Proto.DiscoveredDevice>(new UnboundedChannelOptions { SingleReader = true });
        var latest = new ConcurrentDictionary<string, (DiscoveredDevice Device, Managed Managed, int Progress)>(StringComparer.OrdinalIgnoreCase);
        using var subscription = authenticator.Subscribe(sessionId, serial =>
        {
            if (latest.TryGetValue(serial, out var known))
            {
                output.Writer.TryWrite(Map(sessionId, known.Device, known.Managed, known.Progress));
            }
        });

        var pump = Task.Run(
            async () =>
            {
                try
                {
                    await foreach (var e in events.ConfigureAwait(false))
                    {
                        if (e.Device is { } device)
                        {
                            var managed = await ManagedAsync(device.Serial, view, ct).ConfigureAwait(false);
                            latest[device.Serial] = (device, managed, e.ProgressPercent);
                            authenticator.Ensure(sessionId, device, managed != Managed.No);
                            var message = Map(sessionId, device, managed, e.ProgressPercent);
                            message.ScanFinished = e.Finished;
                            output.Writer.TryWrite(message);
                        }
                        else
                        {
                            var progress = Mappers.ToProgressProto(e.ProgressPercent, e.Finished);
                            progress.Problem = e.Problem ?? string.Empty;
                            output.Writer.TryWrite(progress);
                        }
                    }

                    // Scan finished or session stopped: the logins it started still report back.
                    await authenticator.WhenIdleAsync(sessionId, ct).ConfigureAwait(false);
                    output.Writer.TryComplete();
                }
                catch (Exception ex)
                {
                    output.Writer.TryComplete(ex);
                }
            },
            CancellationToken.None);

        try
        {
            await foreach (var message in output.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                await responseStream.WriteAsync(message, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Client went away or server is stopping.
        }
        finally
        {
            await pump.ConfigureAwait(false);
        }
    }

    public override Task<Proto.Empty> StopScan(Proto.DiscoverySession request, ServerCallContext context)
    {
        // Unknown ids are ignored, and so are the sessions of other users (the same answer).
        if (owners.Owns(request.SessionId, AuthInterceptor.CallerOf(context)))
        {
            discovery.StopScan(request.SessionId);
        }

        return Task.FromResult(new Proto.Empty());
    }

    public override async Task<Proto.Empty> Stop(Proto.DiscoverySession request, ServerCallContext context)
    {
        if (owners.Owns(request.SessionId, AuthInterceptor.CallerOf(context)))
        {
            await discovery.StopAsync(request.SessionId).ConfigureAwait(false);
        }

        return new Proto.Empty();
    }

    /// <summary>The caller's reach; a caller who cannot add devices anywhere is refused before anything is asked.</summary>
    private async Task<AddReach> ReachAsync(ServerCallContext context)
    {
        var reach = await AddReach.ForAsync(views, AuthInterceptor.CallerOf(context), context.CancellationToken).ConfigureAwait(false);
        return reach.CanAdd ? reach : throw new RpcException(new Status(StatusCode.PermissionDenied, AddDeviceTexts.CannotAdd));
    }

    /// <summary>
    /// Registers the caller as the session's owner before anyone can watch it, and lets its automatic login use the
    /// credential list only for a user with all devices (before the first device is seen).
    /// </summary>
    private string Own(string sessionId, AddReach reach, ServerCallContext context)
    {
        if (reach.UsesCredentialList)
        {
            authenticator.AllowCredentialList(sessionId);
        }

        owners.Register(sessionId, AuthInterceptor.CallerOf(context));
        return sessionId;
    }

    /// <summary>Whether OADM manages the serial, and whether the device is one of the caller's.</summary>
    private async Task<Managed> ManagedAsync(string serial, DeviceView view, CancellationToken ct)
    {
        var device = await adder.FindManagedAsync(serial, ct).ConfigureAwait(false);
        return device is null ? Managed.No : view.Contains(device) ? Managed.Visible : Managed.Hidden;
    }

    private Proto.DiscoveredDevice Map(string sessionId, DiscoveredDevice device, Managed managed, int progress)
    {
        if (managed == Managed.Hidden)
        {
            // A device another user manages: only that it is added already (docs/spec/device-scope.md, "Small leaks closed").
            return AlreadyAdded(device, progress);
        }

        var message = Mappers.ToProto(device, managed != Managed.No, progress, authenticator.Get(sessionId, device.Serial));
        Mappers.AddRoutes(message, device, id => states?.NameOf(id));
        return message;
    }

    /// <summary>A found device outside the caller's devices: its id, "already added" and what the user typed, nothing else.</summary>
    internal static Proto.DiscoveredDevice AlreadyAdded(DiscoveredDevice device, int progress)
    {
        ArgumentNullException.ThrowIfNull(device);
        return new Proto.DiscoveredDevice
        {
            DiscoveredId = device.DiscoveredId,
            Serial = device.Serial,
            EnteredAddress = device.EnteredAddress ?? string.Empty,
            AlreadyManaged = true,
            AuthState = Proto.AuthState.AlreadyAdded,
            ProgressPercent = progress,
        };
    }

    /// <summary>A found device: not managed, managed and one of the caller's, managed by someone else.</summary>
    private enum Managed
    {
        No,
        Visible,
        Hidden,
    }
}
