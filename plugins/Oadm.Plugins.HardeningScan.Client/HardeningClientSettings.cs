using System.Text.Json;

namespace Oadm.Plugins.HardeningScan.Client;

/// <summary>What the page remembers per client: the shown level, the detail height, column order and widths.</summary>
public sealed class HardeningClientSettings
{
    public ScanLevel Level { get; set; }

    public double DetailHeight { get; set; } = 240;

    /// <summary>Per column key (check id or a fixed column name): its width.</summary>
    public Dictionary<string, double> ColumnWidths { get; set; } = [];

    /// <summary>Column keys in display order (a key missing here keeps its default place).</summary>
    public List<string> ColumnOrder { get; set; } = [];
}

/// <summary>
/// <c>LocalApplicationData/Oadm/plugins/oadm.hardening-scan/client.json</c>. Failures are ignored (defaults): remembering is a
/// convenience only.
/// </summary>
public sealed class HardeningClientSettingsStore(string? path = null)
{
    public string Path { get; } = path ?? System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Oadm",
        "plugins",
        HardeningScanPluginInfo.PluginId,
        "client.json");

    public HardeningClientSettings Load()
    {
        try
        {
            return File.Exists(Path)
                ? JsonSerializer.Deserialize<HardeningClientSettings>(File.ReadAllText(Path), HardeningJson.Options) ?? new HardeningClientSettings()
                : new HardeningClientSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new HardeningClientSettings();
        }
    }

    public void Save(HardeningClientSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, JsonSerializer.Serialize(settings, HardeningJson.Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // convenience only
        }
    }
}
