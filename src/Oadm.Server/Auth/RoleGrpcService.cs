using Grpc.Core;

using Oadm.Core.Auth;
using Oadm.Core.Plugins;
using Oadm.Server.Common;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Auth;

/// <summary>
/// gRPC RoleService: the permission matrix of the Users page (Administrator only) and the caller's own rights (Mine,
/// every user). Every change goes to the audit log per role ("+Web View: Open devices -Restart").
/// </summary>
public sealed class RoleGrpcService(RoleStore roles, PluginRegistry registry, AuditLog audit) : Proto.RoleService.RoleServiceBase
{
    public override async Task<Proto.RoleMatrix> GetMatrix(Proto.Empty request, ServerCallContext context) =>
        await MatrixAsync(context.CancellationToken).ConfigureAwait(false);

    public override async Task<Proto.RoleMatrix> SaveRoles(Proto.SaveRolesRequest request, ServerCallContext context)
    {
        var ct = context.CancellationToken;
        var changes = request.Roles.Select(r => new RoleChange(
            string.IsNullOrEmpty(r.Id) ? null : GrpcGuard.ParseId(r.Id, "role id"),
            r.Name,
            r.Description,
            [.. r.Permissions],
            r.Version,
            r.HasStartPage ? StartPageOf(r.StartPage) : null)).ToList();
        try
        {
            var diffs = await roles.SaveAsync(changes, ct).ConfigureAwait(false);
            foreach (var diff in diffs)
            {
                await audit.WriteAsync(diff.Created ? AuditActions.RoleCreated : AuditActions.RoleChanged, diff.RoleName, Describe(diff), ct).ConfigureAwait(false);
            }

            return await MatrixAsync(ct).ConfigureAwait(false);
        }
        catch (AuthException ex)
        {
            throw AuthMapping.ToRpc(ex);
        }
    }

    public override async Task<Proto.RoleMatrix> DeleteRole(Proto.RoleIdRequest request, ServerCallContext context)
    {
        var id = GrpcGuard.ParseId(request.Id, "role id");
        try
        {
            var name = await roles.DeleteAsync(id, context.CancellationToken).ConfigureAwait(false);
            await audit.WriteAsync(AuditActions.RoleDeleted, name, null, context.CancellationToken).ConfigureAwait(false);
            return await MatrixAsync(context.CancellationToken).ConfigureAwait(false);
        }
        catch (AuthException ex)
        {
            throw AuthMapping.ToRpc(ex);
        }
    }

    public override async Task<Proto.MyRole> Mine(Proto.Empty request, ServerCallContext context)
    {
        var caller = AuthInterceptor.RequireCaller();
        var reply = new Proto.MyRole
        {
            RoleName = await roles.NameOfAsync(caller.RoleId, context.CancellationToken).ConfigureAwait(false),
            AllPermissions = caller.Permissions.IsAll,
        };
        reply.Permissions.AddRange(roles.Catalog.Entries.Where(e => caller.Has(e.Key)).Select(ToProto));
        return reply;
    }

    /// <summary>
    /// The pages a role can start on (the client's rail keys, <c>HostPages</c>): Devices (stored empty), Compliance, Event
    /// rules, Logs and every core plugin page, on or off (a page the user cannot open opens Devices). Anything else is
    /// refused.
    /// </summary>
    private static readonly HashSet<string> HostStartPages = new(StringComparer.Ordinal) { "compliance", "sites", "eventRules", "logs" };

    private string StartPageOf(string? value)
    {
        var page = (value ?? string.Empty).Trim();
        if (page.Length == 0 || page == "devices")
        {
            return string.Empty;
        }

        if (HostStartPages.Contains(page)
            || (page.StartsWith("plugin:", StringComparison.Ordinal)
                && registry.AllCorePlugins.Any(p => p.Plugin.HasPage && "plugin:" + p.Id == page)))
        {
            return page;
        }

        throw GrpcGuard.InvalidArgument("Choose a start page from the list.");
    }

    /// <summary>The name a start page has in the rail ("Compliance", "Live View"), for the audit log.</summary>
    private string StartPageName(string page) => page switch
    {
        "" => "Devices",
        "compliance" => "Compliance",
        "eventRules" => "Event rules",
        "logs" => "Logs",
        _ => registry.AllCorePlugins.FirstOrDefault(p => "plugin:" + p.Id == page)?.Plugin.DisplayName ?? page,
    };

    /// <summary>"+Web View: Open devices, -Restart" (names as the matrix shows them), plus "renamed" when it was.</summary>
    private string Describe(RoleDiff diff)
    {
        var parts = new List<string>();
        if (diff.Renamed)
        {
            parts.Add("name or description");
        }

        if (diff.StartPage is { } start)
        {
            parts.Add("start page " + StartPageName(start));
        }

        parts.AddRange(diff.Added.Select(k => "+" + NameOf(k)));
        parts.AddRange(diff.Removed.Select(k => "-" + NameOf(k)));
        return string.Join(", ", parts);
    }

    private string NameOf(string key)
    {
        var entry = roles.Catalog.Find(key);
        return entry is null ? key
            : entry.Group is HostPermissions.DevicesGroup or HostPermissions.TasksGroup or HostPermissions.EventRulesGroup or HostPermissions.LogsGroup ? entry.Name
            : entry.Group + ": " + entry.Name;
    }

    private async Task<Proto.RoleMatrix> MatrixAsync(CancellationToken ct)
    {
        var reply = new Proto.RoleMatrix();
        reply.Permissions.AddRange(roles.Catalog.Entries.Select(ToProto));
        foreach (var role in await roles.ListAsync(ct).ConfigureAwait(false))
        {
            var info = new Proto.RoleInfo
            {
                Id = role.Id.ToString(),
                Name = role.Name,
                Description = role.Description,
                Kind = (Proto.RoleKind)(int)role.Kind,
                UserCount = role.UserCount,
                Version = role.Version,
                StartPage = role.StartPage,
            };
            info.Permissions.AddRange(role.Permissions.Keys.Order(StringComparer.Ordinal));
            reply.Roles.Add(info);
        }

        return reply;
    }

    private Proto.PermissionInfo ToProto(PermissionEntry entry) => new()
    {
        Key = entry.Key,
        Name = entry.Name,
        Description = entry.Description,
        Group = entry.Group,
        IconKey = entry.IconKey ?? string.Empty,
        OperatorDefault = entry.OperatorDefault,
        ViewerDefault = entry.ViewerDefault,
        FullDeviceControl = entry.FullDeviceControl,
        Available = entry.PackageId is null || registry.IsEnabled(entry.PackageId),
    };
}
