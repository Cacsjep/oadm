using System.Text.Json;
using System.Text.Json.Serialization;

namespace Oadm.Plugins.SnapshotReport;

/// <summary>Ids, method names and defaults shared by the server and the client part.</summary>
public static class SnapshotReportPluginInfo
{
    public const string PluginId = "oadm.snapshot-report";
    public const string DisplayName = "Snapshot report";
    public const string IconKey = "snapshot";

    /// <summary>Picture size of the page grid (largest source resolution that fits).</summary>
    public const int GridMaxWidth = 1280;
    public const int GridMaxHeight = 720;

    /// <summary>Picture size of the PDF report.</summary>
    public const int ReportMaxWidth = 1920;
    public const int ReportMaxHeight = 1080;

    /// <summary>Snapshots taken at the same time by the server (all callers together).</summary>
    public const int Parallelism = 4;

    /// <summary>Time one snapshot (or one source list) may take.</summary>
    public static readonly TimeSpan SnapshotTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Bytes of the PDF returned per <see cref="SnapshotReportMethods.ReadReport"/> call.</summary>
    public const int ReportChunkBytes = 2 * 1024 * 1024;
}

/// <summary>InvokeAsync methods of the core plugin (PluginService.Invoke).</summary>
public static class SnapshotReportMethods
{
    /// <summary><see cref="ListSourcesRequest"/> -> <see cref="ListSourcesResult"/>: one tile per video source.</summary>
    public const string ListSources = "listSources";

    /// <summary><see cref="SnapshotRequest"/> -> <see cref="SnapshotResult"/>: one JPEG (base64) with its capture time.</summary>
    public const string Snapshot = "snapshot";

    /// <summary><see cref="ReportRequest"/> -> <see cref="ReportJobStatus"/>: starts the PDF report in the background.</summary>
    public const string GenerateReport = "generateReport";

    /// <summary><see cref="ReportJobRequest"/> -> <see cref="ReportJobStatus"/>: progress of a report.</summary>
    public const string ReportStatus = "reportStatus";

    /// <summary><see cref="ReadReportRequest"/> -> <see cref="ReportChunk"/>: the finished PDF in chunks.</summary>
    public const string ReadReport = "readReport";

    /// <summary><see cref="ReportJobRequest"/> -> null: forgets (and cancels) a report.</summary>
    public const string DeleteReport = "deleteReport";
}

/// <summary>JSON of all payloads: camelCase, enums as strings.</summary>
public static class SnapshotReportJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    /// <summary>Deserializes a payload; an empty payload gives a new instance.</summary>
    public static T Deserialize<T>(string? json)
        where T : new()
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new T();
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, Options) ?? new T();
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("The request is not valid JSON: " + ex.Message, nameof(json), ex);
        }
    }
}

public sealed class ListSourcesRequest
{
    /// <summary>Devices to list; empty = every managed video device.</summary>
    public List<Guid> DeviceIds { get; set; } = [];
}

public sealed class ListSourcesResult
{
    /// <summary>Tiles in device order (address), sources in camera order.</summary>
    public List<SnapshotTile> Tiles { get; set; } = [];
}

/// <summary>Facts of a device as shown on a tile and in the report.</summary>
public sealed class DeviceFacts
{
    public Guid DeviceId { get; set; }
    public string Address { get; set; } = string.Empty;
    public string? HostName { get; set; }
    public string? Model { get; set; }

    /// <summary>MAC address (serial number), upper hex without separators.</summary>
    public string Serial { get; set; } = string.Empty;

    public string? Firmware { get; set; }

    /// <summary>Device status name: Ok, Unreachable, CredentialsRequired, PasswordNotSet, CertificateChanged, Unknown.</summary>
    public string Status { get; set; } = "Unknown";

    public DateTime? CertNotAfterUtc { get; set; }

    /// <summary>Trusted, SelfSigned, Untrusted, Expired; null when unknown or HTTP only.</summary>
    public string? CertTrust { get; set; }
}

/// <summary>One picture of the grid: a device with one source, or one source of a multi-source device.</summary>
public sealed class SnapshotTile
{
    public DeviceFacts Device { get; set; } = new();

    /// <summary>1-based VAPIX camera number.</summary>
    public int Camera { get; set; } = 1;

    /// <summary>Source label ("Sensor 2", "View Area 1"); null for a device with one source.</summary>
    public string? SourceLabel { get; set; }

    /// <summary>Number of sources of the device.</summary>
    public int SourceCount { get; set; } = 1;

    /// <summary>"10.0.0.48" or "10.0.0.48 - View Area 1".</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Why the sources could not be read (the tile then stands for the whole device).</summary>
    public string? Error { get; set; }
}

public sealed class SnapshotRequest
{
    public Guid DeviceId { get; set; }
    public int Camera { get; set; } = 1;
    public int MaxWidth { get; set; } = SnapshotReportPluginInfo.GridMaxWidth;
    public int MaxHeight { get; set; } = SnapshotReportPluginInfo.GridMaxHeight;
}

public sealed class SnapshotResult
{
    public string? JpegBase64 { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public DateTimeOffset? CapturedUtc { get; set; }

    /// <summary>Readable failure ("Timeout after 10 s", "Unauthorized - HTTP 401", device error text); null on success.</summary>
    public string? Error { get; set; }
}

public sealed class ReportItem
{
    public Guid DeviceId { get; set; }
    public int Camera { get; set; } = 1;
}

public sealed class ReportRequest
{
    public string Site { get; set; } = string.Empty;
    public string Technician { get; set; } = string.Empty;

    /// <summary>Report date (yyyy-MM-dd).</summary>
    public DateOnly Date { get; set; }

    /// <summary>The selected tiles, in grid order.</summary>
    public List<ReportItem> Items { get; set; } = [];

    public int MaxWidth { get; set; } = SnapshotReportPluginInfo.ReportMaxWidth;
    public int MaxHeight { get; set; } = SnapshotReportPluginInfo.ReportMaxHeight;

    /// <summary>OADM version printed on the cover; the server fills its own when empty.</summary>
    public string? OadmVersion { get; set; }
}

public sealed class ReportJobRequest
{
    public string JobId { get; set; } = string.Empty;
}

public static class ReportJobStates
{
    public const string Running = "running";
    public const string Done = "done";
    public const string Failed = "failed";
}

public sealed class ReportJobStatus
{
    public string JobId { get; set; } = string.Empty;

    /// <summary><see cref="ReportJobStates"/>.</summary>
    public string State { get; set; } = ReportJobStates.Running;

    /// <summary>Snapshots taken so far, of <see cref="Total"/>.</summary>
    public int Done { get; set; }

    public int Total { get; set; }

    /// <summary>Short text of the current work ("Take snapshot 3 of 12", "Build PDF").</summary>
    public string? Message { get; set; }

    public string? Error { get; set; }

    /// <summary>PDF size in bytes once done.</summary>
    public long Size { get; set; }

    public int Pages { get; set; }

    /// <summary>Snapshots that failed (still in the report with their error).</summary>
    public int Failed { get; set; }
}

public sealed class ReadReportRequest
{
    public string JobId { get; set; } = string.Empty;
    public long Offset { get; set; }
}

public sealed class ReportChunk
{
    public string DataBase64 { get; set; } = string.Empty;
    public long Offset { get; set; }
    public long Total { get; set; }
    public bool Eof { get; set; }
}
