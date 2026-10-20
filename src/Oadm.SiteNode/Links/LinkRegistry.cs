using System.Net;

using Oadm.SiteNode.Admin;
using Oadm.SiteNode.State;
using Oadm.Sites.Transport;

namespace Oadm.SiteNode.Links;

/// <summary>
/// The open links and the rule "one server session at a time" (docs/sites.md, "Server identity"): the session of the bound
/// identity is the one of its oldest open link (outbound: every link the node opened; inbound: the links from one
/// address); a link of the same identity from another session is refused ("Another OADM server with this identity is
/// connected from 10.0.0.5"), so a restored copy of the server cannot drive the node alongside the real one. Checked on
/// every call against the binding of that moment (<see cref="RefusalFor"/>), so a later bind, rotation or a closed session
/// applies at once. Serves every link on Kestrel through the <see cref="AcceptedStreamListener"/>.
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

    /// <summary>Links of the bound server's one session (control and workers).</summary>
    public int BoundLinks
    {
        get
        {
            var bound = state.Bound?.Fingerprint;
            if (string.IsNullOrEmpty(bound))
                return 0;
            lock (_lock)
            {
                var active = ActiveSession(bound);
                return active is null ? 0 : _links.Values.Count(l => Of(l, bound) && SameSession(l, active));
            }
        }
    }

    /// <summary>
    /// Null when calls of the bound server may run on <paramref name="link"/>; else the refusal: another session of the
    /// same identity is the active one. Links of other identities are not this rule's business (the caller checks the binding).
    /// </summary>
    public string? RefusalFor(SiteLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        var bound = state.Bound?.Fingerprint;
        if (string.IsNullOrEmpty(bound) || !Of(link, bound))
            return null;
        lock (_lock)
        {
            var active = ActiveSession(bound);
            return active is null || SameSession(active, link) ? null : AdminTexts.AnotherSession(active.RemoteHost);
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

    // The session of the oldest open link of the bound identity that bound or served a call; before any did, the oldest
    // open one (under the lock).
    private SiteLink? ActiveSession(string bound) =>
        _links.Values.FirstOrDefault(l => Of(l, bound) && l.Served && !l.Closing.IsCancellationRequested)
        ?? _links.Values.FirstOrDefault(l => Of(l, bound) && !l.Closing.IsCancellationRequested);

    private static bool Of(SiteLink link, string fingerprint) =>
        string.Equals(link.ServerFingerprintHex, fingerprint, StringComparison.OrdinalIgnoreCase);

    // Outbound links are all one session (the node opened them to the server it chose); inbound ones by their address.
    private static bool SameSession(SiteLink a, SiteLink b) =>
        a.Outbound == b.Outbound && (a.Outbound || SameHost(a.RemoteHost, b.RemoteHost));

    private void Add(SiteLink link)
    {
        lock (_lock)
            _links[link.Id] = link;
        Changed?.Invoke();
    }

    private void Remove(SiteLink link)
    {
        lock (_lock)
            _links.Remove(link.Id);
        Changed?.Invoke();
    }

    private static bool SameHost(string a, string b) =>
        IPAddress.TryParse(a, out var x) && IPAddress.TryParse(b, out var y) ? x.Equals(y) : string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
