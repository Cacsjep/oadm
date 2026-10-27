using Grpc.Core;

using Oadm.Core.Auth;
using Oadm.Core.Devices;
using Oadm.Core.Sites;
using Oadm.Core.Vapix;

namespace Oadm.Server.Sites;

/// <summary>
/// Removing and replacing sites with their devices (docs/spec/sites.md, Server side: SiteAdmin). A site
/// removed with Keep devices leaves a tombstone (<see cref="SiteManager.MarkRemovedAsync"/>): its devices show
/// "&lt;site&gt; (removed)", their status events go off, the Sites page lists it as Removed with Replace with and Remove
/// devices until the last device left. Waiting entries of a removed site are removed; Replace moves them to the new site.
/// </summary>
public sealed class SiteAdmin(SiteManager sites, DeviceRepository devices, VapixProbe probe, AuditLog audit, WaitingDeviceStore? waiting = null)
{
    /// <summary>Parallel serial checks of Replace.</summary>
    public const int ReplaceParallelism = 8;

    /// <summary>
    /// Removes the site: Unbind to the node (forgotten anyway when it does not answer), then the devices either kept (Site
    /// offline until replaced or moved, the site listed as Removed meanwhile) or removed with their credentials and tags in
    /// one transaction. A removed site (tombstone) takes only <paramref name="keepDevices"/> false: its devices are removed.
    /// </summary>
    public async Task<(bool NodeAnswered, int DevicesRemoved)> RemoveAsync(Guid siteId, bool keepDevices, CancellationToken ct)
    {
        if (sites.NameOf(siteId) is null && sites.RemovedNameOf(siteId) is { } removedName)
        {
            return (true, await RemoveDevicesOfRemovedAsync(siteId, removedName, keepDevices, ct).ConfigureAwait(false));
        }

        var name = sites.NameOf(siteId) ?? throw new SiteRefusedException(StatusCode.NotFound, SiteTexts.SiteGone);
        var ids = await devices.ListIdsBySitesAsync([siteId], ct).ConfigureAwait(false);
        if (keepDevices && ids.Count > 0)
        {
            // Before the site row goes: the devices' next publish (the site's change) names it "(removed)".
            await sites.MarkRemovedAsync(siteId, name, ct).ConfigureAwait(false);
        }

        var answered = await sites.ForgetAsync(siteId, ct).ConfigureAwait(false);
        var removed = 0;
        if (!keepDevices && ids.Count > 0)
        {
            removed = (await devices.RemoveManyAsync(ids, CancellationToken.None).ConfigureAwait(false)).Count;
        }

        if (waiting is not null)
        {
            await waiting.RemoveForSiteAsync(siteId, CancellationToken.None).ConfigureAwait(false);
        }

        var detail = keepDevices ? $"{ids.Count} devices kept" : $"{removed} devices removed";
        await audit.WriteAsync(SiteAudit.Removed, name, answered ? detail : detail + "; the site node did not answer", CancellationToken.None).ConfigureAwait(false);
        return (answered, removed);
    }

    private async Task<int> RemoveDevicesOfRemovedAsync(Guid siteId, string name, bool keepDevices, CancellationToken ct)
    {
        if (keepDevices)
        {
            throw new SiteRefusedException(StatusCode.FailedPrecondition, SiteTexts.RemovedKeepsDevices);
        }

        var ids = await devices.ListIdsBySitesAsync([siteId], ct).ConfigureAwait(false);
        var removed = ids.Count == 0 ? 0 : (await devices.RemoveManyAsync(ids, CancellationToken.None).ConfigureAwait(false)).Count;
        await sites.DropRemovedAsync(siteId, CancellationToken.None).ConfigureAwait(false);
        await audit.WriteAsync(SiteAudit.Removed, sites.RemovedNameOf(siteId) ?? name, $"{removed} devices removed", CancellationToken.None).ConfigureAwait(false);
        return removed;
    }

    /// <summary>
    /// Moves every device of a site (also of a removed site) to another bound site after checking its serial at the same
    /// address through the new one. Devices that do not answer there (or answer with another serial) stay and are returned.
    /// The waiting entries of the site wait for the new one; a removed site whose last device moved is forgotten.
    /// </summary>
    public async Task<(int Moved, IReadOnlyList<Guid> Stayed)> ReplaceAsync(Guid siteId, Guid newSiteId, CancellationToken ct)
    {
        var oldName = sites.NameOf(siteId) ?? sites.RemovedNameOf(siteId) ?? throw new SiteRefusedException(StatusCode.NotFound, SiteTexts.SiteGone);
        var newName = sites.NameOf(newSiteId) ?? throw new SiteRefusedException(StatusCode.NotFound, SiteTexts.NewSiteGone);
        if (siteId == newSiteId)
        {
            throw new SiteRefusedException(StatusCode.InvalidArgument, SiteTexts.ChooseAnotherSite);
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

        if (waiting is not null)
        {
            await waiting.MoveSiteAsync(siteId, newSiteId, CancellationToken.None).ConfigureAwait(false);
        }

        if (sites.IsRemoved(siteId) && stayed.IsEmpty)
        {
            await sites.DropRemovedAsync(siteId, CancellationToken.None).ConfigureAwait(false);
        }

        sites.RaiseList(); // the device counts changed
        await audit.WriteAsync(SiteAudit.Replaced, oldName, $"{oldName} to {newName}: {moved} moved, {stayed.Count} stayed", CancellationToken.None).ConfigureAwait(false);
        return (moved, [.. stayed]);
    }
}
