using System.Text.Json;
using System.Text.Json.Serialization;

namespace Oadm.Plugins.IoMonitor;

/// <summary>Ids and timings shared by the server part, the page, the fake mode and the tests.</summary>
public static class IoMonitorPluginInfo
{
    public const string PluginId = "oadm.io-monitor";
    public const string DisplayName = "I/O Monitor";
    public const string IconKey = "toggle";

    /// <summary>The I/O port management API (<c>/axis-cgi/io/portmanagement.cgi</c>); only devices that list it are shown.</summary>
    public const string ApiId = "io-port-management";

    public const string Path = "/axis-cgi/io/portmanagement.cgi";

    /// <summary>The ports are read this often while a page shows a device (200 ms).</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);
}

/// <summary>Page backend methods and the live event topic.</summary>
public static class IoMonitorMethods
{
    /// <summary><see cref="StartRequest"/> -> <see cref="StartReply"/>: starts reading the device's ports for this page.</summary>
    public const string Start = "start";

    /// <summary><see cref="StreamRequest"/>: stops it (another device chosen, page left).</summary>
    public const string Stop = "stop";

    /// <summary>
    /// <see cref="StreamRequest"/> -> <see cref="KeepAliveReply"/>: every 5 s while the page shows the device; without it the
    /// server stops after 20 s. A stream of the user that no longer runs (expired) answers <c>running: false</c>, an unknown
    /// one (server restarted, another user's) fails with <see cref="StreamEnded"/>: the page starts again.
    /// </summary>
    public const string KeepAlive = "keepAlive";

    /// <summary>The answer to <see cref="Stop"/> and <see cref="KeepAlive"/> for a stream id the user does not have.</summary>
    public const string StreamEnded = "The live reading has ended.";

    /// <summary>Event: <see cref="PortsEvent"/> when a port or the read result changed.</summary>
    public const string PortsTopic = "ports";
}

/// <summary>One port as the device reports it. <see cref="Active"/>: the state differs from the normal state.</summary>
public sealed record IoPort(
    string Port,
    string Name,
    bool IsOutput,
    string State,
    string NormalState,
    string Usage,
    bool Configurable)
{
    public bool Active => !string.Equals(State, NormalState, StringComparison.OrdinalIgnoreCase);
}

public sealed record StartRequest(Guid DeviceId);

public sealed record StartReply(string? StreamId, string? Error);

public sealed record StreamRequest(string? StreamId);

/// <summary>False: the server no longer reads for this stream.</summary>
public sealed record KeepAliveReply(bool Running);

/// <summary>The ports of a stream after a change; <see cref="Error"/> while the device cannot be read (the last ports stay).</summary>
public sealed record PortsEvent(string StreamId, IReadOnlyList<IoPort> Ports, string? Error, DateTimeOffset ReadUtc);

/// <summary>JSON of the page contract (camelCase).</summary>
public static class IoMonitorJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Deserialize<T>(string? json) =>
        JsonSerializer.Deserialize<T>(string.IsNullOrWhiteSpace(json) ? "{}" : json, Options)
        ?? throw new ArgumentException("The request is empty.", nameof(json));
}
