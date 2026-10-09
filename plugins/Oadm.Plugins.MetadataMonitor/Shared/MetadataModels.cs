using System.Text.Json;
using System.Text.Json.Serialization;

namespace Oadm.Plugins.MetadataMonitor;

/// <summary>Ids, names and limits shared by the server part, the page, the fake mode and the tests.</summary>
public static class MetadataMonitorPluginInfo
{
    public const string PluginId = "oadm.metadata-monitor";
    public const string DisplayName = "Metadata Monitor";
    public const string IconKey = "activity";

    /// <summary>Messages the page keeps (the oldest are removed in one step).</summary>
    public const int MaxClientMessages = 10_000;

    /// <summary>Messages per pushed batch; above that the oldest of the batch are dropped and counted as lost.</summary>
    public const int MaxBatchMessages = 500;

    /// <summary>Raw XML kept per message (a whole document may be 1 MB; one event payload must stay below 1 M characters).</summary>
    public const int MaxXmlCharacters = 256 * 1024;

    /// <summary>Timestamp format of the list (UTC).</summary>
    public const string TimeFormat = "yyyy-MM-dd HH:mm:ss.fff";
}

/// <summary>Page backend methods (<c>InvokeAsync</c>) and live event topics (<c>ICorePluginContext.Events</c>).</summary>
public static class MetadataMethods
{
    /// <summary><see cref="StartRequest"/> -> <see cref="StartReply"/>: opens the event stream of one device.</summary>
    public const string Start = "start";

    /// <summary><see cref="RtspPortRequest"/> -> <see cref="RtspPortReply"/>: the RTSP port stored for the device.</summary>
    public const string GetRtspPort = "getRtspPort";

    /// <summary><see cref="StreamRequest"/> -> null: ends the stream (RTSP TEARDOWN).</summary>
    public const string Stop = "stop";

    /// <summary>
    /// <see cref="StreamRequest"/> -> null: the page is still watching. A stream without keep-alive for
    /// <see cref="MetadataMonitorOptions.LeaseTimeout"/> ends (client closed or gone).
    /// </summary>
    public const string KeepAlive = "keepAlive";

    /// <summary>Event: <see cref="MessagesEvent"/>, new messages (batched every 250 ms, at most 500 per batch).</summary>
    public const string MessagesTopic = "messages";

    /// <summary>Event: <see cref="MonitorState"/>, the stream state changed or new messages were counted.</summary>
    public const string StateTopic = "state";
}

/// <summary>States of a stream (<see cref="MonitorState.State"/>).</summary>
public static class MonitorStates
{
    public const string Connecting = "Connecting";
    public const string Live = "Live";
    public const string Reconnecting = "Reconnecting";
    public const string Stopped = "Stopped";
    public const string Error = "Error";
}

/// <summary>Categories of a message.</summary>
public static class MessageCategories
{
    public const string Event = "Event";
    public const string Invalid = "Invalid";
}

/// <summary>
/// Opens the event stream of a device. <paramref name="RtspPort"/>: the device's RTSP port when it is forwarded (camera
/// behind NAT), null = 554; stored on the server for the device (the Image Health Dashboard uses it too).
/// </summary>
public sealed record StartRequest(Guid DeviceId, int? RtspPort = null);

public sealed record RtspPortRequest(Guid DeviceId);

/// <summary>The stored RTSP port, null = 554.</summary>
public sealed record RtspPortReply(int? Port);

/// <summary>The new stream id, or the reason the device refused (<c>error</c>).</summary>
public sealed record StartReply(string? StreamId, string? Error);

public sealed record StreamRequest(string StreamId);

/// <summary>A Source, Key or Data <c>tt:SimpleItem</c>.</summary>
public sealed record MetadataItem(string Name, string Value);

/// <summary>
/// One event notification of the stream.
/// </summary>
/// <param name="Seq">Per stream, from 1.</param>
/// <param name="UtcTime">UtcTime of the tt:Message (null when missing or invalid).</param>
/// <param name="Category"><see cref="MessageCategories"/>.</param>
/// <param name="Topic">Topic tree text without the ONVIF / Axis prefixes ("Device/IO/VirtualInput").</param>
/// <param name="CaptureUtc">When the server received the document.</param>
/// <param name="Operation">PropertyOperation: Initialized, Changed, Deleted, or null.</param>
/// <param name="Info">"[INIT] port = 33; active = 0;"</param>
/// <param name="Xml">The notification's XML as the device sent it (the whole text for an invalid document).</param>
public sealed record MetadataMessage(
    long Seq,
    DateTimeOffset? UtcTime,
    string Category,
    string Topic,
    DateTimeOffset CaptureUtc,
    string? Operation,
    string Info,
    string Xml);

/// <summary>A batch of new messages of one stream; <paramref name="Dropped"/> = messages of this batch left out (too many).</summary>
public sealed record MessagesEvent(string StreamId, IReadOnlyList<MetadataMessage> Messages, int Dropped);

/// <summary>State of one stream.</summary>
/// <param name="StreamId">The stream.</param>
/// <param name="DeviceId">Its device.</param>
/// <param name="State"><see cref="MonitorStates"/>.</param>
/// <param name="Text">Error or reconnect reason, null otherwise.</param>
/// <param name="Messages">Messages so far.</param>
/// <param name="Lost">Messages lost so far (packet loss, documents too large, dropped from full batches).</param>
public sealed record MonitorState(string StreamId, Guid DeviceId, string State, string? Text, long Messages, long Lost);

/// <summary>JSON of the page contract (camelCase, like the other core plugins).</summary>
public static class MetadataJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Deserialize<T>(string? json) =>
        JsonSerializer.Deserialize<T>(string.IsNullOrWhiteSpace(json) ? "{}" : json, Options)
        ?? throw new ArgumentException("Missing payload.", nameof(json));
}
