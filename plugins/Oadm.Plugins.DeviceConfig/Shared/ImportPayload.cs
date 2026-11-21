using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

using Oadm.Sdk.Tasks;

namespace Oadm.Plugins.DeviceConfig;

/// <summary>
/// Payload of "Import configuration". The dialog uploads the chosen file (one <c>.json</c> or the ZIP of an export) and
/// names, per device, the file it gets: the empty name for a single file, else the ZIP entry. Devices the dialog left out
/// (no file, two files) carry their reason; their task changes nothing. An AI agent (<see cref="ImportMcpPayload"/>) sends
/// no <see cref="Files"/> at all: its one <c>.json</c> goes to every device of the run (<see cref="ForEveryDevice"/>).
/// </summary>
public sealed class ImportPayload
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The names the payload is read with (the agents' schema of <see cref="ImportMcpPayload"/> is exported with them).</summary>
    public static JsonSerializerOptions JsonOptions => Options;

    /// <summary>Upload id of the file.</summary>
    public string FileId { get; init; } = string.Empty;

    /// <summary>Name of the file the user chose (task name).</summary>
    public string FileName { get; init; } = string.Empty;

    /// <summary>The upload is a ZIP of configuration files.</summary>
    public bool IsZip { get; init; }

    /// <summary>Section keys to import ("param.v2"), in file order.</summary>
    public IReadOnlyList<string> Sections { get; init; } = [];

    /// <summary>Device id -> ZIP entry name ("" = the single file).</summary>
    public IReadOnlyDictionary<Guid, string> Files { get; init; } = new Dictionary<Guid, string>();

    /// <summary>Device id -> why the dialog left it out.</summary>
    public IReadOnlyDictionary<Guid, string> LeftOut { get; init; } = new Dictionary<Guid, string>();

    /// <summary>
    /// The payload names no <see cref="Files"/> (an agent's): the single file goes to every device of the run, and each device
    /// checks the file's model itself (the plan skips the others first).
    /// </summary>
    [JsonIgnore]
    public bool ForEveryDevice { get; private init; }

    /// <summary>The file of <paramref name="deviceId"/> ("" = the single file), or null when the device is left out.</summary>
    public string? EntryFor(Guid deviceId) =>
        ForEveryDevice ? string.Empty : Files.TryGetValue(deviceId, out var entry) ? entry : null;

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<string, ImportPayload> Parsed = new();

    /// <summary>
    /// <see cref="Parse"/> once per payload string: the engine hands the same string to every task of a run, so 5,000 devices
    /// with their 5,000 entries parse it once, not 5,000 times. The payload is never changed after it is read.
    /// </summary>
    public static ImportPayload ParseShared(string? json)
    {
        if (json is null)
        {
            return Parse(json);
        }

        if (Parsed.TryGetValue(json, out var cached))
        {
            return cached;
        }

        var payload = Parse(json);
        Parsed.AddOrUpdate(json, payload);
        return payload;
    }

    public const string NeedsFile = "The import needs a configuration file.";

    public const string NotValid = "The import payload is not valid.";

    public const string NeedsFileAndArea = "The import needs a configuration file and at least one settings area.";

    /// <summary>The dialog's text when no settings area is checked.</summary>
    public const string ChooseArea = "Choose at least one settings area";

    /// <summary>An agent's payload with a ZIP: only one <c>.json</c> goes to every device.</summary>
    public const string OneFileOnly = "Upload one configuration file (.json) of Export configuration.";

    /// <summary>Throws <see cref="ArgumentException"/> for a missing or broken payload.</summary>
    public static ImportPayload Parse(string? json)
    {
        var (payload, errors) = Check(json);
        if (errors.Count == 0)
        {
            return payload!;
        }

        // A payload without its file or areas keeps its one text (the task's log of older versions).
        var missing = !string.IsNullOrWhiteSpace(json) && errors[0].Message is NeedsFile or ChooseArea;
        throw new ArgumentException(missing ? NeedsFileAndArea : errors[0].Message, nameof(json));
    }

    /// <summary>
    /// Reads and checks the payload of the dialog or of an agent: each problem at its JSON field (<c>fileId</c>,
    /// <c>sections</c>). Unknown areas are refused here only in an agent's form (the dialog's task skips them). Reads only the
    /// payload, never the file or a device.
    /// </summary>
    public static (ImportPayload? Payload, IReadOnlyList<PayloadError> Errors) Check(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return (null, [new("fileId", NeedsFile)]);
        }

        ImportPayload? payload;
        bool hasFiles;
        try
        {
            payload = JsonSerializer.Deserialize<ImportPayload>(json, Options);
            using var document = JsonDocument.Parse(json);
            hasFiles = document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.EnumerateObject().Any(p => string.Equals(p.Name, nameof(Files), StringComparison.OrdinalIgnoreCase));
        }
        catch (JsonException)
        {
            return (null, [new(string.Empty, NotValid)]);
        }

        if (payload is null)
        {
            return (null, [new(string.Empty, NotValid)]);
        }

        var errors = new List<PayloadError>();
        if (string.IsNullOrWhiteSpace(payload.FileId))
        {
            errors.Add(new("fileId", NeedsFile));
        }

        if (payload.Sections is not { Count: > 0 })
        {
            errors.Add(new("sections", ChooseArea));
        }

        if (!hasFiles)
        {
            if (payload.IsZip)
            {
                errors.Add(new("fileId", OneFileOnly));
            }

            for (var i = 0; payload.Sections is not null && i < payload.Sections.Count; i++)
            {
                if (payload.Sections[i] is not { } key || !ConfigSections.IsKnown(key))
                {
                    errors.Add(new(string.Create(CultureInfo.InvariantCulture, $"sections[{i}]"), ConfigSections.UnknownReason + "."));
                }
            }
        }

        if (errors.Count > 0)
        {
            return (null, errors);
        }

        return (new ImportPayload
        {
            FileId = payload.FileId,
            FileName = payload.FileName ?? string.Empty,
            IsZip = payload.IsZip,
            Sections = payload.Sections!,
            Files = payload.Files ?? new Dictionary<Guid, string>(),
            LeftOut = payload.LeftOut ?? new Dictionary<Guid, string>(),
            ForEveryDevice = !hasFiles,
        }, []);
    }

    /// <summary>"Import configuration lobby.json".</summary>
    public string TaskName() =>
        string.IsNullOrWhiteSpace(FileName) ? DeviceConfigIds.ImportName : DeviceConfigIds.ImportName + " " + FileName.Trim();
}

/// <summary>The texts of Import configuration the dialog and the plan of an agent's run share.</summary>
public static class ImportTexts
{
    /// <summary>Part of the confirmation, and a warning of every plan.</summary>
    public const string ReplaceNote = "The settings in the file replace the current ones.";

    public const string IdentityNote = "Host name, IP address and device names stay as they are on each device.";

    public const string PasswordNote = "The file has no passwords. Event recipients that need one must get it again.";

    /// <summary>"2 of 6 devices are another model than the file: each takes only the settings it has." (all: "The devices are ...").</summary>
    public static string OtherModelNote(int others, int total) => others == total
        ? (total == 1 ? "The device is another model than the file: it takes only the settings it has." : "The devices are another model than the file: each takes only the settings it has.")
        : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{others:N0} of {total:N0} devices are another model than the file: each takes only the settings it has.");

    /// <summary>
    /// "2 of 6 devices have AXIS OS older than 12.5: they take only the network and system settings." (all: "The devices have
    /// ..."): below 12.5 the parameters are set the old way, every other area needs the configuration API.
    /// </summary>
    public static string OlderOsNote(int older, int total) => older == total
        ? (total == 1
            ? $"The device has AXIS OS older than {DeviceConfigIds.MinFirmwareText}: it takes only the network and system settings."
            : $"The devices have AXIS OS older than {DeviceConfigIds.MinFirmwareText}: they take only the network and system settings.")
        : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{older:N0} of {total:N0} devices have AXIS OS older than {DeviceConfigIds.MinFirmwareText}: they take only the network and system settings.");

    /// <summary>
    /// The task's warning on a device below 12.5 for the areas it left out: "Event rules and schedules were not applied: this
    /// needs AXIS OS 12.5 or later." (one: "Time zone was not applied: ...").
    /// </summary>
    public static string NotAppliedNote(IReadOnlyList<string> areas)
    {
        ArgumentNullException.ThrowIfNull(areas);
        var names = areas.Select((a, i) => i == 0 || a.StartsWith("MQTT", StringComparison.Ordinal) ? a : char.ToLowerInvariant(a[0]) + a[1..]).ToList();
        var list = names.Count == 1 ? names[0] : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];
        var verb = names.Count > 1 || list.EndsWith('s') ? "were" : "was";
        return $"{list} {verb} not applied: this needs AXIS OS {DeviceConfigIds.MinFirmwareText} or later.";
    }

    /// <summary>Warning when the file has connection settings (off by default in the dialog).</summary>
    public const string ConnectionNote = "Connection settings (HTTPS, ports, login method, IEEE 802.1X, IPv6, DNS, network link, web interface) can make the devices unreachable.";

    /// <summary>Added to the confirmation when the connection settings are chosen.</summary>
    public const string UnreachableWarning = "The devices may become unreachable.";

    /// <summary>The plan of a file that is no longer among the uploads.</summary>
    public const string FileGone = "The configuration file is no longer on the server. Upload it again.";
}
