using System.Text.Json;
using System.Text.Json.Serialization;

namespace Oadm.Plugins.SystemReport;

/// <summary>Ids, method names and limits shared by the server and the client part.</summary>
public static class SystemReportPluginInfo
{
    public const string PluginId = "oadm.system-report";
    public const string DisplayName = "System report";
    public const string IconKey = "file";

    /// <summary>Devices whose report is downloaded at the same time (a report takes 30-40 s on the device).</summary>
    public const int Parallelism = 4;

    /// <summary>Devices per job: the selection of a large site.</summary>
    public const int MaxDevices = 5000;

    /// <summary>Largest report of one device that is kept; larger answers are dropped (AXIS P3265-V: about 0.3 MB).</summary>
    public const long MaxReportBytes = 256L * 1024 * 1024;

    /// <summary>Time one device may take for its report (headers and body; 10.0.0.48 needs about 37 s).</summary>
    public static readonly TimeSpan DeviceTimeout = TimeSpan.FromMinutes(5);

    /// <summary>Bytes of the bundled ZIP returned per <see cref="SystemReportMethods.Read"/> call.</summary>
    public const int ChunkBytes = 2 * 1024 * 1024;

    /// <summary>Name of the text file with the list of reports and failures inside the bundle.</summary>
    public const string SummaryFileName = "summary.txt";
}

/// <summary>InvokeAsync methods of the core plugin (PluginService.Invoke). All need the Operator role; start is audited.</summary>
public static class SystemReportMethods
{
    /// <summary><see cref="StartRequest"/> -> <see cref="JobStatus"/> (every device): starts the downloads in the background.</summary>
    public const string Start = "start";

    /// <summary><see cref="StatusRequest"/> -> <see cref="JobStatus"/> with the devices changed since <see cref="StatusRequest.SinceVersion"/>.</summary>
    public const string Status = "status";

    /// <summary><see cref="ReadRequest"/> -> <see cref="ReportChunk"/>: the finished bundle in chunks.</summary>
    public const string Read = "read";

    /// <summary><see cref="JobRequest"/> -> null: cancels the job and deletes its files.</summary>
    public const string Delete = "delete";
}

/// <summary>States of a job.</summary>
public static class JobStates
{
    public const string Running = "running";
    public const string Packing = "packing";
    public const string Done = "done";
    public const string Failed = "failed";
}

/// <summary>States of one device in a job.</summary>
public static class DeviceReportStates
{
    public const string Waiting = "waiting";
    public const string Downloading = "downloading";
    public const string Done = "done";
    public const string Failed = "failed";
}

/// <summary>JSON of all payloads: camelCase, nulls left out.</summary>
public static class SystemReportJson
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

public sealed class StartRequest
{
    /// <summary>Devices whose system report is downloaded, in the order of the bundle's summary.</summary>
    public List<Guid> DeviceIds { get; set; } = [];
}

public sealed class JobRequest
{
    public string JobId { get; set; } = string.Empty;
}

public sealed class StatusRequest
{
    public string JobId { get; set; } = string.Empty;

    /// <summary>Only devices that changed after this <see cref="JobStatus.Version"/> are listed (0 = every device).</summary>
    public long SinceVersion { get; set; }
}

public sealed class ReadRequest
{
    public string JobId { get; set; } = string.Empty;
    public long Offset { get; set; }
}

/// <summary>Progress of a job. <see cref="Devices"/> holds the devices changed since the version the caller asked for.</summary>
public sealed class JobStatus
{
    public string JobId { get; set; } = string.Empty;

    /// <summary><see cref="JobStates"/>.</summary>
    public string State { get; set; } = JobStates.Running;

    public int Total { get; set; }

    /// <summary>Devices finished (successful or failed).</summary>
    public int Finished { get; set; }

    public int Failed { get; set; }

    /// <summary>Size of the bundle in bytes once <see cref="State"/> is done.</summary>
    public long Size { get; set; }

    /// <summary>Why the whole job failed (cancelled, disk full).</summary>
    public string? Error { get; set; }

    /// <summary>Change counter: pass it as <see cref="StatusRequest.SinceVersion"/> to get only later changes.</summary>
    public long Version { get; set; }

    public List<DeviceReportStatus> Devices { get; set; } = [];
}

/// <summary>One device of a job.</summary>
public sealed class DeviceReportStatus
{
    public Guid DeviceId { get; set; }
    public string Address { get; set; } = string.Empty;

    /// <summary>MAC address (serial number).</summary>
    public string Serial { get; set; } = string.Empty;

    public string? Model { get; set; }

    /// <summary><see cref="DeviceReportStates"/>.</summary>
    public string State { get; set; } = DeviceReportStates.Waiting;

    public string? Error { get; set; }

    /// <summary>Bytes of the device's report.</summary>
    public long Size { get; set; }

    /// <summary>Name of the report inside the bundle.</summary>
    public string? FileName { get; set; }
}

public sealed class ReportChunk
{
    public string DataBase64 { get; set; } = string.Empty;
    public long Offset { get; set; }
    public long Total { get; set; }
    public bool Eof { get; set; }
}
