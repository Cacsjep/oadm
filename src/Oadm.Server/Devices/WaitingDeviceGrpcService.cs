using System.Globalization;
using System.Threading.Channels;

using Google.Protobuf.WellKnownTypes;

using Grpc.Core;

using Oadm.Core.Auth;
using Oadm.Core.Devices;
using Oadm.Server.AddDevices;
using Oadm.Server.Common;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Devices;

/// <summary>
/// gRPC WaitingDeviceService: the waiting list of the import (<see cref="WaitingDeviceStore"/>) and its retries
/// (<see cref="WaitingDeviceRunner"/>). Passwords arrive here and are stored encrypted; nothing sends them back.
/// </summary>
public sealed class WaitingDeviceGrpcService(
    WaitingDeviceStore store,
    WaitingDeviceRunner runner,
    AuditLog audit,
    IHostApplicationLifetime lifetime) : Proto.WaitingDeviceService.WaitingDeviceServiceBase
{
    public const int MaxUserNameLength = 64;
    public const int MaxPasswordLength = 256;

    /// <summary>The Watch summary at most this often.</summary>
    public static readonly TimeSpan WatchInterval = TimeSpan.FromSeconds(1);

    public override async Task<Proto.AddWaitingReply> Add(Proto.AddWaitingRequest request, ServerCallContext context)
    {
        var ct = context.CancellationToken;
        if (request.Lines.Count > WaitingDeviceStore.MaxEntries)
        {
            throw new RpcException(new Status(StatusCode.ResourceExhausted, WaitingDeviceStore.FullText));
        }

        var lines = new List<WaitingLine>(request.Lines.Count);
        foreach (var line in request.Lines)
        {
            lines.Add(Check(line));
        }

        var source = string.IsNullOrWhiteSpace(request.Source) ? "Import" : request.Source.Trim();
        WaitingAddResult result;
        try
        {
            result = await store.AddAsync(lines, source, CallerContext.Current?.UserName ?? "system", ct).ConfigureAwait(false);
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
        var (items, total) = await store.ListAsync(request.Offset, request.Limit, context.CancellationToken).ConfigureAwait(false);
        var reply = new Proto.WaitingList { TotalCount = total };
        reply.Devices.AddRange(items.Select(ToProto));
        return reply;
    }

    public override async Task Watch(Proto.Empty request, IServerStreamWriter<Proto.WaitingSummary> responseStream, ServerCallContext context)
    {
        using var linked = GrpcGuard.LinkWithShutdown(context, lifetime.ApplicationStopping);
        var ct = linked.Token;
        var signal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
        void OnChanged(object? sender, EventArgs e) => signal.Writer.TryWrite(true);
        store.Changed += OnChanged;
        runner.StateChanged += OnChanged;
        try
        {
            await responseStream.WriteAsync(await SummaryAsync(ct).ConfigureAwait(false), ct).ConfigureAwait(false);
            while (await signal.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                await Task.Delay(WatchInterval, ct).ConfigureAwait(false); // a round writes many results: one message a second
                signal.Reader.TryRead(out _);
                await responseStream.WriteAsync(await SummaryAsync(ct).ConfigureAwait(false), ct).ConfigureAwait(false);
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
        var ids = request.All ? [] : GrpcGuard.ParseIds(request.Ids, "ids");
        var removed = await store.RemoveAsync(ids, request.All, ct).ConfigureAwait(false);
        if (removed > 0)
        {
            await audit.WriteAsync(AuditActions.WaitingRemoved, string.Create(CultureInfo.InvariantCulture, $"{removed:N0} devices"), null, ct).ConfigureAwait(false);
        }

        return await SummaryAsync(ct).ConfigureAwait(false);
    }

    public override async Task<Proto.WaitingSummary> TryNow(Proto.WaitingIds request, ServerCallContext context)
    {
        runner.TryNow(request.All ? null : GrpcGuard.ParseIds(request.Ids, "ids"));
        return await SummaryAsync(context.CancellationToken).ConfigureAwait(false);
    }

    /// <summary>Checks one line like the import does; a tag that cannot be used is left out.</summary>
    internal static WaitingLine Check(Proto.WaitingLine line)
    {
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
            line.Password.Length == 0 ? null : line.Password, tags);
    }

    private async Task<Proto.WaitingSummary> SummaryAsync(CancellationToken ct)
    {
        var (count, loginFailed) = await store.CountsAsync(ct).ConfigureAwait(false);
        var summary = new Proto.WaitingSummary { Count = count, LoginFailed = loginFailed, Trying = runner.IsTrying };
        if (runner.NextTry is { } next)
        {
            summary.NextTry = Timestamp.FromDateTimeOffset(next);
        }

        return summary;
    }

    private static Proto.WaitingDevice ToProto(WaitingDeviceItem item)
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
            Tries = item.Tries,
            HasLogin = item.HasLogin,
        };
        proto.Tags.AddRange(item.Tags);
        if (item.LastTryUtc is { } last)
        {
            proto.LastTry = Timestamp.FromDateTime(DateTime.SpecifyKind(last, DateTimeKind.Utc));
        }

        return proto;
    }
}
