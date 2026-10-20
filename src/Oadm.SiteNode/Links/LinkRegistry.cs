using Oadm.SiteNode.State;
using Oadm.Sites.Transport;

namespace Oadm.SiteNode.Links;

/// <summary>
/// The open links. Every link is one the node opened to the one server address it chose, so they all belong to one server
/// session (docs/sites.md, "Server identity"; the server refuses a second control link of the same identity). Serves every
/// link on Kestrel through the <see cref="AcceptedStreamListener"/>.
/// </summary>
public sealed class LinkRegistry(AcceptedStreamListener listener, BindingState state)
{
    private readonly Lock _lock = new();
    private readonly SortedDictionary<long, SiteLink> _links = [];

    /// <summary>Raised when a link opens or closes.</summary>
    public event Action? Changed;

    public IReadOnlyList<SiteLink> Links
    {
        get
        {
            lock (_lock)
                return [.. _links.Values];
        }
    }

    /// <summary>Open links of the bound server (control and workers).</summary>
    public int BoundLinks
    {
        get
        {
            var bound = state.Bound?.Fingerprint;
            if (string.IsNullOrEmpty(bound))
                return 0;
            lock (_lock)
                return _links.Values.Count(l => Of(l, bound) && !l.Closing.IsCancellationRequested);
        }
    }

    /// <summary>Serves gRPC on <paramref name="tls"/> until either side closes it or <paramref name="link"/> is closed.</summary>
    public async Task ServeAsync(Stream tls, SiteLink link, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tls);
        ArgumentNullException.ThrowIfNull(link);
        Add(link);
        try
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, link.Closing.Token);
            await listener.ServeAsync(tls, new LinkEndPoint(link), stop.Token).ConfigureAwait(false);
        }
        finally
        {
            Remove(link);
        }
    }

    /// <summary>Closes every link (Reset: the identity changed).</summary>
    public void CloseAll()
    {
        foreach (var link in Links)
            link.Close();
    }

    /// <summary>Closes the links whose server is not the bound one any more (Unbind keeps the link: the server may bind again).</summary>
    public void CloseWhere(Func<SiteLink, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        foreach (var link in Links.Where(predicate))
            link.Close();
    }

    private static bool Of(SiteLink link, string fingerprint) =>
        string.Equals(link.ServerFingerprintHex, fingerprint, StringComparison.OrdinalIgnoreCase);

    private void Add(SiteLink link)
    {
        lock (_lock)
            _links[link.Id] = link;
        NodeEvents.Raise(Changed);
    }

    private void Remove(SiteLink link)
    {
        lock (_lock)
            _links.Remove(link.Id);
        NodeEvents.Raise(Changed);
    }
}
