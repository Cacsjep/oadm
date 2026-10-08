using System.Text.Json;
using System.Text.Json.Serialization;

using Oadm.Sdk.Network;

namespace Oadm.Plugins.NtpServer;

/// <summary>Ids and names shared by the server part, the page and the tests.</summary>
public static class NtpServerPluginInfo
{
    public const string PluginId = "oadm.ntp-server";
    public const string DisplayName = "NTP server";
    public const string IconKey = "clock";

    /// <summary>Contributed task "Use OADM as NTP server".</summary>
    public const string UseTaskId = "oadm.ntp-server.use";

    /// <summary>Requests kept in the log and shown on the page.</summary>
    public const int MaxRequests = 40;

    /// <summary>Interface option that binds 0.0.0.0 and [::].</summary>
    public const string AllInterfaces = "all";

    public const string AllInterfacesLabel = "All interfaces";
}

/// <summary>Page backend methods (<c>InvokeAsync</c>) and live event topics (<c>ICorePluginContext.Events</c>).</summary>
public static class NtpServerMethods
{
    /// <summary>() -> <see cref="NtpState"/>; also refreshes the interface list.</summary>
    public const string GetState = "getState";

    /// <summary><see cref="SaveRequest"/> -> <see cref="SaveReply"/>: validates the upstream (one query), stores, applies.</summary>
    public const string Save = "save";

    /// <summary>Event: <see cref="NtpState"/> without interfaces and requests (status or upstream changed).</summary>
    public const string StateTopic = "state";

    /// <summary>Event: <see cref="RequestsEvent"/>, new log entries (batched, at most every 500 ms).</summary>
    public const string RequestsTopic = "requests";
}

/// <summary>Persisted settings (plugin setting <c>config</c>), restored on server start.</summary>
public sealed record NtpConfig
{
    public bool Enabled { get; init; }

    /// <summary><see cref="NtpServerPluginInfo.AllInterfaces"/> or the OS interface id.</summary>
    public string InterfaceId { get; init; } = NtpServerPluginInfo.AllInterfaces;

    /// <summary>Interface name when it was saved ("Ethernet"), for "Interface Ethernet is not available".</summary>
    public string? InterfaceName { get; init; }

    /// <summary>Optional upstream host name or address (host[:port]); null or empty = server clock only.</summary>
    public string? Upstream { get; init; }
}

/// <summary>One answered or limited request.</summary>
/// <param name="Seq">Increasing number (newest = largest).</param>
/// <param name="TimeUtc">When it was received.</param>
/// <param name="Client">Client IP address.</param>
/// <param name="OffsetMilliseconds">Client clock minus server clock (from the client's transmit timestamp), null when unknown.</param>
/// <param name="Result">"Answered" or "Rate limited".</param>
public sealed record RequestEntry(long Seq, DateTime TimeUtc, string Client, double? OffsetMilliseconds, string Result)
{
    public const string Answered = "Answered";
    public const string RateLimited = "Rate limited";
}

/// <summary>State of the upstream server.</summary>
/// <param name="Host">As configured.</param>
/// <param name="Reachable">True after a good answer, false after repeated failures, null while the first query runs.</param>
/// <param name="Stratum">Upstream stratum of the last good answer.</param>
/// <param name="OffsetMilliseconds">Upstream clock minus server clock of the last good answer.</param>
/// <param name="LastSyncUtc">Time of the last good answer.</param>
/// <param name="Message">Last error ("No answer within 2 s").</param>
public sealed record UpstreamInfo(string Host, bool? Reachable, int? Stratum, double? OffsetMilliseconds, DateTime? LastSyncUtc, string? Message);

/// <summary>Everything the page shows.</summary>
public sealed record NtpState
{
    public NtpConfig Config { get; init; } = new();

    public ServiceStatus Status { get; init; } = new(ServiceStatus.Neutral, "Stopped");

    public IReadOnlyList<InterfaceOption> Interfaces { get; init; } = [];

    /// <summary>Newest first.</summary>
    public IReadOnlyList<RequestEntry> Requests { get; init; } = [];

    public UpstreamInfo? Upstream { get; init; }

    /// <summary>Stratum the server answers with right now (10 = local clock).</summary>
    public int Stratum { get; init; }

    public int Port { get; init; }
}

public sealed record SaveRequest(bool Enabled, string? InterfaceId, string? Upstream);

/// <param name="Saved">False when the upstream did not validate (nothing changed).</param>
/// <param name="UpstreamError">Shown under the upstream field.</param>
/// <param name="UpstreamResult">Shown under the field on success ("3 ms off, 12 ms round trip").</param>
/// <param name="State">The state after saving.</param>
public sealed record SaveReply(bool Saved, string? UpstreamError, string? UpstreamResult, NtpState State);

public sealed record RequestsEvent(IReadOnlyList<RequestEntry> Entries);

public static class NtpJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    /// <summary>Throws <see cref="ArgumentException"/> (INVALID_ARGUMENT) for missing or unreadable JSON.</summary>
    public static T Deserialize<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new ArgumentException("The request is empty.", nameof(json));
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, Options) ?? throw new ArgumentException("The request is empty.", nameof(json));
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("The request cannot be read: " + ex.Message, nameof(json), ex);
        }
    }
}
