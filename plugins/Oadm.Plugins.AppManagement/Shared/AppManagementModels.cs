using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

using Oadm.Plugins.Acap;

namespace Oadm.Plugins.AppManagement;

/// <summary>Ids, names, texts and limits shared by the server part, the page and the tests (docs/spec/app-management.md).</summary>
public static class AppManagementInfo
{
    public const string PluginId = "oadm.app-management";
    public const string DisplayName = "Applications";

    /// <summary>The icon of the Applications context menu group.</summary>
    public const string IconKey = "app";

    /// <summary>The page's own permission ("oadm.app-management:view"; Administrator and Operator by default).</summary>
    public const string ViewPermission = "view";

    /// <summary>Devices read at the same time, by every read of every session together.</summary>
    public const int Parallelism = 16;

    /// <summary>Time for the reads of one device.</summary>
    public static readonly TimeSpan DeviceTimeout = TimeSpan.FromSeconds(15);

    /// <summary>A license that ends within this many days counts as "expires soon" (fixed).</summary>
    public const int ExpiresSoonDays = 5;

    /// <summary>Devices per page of <see cref="AppManagementMethods.GetRows"/> (each page well below the message limit).</summary>
    public const int RowsPerPage = 1_000;

    /// <summary>How often the server asks whether the page's tasks ended.</summary>
    public static readonly TimeSpan FollowInterval = TimeSpan.FromSeconds(2);

    /// <summary>Changed rows and the progress go to the page at most this often while a read runs.</summary>
    public static readonly TimeSpan EventInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>Login sessions whose results are kept in memory; the least recently used is dropped first.</summary>
    public const int MaxSessions = 32;

    /// <summary>The row of a device without the Application API 1.x (nothing is sent to it).</summary>
    public const string NoApplicationApi = "Applications cannot be managed on this device";

    /// <summary>The row of a device that is not Ok, like Compliance: "Not checked: Credentials required".</summary>
    public static string NotChecked(string status) => "Not checked: " + status;

    /// <summary>The refusal of an action the role lacks, like the server's other refusals.</summary>
    public static string NotAllowed(string permissionName) => $"Your role does not allow: {permissionName}.";

    /// <summary>The names of the Applications plugin's permissions the page's actions need (as its matrix rows say).</summary>
    public const string StartStopName = "Start and stop applications";
    public const string RemoveName = "Remove applications";
    public const string SettingsName = "Change application settings";
    public const string InstallName = "Install and upgrade applications";
    public const string DowngradeName = "Downgrade applications";
    public const string LicenseName = "Install license keys";

    /// <summary>Devices of one settings read (the settings window of an application over its devices).</summary>
    public const int MaxSettingsDevices = 1_000;

    /// <summary>The row of a task that failed or ended with warnings without a message of its own.</summary>
    public const string TaskFailed = "The task failed.";
    public const string TaskWarnings = "The task ended with warnings.";

    /// <summary>The refusal of a settings read over <see cref="MaxSettingsDevices"/>.</summary>
    public const string TooManySettingsDevices = "Settings open for at most 1,000 devices at once. Choose fewer.";
}

/// <summary>Page backend methods (<c>InvokeAsync</c>) and live event topics.</summary>
public static class AppManagementMethods
{
    /// <summary><see cref="ReadRequest"/> -> <see cref="ReadState"/>: reads the chosen devices (the session's rows become them).</summary>
    public const string Read = "read";

    /// <summary>{} -> <see cref="ReadState"/>: the progress of the session's read (no device access).</summary>
    public const string GetState = "getState";

    /// <summary><see cref="RowsRequest"/> -> <see cref="RowsPage"/>: the session's rows, one page of devices.</summary>
    public const string GetRows = "getRows";

    /// <summary><see cref="RunRequest"/> -> <see cref="RunReply"/>: starts Start, Stop or Remove tasks of the Applications plugin.</summary>
    public const string Run = "run";

    /// <summary><see cref="FollowRequest"/> -> {}: reads the devices again once the tasks the dialog started ended.</summary>
    public const string Follow = "follow";

    /// <summary><see cref="LogRequest"/> -> <see cref="LogReply"/>: one application's log lines on one device (read only).</summary>
    public const string GetLog = "getLog";

    /// <summary><see cref="SettingsRequest"/> -> <see cref="SettingsReply"/>: one application's settings on its devices (read only).</summary>
    public const string GetSettings = "getSettings";

    /// <summary>Event: <see cref="RowsEvent"/>, changed and removed rows of the session.</summary>
    public const string RowsTopic = "rows";

    /// <summary>Event: <see cref="ReadState"/>, the progress of the session's read.</summary>
    public const string StateTopic = "state";
}

/// <summary>What a task of the page does to a device while it runs (shown in the row).</summary>
public static class AppActivities
{
    public const string Installing = "Installing";
    public const string Starting = "Starting";
    public const string Stopping = "Stopping";
    public const string Removing = "Removing";
    public const string ChangingSettings = "Changing settings";

    public static string Of(AcapAction action) => action switch
    {
        AcapAction.Start => Starting,
        AcapAction.Stop => Stopping,
        AcapAction.Remove => Removing,
        AcapAction.Settings => ChangingSettings,
        _ => Installing,
    };
}

/// <summary>The devices to read: every device of the caller (<see cref="All"/>) or the chosen ones.</summary>
public sealed record ReadRequest
{
    public bool All { get; init; }

    public IReadOnlyList<Guid> DeviceIds { get; init; } = [];
}

/// <summary>The progress of the session's read.</summary>
public sealed record ReadState
{
    public string? JobId { get; init; }

    public bool Reading { get; init; }

    public int Done { get; init; }

    public int Total { get; init; }

    /// <summary>Devices with a row.</summary>
    public int Devices { get; init; }

    public DateTimeOffset? FinishedUtc { get; init; }
}

/// <summary>One device and its applications, or why they are not known.</summary>
public sealed record DeviceApps
{
    public Guid DeviceId { get; init; }

    public string Address { get; init; } = string.Empty;

    public string? Model { get; init; }

    /// <summary>The MAC address (the serial number), as the Devices grid shows it.</summary>
    public string? Mac { get; init; }

    public string? Firmware { get; init; }

    /// <summary>The system on chip as the device reports it ("Axis Artpec-8"); null when not known.</summary>
    public string? Soc { get; init; }

    /// <summary>The CPU architecture ("aarch64"); null when not known.</summary>
    public string? Architecture { get; init; }

    /// <summary>config.cgi AllowUnsigned; null when the device does not offer it (before AXIS OS 11.2, AXIS OS 13+).</summary>
    public bool? AllowUnsigned { get; init; }

    /// <summary>The embedded development (ACAP SDK) version, for old package.conf packages; null when not known.</summary>
    public string? EmbeddedDevelopmentVersion { get; init; }

    /// <summary>Not read (not Ok, no Application API) or the read failed: the text of the status cell.</summary>
    public string? Error { get; init; }

    /// <summary>The device is not Ok and was not contacted.</summary>
    public bool NotChecked { get; init; }

    /// <summary>A task of the page runs on the device (<see cref="AppActivities"/>); null otherwise.</summary>
    public string? Activity { get; init; }

    /// <summary>The applications (package names) <see cref="Activity"/> concerns; empty: the whole device (Install).</summary>
    public IReadOnlyList<string> ActivityApplications { get; init; } = [];

    public IReadOnlyList<InstalledApplication> Applications { get; init; } = [];

    public DateTimeOffset? ReadUtc { get; init; }

    /// <summary>
    /// The failed and warning tasks of the page that ended on the device before this read (their messages), kept until the
    /// next read the user starts (Refresh, a new choice); empty otherwise.
    /// </summary>
    public IReadOnlyList<AppTaskResult> TaskResults { get; init; } = [];
}

/// <summary>A task of the page that ended Failed or Done with warnings on a device, with the task's message.</summary>
public sealed record AppTaskResult
{
    /// <summary>The application of the task (Start, Stop, Remove, Change settings, a license of one application); null: the device's (Install).</summary>
    public string? Application { get; init; }

    /// <summary>Failed; false: Done with warnings.</summary>
    public bool Failed { get; init; }

    public string Message { get; init; } = string.Empty;
}

public sealed record RowsRequest
{
    public int Skip { get; init; }
}

/// <summary>One page of the session's rows; <see cref="Next"/> is the next <see cref="RowsRequest.Skip"/>, null at the end.</summary>
public sealed record RowsPage
{
    public IReadOnlyList<DeviceApps> Devices { get; init; } = [];

    public int? Next { get; init; }

    public ReadState State { get; init; } = new();
}

/// <summary>Changed rows (whole devices) and devices that left the session's rows.</summary>
public sealed record RowsEvent
{
    public IReadOnlyList<DeviceApps> Devices { get; init; } = [];

    public IReadOnlyList<Guid> Removed { get; init; } = [];
}

/// <summary>Install, Install licenses, Start, Stop, Remove or Change settings: one item per application with its devices.</summary>
public sealed record RunRequest
{
    public AcapAction Action { get; init; }

    public IReadOnlyList<RunItem> Items { get; init; } = [];

    /// <summary>Change settings: the changed values by setting name (memory only, never logged).</summary>
    public IReadOnlyDictionary<string, string>? Values { get; init; }

    /// <summary>Install: the packages and options (one item: the package's application and the devices that fit).</summary>
    public AppInstall? Install { get; init; }

    /// <summary>The client's "user@machine"; the server records the caller as the tasks' owner.</summary>
    public string? Owner { get; init; }
}

/// <summary>One application (its package name, as list.cgi names it) and the devices to run the action on.</summary>
public sealed record RunItem
{
    public string Application { get; init; } = string.Empty;

    /// <summary>The friendly name for the task name ("Stop AXIS Object Analytics").</summary>
    public string? DisplayName { get; init; }

    public IReadOnlyList<Guid> DeviceIds { get; init; } = [];

    /// <summary>Install licenses: the keys of the item's devices (memory only, never logged).</summary>
    public IReadOnlyList<LicenseKeyEntry>? LicenseKeys { get; init; }

    /// <summary>Install licenses: the application id the keys are for, null when the devices report none.</summary>
    public string? LicenseApplicationId { get; init; }
}

/// <summary>Install: the uploaded packages (one, or one per architecture) of one application and version, and the options.</summary>
public sealed record AppInstall
{
    public IReadOnlyList<AcapPackageFile> Files { get; init; } = [];

    public string? Version { get; init; }

    public bool AllowDowngrade { get; init; }

    public bool StartAfterInstall { get; init; } = true;

    /// <summary>The kind on every device of the run (null = mixed); only names the task.</summary>
    public InstallKind? Kind { get; init; }
}

public sealed record RunReply
{
    public IReadOnlyList<Guid> TaskIds { get; init; } = [];
}

/// <summary>The tasks the Applications dialog started for the page's devices (Install, Install licenses).</summary>
public sealed record FollowRequest
{
    public IReadOnlyList<Guid> TaskIds { get; init; } = [];

    public IReadOnlyList<Guid> DeviceIds { get; init; } = [];

    /// <summary>The application the tasks are about when the page knows it (Install licenses of one application); null otherwise.</summary>
    public string? Application { get; init; }
}

public sealed record LogRequest
{
    public Guid DeviceId { get; init; }

    public string Application { get; init; } = string.Empty;
}

/// <summary>The application's log lines (oldest first, newest last), or why they could not be read.</summary>
public sealed record LogReply
{
    public IReadOnlyList<AppLogLine> Lines { get; init; } = [];

    /// <summary>Only the newest lines are kept (2 MB, 10,000 lines).</summary>
    public bool Truncated { get; init; }

    public string? Error { get; init; }
}

public sealed record SettingsRequest
{
    public string Application { get; init; } = string.Empty;

    public IReadOnlyList<Guid> DeviceIds { get; init; } = [];
}

/// <summary>One device's settings of the application (hidden ones left out), or why they could not be read.</summary>
public sealed record DeviceSettings
{
    public Guid DeviceId { get; init; }

    public string Address { get; init; } = string.Empty;

    public string? Error { get; init; }

    public IReadOnlyList<AppSettingDefinition> Settings { get; init; } = [];
}

public sealed record SettingsReply
{
    public IReadOnlyList<DeviceSettings> Devices { get; init; } = [];

    /// <summary>The caller may change settings and sees their values; without, every value is left out.</summary>
    public bool ValuesShown { get; init; }
}

/// <summary>License states the page counts, from list.cgi's License and LicenseExpirationDate.</summary>
public static class AppLicenses
{
    /// <summary>The device reports no license for an application that needs one.</summary>
    public static bool IsMissing(InstalledApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return string.Equals(app.License, "Missing", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The device rejects the license.</summary>
    public static bool IsInvalid(InstalledApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return string.Equals(app.License, "Invalid", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The end date of a valid license with one ("2027-09-18"), else null.</summary>
    public static DateOnly? ExpiresOn(InstalledApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return string.Equals(app.License, "Valid", StringComparison.OrdinalIgnoreCase)
            && app.LicenseExpirationDate is { Length: > 0 } text
            && DateOnly.TryParseExact(text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;
    }

    /// <summary>A valid license that ends within <see cref="AppManagementInfo.ExpiresSoonDays"/> days of <paramref name="today"/>.</summary>
    public static bool ExpiresSoon(InstalledApplication app, DateOnly today) =>
        ExpiresOn(app) is { } end && end.DayNumber - today.DayNumber <= AppManagementInfo.ExpiresSoonDays;
}

public static class AppManagementJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    /// <summary>Parses a request or event; an unreadable one is an <see cref="ArgumentException"/> (nothing runs).</summary>
    public static T Deserialize<T>(string? json)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(string.IsNullOrWhiteSpace(json) ? "{}" : json, Options)
                ?? throw new ArgumentException("The request is empty.", nameof(json));
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("The request is not valid.", nameof(json), ex);
        }
    }
}
