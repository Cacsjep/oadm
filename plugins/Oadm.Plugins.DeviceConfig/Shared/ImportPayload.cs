using System.Text.Json;
using System.Text.Json.Serialization;

namespace Oadm.Plugins.DeviceConfig;

/// <summary>
/// Payload of "Import configuration". The dialog uploads the chosen file (one <c>.json</c> or the ZIP of an export) and
/// names, per device, the file it gets: the empty name for a single file, else the ZIP entry. Devices the dialog left out
/// (other model, no file) carry their reason; their task changes nothing.
/// </summary>
public sealed class ImportPayload
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

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

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    /// <summary>Throws <see cref="ArgumentException"/> for a missing or broken payload.</summary>
    public static ImportPayload Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new ArgumentException("The import needs a configuration file.", nameof(json));
        }

        ImportPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<ImportPayload>(json, Options);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("The import payload is not valid.", nameof(json), ex);
        }

        if (payload is null || string.IsNullOrWhiteSpace(payload.FileId) || payload.Sections is not { Count: > 0 })
        {
            throw new ArgumentException("The import needs a configuration file and at least one settings area.", nameof(json));
        }

        return payload.Files is null || payload.LeftOut is null
            ? new ImportPayload
            {
                FileId = payload.FileId,
                FileName = payload.FileName,
                IsZip = payload.IsZip,
                Sections = payload.Sections,
                Files = payload.Files ?? new Dictionary<Guid, string>(),
                LeftOut = payload.LeftOut ?? new Dictionary<Guid, string>(),
            }
            : payload;
    }

    /// <summary>"Import configuration lobby.json".</summary>
    public string TaskName() =>
        string.IsNullOrWhiteSpace(FileName) ? DeviceConfigIds.ImportName : DeviceConfigIds.ImportName + " " + FileName.Trim();
}
