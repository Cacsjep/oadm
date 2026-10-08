using System.Text.Json;
using System.Text.Json.Serialization;

namespace Oadm.Plugins.HardeningScan;

/// <summary>Ids, limits and defaults shared by the server and the client part.</summary>
public static class HardeningScanPluginInfo
{
    public const string PluginId = "oadm.hardening-scan";
    public const string DisplayName = "Hardening scan";
    public const string IconKey = "shieldCheck";

    /// <summary>The AXIS OS Hardening Guide the checks follow.</summary>
    public const string GuideUrl = "https://help.axis.com/en-us/axis-os-hardening-guide";

    /// <summary>Devices scanned at the same time (plugin setting <c>config.parallelism</c>, 1..64).</summary>
    public const int DefaultParallelism = 16;
    public const int MinParallelism = 1;
    public const int MaxParallelism = 64;

    /// <summary>Time one device request may take (no retries within one scan).</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Time all reads of one device may take together.</summary>
    public static readonly TimeSpan DeviceTimeout = TimeSpan.FromMinutes(2);

    /// <summary>Devices kept per level in the stored results (plugin setting <c>results</c>).</summary>
    public const int MaxStoredDevices = 10_000;

    /// <summary>Characters of a value (grid tooltip) and of a detail (detail pane) kept per check.</summary>
    public const int MaxValueLength = 160;
    public const int MaxDetailLength = 600;

    /// <summary>Rows per <see cref="HardeningMethods.ResultsTopic"/> event.</summary>
    public const int EventBatchRows = 1_000;

    /// <summary>Certificates expiring within this many days warn (E3).</summary>
    public const int CertificateWarningDays = 30;
}

/// <summary>InvokeAsync methods and event topics of the core plugin (PluginService.Invoke / Watch).</summary>
public static class HardeningMethods
{
    /// <summary>null -> <see cref="HardeningState"/>: catalog, the last results of every device (compact), the running scan.</summary>
    public const string GetState = "getState";

    /// <summary><see cref="StartScanRequest"/> -> <see cref="ScanJobStatus"/>; a start while a scan runs returns the running scan.</summary>
    public const string StartScan = "startScan";

    /// <summary><see cref="CancelScanRequest"/> -> <see cref="ScanJobStatus"/> (or null for an unknown scan).</summary>
    public const string CancelScan = "cancelScan";

    /// <summary><see cref="DetailRequest"/> -> <see cref="DetailReply"/>: values and texts of every check (detail pane, export).</summary>
    public const string GetDetail = "getDetail";

    /// <summary>Event: <see cref="ScanJobStatus"/> every 500 ms while a scan runs and once at its end.</summary>
    public const string ProgressTopic = "progress";

    /// <summary>Event: <see cref="ResultsEvent"/> with the rows that changed, batched every 500 ms.</summary>
    public const string ResultsTopic = "results";

    public static readonly IReadOnlyList<string> All = [GetState, StartScan, CancelScan, GetDetail];
}

/// <summary>The two levels of the guide; Extended = Basic + the Extended checks.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ScanLevel>))]
public enum ScanLevel
{
    Basic = 0,
    Extended = 1,
}

/// <summary>Result of one check on one device.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CheckState>))]
public enum CheckState
{
    /// <summary>No result (never scanned at this level).</summary>
    NotScanned = 0,
    Pass = 1,
    Warn = 2,
    Fail = 3,

    /// <summary>The check does not apply (feature or API missing on this model / firmware).</summary>
    NotApplicable = 4,

    /// <summary>The read failed (e.g. "Timeout after 15 s"); the other checks still count.</summary>
    Error = 5,

    /// <summary>Shown without a rating (B2: the AXIS OS version).</summary>
    Info = 6,
}

/// <summary>One character per check state in <see cref="DeviceResult.States"/> (compact for 5,000 devices).</summary>
public static class CheckStateCodes
{
    public static char ToCode(CheckState state) => state switch
    {
        CheckState.Pass => 'p',
        CheckState.Warn => 'w',
        CheckState.Fail => 'f',
        CheckState.NotApplicable => 'n',
        CheckState.Error => 'e',
        CheckState.Info => 'i',
        _ => '-',
    };

    public static CheckState FromCode(char code) => code switch
    {
        'p' => CheckState.Pass,
        'w' => CheckState.Warn,
        'f' => CheckState.Fail,
        'n' => CheckState.NotApplicable,
        'e' => CheckState.Error,
        'i' => CheckState.Info,
        _ => CheckState.NotScanned,
    };

    /// <summary>"pass", "warn", "fail", "n/a", "error", "info", "" (CSV export).</summary>
    public static string ToText(CheckState state) => state switch
    {
        CheckState.Pass => "pass",
        CheckState.Warn => "warn",
        CheckState.Fail => "fail",
        CheckState.NotApplicable => "n/a",
        CheckState.Error => "error",
        CheckState.Info => "info",
        _ => string.Empty,
    };

    /// <summary>"Pass", "Warning", "Fail", "Does not apply", "Could not be read", "Information", "Not scanned".</summary>
    public static string ToLabel(CheckState state) => state switch
    {
        CheckState.Pass => "Pass",
        CheckState.Warn => "Warning",
        CheckState.Fail => "Fail",
        CheckState.NotApplicable => "Does not apply",
        CheckState.Error => "Could not be read",
        CheckState.Info => "Information",
        _ => "Not scanned",
    };
}

/// <summary>JSON of all payloads: camelCase, enums as strings, nulls left out.</summary>
public static class HardeningJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    /// <summary>Deserializes a payload; an empty payload is a new <typeparamref name="T"/>, invalid JSON an ArgumentException.</summary>
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
            throw new ArgumentException("The request could not be read: " + ex.Message, nameof(json), ex);
        }
    }
}

/// <summary>One row of the guide: a checked column, or an item that cannot be checked remotely (<see cref="IsInfo"/>).</summary>
public sealed class CheckInfo
{
    /// <summary>"B14", "E5", "X3" (guide order: B = Basic, E = Extended, X = additional Extended columns).</summary>
    public string Id { get; init; } = string.Empty;

    public ScanLevel Level { get; init; }

    /// <summary>Guide section, e.g. "Disable unused services/functions > SSH access".</summary>
    public string Section { get; init; } = string.Empty;

    /// <summary>Full name, e.g. "SSH access".</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>Narrow column header, e.g. "SSH".</summary>
    public string Header { get; init; } = string.Empty;

    /// <summary>Rule in plain language: what passes, warns and fails.</summary>
    public string Rule { get; init; } = string.Empty;

    /// <summary>The guide's recommendation in one sentence.</summary>
    public string Recommendation { get; init; } = string.Empty;

    /// <summary>Not checkable remotely: listed once in "Not checked automatically", never a column.</summary>
    public bool IsInfo { get; init; }

    /// <summary>Shown but not rated (B2): not counted in the score.</summary>
    public bool IsRated { get; init; } = true;

    /// <summary>Extra remark for the tooltip (Bonjour: OADM uses it), or null.</summary>
    public string? Note { get; init; }
}

/// <summary>The result of one check on one device.</summary>
public sealed class CheckResult
{
    public string Id { get; init; } = string.Empty;

    public CheckState State { get; init; }

    /// <summary>The value(s) found, short: "yes", "4 administrators, 1 operator", "Timeout after 15 s".</summary>
    public string? Value { get; init; }

    /// <summary>More text for the detail pane (the applications, the ciphers), or null.</summary>
    public string? Detail { get; init; }
}

/// <summary>Compact result of one device at one level (getState, results events): one state code per checked column.</summary>
public sealed class DeviceResult
{
    public Guid DeviceId { get; init; }

    public ScanLevel Level { get; init; }

    public DateTimeOffset ScannedUtc { get; init; }

    /// <summary>Why the device could not be scanned ("Credentials required - ..."), null when it was read.</summary>
    public string? Status { get; init; }

    /// <summary>One <see cref="CheckStateCodes"/> character per entry of <see cref="HardeningCatalog.Columns"/>.</summary>
    public string States { get; init; } = string.Empty;

    /// <summary>The short value per column (same order as <see cref="States"/>; null = none).</summary>
    public IReadOnlyList<string?> Values { get; init; } = [];
}

/// <summary>Full result of one device at one level (stored, getDetail).</summary>
public sealed class DeviceDetail
{
    public Guid DeviceId { get; init; }

    public ScanLevel Level { get; init; }

    public DateTimeOffset ScannedUtc { get; init; }

    public string? Status { get; init; }

    public IReadOnlyList<CheckResult> Checks { get; init; } = [];
}

public sealed class HardeningState
{
    /// <summary>Every row of the guide in order: the checked columns and the info items.</summary>
    public IReadOnlyList<CheckInfo> Catalog { get; init; } = [];

    /// <summary>The ids of <see cref="DeviceResult.States"/> positions, in order.</summary>
    public IReadOnlyList<string> Columns { get; init; } = [];

    /// <summary>The last result per device and level.</summary>
    public IReadOnlyList<DeviceResult> Results { get; init; } = [];

    /// <summary>The running (or last) scan; null when none ran since the server started.</summary>
    public ScanJobStatus? Job { get; init; }
}

public sealed class StartScanRequest
{
    public ScanLevel Level { get; init; }

    /// <summary>Devices to scan; empty = every managed device.</summary>
    public IReadOnlyList<Guid> DeviceIds { get; init; } = [];
}

public sealed class CancelScanRequest
{
    public string? JobId { get; init; }
}

public static class ScanJobStates
{
    public const string Running = "running";
    public const string Done = "done";
    public const string Cancelled = "cancelled";
}

public sealed class ScanJobStatus
{
    public string JobId { get; init; } = string.Empty;

    public ScanLevel Level { get; init; }

    /// <summary><see cref="ScanJobStates"/>.</summary>
    public string State { get; init; } = ScanJobStates.Running;

    public int Done { get; init; }

    public int Total { get; init; }

    /// <summary>Devices that could not be scanned (refused status or not reachable).</summary>
    public int Failed { get; init; }

    public DateTimeOffset StartedUtc { get; init; }

    public DateTimeOffset? FinishedUtc { get; init; }

    [JsonIgnore]
    public bool IsRunning => State == ScanJobStates.Running;
}

public sealed class ResultsEvent
{
    public IReadOnlyList<DeviceResult> Results { get; init; } = [];
}

public sealed class DetailRequest
{
    public IReadOnlyList<Guid> DeviceIds { get; init; } = [];
}

public sealed class DetailReply
{
    /// <summary>Every stored level of every requested device that has a result.</summary>
    public IReadOnlyList<DeviceDetail> Devices { get; init; } = [];
}
