using System.Net;

using Oadm.SiteNode.Admin;
using Oadm.SiteNode.State;
using Oadm.Sites.Transport;

namespace Oadm.SiteNode.Links;

/// <summary>
/// The open links and the rule "one server session at a time" (docs/sites.md, "Server identity"): while the bound server
/// is connected from one address, a link with the same identity from another address is refused ("Another OADM server
/// with this identity is connected from 10.0.0.5"), so a restored copy of the server cannot drive the node alongside the
/// real one. Serves every link on Kestrel through the <see cref="AcceptedStreamListener"/>.
/// </summary>
public sealed class LinkRegistry(AcceptedStreamListener listener, BindingState state)
{
    private readonly Lock _lock = new();
    private readonly Dictionary<long, SiteLink> _links = [];

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

    /// <summary>Links of the bound server (control and workers).</summary>
    public int BoundLinks
    {
        get
        {
            lock (_lock)
                return _links.Values.Count(l => l.Refusal is null && state.IsBoundTo(l.ServerFingerprintHex));
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

    private void Add(SiteLink link)
    {
        lock (_lock)
        {
            if (!link.Outbound && state.IsBoundTo(link.ServerFingerprintHex))
            {
                var other = _links.Values.FirstOrDefault(l => l.Refusal is null && !l.Outbound
                    && string.Equals(l.ServerFingerprintHex, link.ServerFingerprintHex, StringComparison.Ordinal)
                    && !SameHost(l.RemoteHost, link.RemoteHost));
                if (other is not null)
                    link.Refusal = AdminTexts.AnotherSession(other.RemoteHost);
            }

            _links[link.Id] = link;
        }

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
