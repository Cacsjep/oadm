using System.Text.Json;
using System.Text.Json.Serialization;

namespace Oadm.Plugins.Acap;

/// <summary>Constants shared by the server task and the client dialog.</summary>
public static class AcapPlugin
{
    public const string Id = "oadm.acap";

    /// <summary>VAPIX Application API (applications/list.cgi, upload.cgi, control.cgi, config.cgi) in apidiscovery.</summary>
    public const string ApplicationApiId = "application";

    public const string ApplicationApiMinVersion = "1.0";

    /// <summary>Read-only query: installed applications plus the device facts needed for compatibility checks.</summary>
    public const string ListApplicationsMethod = "listApplications";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}

public enum AcapAction
{
    Install = 0,
    Remove = 1,
    Start = 2,
    Stop = 3,
}

/// <summary>Task payload produced by the dialog. Contains no secrets.</summary>
public sealed record AcapPayload
{
    public AcapAction Action { get; init; }

    /// <summary>Package name for remove/start/stop; for install the package name read in the dialog (cross-checked).</summary>
    public string? Application { get; init; }

    /// <summary>Install: id from <c>ITaskDialogContext.UploadAsync</c>.</summary>
    public string? FileId { get; init; }

    /// <summary>Install: SHA-256 of the uploaded file as reported by the upload (upper hex).</summary>
    public string? Sha256 { get; init; }

    /// <summary>Install: package version read in the dialog (cross-checked against the server-side read).</summary>
    public string? Version { get; init; }

    /// <summary>Install: permit installing a lower version than the installed one.</summary>
    public bool AllowDowngrade { get; init; }

    /// <summary>Install: start the application after a successful install.</summary>
    public bool StartAfterInstall { get; init; }

    public string ToJson() => JsonSerializer.Serialize(this, AcapPlugin.Json);

    /// <summary>Parses and validates a payload. Throws <see cref="ArgumentException"/> for invalid input; nothing has been changed then.</summary>
    public static AcapPayload Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new ArgumentException("The task needs a payload from the Applications dialog. Nothing was changed.", nameof(json));
        }

        AcapPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<AcapPayload>(json, AcapPlugin.Json);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("The task payload is not valid. Nothing was changed.", nameof(json), ex);
        }

        if (payload is null || !Enum.IsDefined(payload.Action))
        {
            throw new ArgumentException("The task payload has no valid action. Nothing was changed.", nameof(json));
        }

        if (payload.Action == AcapAction.Install)
        {
            if (string.IsNullOrWhiteSpace(payload.FileId))
            {
                throw new ArgumentException("Install needs an uploaded package. Nothing was changed.", nameof(json));
            }
        }
        else if (!EapReader.IsValidAppName(payload.Application))
        {
            throw new ArgumentException("The application name is not valid. Nothing was changed.", nameof(json));
        }

        return payload;
    }
}

/// <summary>Device facts needed for the compatibility checks.</summary>
public sealed record AcapDeviceFacts
{
    /// <summary>basicdeviceinfo Architecture, e.g. aarch64 or armv7hf.</summary>
    public string? Architecture { get; init; }

    public string? FirmwareVersion { get; init; }

    /// <summary>Properties.EmbeddedDevelopment.Version, e.g. 2.18.</summary>
    public string? EmbeddedDevelopmentVersion { get; init; }

    /// <summary>config.cgi AllowUnsigned; null when the device does not offer it (before AXIS OS 11.2).</summary>
    public bool? AllowUnsigned { get; init; }
}

/// <summary>One entry of applications/list.cgi.</summary>
public sealed record InstalledApplication
{
    public required string Name { get; init; }

    public string? NiceName { get; init; }

    public string? Vendor { get; init; }

    public string? Version { get; init; }

    /// <summary>Running, Stopped or Idle.</summary>
    public string? Status { get; init; }

    /// <summary>Valid, Invalid, Missing, Custom or None.</summary>
    public string? License { get; init; }

    public string? LicenseExpirationDate { get; init; }

    public string? ApplicationId { get; init; }

    public bool Bundled { get; init; }

    /// <summary>Signed, Unsigned or Unknown.</summary>
    public string? SignatureStatus { get; init; }

    public IReadOnlyList<OsVersionRange> CompatibleOsVersions { get; init; } = [];

    [JsonIgnore]
    public bool IsRunning => string.Equals(Status, "Running", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(NiceName) ? Name : NiceName;
}

/// <summary>Result of the <see cref="AcapPlugin.ListApplicationsMethod"/> query.</summary>
public sealed record ListApplicationsResult
{
    public AcapDeviceFacts Device { get; init; } = new();

    public IReadOnlyList<InstalledApplication> Applications { get; init; } = [];

    public string ToJson() => JsonSerializer.Serialize(this, AcapPlugin.Json);

    public static ListApplicationsResult FromJson(string json) =>
        JsonSerializer.Deserialize<ListApplicationsResult>(json, AcapPlugin.Json)
        ?? throw new JsonException("Empty listApplications result.");
}
