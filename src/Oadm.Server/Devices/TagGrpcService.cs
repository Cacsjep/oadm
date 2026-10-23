using System.Globalization;
using System.Threading.Channels;

using Grpc.Core;

using Oadm.Core.Auth;
using Oadm.Core.DeviceGroups;
using Oadm.Core.Devices;
using Oadm.Server.Auth;
using Oadm.Server.Common;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Devices;

/// <summary>
/// gRPC TagService: tag definitions (name + color) and tagging many devices in one call (<see cref="DeviceTagStore"/>).
/// Roles (<c>AccessPolicy</c>): Update and Delete need tags.manage and all devices, the rest tags.assign. Every change is
/// audited. Device groups (docs/spec/device-scope.md): the counts are those of the caller's view, the devices of
/// SetDeviceTags come from the view (an unknown or outside id refuses the call), the store refuses tags a group uses for
/// users with a group, and every move of a device into or out of a group is audited (<see cref="DeviceMembershipAudit"/>).
/// </summary>
public sealed class TagGrpcService(
    DeviceTagStore tags,
    DeviceViewFactory views,
    DeviceGroupStore groups,
    DeviceMembershipAudit membership,
    AuditLog audit,
    IHostApplicationLifetime lifetime) : Proto.TagService.TagServiceBase
{
    public override async Task<Proto.TagList> List(Proto.Empty request, ServerCallContext context)
    {
        var view = await views.ForAsync(context).ConfigureAwait(false);
        return await ListAsync(view, context.CancellationToken).ConfigureAwait(false);
    }

    /// <summary>The list now and after every change of the definitions; a burst of changes sends the newest list once.</summary>
    public override async Task Watch(Proto.Empty request, IServerStreamWriter<Proto.TagList> responseStream, ServerCallContext context)
    {
        var view = await views.ForAsync(context).ConfigureAwait(false);
        using var linked = GrpcGuard.LinkWithShutdown(context, lifetime.ApplicationStopping);
        var ct = linked.Token;
        var signal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
        void OnChanged(object? sender, EventArgs e) => signal.Writer.TryWrite(true);
        tags.DefinitionsChanged += OnChanged;
        try
        {
            await responseStream.WriteAsync(await ListAsync(view, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
            while (await signal.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                signal.Reader.TryRead(out _);
                await responseStream.WriteAsync(await ListAsync(view, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
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

        // A rename keeps the tag in its groups (groups name tags by id): nobody moves.
        await groups.LoadAsync(ct).ConfigureAwait(false);
        return ToProto(new TagSummary(result.Tag.Id, result.Tag.Name, result.Tag.Color, 0, Defined: true), groups.GroupsUsingTag(result.Tag.Id));
    }

    /// <summary>
    /// Deletes the tag (users with all devices). The client asks first when <see cref="Proto.DeviceTag.GroupNames"/> names
    /// groups ("Used by device group Building B"); their devices of this tag leave them, audited per group.
    /// </summary>
    public override async Task<Proto.DeleteTagReply> Delete(Proto.TagName request, ServerCallContext context)
    {
        var ct = context.CancellationToken;
        var before = await membership.CaptureAsync(ct).ConfigureAwait(false);
        var result = await Guard(() => tags.DeleteAsync(request.Name, ct)).ConfigureAwait(false);
        await audit.WriteAsync(AuditActions.TagDeleted, result.Name, "removed from " + DevicesText(result.DevicesChanged), ct).ConfigureAwait(false);
        await membership.WriteAsync(before, result.Changes, $"tag {result.Name} deleted", ct).ConfigureAwait(false);
        return new Proto.DeleteTagReply { DevicesChanged = result.DevicesChanged };
    }

    public override async Task<Proto.SetDeviceTagsReply> SetDeviceTags(Proto.SetDeviceTagsRequest request, ServerCallContext context)
    {
        var ct = context.CancellationToken;
        var ids = GrpcGuard.ParseIds(request.DeviceIds, "device id");
        var view = await views.ForAsync(context).ConfigureAwait(false);
        var devices = await view.GetAsync(ids, ct).ConfigureAwait(false); // unknown or outside the view: NOT_FOUND, nothing changed
        var before = await membership.CaptureAsync(ct).ConfigureAwait(false);
        var result = await Guard(() => tags.SetDeviceTagsAsync(devices, [.. request.Add], [.. request.Remove], ct)).ConfigureAwait(false);
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
            await membership.WriteAsync(before, result.Changes, TagNames.ChangeText(added, removed), ct).ConfigureAwait(false);
        }

        return reply;
    }

    /// <summary>The tags with the counts of the view; users with all devices also get the groups that use each tag.</summary>
    private async Task<Proto.TagList> ListAsync(DeviceView view, CancellationToken ct)
    {
        var summaries = await tags.ListAsync(view, ct).ConfigureAwait(false);
        await groups.LoadAsync(ct).ConfigureAwait(false);
        var list = new Proto.TagList();
        list.Tags.AddRange(summaries.Select(s => ToProto(s, view.HasAllDevices && s.Id is { } id ? groups.GroupsUsingTag(id) : [])));
        return list;
    }

    internal static Proto.DeviceTag ToProto(TagSummary tag, IReadOnlyList<string>? groupNames = null)
    {
        var proto = new Proto.DeviceTag
        {
            Id = tag.Id?.ToString() ?? string.Empty,
            Name = tag.Name,
            Color = (Proto.TagColor)(int)tag.Color,
            DeviceCount = tag.DeviceCount,
            Defined = tag.Defined,
        };
        proto.GroupNames.AddRange(groupNames ?? []);
        return proto;
    }

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
        catch (TagInUseByGroupException ex)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, ex.Message));
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
