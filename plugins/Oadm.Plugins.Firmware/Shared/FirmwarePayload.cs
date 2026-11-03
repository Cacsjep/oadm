using System.Text.Json;
using System.Text.Json.Serialization;

using Oadm.Sdk.Devices;

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
/// <param name="FileId">The upload's id.</param>
/// <param name="FileName">The file name as chosen (product and version are read from it).</param>
/// <param name="Devices">
/// The devices this file is for (chosen per device in the dialog); null: every device of its product (the file name tells
/// it), as before.
/// </param>
public sealed record FirmwareFile(string FileId, string? FileName, IReadOnlyList<Guid>? Devices = null);

/// <summary>
/// One version from axis.com of the payload (README "Firmware online"): for the listed <see cref="Devices"/>, or without a
/// list for every device of <see cref="Product"/> whose AXIS OS major is <see cref="FromMajor"/> (null: devices with an
/// unknown version) that no listed entry names. The server reads the URL, size and SHA-256 from axis.com itself.
/// </summary>
/// <param name="Source">"MPQT" or "PACS".</param>
/// <param name="Product">The product folder on axis.com ("P3265-V").</param>
/// <param name="Version">"12.11.147".</param>
/// <param name="FromMajor">The major version of the devices it is for, when it lists none.</param>
/// <param name="Devices">The devices it is for; null: by product and major.</param>
public sealed record FirmwareOnline(string Source, string Product, string Version, int? FromMajor, IReadOnlyList<Guid>? Devices = null);

/// <summary>Payload of the "Upgrade AXIS OS" task, written by the dialog.</summary>
public sealed record FirmwarePayload
{
    /// <summary>Id of the uploaded firmware file (<c>ITaskDialogContext.UploadAsync</c>); the first one of <see cref="Files"/>.</summary>
    public string? FileId { get; init; }

    /// <summary>Original file name, used to read product and version.</summary>
    public string? FileName { get; init; }

    /// <summary>Several files (one per model): each device installs the file for its product. Null with one file.</summary>
    public IReadOnlyList<FirmwareFile>? Files { get; init; }

    /// <summary><see cref="Files"/>, or the one file; empty when only versions from axis.com were chosen.</summary>
    public IReadOnlyList<FirmwareFile> AllFiles() =>
        Files is { Count: > 0 } files ? files : FileId is null && FileName is null ? [] : [new FirmwareFile(FileId ?? string.Empty, FileName)];

    /// <summary>Versions from axis.com, one per model and track (README "Firmware online"); null without.</summary>
    public IReadOnlyList<FirmwareOnline>? Online { get; init; }

    /// <summary><see cref="Online"/>, never null.</summary>
    public IReadOnlyList<FirmwareOnline> AllOnline() => Online ?? [];

    public FactoryDefaultMode FactoryDefaultMode { get; init; } = FactoryDefaultMode.None;

    /// <summary>Allow installing an older version than the one on the device.</summary>
    public bool AllowDowngrade { get; init; }

    /// <summary>Upgrade or downgrade on every installing device (dialog preview); only names the task, never decides anything.</summary>
    public FirmwareDirection Direction { get; init; }

    /// <summary>
    /// The task name: "Upgrade AXIS OS to 12.11.77", "Downgrade AXIS OS to 10.12.236", "Install AXIS OS
    /// 12.11.77" (mixed), "Install AXIS OS" (version unknown), plus " (factory default)" with a soft or hard
    /// factory default.
    /// </summary>
    public string TaskName()
    {
        var versions = AllFiles().Select(f => FirmwareImageInspector.ParseFileName(f.FileName).Version?.Text)
            .Concat(AllOnline().Select(o => AxisOsVersion.TryParse(o.Version)?.Text)).Distinct().ToList();
        var version = versions.Count == 1 ? versions[0] : null;
        var name = versions.Count > 1
            ? Direction == FirmwareDirection.Downgrade ? AxisOsTexts.DowngradeTask : AxisOsTexts.UpgradeTask
            : version is null
            ? "Install AXIS OS"
            : Direction switch
            {
                FirmwareDirection.Downgrade => AxisOsTexts.DowngradeTask + " to " + version,
                FirmwareDirection.Unknown when AllowDowngrade => "Install " + AxisOsTexts.Version(version),
                _ => AxisOsTexts.UpgradeTask + " to " + version,
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
            throw new ArgumentException("No AXIS OS file was selected. Nothing was changed.", nameof(json));
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

        if (payload is null || payload.AllFiles().Any(f => string.IsNullOrWhiteSpace(f.FileId))
            || (payload.AllFiles().Count == 0 && payload.AllOnline().Count == 0))
        {
            throw new ArgumentException("No AXIS OS file was selected. Nothing was changed.", nameof(json));
        }

        foreach (var online in payload.AllOnline())
        {
            if (online is null || !OnlineFirmware.IsValidSource(online.Source) || !OnlineFirmware.IsValidProduct(online.Product)
                || OnlineListing.ParseVerTxt(online.Version) is null)
            {
                throw new ArgumentException("The AXIS OS version from axis.com is not valid. Nothing was changed.", nameof(json));
            }
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
