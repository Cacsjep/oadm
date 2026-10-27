using Oadm.SiteNode.Admin;

namespace Oadm.SiteNode.Config;

/// <summary>
/// The two port lists (docs/spec/sites.md, The node: Node app, Ports): Connect to (devices) and Listen on (relays). A port allowed for one use
/// never allows the other. The server asks for more with RequestPorts; nothing is allowed by itself.
/// </summary>
public sealed class PortPolicy(NodeStore store)
{
    /// <summary>Requests kept at most (a server cannot fill the app).</summary>
    public const int MaxRequests = 200;

    public bool Allows(PortUse use, PortProtocol protocol, int port) =>
        store.Read(c => c.Allowed.Exists(p => p.Use == use && p.Protocol == protocol && p.Port == port));

    /// <summary>Adds the ports the server asks for that are neither allowed nor asked for yet. Returns how many were new.</summary>
    public int Request(IEnumerable<PortEntry> ports)
    {
        ArgumentNullException.ThrowIfNull(ports);
        var added = 0;
        store.TryUpdate(c =>
        {
            foreach (var port in ports)
            {
                if (c.Requested.Count >= MaxRequests)
                    break;
                if (!IsValid(port) || c.Allowed.Exists(port.SameAs) || c.Requested.Exists(port.SameAs))
                    continue;
                c.Requested.Add(port with { Reason = Reason(port.Reason) });
                added++;
            }

            return added > 0;
        });
        return added;
    }

    /// <summary>
    /// Allow (administrator): adds the port to its list and drops the request. <paramref name="fromRequest"/>: Allow of the
    /// server's request (the port counts as the server's and a release removes it); false for a port the administrator adds.
    /// </summary>
    public void Allow(PortEntry port, bool fromRequest = true)
    {
        if (!IsValid(port))
            throw new ArgumentException("The port must be 1 to 65535.", nameof(port));
        store.TryUpdate(c =>
        {
            var request = c.Requested.Find(port.SameAs);
            c.Requested.RemoveAll(port.SameAs);
            if (!c.Allowed.Exists(port.SameAs))
                c.Allowed.Add(port with { Reason = request?.Reason ?? Reason(port.Reason), ByServer = fromRequest && request is not null });
            return true;
        });
    }

    /// <summary>
    /// The server gives the port back (a released relay): its request goes away, and the allowed port too when it was
    /// allowed from a request of the server. Returns true when something changed.
    /// </summary>
    public bool Release(PortEntry port) =>
        store.TryUpdate(c => (c.Requested.RemoveAll(port.SameAs) + c.Allowed.RemoveAll(p => p.ByServer && p.SameAs(port))) > 0);

    /// <summary>Removes the port from its list (and a request for it).</summary>
    public bool Remove(PortEntry port) =>
        store.TryUpdate(c => (c.Allowed.RemoveAll(port.SameAs) + c.Requested.RemoveAll(port.SameAs)) > 0);

    /// <summary>Dismiss: the request goes away; the server may ask again.</summary>
    public bool Dismiss(PortEntry port) => store.TryUpdate(c => c.Requested.RemoveAll(port.SameAs) > 0);

    public static bool IsValid(PortEntry port) => port is not null && port.Port is > 0 and <= 65535 && Enum.IsDefined(port.Use) && Enum.IsDefined(port.Protocol);

    private static string Reason(string? reason)
    {
        var text = new string([.. (reason ?? "").Where(ch => !char.IsControl(ch))]).Trim();
        return text.Length > 120 ? text[..120] : text;
    }
}
