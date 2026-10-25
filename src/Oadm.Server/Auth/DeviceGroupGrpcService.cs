using Google.Protobuf.WellKnownTypes;

using Grpc.Core;

using Oadm.Core.Auth;
using Oadm.Core.DeviceGroups;
using Oadm.Server.Common;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Auth;

/// <summary>
/// gRPC DeviceGroupService (Administrator only, <see cref="AccessPolicy"/>): the device groups of the Users page and the
/// group editor's live count. Every change goes to the audit log ("Created device group", "Changed device group" with what
/// changed, "Deleted device group" with the users who lost their devices). No device ids: rules name sites, tags and serials.
/// </summary>
public sealed class DeviceGroupGrpcService(DeviceGroupStore groups, DeviceViewFactory views, AuditLog audit) : Proto.DeviceGroupService.DeviceGroupServiceBase
{
    public override async Task<Proto.DeviceGroupList> List(Proto.Empty request, ServerCallContext context)
    {
        var ct = context.CancellationToken;
        var all = await groups.ListAsync(ct).ConfigureAwait(false);
        var (counts, total) = await views.CountGroupsAsync(all, ct).ConfigureAwait(false);
        var users = await groups.UserNamesByGroupAsync(ct).ConfigureAwait(false);
        var reply = new Proto.DeviceGroupList { TotalDevices = total };
        reply.Groups.AddRange(all.Select(g => ToProto(g, counts.GetValueOrDefault(g.Id), users.GetValueOrDefault(g.Id) ?? [], views.Catalog)));
        return reply;
    }

    public override async Task<Proto.DeviceGroupInfo> Get(Proto.DeviceGroupIdRequest request, ServerCallContext context)
    {
        var id = GrpcGuard.ParseId(request.Id, "device group id");
        var group = await groups.GetAsync(id, context.CancellationToken).ConfigureAwait(false)
            ?? throw GrpcGuard.NotFound(DeviceGroupTexts.UnknownGroup);
        return await InfoAsync(group, context.CancellationToken).ConfigureAwait(false);
    }

    public override async Task<Proto.DeviceGroupInfo> Save(Proto.SaveDeviceGroupRequest request, ServerCallContext context)
    {
        var ct = context.CancellationToken;
        Guid? id = string.IsNullOrEmpty(request.Id) ? null : GrpcGuard.ParseId(request.Id, "device group id");
        try
        {
            var result = await groups.SaveAsync(id, request.Name, request.Description, RulesOf(request.Rules), request.Revision, ct).ConfigureAwait(false);
            if (result.Created || result.Changes.Length > 0)
            {
                await audit.WriteAsync(result.Created ? AuditActions.DeviceGroupCreated : AuditActions.DeviceGroupChanged, result.Group.Name, result.Changes, CancellationToken.None).ConfigureAwait(false); // stored: always audited
            }

            return await InfoAsync(result.Group, ct).ConfigureAwait(false);
        }
        catch (DeviceGroupException ex)
        {
            throw ToRpc(ex);
        }
    }

    public override async Task<Proto.DeleteDeviceGroupReply> Delete(Proto.DeviceGroupIdRequest request, ServerCallContext context)
    {
        var id = GrpcGuard.ParseId(request.Id, "device group id");
        try
        {
            var result = await groups.DeleteAsync(id, context.CancellationToken).ConfigureAwait(false);
            var detail = string.Join("; ", new[]
            {
                result.UserNames.Count == 0 ? null : "no devices now: " + string.Join(", ", result.UserNames),
                result.DisabledObjects.Count == 0 ? null : "turned off: " + string.Join(", ", result.DisabledObjects),
            }.OfType<string>());
            await audit.WriteAsync(AuditActions.DeviceGroupDeleted, result.Name, detail, CancellationToken.None).ConfigureAwait(false); // deleted: always audited
            var reply = new Proto.DeleteDeviceGroupReply();
            reply.UserNames.AddRange(result.UserNames);
            reply.Disabled.AddRange(result.DisabledObjects);
            return reply;
        }
        catch (DeviceGroupException ex)
        {
            throw ToRpc(ex);
        }
    }

    public override async Task<Proto.DeviceGroupPreview> Preview(Proto.PreviewDeviceGroupRequest request, ServerCallContext context)
    {
        Guid? groupId = string.IsNullOrEmpty(request.GroupId) ? null : GrpcGuard.ParseId(request.GroupId, "device group id");
        try
        {
            var count = await views.CountAsync(RulesOf(request.Rules), groupId, context.CancellationToken).ConfigureAwait(false);
            var total = await views.System.CountAsync(context.CancellationToken).ConfigureAwait(false);
            return new Proto.DeviceGroupPreview { DeviceCount = count, TotalDevices = total };
        }
        catch (DeviceGroupException ex)
        {
            throw ToRpc(ex);
        }
    }

    /// <summary>The rules of a request (ids parsed, serials checked by the store).</summary>
    internal static DeviceGroupRules RulesOf(Proto.DeviceGroupRules? rules)
    {
        if (rules is null)
        {
            return DeviceGroupRules.Empty;
        }

        return new DeviceGroupRules(
            rules.ThisServer,
            [.. rules.SiteIds.Select(s => GrpcGuard.ParseId(s, "site id"))],
            [.. rules.TagIds.Select(t => GrpcGuard.ParseId(t, "tag id"))],
            [.. rules.Serials],
            [.. rules.ExceptSerials]);
    }

    internal static Proto.DeviceGroupRules ToProto(DeviceGroupRules rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var proto = new Proto.DeviceGroupRules { ThisServer = rules.ThisServer };
        proto.SiteIds.AddRange(rules.SiteIds.Select(id => id.ToString()));
        proto.TagIds.AddRange(rules.TagIds.Select(id => id.ToString()));
        proto.Serials.AddRange(rules.Serials);
        proto.ExceptSerials.AddRange(rules.ExceptSerials);
        return proto;
    }

    internal static RpcException ToRpc(DeviceGroupException ex) => new(new Status(
        ex.Error switch
        {
            DeviceGroupError.NotFound => StatusCode.NotFound,
            DeviceGroupError.AlreadyExists => StatusCode.AlreadyExists,
            DeviceGroupError.Precondition => StatusCode.FailedPrecondition,
            DeviceGroupError.ResourceExhausted => StatusCode.ResourceExhausted,
            _ => StatusCode.InvalidArgument,
        },
        ex.Message));

    private async Task<Proto.DeviceGroupInfo> InfoAsync(DeviceGroup group, CancellationToken ct)
    {
        var (counts, _) = await views.CountGroupsAsync([group], ct).ConfigureAwait(false);
        var users = await groups.UserNamesAsync(group.Id, ct).ConfigureAwait(false);
        return ToProto(group, counts.GetValueOrDefault(group.Id), users, views.Catalog);
    }

    private static Proto.DeviceGroupInfo ToProto(DeviceGroup group, int deviceCount, IReadOnlyList<string> userNames, DeviceScopeCatalog catalog)
    {
        var info = new Proto.DeviceGroupInfo
        {
            Id = group.Id.ToString(),
            Name = group.Name,
            Description = group.Description,
            Rules = ToProto(group.Rules),
            Revision = group.Revision,
            DeviceCount = deviceCount,
            Summary = DeviceGroupSummary.Text(group.Rules, catalog),
            Created = Timestamp.FromDateTime(DateTime.SpecifyKind(group.CreatedUtc, DateTimeKind.Utc)),
            Changed = Timestamp.FromDateTime(DateTime.SpecifyKind(group.ChangedUtc, DateTimeKind.Utc)),
        };
        info.UserNames.AddRange(userNames);
        return info;
    }
}
