namespace Oadm.Core.Sites;

/// <summary>How the server and a site node find each other (docs/sites.md, Decisions: Direction).</summary>
public enum SiteMode
{
    /// <summary>The node connects to the server's site port (default).</summary>
    Outbound = 0,
    /// <summary>The server connects to the node's port.</summary>
    Inbound = 1,
}

/// <summary>
/// A bound site (row of Sites): a remote network the server reaches through one OADM Site Node. Online or offline lives in
/// memory only; the devices keep their last real status (Site offline is computed).
/// </summary>
public sealed class Site
{
    /// <summary>Most sites one server keeps.</summary>
    public const int MaxSites = 100;

    /// <summary>Longest site name.</summary>
    public const int MaxNameLength = 64;

    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>1..64 characters, trimmed, unique case-insensitive.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Upper invariant of <see cref="Name"/>: the unique index.</summary>
    public string NormalizedName { get; set; } = string.Empty;

    public SiteMode Mode { get; set; }

    /// <summary>Inbound: where the server connects. Outbound: where the node last connected from.</summary>
    public string? Address { get; set; }

    public int? Port { get; set; }

    /// <summary>Hex SHA-256 of the node's certificate, pinned at bind.</summary>
    public string NodeKeyFingerprint { get; set; } = string.Empty;

    /// <summary>The node's random id (new at every Reset in the node app).</summary>
    public string NodeId { get; set; } = string.Empty;

    public DateTime BoundUtc { get; set; }

    /// <summary>Written at most once a minute.</summary>
    public DateTime? LastSeenUtc { get; set; }

    /// <summary>The node program's version from its last Hello.</summary>
    public string? Version { get; set; }

    /// <summary>The node forgot the server (Reset): its devices are Site offline until the site is replaced or removed.</summary>
    public bool NotBound { get; set; }

    public override string ToString() => $"Site {Name} ({Id})";
}
