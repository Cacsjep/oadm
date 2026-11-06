using Oadm.Contracts.SiteNode;

namespace Oadm.Sites.Binding;

/// <summary>
/// How long the lists of a node's status may be (docs/spec/sites.md, Server side: Limits on a bound site's node): the
/// server keeps the status of every site and looks into it for every connection through the site, so a status beyond
/// these counts is refused whole and the node's session ends. A node that keeps the rules stays far below them: it has a
/// few network cards, some dozen ports and at most 200 open requests (<c>PortPolicy.MaxRequests</c>).
/// </summary>
public static class NodeStatusRules
{
    public const int MaxCards = 256;

    /// <summary>The addresses of all cards together.</summary>
    public const int MaxAddresses = 1024;

    public const int MaxExtraSubnets = 256;
    public const int MaxAllowed = 2048;
    public const int MaxRequested = 1024;
    public const int MaxRelays = 256;

    /// <summary>Null when <paramref name="status"/> is acceptable, else what is wrong (for the server log).</summary>
    public static string? Problem(NodeStatus? status)
    {
        if (status is null)
            return "no status";
        if (status.Cards.Count > MaxCards)
            return "too many network cards";
        var addresses = 0;
        foreach (var card in status.Cards)
        {
            addresses += card.Addresses.Count;
            if (addresses > MaxAddresses)
                return "too many addresses";
        }

        if (status.ExtraSubnets.Count > MaxExtraSubnets)
            return "too many extra networks";
        if (status.Allowed.Count > MaxAllowed)
            return "too many allowed ports";
        if (status.Requested.Count > MaxRequested)
            return "too many requested ports";
        if (status.Relays.Count > MaxRelays)
            return "too many relays";
        return null;
    }
}
