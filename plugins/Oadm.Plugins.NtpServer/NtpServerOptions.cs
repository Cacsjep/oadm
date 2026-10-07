using Oadm.Plugins.NtpServer.Serving;
using Oadm.Plugins.NtpServer.Status;
using Oadm.Plugins.NtpServer.Upstream;
using Oadm.Sdk.Network;

namespace Oadm.Plugins.NtpServer;

/// <summary>Everything the NTP server takes from its environment; tests replace the port, interfaces, DNS and timings.</summary>
public sealed record NtpServerOptions
{
    /// <summary>The NTP port (123). Tests use 0 (a random free port) so they never bind the real port.</summary>
    public int Port { get; init; } = 123;

    public IServerNetworkInterfaces Interfaces { get; init; } = SystemNetworkInterfaces.Instance;

    /// <summary>Offer loopback interfaces in the select (tests).</summary>
    public bool IncludeLoopback { get; init; }

    public IUpstreamResolver Resolver { get; init; } = DnsUpstreamResolver.Instance;

    public TimeProvider Time { get; init; } = TimeProvider.System;

    public RateLimitOptions RateLimit { get; init; } = new();

    public UpstreamOptions Upstream { get; init; } = new();

    public IWindowsTimeProbe WindowsTime { get; init; } = new ScQueryWindowsTimeProbe();

    public HostOs Os { get; init; } = NtpStatusTexts.CurrentOs;

    /// <summary>A failed bind (port in use, interface gone) is retried this often while the server is enabled.</summary>
    public TimeSpan RetryBindInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>New log entries and state changes are pushed to the page at most this often.</summary>
    public TimeSpan PublishInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>A global-limit drop keeps the "Too many requests" warning for this long.</summary>
    public TimeSpan TooManyRequestsWarning { get; init; } = TimeSpan.FromSeconds(10);
}
