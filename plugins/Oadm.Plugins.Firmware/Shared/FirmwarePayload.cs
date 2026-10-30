using System.Text.Json;
using System.Text.Json.Serialization;

namespace Oadm.Plugins.Firmware;

/// <summary>fwmgr <c>factoryDefaultMode</c> of the upgrade method.</summary>
public enum FactoryDefaultMode
{
    /// <summary>Keep all settings (default).</summary>
    None = 0,

    /// <summary>Reset everything except network settings (IP, boot protocol, 802.1X) and time. Users and passwords are reset.</summary>
    Soft = 1,

    /// <summary>Reset everything including the IP configuration. The device may come back at another address.</summary>
    Hard = 2,
}

/// <summary>What the dialog preview expects on the devices that will install the file (only for the task name).</summary>
public enum FirmwareDirection
{
    /// <summary>Unknown or mixed (some devices upgrade, others downgrade).</summary>
    Unknown = 0,
    Upgrade = 1,
    Downgrade = 2,
}

/// <summary>One uploaded firmware file of the payload.</summary>
public sealed record FirmwareFile(string FileId, string? FileName);

/// <summary>Payload of the "Upgrade firmware" task, written by the dialog.</summary>
public sealed record FirmwarePayload
{
    /// <summary>Id of the uploaded firmware file (<c>ITaskDialogContext.UploadAsync</c>); the first one of <see cref="Files"/>.</summary>
    public string? FileId { get; init; }

    /// <summary>Original file name, used to read product and version.</summary>
    public string? FileName { get; init; }

    /// <summary>Several files (one per model): each device installs the file for its product. Null with one file.</summary>
    public IReadOnlyList<FirmwareFile>? Files { get; init; }

    /// <summary><see cref="Files"/>, or the one file.</summary>
    public IReadOnlyList<FirmwareFile> AllFiles() => Files is { Count: > 0 } files ? files : [new FirmwareFile(FileId ?? string.Empty, FileName)];

    public FactoryDefaultMode FactoryDefaultMode { get; init; } = FactoryDefaultMode.None;

    /// <summary>Allow installing an older version than the one on the device.</summary>
    public bool AllowDowngrade { get; init; }

    /// <summary>Upgrade or downgrade on every installing device (dialog preview); only names the task, never decides anything.</summary>
    public FirmwareDirection Direction { get; init; }

    /// <summary>
    /// The task name: "Upgrade firmware to 12.11.77", "Downgrade firmware to 10.12.236", "Install firmware
    /// 12.11.77" (mixed), "Install firmware" (version unknown), plus " (factory default)" with a soft or hard
    /// factory default.
    /// </summary>
    public string TaskName()
    {
        var versions = AllFiles().Select(f => FirmwareImageInspector.ParseFileName(f.FileName).Version?.Text).Distinct().ToList();
        var version = versions.Count == 1 ? versions[0] : null;
        var name = versions.Count > 1
            ? Direction == FirmwareDirection.Downgrade ? "Downgrade firmware" : "Upgrade firmware"
            : version is null
            ? "Install firmware"
            : Direction switch
            {
                FirmwareDirection.Downgrade => "Downgrade firmware to " + version,
                FirmwareDirection.Unknown when AllowDowngrade => "Install firmware " + version,
                _ => "Upgrade firmware to " + version,
            };
        return FactoryDefaultMode == FactoryDefaultMode.None ? name : name + " (factory default)";
    }

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters =
        {
            new JsonStringEnumConverter<FactoryDefaultMode>(JsonNamingPolicy.CamelCase),
            new JsonStringEnumConverter<FirmwareDirection>(JsonNamingPolicy.CamelCase),
        },
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>Parses and validates the payload; throws <see cref="ArgumentException"/> with a user-readable message.</summary>
    public static FirmwarePayload Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new ArgumentException("No firmware file was selected. Nothing was changed.", nameof(json));
        }

        FirmwarePayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<FirmwarePayload>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("The task payload is not valid. Nothing was changed.", nameof(json), ex);
        }

        if (payload is null || payload.AllFiles().Any(f => string.IsNullOrWhiteSpace(f.FileId)))
        {
            throw new ArgumentException("No firmware file was selected. Nothing was changed.", nameof(json));
        }

        if (!Enum.IsDefined(payload.FactoryDefaultMode))
        {
            throw new ArgumentException("Unknown factory default mode. Nothing was changed.", nameof(json));
        }

        return payload;
    }
}

/// <summary>fwmgr status of one device, as returned by the "status" query.</summary>
public sealed record FirmwareStatusInfo
{
    public bool Supported { get; init; }

    public string? FwmgrVersion { get; init; }

    public string? ActiveVersion { get; init; }

    public string? ActivePart { get; init; }

    /// <summary>Version that a rollback would restore; null when nothing can be rolled back.</summary>
    public string? InactiveVersion { get; init; }

    /// <summary>False while an upgrade waits for commit (auto rollback pending). Null when the device does not say.</summary>
    public bool? IsCommitted { get; init; }

    public string? PendingCommit { get; init; }

    /// <summary>Seconds until the device rolls back automatically.</summary>
    public int? TimeToRollback { get; init; }

    public string? LastUpgradeAt { get; init; }

    public string? ResetSource { get; init; }

    public string? Model { get; init; }

    public string? Architecture { get; init; }

    public string? Soc { get; init; }

    /// <summary>The installed applications (<see cref="InstalledAppRules"/>); null when the device could not list them.</summary>
    public IReadOnlyList<InstalledApp>? Applications { get; init; }

    public string ToJson() => JsonSerializer.Serialize(this, FirmwarePayload.JsonOptions);

    public static FirmwareStatusInfo? FromJson(string? json) =>
        string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<FirmwareStatusInfo>(json, FirmwarePayload.JsonOptions);
}
