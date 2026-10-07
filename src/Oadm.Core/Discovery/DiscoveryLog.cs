using System.Net;

using Microsoft.Extensions.Logging;

namespace Oadm.Core.Discovery;

/// <summary>Source-generated log messages for the discovery feature.</summary>
internal static partial class DiscoveryLog
{
    [LoggerMessage(EventId = 4000, Level = LogLevel.Debug, Message = "mDNS query socket bound to {Address}")]
    public static partial void QuerySocketBound(ILogger logger, IPAddress address);

    [LoggerMessage(EventId = 4001, Level = LogLevel.Warning, Message = "mDNS: cannot open query socket on {Address}")]
    public static partial void QuerySocketFailed(ILogger logger, Exception exception, IPAddress address);

    [LoggerMessage(EventId = 4002, Level = LogLevel.Warning, Message = "mDNS: no multicast-capable IPv4 interface found")]
    public static partial void NoInterfaces(ILogger logger);

    [LoggerMessage(EventId = 4003, Level = LogLevel.Debug, Message = "mDNS: cannot join multicast group on {Address}")]
    public static partial void JoinFailed(ILogger logger, Exception exception, IPAddress address);

    [LoggerMessage(EventId = 4004, Level = LogLevel.Debug, Message = "mDNS: port 5353 not shareable, relying on legacy unicast responses only")]
    public static partial void ListenerUnavailable(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 4005, Level = LogLevel.Debug, Message = "mDNS: socket error on {Address}")]
    public static partial void SocketError(ILogger logger, Exception exception, IPAddress? address);

    [LoggerMessage(EventId = 4010, Level = LogLevel.Debug, Message = "Probe {Address}: {Scheme} answered {StatusCode} without Axis signature")]
    public static partial void ProbeNotAxis(ILogger logger, IPAddress address, string scheme, HttpStatusCode statusCode);

    [LoggerMessage(EventId = 4011, Level = LogLevel.Debug, Message = "Probe {Address}: Axis {Serial} {Model} {Status} via {Scheme}")]
    public static partial void ProbeAxis(ILogger logger, IPAddress address, string serial, string? model, DiscoveredDeviceStatus status, string scheme);

    [LoggerMessage(EventId = 4020, Level = LogLevel.Information, Message = "Discovery session {SessionId} started ({Kind})")]
    public static partial void SessionStarted(ILogger logger, string sessionId, DiscoverySessionKind kind);

    [LoggerMessage(EventId = 4021, Level = LogLevel.Information, Message = "Discovery session {SessionId} finished with {Count} devices")]
    public static partial void SessionFinished(ILogger logger, string sessionId, int count);

    [LoggerMessage(EventId = 4022, Level = LogLevel.Warning, Message = "Discovery session {SessionId} failed")]
    public static partial void SessionFailed(ILogger logger, Exception exception, string sessionId);
}
