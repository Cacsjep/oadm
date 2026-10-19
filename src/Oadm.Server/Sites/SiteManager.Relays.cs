using Oadm.Core.Sites;
using Oadm.Sdk.Sites;

namespace Oadm.Server.Sites;

/// <summary>
/// Relays into the sites (docs/sites.md, "Relay", <see cref="ISiteRelays"/>): the bound sites for plugins and one
/// self-reopening relay per <see cref="OpenUdp"/> / <see cref="OpenTcp"/> over the node's <c>Relay</c> call.
/// </summary>
public sealed partial class SiteManager : ISiteRelays
{
    private EventHandler? _sitesChanged;
    private int _forwarding;

    /// <summary>Every bound site, by name.</summary>
    public IReadOnlyList<ISiteInfo> BoundSites
    {
        get
        {
            lock (_lock)
            {
                return [.. _sites.Values
                    .Where(e => !e.Row.NotBound)
                    .OrderBy(e => e.Row.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(e => new SiteInfo(e.Row.Id, e.Row.Name, e.State == SiteState.Online))];
            }
        }
    }

    /// <summary>A site was bound, renamed, removed, or went online or offline (<see cref="Changed"/> without the ids).</summary>
    public event EventHandler? SitesChanged
    {
        add
        {
            if (Interlocked.Exchange(ref _forwarding, 1) == 0)
            {
                Changed += (_, _) => _sitesChanged?.Invoke(this, EventArgs.Empty);
            }

            _sitesChanged += value;
        }

        remove => _sitesChanged -= value;
    }

    public IUdpRelay OpenUdp(Guid siteId, int port, string reason)
    {
        var relay = new SiteUdpRelay(this, Known(siteId), siteId, port, reason);
        relay.Start();
        return relay;
    }

    public ITcpRelay OpenTcp(Guid siteId, int port, string reason)
    {
        var relay = new SiteTcpRelay(this, Known(siteId), siteId, port, reason);
        relay.Start();
        return relay;
    }

    /// <summary>The site's name; <see cref="ArgumentException"/> for an unknown site.</summary>
    private string Known(Guid siteId) =>
        NameOf(siteId) ?? throw new ArgumentException("Unknown site " + siteId + ".", nameof(siteId));

    /// <summary>
    /// The session a relay at <paramref name="siteId"/> can use now (online, ready), else null; the site's current name
    /// (null when it was removed).
    /// </summary>
    internal (SiteSession? Session, string? Name) RelayTarget(Guid siteId)
    {
        lock (_lock)
        {
            if (!_sites.TryGetValue(siteId, out var entry))
            {
                return (null, null);
            }

            var session = entry.Session;
            var usable = !entry.Row.NotBound && entry.State == SiteState.Online && session is { IsEnded: false, Ready: true };
            return (usable ? session : null, entry.Row.Name);
        }
    }
}
