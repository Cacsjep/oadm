using System.Net;
using System.Text.Json.Serialization;

using Oadm.Sdk.Devices;

namespace Oadm.Sdk.Sites;

/// <summary>
/// Whether a plugin package works with devices at a site (a remote network the server reaches through an OADM Site Node).
/// Declared in plugin.json (<c>siteSupport</c>, written by the plugin project SDK from <c>OadmSiteSupport</c>); a manifest
/// without it counts as <see cref="None"/>.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<SiteSupport>))]
public enum SiteSupport
{
    /// <summary>
    /// Not for devices at a site: its tasks are greyed for them ("Not available for devices at a site"), its compliance
    /// types say Not checked, its pages and actions refuse them. For plugins that open their own sockets or need broadcast.
    /// </summary>
    None = 0,

    /// <summary>
    /// Works at sites: the plugin reaches devices only through the host (<see cref="Vapix.IVapixClient"/>, event streams,
    /// <see cref="IDeviceNetwork"/>, relays), which routes everything through the device's site.
    /// </summary>
    Full = 1,
}

/// <summary>A transport protocol of a port at a site.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SiteProtocol>))]
public enum SiteProtocol
{
    /// <summary>TCP.</summary>
    Tcp = 0,
    /// <summary>UDP.</summary>
    Udp = 1,
}

/// <summary>
/// A port a plugin listens on at sites (plugin.json <c>sitePorts</c>, from <c>OadmSitePorts</c>), listed on the Plugins
/// page and requested from the node app when the plugin opens its relay.
/// </summary>
/// <param name="Protocol">UDP or TCP.</param>
/// <param name="Port">1..65535.</param>
/// <param name="Reason">Why, in a few plain words ("NTP server"); shown in the node app.</param>
public sealed record SitePort(SiteProtocol Protocol, int Port, string Reason);

/// <summary>A bound site as plugins see it.</summary>
public interface ISiteInfo
{
    /// <summary>OADM's id of the site; <see cref="IDeviceInfo.SiteId"/> of its devices.</summary>
    Guid Id { get; }

    /// <summary>The site's name ("Building B").</summary>
    string Name { get; }

    /// <summary>The site node is connected (after the waiting time that keeps a short dropout from counting).</summary>
    bool IsOnline { get; }
}

/// <summary>
/// The bound sites and relays into them (<see cref="Plugins.ICorePluginContext.Sites"/>). A plugin that offers a server to
/// devices (they connect to it: NTP, syslog, an HTTP endpoint) opens one relay per site; the node listens on the port and
/// passes every datagram or connection to the plugin. The host closes a plugin's relays when it turns off or stops and
/// opens them again after the node reconnects.
/// </summary>
public interface ISites
{
    /// <summary>Every bound site now.</summary>
    IReadOnlyList<ISiteInfo> List { get; }

    /// <summary>A site was bound, renamed, removed, or went online or offline.</summary>
    event EventHandler? SitesChanged;

    /// <summary>
    /// Listens on UDP <paramref name="port"/> at the site. Asks the node app for the port (Listen on, with
    /// <paramref name="reason"/>) when it is not allowed yet; the relay's <see cref="IRelay.State"/> says so meanwhile.
    /// </summary>
    Task<IUdpRelay> OpenUdpRelayAsync(Guid siteId, int port, string reason, CancellationToken ct);

    /// <summary>Listens on TCP <paramref name="port"/> at the site, like <see cref="OpenUdpRelayAsync"/>.</summary>
    Task<ITcpRelay> OpenTcpRelayAsync(Guid siteId, int port, string reason, CancellationToken ct);
}

/// <summary>The state of a relay or a local listener.</summary>
public enum RelayState
{
    /// <summary>Listening.</summary>
    Running = 0,
    /// <summary>The port waits for Allow in the OADM Site Node app.</summary>
    WaitingForApproval = 1,
    /// <summary>Another program uses the port.</summary>
    PortInUse = 2,
    /// <summary>The node or server may not bind the port (a port below 1024 without the right).</summary>
    NoPermission = 3,
    /// <summary>The site node is offline; the relay opens again when it is back.</summary>
    SiteOffline = 4,
    /// <summary>The site node is too old for this kind of relay. Update it.</summary>
    NotSupported = 5,
}

/// <summary>A listener at a site (or on the server's own network, <see cref="IPluginNetwork"/>).</summary>
public interface IRelay : IAsyncDisposable
{
    /// <summary>The site; null for a listener on the server's own network.</summary>
    Guid? SiteId { get; }

    /// <summary>UDP or TCP.</summary>
    SiteProtocol Protocol { get; }

    /// <summary>The port.</summary>
    int Port { get; }

    /// <summary>Now.</summary>
    RelayState State { get; }

    /// <summary>The state in plain words for a page ("NTP at Building B: waiting for approval in the OADM Site Node app").</summary>
    string StateText { get; }

    /// <summary>The state changed.</summary>
    event EventHandler? StateChanged;

    /// <summary>
    /// The addresses a listener on the server's own network has bound now: the chosen addresses that were ready (an address
    /// of the interface that is not ready yet, such as an IPv6 address still checked, is left out while the others serve),
    /// or the wildcard addresses of a listener on every address; empty while it does not listen. Null when the host does not
    /// tell (a relay at a site, a host before SDK 0.4.0, OADM 4.5): then assume the requested addresses. A plugin that wants
    /// every chosen address listens again on its own later when one is missing.
    /// </summary>
    IReadOnlyList<IPAddress>? BoundAddresses => null;

    /// <summary>
    /// Closes the relay for good because the plugin no longer serves there (a setting changed): the site node gives the
    /// port back (it drops the port it allowed from the plugin's request; a port its administrator added stays). Dispose
    /// instead when the plugin only stops (the server shuts down, the plugin is turned off): the port stays allowed for the
    /// next start. Default: dispose.
    /// </summary>
    ValueTask ReleaseAsync() => DisposeAsync();
}

/// <summary>One datagram and who sent it.</summary>
/// <param name="Data">The payload.</param>
/// <param name="Remote">The sender as the node (or the server) saw it.</param>
public readonly record struct Datagram(ReadOnlyMemory<byte> Data, IPEndPoint Remote);

/// <summary>Datagrams in and replies out.</summary>
public interface IDatagramEndpoint
{
    /// <summary>The next datagram. Ends with <see cref="OperationCanceledException"/> when the token is cancelled.</summary>
    ValueTask<Datagram> ReceiveAsync(CancellationToken ct);

    /// <summary>A datagram to <paramref name="remote"/> (usually a reply to a sender).</summary>
    ValueTask SendAsync(ReadOnlyMemory<byte> data, IPEndPoint remote, CancellationToken ct);
}

/// <summary>A UDP listener at a site or on the server's network.</summary>
public interface IUdpRelay : IRelay, IDatagramEndpoint;

/// <summary>An accepted TCP connection.</summary>
public interface IIncomingConnection : IAsyncDisposable
{
    /// <summary>
    /// Both directions. A read returns 0 when the device ended its side; disposing the stream (or the connection) closes
    /// the connection.
    /// </summary>
    Stream Stream { get; }

    /// <summary>The device that connected, as the node (or the server) saw it.</summary>
    IPEndPoint Remote { get; }

    /// <summary>The site it came through; null on the server's own network.</summary>
    Guid? SiteId { get; }
}

/// <summary>Accepts TCP connections.</summary>
public interface IStreamListener
{
    /// <summary>The next connection. Ends with <see cref="OperationCanceledException"/> when the token is cancelled.</summary>
    ValueTask<IIncomingConnection> AcceptAsync(CancellationToken ct);
}

/// <summary>A TCP listener at a site or on the server's network.</summary>
public interface ITcpRelay : IRelay, IStreamListener;

/// <summary>
/// Network checks towards one device, routed like the device: through its site, or on the server's own network
/// (<see cref="Plugins.ITaskExecutionContext.Network"/>, <see cref="Plugins.ITaskQueryContext.Network"/>). Never open
/// sockets to devices yourself. Through a site that cannot carry a call (offline, a site node too old) a call throws an
/// exception whose message says why ("Site offline: Building B").
/// </summary>
public interface IDeviceNetwork
{
    /// <summary>The site of this route; null = the server's own network.</summary>
    Guid? SiteId { get; }

    /// <summary>True when <paramref name="address"/> answers a ping within <paramref name="timeout"/>.</summary>
    Task<bool> PingAsync(string address, TimeSpan timeout, CancellationToken ct);

    /// <summary>
    /// True when a host answers on TCP <paramref name="port"/> at <paramref name="address"/> within <paramref name="timeout"/>:
    /// it accepts the connection or actively refuses it (a refusal also proves the address is taken). The address-in-use
    /// check relies on it; the connection is closed at once.
    /// </summary>
    Task<bool> ProbeTcpAsync(string address, int port, TimeSpan timeout, CancellationToken ct);

    /// <summary>The addresses of a host name as this route resolves it; empty when unknown.</summary>
    Task<IReadOnlyList<IPAddress>> ResolveAsync(string name, CancellationToken ct);

    /// <summary>
    /// The address OADM has towards <paramref name="address"/> on this route: the server's own, or the site node's in the
    /// site network (what a device uses to reach OADM's services, e.g. "Use OADM as NTP server"). Null when none.
    /// </summary>
    Task<IPAddress?> LocalAddressTowardsAsync(string address, CancellationToken ct);

    /// <summary>
    /// True when <paramref name="address"/> is on a network this route reaches: always on the server's own network; at a
    /// site only the networks the site node reaches (its chosen network cards and extra networks). A device at a site
    /// cannot move to an address outside them.
    /// </summary>
    bool Reaches(IPAddress address) => true;
}

/// <summary>
/// The network of a core plugin (<see cref="Plugins.ICorePluginContext.Network"/>): the route of any device or site, and
/// listeners on the server's own network through the same interfaces as relays, so a plugin writes its server once.
/// </summary>
public interface IPluginNetwork
{
    /// <summary>The route of <paramref name="device"/>.</summary>
    IDeviceNetwork ForDevice(IDeviceInfo device);

    /// <summary>The route of a site; null = the server's own network.</summary>
    IDeviceNetwork ForSite(Guid? siteId);

    /// <summary>Listens on UDP <paramref name="port"/> on the server's own network (the host binds it and opens the firewall).</summary>
    Task<IUdpRelay> ListenUdpAsync(int port, string reason, CancellationToken ct);

    /// <summary>Listens on TCP <paramref name="port"/> on the server's own network.</summary>
    Task<ITcpRelay> ListenTcpAsync(int port, string reason, CancellationToken ct);

    /// <summary>
    /// Listens on TCP <paramref name="port"/> on <paramref name="addresses"/> of the server only (the addresses of the
    /// interface a page's "Listen on" chose); an empty list listens on every address. SDK 0.3.0 (OADM 4.1): a plugin that
    /// uses it sets <c>minSdkVersion</c> 0.3.0. Implementations that do not override it listen on every address.
    /// </summary>
    Task<ITcpRelay> ListenTcpAsync(int port, string reason, IReadOnlyList<IPAddress> addresses, CancellationToken ct) =>
        ListenTcpAsync(port, reason, ct);

    /// <summary>
    /// Listens on UDP <paramref name="port"/> on <paramref name="addresses"/> of the server only (the addresses of the
    /// interface a page's "Listen on" chose); an empty list listens on every address. SDK 0.4.0 (OADM 4.5): implementations
    /// that do not override it listen on every address.
    /// </summary>
    Task<IUdpRelay> ListenUdpAsync(int port, string reason, IReadOnlyList<IPAddress> addresses, CancellationToken ct) =>
        ListenUdpAsync(port, reason, ct);
}

/// <summary>Texts about sites shared by host and plugins.</summary>
public static class SiteTexts
{
    /// <summary>Why a task, compliance type, page or action of a <see cref="SiteSupport.None"/> package refuses a device at a site.</summary>
    public const string NotAvailableAtSite = "Not available for devices at a site";

    /// <summary>The site's node was reset (its site is not bound any more) and what to do about it.</summary>
    public const string NodeWasReset = "The site node was reset. Replace or remove the site.";

    /// <summary>The status text of a device whose site is offline: "Site offline: Building B".</summary>
    public static string SiteOffline(string? siteName) =>
        string.IsNullOrWhiteSpace(siteName) ? "Site offline" : "Site offline: " + siteName;

    /// <summary>
    /// The site name of a device whose site was removed with its devices kept: "Building B (removed)". The device shows Site
    /// offline until it is moved to another site or removed.
    /// </summary>
    /// <param name="siteName">The removed site's name.</param>
    public static string RemovedSite(string siteName) => siteName + " (removed)";

    /// <summary>Why a new address of a device at a site is refused: it is outside the networks the site node reaches.</summary>
    public const string OutsideSiteNetworks = "The address is outside the networks the site reaches";

    /// <summary>The name of the server's own network in relay texts ("NTP at this server: running").</summary>
    public const string ThisServer = "this server";

    /// <summary>
    /// A relay's or listener's state for a page: "NTP at Building B: waiting for approval in the OADM Site Node app".
    /// </summary>
    /// <param name="reason">What listens, as the plugin named it ("NTP").</param>
    /// <param name="where">The site's name, or <see cref="ThisServer"/> for a listener on the server's own network.</param>
    /// <param name="state">The state.</param>
    /// <param name="protocol">UDP or TCP.</param>
    /// <param name="port">The port.</param>
    public static string RelayStateText(string reason, string where, RelayState state, SiteProtocol protocol, int port)
    {
        var name = (protocol == SiteProtocol.Udp ? "UDP " : "TCP ") + port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var what = state switch
        {
            RelayState.Running => "running",
            RelayState.WaitingForApproval => "waiting for approval in the OADM Site Node app",
            RelayState.PortInUse => Network.PortBindErrors.InUseText(port).TrimEnd('.'), // the shared text inside a sentence
            RelayState.NoPermission => "not allowed to use " + name,
            RelayState.SiteOffline => "site offline",
            RelayState.NotSupported => "update the site node",
            _ => state.ToString(),
        };
        return $"{reason} at {where}: {what}";
    }
}
