using Oadm.Plugins.DhcpServer.Serving;
using Oadm.Plugins.Network;
using Oadm.Sdk.Network;

namespace Oadm.Plugins.DhcpServer;

/// <summary>Everything the DHCP server takes from its environment; tests replace the network, ports, probe and timings.</summary>
public sealed record DhcpServerOptions
{
    /// <summary>Server port (67). Tests use an in-memory network or other ports on loopback, never the real port.</summary>
    public int ServerPort { get; init; } = 67;

    /// <summary>Client port (68): replies and the check for other servers.</summary>
    public int ClientPort { get; init; } = 68;

    public IServerNetworkInterfaces Interfaces { get; init; } = SystemNetworkInterfaces.Instance;

    /// <summary>Offer loopback interfaces in the select (tests).</summary>
    public bool IncludeLoopback { get; init; }

    public IDhcpSocketFactory Sockets { get; init; } = UdpDhcpSocketFactory.Instance;

    /// <summary>Is an address in use before it is offered (the Network plugin's ping + TCP 80/443 probe).</summary>
    public IAddressProbe Probe { get; init; } = NetworkAddressProbe.Instance;

    public TimeProvider Time { get; init; } = TimeProvider.System;

    public DhcpRateLimits RateLimits { get; init; } = new();

    public DhcpEngineOptions Engine { get; init; } = new();

    /// <summary>How long the check for other DHCP servers collects answers.</summary>
    public TimeSpan OtherServerWait { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>While running, other DHCP servers are checked for this often.</summary>
    public TimeSpan OtherServerInterval { get; init; } = TimeSpan.FromMinutes(10);

    public IWindowsServiceProbe WindowsServices { get; init; } = ScQueryServiceProbe.Instance;

    public HostOs Os { get; init; } = HostOsInfo.Current;

    /// <summary>A failed bind (port in use, interface gone) is retried this often; the interface address is re-checked as often.</summary>
    public TimeSpan RetryBindInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Lease changes and state changes are pushed to the page at most this often.</summary>
    public TimeSpan PublishInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Changed leases are stored at most this often.</summary>
    public TimeSpan PersistInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Offers and leases are expired this often.</summary>
    public TimeSpan SweepInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>An exhausted pool keeps the warning for this long.</summary>
    public TimeSpan ExhaustedWarning { get; init; } = TimeSpan.FromMinutes(5);
}
