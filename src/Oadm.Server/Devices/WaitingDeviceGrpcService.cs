using System.Globalization;
using System.Threading.Channels;

using Google.Protobuf.WellKnownTypes;

using Grpc.Core;

using Oadm.Core.Auth;
using Oadm.Core.DeviceGroups;
using Oadm.Core.Devices;
using Oadm.Server.AddDevices;
using Oadm.Server.Auth;
using Oadm.Server.Common;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Devices;

/// <summary>
/// gRPC WaitingDeviceService: the waiting list of the import (<see cref="WaitingDeviceStore"/>) and its retries
/// (<see cref="WaitingDeviceRunner"/>). Passwords arrive here and are stored encrypted; nothing sends them back.
/// <para>Scoped by site (docs/spec/device-scope.md, Decisions "New devices"): a user with a device group sees, removes and
/// tries only the entries of the sites of the group (<see cref="WaitingScope"/>); an id outside answers NOT_FOUND like an
/// unknown one, the whole call refused. Lines kept by such a user name only the group's sites; every entry remembers who
/// kept it, and a try adds the device as that user is at that time.</para>
/// </summary>
public sealed class WaitingDeviceGrpcService(
    WaitingDeviceStore store,
    WaitingDeviceRunner runner,
    AuditLog audit,
    DeviceViewFactory views,
    IHostApplicationLifetime lifetime,
    Core.Sites.ISiteStates? sites = null) : Proto.WaitingDeviceService.WaitingDeviceServiceBase
{
    public const int MaxUserNameLength = 64;
    public const int MaxPasswordLength = 256;

    /// <summary>A line of a user with a device group names a site outside the group (answered like an unknown site).</summary>
    public const string SiteOutsideText = "A line names a site that is not in your device group.";

    /// <summary>The ids that are unknown or outside the caller's entries.</summary>
    public const string NotFoundText = "Some of these devices are no longer waiting.";

    /// <summary>The Watch summary at most this often.</summary>
    public static readonly TimeSpan WatchInterval = TimeSpan.FromSeconds(1);

    public override async Task<Proto.AddWaitingReply> Add(Proto.AddWaitingRequest request, ServerCallContext context)
    {
        var ct = context.CancellationToken;
        if (request.Lines.Count > WaitingDeviceStore.MaxEntries)
        {
            throw new RpcException(new Status(StatusCode.ResourceExhausted, WaitingDeviceStore.FullText));
        }

        var caller = AuthInterceptor.CallerOf(context);
        var reach = await ReachAsync(context, forAdd: true).ConfigureAwait(false);
        var lines = new List<WaitingLine>(request.Lines.Count);
        foreach (var line in request.Lines)
        {
            lines.Add(Scoped(Check(line), reach));
        }

        var source = string.IsNullOrWhiteSpace(request.Source) ? "Import" : request.Source.Trim();
        WaitingAddResult result;
        try
        {
            result = await store.AddAsync(lines, source, caller.UserName, caller.UserId, reach.Waiting, ct).ConfigureAwait(false);
        }
        catch (WaitingListFullException ex)
        {
            throw new RpcException(new Status(StatusCode.ResourceExhausted, ex.Message));
        }

        await audit.WriteAsync(AuditActions.WaitingAdded, source,
            string.Create(CultureInfo.InvariantCulture, $"{result.Added + result.Updated:N0} devices ({result.Total:N0} waiting)"), ct).ConfigureAwait(false);
        return new Proto.AddWaitingReply { Added = result.Added, Updated = result.Updated, Total = result.Total };
    }

    public override async Task<Proto.WaitingList> List(Proto.ListWaitingRequest request, ServerCallContext context)
    {
        var reach = await ReachAsync(context, forAdd: false).ConfigureAwait(false);
        var page = await store.ListAsync(request.After, request.Limit, reach.Waiting, context.CancellationToken).ConfigureAwait(false);
        var reply = new Proto.WaitingList { TotalCount = page.Total, NextAfter = page.NextAfter ?? string.Empty };
        reply.Devices.AddRange(page.Items.Select(ToProto));
        return reply;
    }

    public override async Task Watch(Proto.Empty request, IServerStreamWriter<Proto.WaitingSummary> responseStream, ServerCallContext context)
    {
        using var linked = GrpcGuard.LinkWithShutdown(context, lifetime.ApplicationStopping);
        var ct = linked.Token;
        var scope = (await ReachAsync(context, forAdd: false).ConfigureAwait(false)).Waiting; // a change of the user's devices ends the stream
        var signal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
        void OnChanged(object? sender, EventArgs e) => signal.Writer.TryWrite(true);
        store.Changed += OnChanged;
        runner.StateChanged += OnChanged;
        try
        {
            await responseStream.WriteAsync(await SummaryAsync(scope, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
            while (await signal.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                await Task.Delay(WatchInterval, ct).ConfigureAwait(false); // a round writes many results: one message a second
                signal.Reader.TryRead(out _);
                await responseStream.WriteAsync(await SummaryAsync(scope, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Client went away or server is stopping.
        }
        finally
        {
            store.Changed -= OnChanged;
            runner.StateChanged -= OnChanged;
        }
    }

    public override async Task<Proto.WaitingSummary> Remove(Proto.WaitingIds request, ServerCallContext context)
    {
        var ct = context.CancellationToken;
        var scope = (await ReachAsync(context, forAdd: false).ConfigureAwait(false)).Waiting;
        var ids = request.All ? [] : GrpcGuard.ParseIds(request.Ids, "ids");
        await RequireInScopeAsync(ids, scope, ct).ConfigureAwait(false);
        var removed = await store.RemoveAsync(ids, request.All, scope, ct).ConfigureAwait(false);
        if (removed > 0)
        {
            await audit.WriteAsync(AuditActions.WaitingRemoved, string.Create(CultureInfo.InvariantCulture, $"{removed:N0} devices"), null, ct).ConfigureAwait(false);
        }

        return await SummaryAsync(scope, ct).ConfigureAwait(false);
    }

    public override async Task<Proto.WaitingSummary> TryNow(Proto.WaitingIds request, ServerCallContext context)
    {
        var ct = context.CancellationToken;
        var scope = (await ReachAsync(context, forAdd: false).ConfigureAwait(false)).Waiting;
        if (request.All)
        {
            // All = every entry of the caller (for a user with all devices every entry, also the failed logins, as before).
            runner.TryNow(scope.All ? null : await store.IdsAsync(scope, ct).ConfigureAwait(false));
        }
        else
        {
            var ids = GrpcGuard.ParseIds(request.Ids, "ids");
            await RequireInScopeAsync(ids, scope, ct).ConfigureAwait(false);
            runner.TryNow(ids);
        }

        return await SummaryAsync(scope, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The caller's reach; to keep devices waiting the caller must be able to add devices somewhere (a group of tags or single
    /// devices only is refused with <see cref="AddDeviceTexts.CannotAdd"/>); reading an empty scope is fine.
    /// </summary>
    private async Task<AddReach> ReachAsync(ServerCallContext context, bool forAdd)
    {
        var reach = await AddReach.ForAsync(views, AuthInterceptor.CallerOf(context), context.CancellationToken).ConfigureAwait(false);
        return !forAdd || reach.CanAdd ? reach : throw new RpcException(new Status(StatusCode.PermissionDenied, AddDeviceTexts.CannotAdd));
    }

    /// <summary>Every id must be an entry of the scope, else the whole call is refused (an id outside answers like an unknown one).</summary>
    private async Task RequireInScopeAsync(Guid[] ids, WaitingScope scope, CancellationToken ct)
    {
        if (scope.All || ids.Length == 0)
        {
            return; // users with all devices: unknown ids are ignored as before
        }

        var found = await store.InScopeAsync(ids, scope, ct).ConfigureAwait(false);
        if (ids.Distinct().Any(id => !found.Contains(id)))
        {
            throw GrpcGuard.NotFound(NotFoundText);
        }
    }

    /// <summary>
    /// A line of a user with a device group: only a site of the group; a line for everywhere of a group with exactly one site
    /// and without "This server" waits for that site (so the user sees it).
    /// </summary>
    internal static WaitingLine Scoped(WaitingLine line, AddReach reach)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(reach);
        if (reach.AllDevices)
        {
            return line;
        }

        if (line.SiteId is { } site)
        {
            return reach.Routes.Allows(site) ? line : throw GrpcGuard.NotFound(SiteOutsideText);
        }

        return !reach.Routes.Local && reach.Routes.Sites is { Count: 1 } only ? line with { SiteId = only.First() } : line;
    }

    /// <summary>Checks one line like the import does; a tag that cannot be used is left out.</summary>
    internal static WaitingLine Check(Proto.WaitingLine line)
    {
        Guid? siteId = null;
        if (!string.IsNullOrWhiteSpace(line.SiteId))
        {
            siteId = Guid.TryParse(line.SiteId, out var site) ? site : throw GrpcGuard.InvalidArgument("The site of a line is not a site id.");
        }

        var address = line.Address.Trim();
        if (address.Length is 0 or > WaitingDeviceStore.MaxAddressLength)
        {
            throw GrpcGuard.InvalidArgument(string.Create(CultureInfo.InvariantCulture,
                $"Every line needs an address of at most {WaitingDeviceStore.MaxAddressLength} characters."));
        }

        var user = line.UserName.Trim();
        if (user.Length > MaxUserNameLength || line.Password.Length > MaxPasswordLength)
        {
            throw GrpcGuard.InvalidArgument(string.Create(CultureInfo.InvariantCulture,
                $"A user name has at most {MaxUserNameLength} characters, a password at most {MaxPasswordLength}."));
        }

        var serial = line.ExpectedSerial.Replace(":", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal).Trim();
        if (serial.Length > 0 && (serial.Length != 12 || !serial.All(Uri.IsHexDigit)))
        {
            throw GrpcGuard.InvalidArgument("The MAC address must be 12 hexadecimal digits.");
        }

        var tags = TagNames.Canonical(line.Tags.Select(t => t.Trim()).Where(t => TagNames.Problem(t) is null)).Take(TagNames.MaxTagsPerDevice).ToList();
        return new WaitingLine(address, serial.Length == 0 ? null : serial, user.Length == 0 ? null : user,
            line.Password.Length == 0 ? null : line.Password, tags, siteId);
    }

    private async Task<Proto.WaitingSummary> SummaryAsync(WaitingScope scope, CancellationToken ct)
    {
        var (count, loginFailed) = await store.CountsAsync(scope, ct).ConfigureAwait(false);
        var remaining = runner.Remaining;
        var summary = new Proto.WaitingSummary
        {
            Count = count,
            LoginFailed = loginFailed,
            Trying = runner.IsTrying,
            Remaining = remaining < 0 ? count : Math.Min(remaining, count),
        };
        if (runner.NextTry is { } next)
        {
            summary.NextTry = Timestamp.FromDateTimeOffset(next);
        }

        return summary;
    }

    private Proto.WaitingDevice ToProto(WaitingDeviceItem item)
    {
        var proto = new Proto.WaitingDevice
        {
            Id = item.Id.ToString(),
            Address = item.Address,
            ExpectedSerial = item.ExpectedSerial ?? string.Empty,
            Source = item.Source,
            AddedBy = item.AddedBy,
            Created = Timestamp.FromDateTime(DateTime.SpecifyKind(item.CreatedUtc, DateTimeKind.Utc)),
            LastResult = item.LastResult,
            LoginFailed = item.LoginFailed,
            Held = item.Held,
            Tries = item.Tries,
            HasLogin = item.HasLogin,
            Checking = runner.IsChecking(item.Id),
            SiteId = item.SiteId?.ToString() ?? string.Empty,
            SiteName = item.SiteId is { } site ? sites?.NameOf(site) ?? string.Empty : string.Empty,
        };
        proto.Tags.AddRange(item.Tags);
        if (item.LastTryUtc is { } last)
        {
            proto.LastTry = Timestamp.FromDateTime(DateTime.SpecifyKind(last, DateTimeKind.Utc));
        }

        return proto;
    }
}
