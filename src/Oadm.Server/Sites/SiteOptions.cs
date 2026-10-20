namespace Oadm.Server.Sites;

/// <summary>The limits and waiting times of the sites (docs/sites.md, the defaults); tests shorten or replace them.</summary>
public sealed class SiteOptions
{
    /// <summary>An online site goes offline after this long without an answer from its node.</summary>
    public TimeSpan OfflineAfter { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>An offline site comes back after its node was connected and answering this long (no flapping).</summary>
    public TimeSpan OnlineAfter { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// After a server start a site is "starting" until its node is online, the first attempt to reach it failed, or this
    /// time passed (a node that connects out retries at most every 60 s).
    /// </summary>
    public TimeSpan StartWindow { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>How often the server asks the node on the control connection (the round trip is measured then).</summary>
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How long one answer of the node may take on the control connection.</summary>
    public TimeSpan CallTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>A node that waits to be bound is dropped after this time.</summary>
    public TimeSpan WaitingMaxAge { get; init; } = TimeSpan.FromHours(24);

    /// <summary>Nodes waiting to be bound at most.</summary>
    public int MaxWaiting { get; init; } = 50;

    /// <summary>New connections a minute from one address of nodes the server does not know.</summary>
    public int NewConnectionsPerMinute { get; init; } = 10;

    /// <summary>TLS handshakes in progress at most.</summary>
    public int MaxHandshakes { get; init; } = 64;

    /// <summary>Large transfers (streamed uploads and answers) per site at once.</summary>
    public int MaxLargeTransfers { get; init; } = 2;

    /// <summary>Worker connections per site at most (the node opens at most 8).</summary>
    public int MaxWorkers { get; init; } = 8;

    /// <summary>A worker connection takes another one when this many dials are open on every worker.</summary>
    public int DialsPerWorker { get; init; } = 2000;

    /// <summary>How long a dial waits for a worker connection the node was asked to open.</summary>
    public TimeSpan WorkerWait { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>The site port; null = the setting Sites.ListenUrl (default https://0.0.0.0:5081). Tests listen on 127.0.0.1:0.</summary>
    public string? ListenUrl { get; init; }

    /// <summary>The clock of the waiting times; null = the server's (tests pass a fake one).</summary>
    public TimeProvider? Time { get; init; }

    /// <summary>The oldest node protocol the server works with.</summary>
    public int MinProtocolVersion { get; init; } = 1;

    /// <summary>
    /// New connections a minute to the site port from one address (IPv6 per /64), counted at accept before TLS. Bound nodes
    /// count too (a control and up to 8 worker links each, several nodes behind one address), so it is wider than
    /// <see cref="NewConnectionsPerMinute"/>, which counts only links of unknown keys after Hello.
    /// </summary>
    public int ConnectionsPerMinute { get; init; } = 120;

    /// <summary>Accepted links whose Hello has not answered yet, at most.</summary>
    public int MaxPendingLinks { get; init; } = 64;

    /// <summary>How long the first Hello on a new link may take.</summary>
    public TimeSpan HelloTimeout { get; init; } = TimeSpan.FromSeconds(5);
}
