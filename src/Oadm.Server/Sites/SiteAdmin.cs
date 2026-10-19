using Grpc.Core;

using Oadm.Core.Auth;
using Oadm.Core.Devices;
using Oadm.Core.Sites;
using Oadm.Core.Vapix;

namespace Oadm.Server.Sites;

/// <summary>Removing and replacing sites with their devices (docs/sites.md, Binding 6 and "Replace and change address").</summary>
public sealed class SiteAdmin(SiteManager sites, DeviceRepository devices, VapixProbe probe, AuditLog audit)
{
    /// <summary>Parallel serial checks of Replace.</summary>
    public const int ReplaceParallelism = 8;

    /// <summary>
    /// Removes the site: Unbind to the node (forgotten anyway when it does not answer), then the devices either kept (Site
    /// offline until replaced or moved) or removed with their credentials and tags in one transaction.
    /// </summary>
    public async Task<(bool NodeAnswered, int DevicesRemoved)> RemoveAsync(Guid siteId, bool keepDevices, CancellationToken ct)
    {
        var name = sites.NameOf(siteId) ?? throw new SiteRefusedException(StatusCode.NotFound, "The site no longer exists.");
        var ids = await devices.ListIdsBySitesAsync([siteId], ct).ConfigureAwait(false);
        var answered = await sites.ForgetAsync(siteId, ct).ConfigureAwait(false);
        var removed = 0;
        if (!keepDevices && ids.Count > 0)
        {
            removed = (await devices.RemoveManyAsync(ids, CancellationToken.None).ConfigureAwait(false)).Count;
        }

        var detail = keepDevices ? $"{ids.Count} devices kept" : $"{removed} devices removed";
        await audit.WriteAsync(SiteAudit.Removed, name, answered ? detail : detail + "; the site node did not answer", CancellationToken.None).ConfigureAwait(false);
        return (answered, removed);
    }

    /// <summary>
    /// Moves every device of a site to another bound site after checking its serial at the same address through the new
    /// one. Devices that do not answer there (or answer with another serial) stay and are returned.
    /// </summary>
    public async Task<(int Moved, IReadOnlyList<Guid> Stayed)> ReplaceAsync(Guid siteId, Guid newSiteId, CancellationToken ct)
    {
        var oldName = sites.NameOf(siteId) ?? throw new SiteRefusedException(StatusCode.NotFound, "The site no longer exists.");
        var newName = sites.NameOf(newSiteId) ?? throw new SiteRefusedException(StatusCode.NotFound, "The new site no longer exists.");
        if (siteId == newSiteId)
        {
            throw new SiteRefusedException(StatusCode.InvalidArgument, "Choose another site.");
        }

        if (!sites.IsOnline(newSiteId))
        {
            throw new SiteRefusedException(StatusCode.FailedPrecondition, Sdk.Sites.SiteTexts.SiteOffline(newName));
        }

        var list = await devices.ListBySitesAsync([siteId], ct).ConfigureAwait(false);
        var route = DeviceRoute.ViaSite(newSiteId);
        var stayed = new System.Collections.Concurrent.ConcurrentBag<Guid>();
        var moved = 0;
        await Parallel.ForEachAsync(list, new ParallelOptions { MaxDegreeOfParallelism = ReplaceParallelism, CancellationToken = ct }, async (device, token) =>
        {
            VapixProbeResult? found = null;
            try
            {
                found = await probe.ProbeAsync(device.Address, token, [DeviceConnections.SchemeOf(device)], route: route).ConfigureAwait(false);
            }
            catch (Exception e) when (e is HttpRequestException or SiteException or TaskCanceledException or IOException)
            {
            }

            if (found is null || !string.Equals(found.Serial, device.Serial, StringComparison.OrdinalIgnoreCase))
            {
                stayed.Add(device.Id);
                return;
            }

            await devices.UpdateAsync(device.Id, d => d.SiteId = newSiteId, token).ConfigureAwait(false);
            Interlocked.Increment(ref moved);
        }).ConfigureAwait(false);

        await audit.WriteAsync(SiteAudit.Replaced, oldName, $"{oldName} to {newName}: {moved} moved, {stayed.Count} stayed", CancellationToken.None).ConfigureAwait(false);
        return (moved, [.. stayed]);
    }
}
