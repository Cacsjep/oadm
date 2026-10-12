using System.Text.Json;
using System.Text.Json.Serialization;

namespace Oadm.Plugins.EdgeRecordingExport;

/// <summary>Ids, names and limits shared by the server and the client part.</summary>
public static class EdgeRecordingInfo
{
    public const string PluginId = "oadm.edge-recording-export";

    public const string DisplayName = "Edge Recording Export";

    public const string IconKey = "hardDriveDownload";

    /// <summary>Permission "Export recordings" (Operator yes, Viewer no); searching needs "show".</summary>
    public const string ExportKey = "export";

    /// <summary>Recordings listed per camera and search at most (a camera recording 10 s clips makes 8,640 a day).</summary>
    public const int MaxRecordingsPerCamera = 50_000;

    /// <summary>Recordings whose size the camera estimates per search; more are extrapolated by their duration.</summary>
    public const int SizeSamples = 40;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>The page methods.</summary>
public static class EdgeRecordingMethods
{
    /// <summary>The recordings of one camera in a range (<see cref="SearchRequest"/> -> <see cref="SearchResult"/>); "show".</summary>
    public const string Search = "search";

    /// <summary>Download: one recording as a file (<see cref="ExportRequest"/>); "export", audited.</summary>
    public const string Export = "export";
}

/// <summary>The recordings of a camera that overlap the range; no start or stop = from the first / to the last.</summary>
public sealed record SearchRequest(Guid DeviceId, DateTimeOffset? StartUtc, DateTimeOffset? StopUtc);

/// <summary>
/// What a search found on one camera: the recordings (oldest first, their times cut to the range), the estimated size of
/// all of them in bytes (null when the camera cannot estimate it), the file format the export uses ("mp4", "matroska"),
/// or the problem in plain words (nothing found then). <paramref name="Truncated"/>: more recordings than
/// <see cref="EdgeRecordingInfo.MaxRecordingsPerCamera"/> (only the first are listed). <paramref name="NoDisk"/>: nothing
/// found and no SD card or network share is in use.
/// </summary>
public sealed record SearchResult(
    Guid DeviceId,
    IReadOnlyList<RecordingEntry> Recordings,
    long? EstimatedBytes,
    string? Format,
    string? Problem,
    bool Truncated = false,
    bool NoDisk = false);

/// <summary>
/// One recording: id and disk as the camera names them, start and stop (stop null while it records), the video source
/// and the type ("continuous", "triggered", "scheduled").
/// </summary>
public sealed record RecordingEntry(string Id, string DiskId, DateTimeOffset Start, DateTimeOffset? Stop, string? Source, string? Type);

/// <summary>One recording to download, cut to the range (no start / stop = the whole recording, up to now while it records).</summary>
public sealed record ExportRequest(Guid DeviceId, string RecordingId, string DiskId, DateTimeOffset? StartUtc, DateTimeOffset? StopUtc, string? Format);
