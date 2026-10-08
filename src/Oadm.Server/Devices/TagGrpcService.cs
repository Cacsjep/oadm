using System.Globalization;
using System.Threading.Channels;

using Grpc.Core;

using Oadm.Core.Auth;
using Oadm.Core.Devices;
using Oadm.Server.Common;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Devices;

/// <summary>
/// gRPC TagService: tag definitions (name + color) and tagging many devices in one call (<see cref="DeviceTagStore"/>).
/// Roles (<c>AccessPolicy</c>): Update and Delete are Admin only, the rest Operator. Every change is audited.
/// </summary>
public sealed class TagGrpcService(DeviceTagStore tags, AuditLog audit, IHostApplicationLifetime lifetime) : Proto.TagService.TagServiceBase
{
    public override async Task<Proto.TagList> List(Proto.Empty request, ServerCallContext context) =>
        ToProto(await tags.ListAsync(context.CancellationToken).ConfigureAwait(false));

    /// <summary>The list now and after every change of the definitions; a burst of changes sends the newest list once.</summary>
    public override async Task Watch(Proto.Empty request, IServerStreamWriter<Proto.TagList> responseStream, ServerCallContext context)
    {
        using var linked = GrpcGuard.LinkWithShutdown(context, lifetime.ApplicationStopping);
        var ct = linked.Token;
        var signal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
        void OnChanged(object? sender, EventArgs e) => signal.Writer.TryWrite(true);
        tags.DefinitionsChanged += OnChanged;
        try
        {
            await responseStream.WriteAsync(ToProto(await tags.ListAsync(ct).ConfigureAwait(false)), ct).ConfigureAwait(false);
            while (await signal.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                signal.Reader.TryRead(out _);
                await responseStream.WriteAsync(ToProto(await tags.ListAsync(ct).ConfigureAwait(false)), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Client went away or server is stopping.
        }
        finally
        {
            tags.DefinitionsChanged -= OnChanged;
        }
    }

    public override async Task<Proto.DeviceTag> Create(Proto.CreateTagRequest request, ServerCallContext context)
    {
        var created = await Guard(() => tags.CreateAsync(request.Name, ToCore(request.Color), context.CancellationToken)).ConfigureAwait(false);
        await audit.WriteAsync(AuditActions.TagCreated, created.Name, ColorText(created.Color), context.CancellationToken).ConfigureAwait(false);
        return ToProto(new TagSummary(created.Id, created.Name, created.Color, 0, Defined: true));
    }

    public override async Task<Proto.DeviceTag> Update(Proto.UpdateTagRequest request, ServerCallContext context)
    {
        var ct = context.CancellationToken;
        var result = await Guard(() => tags.UpdateAsync(request.Name, request.NewName, ToCore(request.Color), ct)).ConfigureAwait(false);
        if (!string.Equals(result.OldName, result.Tag.Name, StringComparison.Ordinal))
        {
            await audit.WriteAsync(AuditActions.TagRenamed, result.OldName, $"to {result.Tag.Name}, {DevicesText(result.DevicesChanged)}", ct).ConfigureAwait(false);
        }

        if (result.OldColor != result.Tag.Color)
        {
            await audit.WriteAsync(AuditActions.TagRecolored, result.Tag.Name, $"{ColorText(result.OldColor)} to {ColorText(result.Tag.Color)}", ct).ConfigureAwait(false);
        }

        return ToProto(new TagSummary(result.Tag.Id, result.Tag.Name, result.Tag.Color, 0, Defined: true));
    }

    public override async Task<Proto.DeleteTagReply> Delete(Proto.TagName request, ServerCallContext context)
    {
        var (name, devicesChanged) = await Guard(() => tags.DeleteAsync(request.Name, context.CancellationToken)).ConfigureAwait(false);
        await audit.WriteAsync(AuditActions.TagDeleted, name, "removed from " + DevicesText(devicesChanged), context.CancellationToken).ConfigureAwait(false);
        return new Proto.DeleteTagReply { DevicesChanged = devicesChanged };
    }

    public override async Task<Proto.SetDeviceTagsReply> SetDeviceTags(Proto.SetDeviceTagsRequest request, ServerCallContext context)
    {
        var ct = context.CancellationToken;
        var ids = GrpcGuard.ParseIds(request.DeviceIds, "device id");
        var result = await Guard(() => tags.SetDeviceTagsAsync(ids, [.. request.Add], [.. request.Remove], ct)).ConfigureAwait(false);
        var reply = new Proto.SetDeviceTagsReply { DevicesChanged = result.DevicesChanged };
        foreach (var created in result.Created)
        {
            reply.Created.Add(ToProto(new TagSummary(created.Id, created.Name, created.Color, 0, Defined: true)));
            await audit.WriteAsync(AuditActions.TagCreated, created.Name, ColorText(created.Color), ct).ConfigureAwait(false);
        }

        if (result.DevicesChanged > 0)
        {
            var added = TagNames.Canonical(request.Add);
            var removed = TagNames.Canonical(request.Remove);
            string detail = $"Tagged {DevicesText(result.DevicesChanged)}: {TagNames.ChangeText(added, removed)}";
            await audit.WriteAsync(AuditActions.DevicesTagged, TagNames.ChangeText(added, removed), detail, ct).ConfigureAwait(false);
        }

        return reply;
    }

    internal static Proto.TagList ToProto(IEnumerable<TagSummary> summaries)
    {
        var list = new Proto.TagList();
        list.Tags.AddRange(summaries.Select(ToProto));
        return list;
    }

    internal static Proto.DeviceTag ToProto(TagSummary tag) => new()
    {
        Id = tag.Id?.ToString() ?? string.Empty,
        Name = tag.Name,
        Color = (Proto.TagColor)(int)tag.Color,
        DeviceCount = tag.DeviceCount,
        Defined = tag.Defined,
    };

    internal static TagColor ToCore(Proto.TagColor color) =>
        Enum.IsDefined((TagColor)(int)color) ? (TagColor)(int)color : TagColor.None;

    private static string ColorText(TagColor color) => color == TagColor.None ? "no color" : color.ToString().ToLowerInvariant();

    private static string DevicesText(int count) =>
        count == 1 ? "1 device" : string.Create(CultureInfo.InvariantCulture, $"{count} devices");

    /// <summary>Maps the store's exceptions to status codes; the detail is the user message.</summary>
    private static async Task<T> Guard<T>(Func<Task<T>> call)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (TagNameException ex)
        {
            throw GrpcGuard.InvalidArgument(ex.Message);
        }
        catch (TagConflictException ex)
        {
            throw new RpcException(new Status(StatusCode.AlreadyExists, ex.Message));
        }
        catch (TagLimitException ex)
        {
            throw new RpcException(new Status(StatusCode.ResourceExhausted, ex.Message));
        }
        catch (KeyNotFoundException ex)
        {
            throw GrpcGuard.NotFound(ex.Message);
        }
    }
}
