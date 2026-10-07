using System.Text.Json;

namespace Oadm.Plugins.SnapshotReport.Client;

/// <summary>What the export dialog remembers per client.</summary>
public sealed class ExportSettings
{
    public string Site { get; set; } = string.Empty;
    public string Technician { get; set; } = string.Empty;

    /// <summary>Folder of the last saved report (suggested start location).</summary>
    public string? LastFolder { get; set; }
}

/// <summary>
/// Remembers the export dialog fields per client in
/// <c>LocalApplicationData/Oadm/plugins/oadm.snapshot-report/export.json</c>. Failures are ignored (defaults).
/// </summary>
public sealed class ExportSettingsStore(string? path = null)
{
    public string Path { get; } = path ?? System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Oadm",
        "plugins",
        SnapshotReportPluginInfo.PluginId,
        "export.json");

    public ExportSettings Load()
    {
        try
        {
            return File.Exists(Path)
                ? JsonSerializer.Deserialize<ExportSettings>(File.ReadAllText(Path), SnapshotReportJson.Options) ?? new ExportSettings()
                : new ExportSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new ExportSettings();
        }
    }

    public void Save(ExportSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, JsonSerializer.Serialize(settings, SnapshotReportJson.Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Remembering is a convenience only.
        }
    }
}
