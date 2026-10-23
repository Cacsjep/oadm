using System.Threading.Channels;

using Grpc.Core;

using Oadm.Core.Auth;
using Oadm.Core.DeviceGroups;
using Oadm.Server.Auth;
using Oadm.Server.Common;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Sites;

/// <summary>
/// gRPC SiteService (sites.proto): the Sites page. List and Watch need "See sites"; Accept site nodes, Bind, Rename,
/// Replace and Remove are Administrator only (AccessPolicy) and audited. A user with a device group sees only the sites of
/// the group, with the group's device counts (docs/spec/device-scope.md, Sites page); users with all devices see every
/// site and the device groups that use it.
/// </summary>
public sealed class SiteGrpcService(SiteManager sites, SiteAdmin admin, AuditLog audit, TimeProvider time, IHostApplicationLifetime lifetime,
    DeviceViewFactory views, DeviceGroupStore groups) : Proto.SiteService.SiteServiceBase
{
    /// <summary>Watch sends the list at most this often.</summary>
    public static readonly TimeSpan WatchInterval = TimeSpan.FromSeconds(1);

    public override async Task<Proto.SiteList> ListSites(Proto.Empty request, ServerCallContext context)
    {
        var view = await views.ForAsync(context).ConfigureAwait(false);
        return await PageAsync(view, context.CancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The Sites page as <paramref name="view"/> may see it: every row with the groups that use each site for all devices;
    /// for a group only its sites (bound or offline; no removed sites, no waiting nodes) with the group's device counts.
    /// </summary>
    private async Task<Proto.SiteList> PageAsync(DeviceView view, CancellationToken ct)
    {
        var page = await sites.PageAsync(ct).ConfigureAwait(false);
        if (view.HasAllDevices)
        {
            await groups.LoadAsync(ct).ConfigureAwait(false);
            foreach (var site in page.Sites)
            {
                if (Guid.TryParse(site.Id, out var id))
                {
                    site.GroupNames.AddRange(groups.GroupsUsingSite(id));
                }
            }

            return page;
        }

        var filter = view.Filter;
        var counts = await view.CountsBySiteAsync(ct).ConfigureAwait(false);
        var mine = new Proto.SiteList { Accept = page.Accept, ListenAddress = page.ListenAddress };
        foreach (var site in page.Sites)
        {
            if (site.State != Proto.SiteState.Removed && Guid.TryParse(site.Id, out var id) && filter.CoversSite(id))
            {
                site.DeviceCount = counts.GetValueOrDefault(id);
                mine.Sites.Add(site);
            }
        }

        return mine;
    }

    public override async Task WatchSites(Proto.Empty request, IServerStreamWriter<Proto.SiteList> responseStream, ServerCallContext context)
    {
        using var linked = GrpcGuard.LinkWithShutdown(context, lifetime.ApplicationStopping);
        var ct = linked.Token;
        var view = await views.ForAsync(context).ConfigureAwait(false);
        var signal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
        void OnChanged(object? sender, EventArgs e) => signal.Writer.TryWrite(true);
        sites.ListChanged += OnChanged;
        try
        {
            await responseStream.WriteAsync(await PageAsync(view, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
            while (await signal.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                await Task.Delay(WatchInterval, time, ct).ConfigureAwait(false);
                signal.Reader.TryRead(out _);
                await responseStream.WriteAsync(await PageAsync(view, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        finally
        {
            sites.ListChanged -= OnChanged;
        }
    }

    public override async Task<Proto.SiteList> SetAccept(Proto.SetAcceptRequest request, ServerCallContext context)
    {
        await Run(async () =>
        {
            await sites.SetAcceptAsync(request.Accept, context.CancellationToken).ConfigureAwait(false);
            return true;
        }).ConfigureAwait(false);
        await audit.WriteAsync(request.Accept ? SiteAudit.AcceptOn : SiteAudit.AcceptOff, "Sites", null, CancellationToken.None).ConfigureAwait(false);
        return await PageAsync(await views.ForAsync(context).ConfigureAwait(false), context.CancellationToken).ConfigureAwait(false);
    }

    public override async Task<Proto.Site> BindSite(Proto.BindSiteRequest request, ServerCallContext context)
    {
        await Run(() => sites.BindAsync(request.NodeId, request.NodeFingerprint, request.Name, request.PairingCode, context.CancellationToken)).ConfigureAwait(false);
        return await FindAsync(request.NodeId, context.CancellationToken).ConfigureAwait(false);
    }

    public override async Task<Proto.Site> RenameSite(Proto.RenameSiteRequest request, ServerCallContext context)
    {
        var id = GrpcGuard.ParseId(request.Id, "site id");
        await Run(() => sites.RenameAsync(id, request.Name, context.CancellationToken)).ConfigureAwait(false);
        return await FindByIdAsync(id, context.CancellationToken).ConfigureAwait(false);
    }

    public override async Task<Proto.ReplaceSiteReply> ReplaceSite(Proto.ReplaceSiteRequest request, ServerCallContext context)
    {
        var id = GrpcGuard.ParseId(request.Id, "site id");
        var newId = GrpcGuard.ParseId(request.NewSiteId, "new site id");
        var (moved, stayed) = await Run(() => admin.ReplaceAsync(id, newId, context.CancellationToken)).ConfigureAwait(false);
        var reply = new Proto.ReplaceSiteReply { Moved = moved };
        reply.StayedDeviceIds.AddRange(stayed.Select(s => s.ToString()));
        return reply;
    }

    public override async Task<Proto.RemoveSiteReply> RemoveSite(Proto.RemoveSiteRequest request, ServerCallContext context)
    {
        var id = GrpcGuard.ParseId(request.Id, "site id");
        var (answered, removed) = await Run(() => admin.RemoveAsync(id, request.KeepDevices, context.CancellationToken)).ConfigureAwait(false);
        return new Proto.RemoveSiteReply { NodeAnswered = answered, DevicesRemoved = removed };
    }

    private async Task<Proto.Site> FindAsync(string nodeId, CancellationToken ct) =>
        (await sites.PageAsync(ct).ConfigureAwait(false)).Sites.FirstOrDefault(s => s.NodeId == nodeId && s.Id.Length > 0)
        ?? throw GrpcGuard.NotFound(SiteTexts.SiteGone);

    private async Task<Proto.Site> FindByIdAsync(Guid id, CancellationToken ct) =>
        (await sites.PageAsync(ct).ConfigureAwait(false)).Sites.FirstOrDefault(s => s.Id == id.ToString())
        ?? throw GrpcGuard.NotFound(SiteTexts.SiteGone);

    private static async Task<T> Run<T>(Func<Task<T>> call)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (SiteRefusedException e)
        {
            throw new RpcException(new Status(e.Code, e.Message));
        }
    }
}
