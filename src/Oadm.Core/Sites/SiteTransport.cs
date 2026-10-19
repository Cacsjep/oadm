using System.Net.Sockets;

namespace Oadm.Core.Sites;

/// <summary>
/// The way to a device: through a site, or on the server's own network (<see cref="Local"/>). Everything that opens a
/// connection to a device takes one (docs/sites.md, Server: Routing).
/// </summary>
/// <param name="SiteId">The site; null = the server's own network.</param>
public readonly record struct DeviceRoute(Guid? SiteId)
{
    /// <summary>The server's own network.</summary>
    public static DeviceRoute Local => default;

    /// <summary>Through a site.</summary>
    public static DeviceRoute ViaSite(Guid siteId) => new(siteId);

    /// <summary>The server's own network.</summary>
    public bool IsLocal => SiteId is null;

    public override string ToString() => SiteId is { } id ? "site " + id : "local";
}

/// <summary>How long a connection through a site stays open (they are counted apart on the node).</summary>
public enum SiteDialKind
{
    /// <summary>Polls, tasks, page reads, probes.</summary>
#pragma warning disable CA1720 // "Short" as in the protocol (DIAL_KIND_SHORT), not the type
    Short = 0,
#pragma warning restore CA1720
    /// <summary>WebSockets, RTSP live view, metadata and event streams.</summary>
    Permanent = 1,
}

/// <summary>
/// Opens connections through a site node: the only way the server reaches a device at a site. Callers never know whether
/// the node connected out or the server connected in.
/// </summary>
public interface ISiteTransport
{
    /// <summary>
    /// One TCP connection to <paramref name="host"/>:<paramref name="port"/> through the site (the node resolves a name).
    /// The device's own socket errors come back as <see cref="SocketException"/> with the device's error (connection
    /// refused, timed out, host unreachable, reset, host not found), exactly like a local connect, so the device status rules
    /// stay the same. Problems of the site (offline, not bound, busy, outside its networks, port not allowed, node too old)
    /// are a <see cref="SiteException"/> and never a device error. <paramref name="timeout"/> bounds the connect on the node.
    /// </summary>
    ValueTask<Stream> OpenStreamAsync(Guid siteId, string host, int port, SiteDialKind kind, TimeSpan timeout, CancellationToken ct);
}

/// <summary>The sites as the rest of the server needs them: names, online or not, the round trip. In memory, O(1).</summary>
public interface ISiteStates
{
    /// <summary>The site's name; null for an unknown id.</summary>
    string? NameOf(Guid siteId);

    /// <summary>True while the site's node is connected (after the hysteresis: 30 s to go offline, 60 s stable to come back).</summary>
    bool IsOnline(Guid siteId);

    /// <summary>The measured round trip to the node; zero when not measured. Timeouts through a site add 4 times it.</summary>
    TimeSpan RoundTrip(Guid siteId);

    /// <summary>
    /// True after a server start until the site's first connection settles (its node is online, the first attempt failed,
    /// or the start window passed). The site counts as not online, but nothing changed: its events and the status events
    /// of its devices keep their state (no edges after every server start).
    /// </summary>
    bool IsStarting(Guid siteId) => false;

    /// <summary>
    /// Asks the site's node now whether it answers (a short request on its control link, shared by everyone asking at the
    /// same time). False when the node is not connected or does not answer: a device that did not answer through the
    /// site is then a site problem, never a device error (docs/sites.md). Never throws but for <paramref name="ct"/>.
    /// </summary>
    ValueTask<bool> LinkAnswersAsync(Guid siteId, CancellationToken ct) => ValueTask.FromResult(IsOnline(siteId));

    /// <summary>
    /// True for a site removed with Keep devices (docs/sites.md, Binding 6): its devices stay Site offline until they are
    /// replaced or removed, but their status events go off (the site is never coming back).
    /// </summary>
    bool IsRemoved(Guid siteId) => false;

    /// <summary>The name of a removed site whose devices were kept; null for any other id.</summary>
    string? RemovedNameOf(Guid siteId) => null;

    /// <summary>A site was added, renamed, removed, or went online or offline. Raised with the ids that changed.</summary>
    event EventHandler<SiteStatesChangedEventArgs>? Changed;
}

/// <summary>The sites a <see cref="ISiteStates.Changed"/> concerns.</summary>
public sealed class SiteStatesChangedEventArgs(IReadOnlyCollection<Guid> siteIds) : EventArgs
{
    public IReadOnlyCollection<Guid> SiteIds { get; } = siteIds;
}

/// <summary>
/// The large transfers of a site (docs/sites.md, Transport: Large transfers): at most a few streamed request bodies or
/// answers at once per site, so a slow site link keeps room for polls, live view and the heartbeat. Implemented by the
/// site transport; the route handler asks it for requests that stream.
/// </summary>
public interface ISiteLargeTransfers
{
    /// <summary>Waits for a free slot of the site; dispose the result when the transfer ended.</summary>
    ValueTask<IDisposable> EnterLargeTransferAsync(Guid siteId, CancellationToken ct);
}

/// <summary>Why a site could not carry a connection. Never a device error.</summary>
public enum SiteProblem
{
    /// <summary>The node is not connected.</summary>
    Offline = 0,
    /// <summary>The node was reset and forgot the server.</summary>
    NotBound = 1,
    /// <summary>Over the node's connection limits; retried, never a device error.</summary>
    Busy = 2,
    /// <summary>The address is not on a network the node may reach.</summary>
    OutsideNetworks = 3,
    /// <summary>The port is not allowed in the node app.</summary>
    PortNotAllowed = 4,
    /// <summary>The node lacks a feature (or is below the minimum version).</summary>
    NotSupported = 5,
}

/// <summary>A site could not carry a connection (<see cref="SiteProblem"/>). The message is the plain text for the user.</summary>
public sealed class SiteException : Exception
{
    public SiteException()
    {
    }

    public SiteException(string message)
        : base(message)
    {
    }

    public SiteException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public SiteException(Guid siteId, SiteProblem problem, string message)
        : base(message)
    {
        SiteId = siteId;
        Problem = problem;
    }

    public Guid SiteId { get; }

    public SiteProblem Problem { get; }

    /// <summary>The standard texts (docs/sites.md); <paramref name="siteName"/> as the user named the site.</summary>
    public static SiteException For(Guid siteId, string siteName, SiteProblem problem, int port = 0) =>
        new(siteId, problem, problem switch
        {
            SiteProblem.Offline => "Site offline: " + siteName,
            SiteProblem.NotBound => $"Site {siteName} is not bound. Reset its site node and bind it again.",
            SiteProblem.Busy => $"Site {siteName} is busy.",
            SiteProblem.OutsideNetworks => $"The address is outside the networks of {siteName}.",
            SiteProblem.PortNotAllowed => $"Port {port} is not allowed at {siteName}. Allow it in the OADM Site Node app.",
            SiteProblem.NotSupported => $"Update the site node of {siteName}.",
            _ => "Site problem: " + siteName,
        });
}
