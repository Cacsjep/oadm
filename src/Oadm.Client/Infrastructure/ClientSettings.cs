using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Logging;

namespace Oadm.Client.Infrastructure;

/// <summary>Client-side settings, stored as JSON in the client data folder.</summary>
public sealed class ClientSettings
{
    public const string DefaultServerAddress = "http://localhost:5080";

    public string ServerAddress { get; set; } = DefaultServerAddress;

    /// <summary>Device grid column layout, in display order.</summary>
    public List<ColumnLayoutEntry> DeviceColumns { get; set; } = [];

    public bool BottomPaneExpanded { get; set; } = true;

    /// <summary>Height of the tasks pane set with the splitter, in pixels. 0 = default.</summary>
    public double TasksPaneHeight { get; set; }

    /// <summary>Navigation rail shows labels (expanded, the default; user decision 2026-10-08) or icons only (collapsed).</summary>
    public bool NavRailExpanded { get; set; } = true;

    /// <summary>Ids of "server key replaced" notices the user dismissed on this client.</summary>
    public List<string> DismissedKeyNotices { get; set; } = [];
}

public sealed class ColumnLayoutEntry
{
    public string Key { get; set; } = "";
    public bool IsVisible { get; set; } = true;
    /// <summary>Pixel width, 0 = default.</summary>
    public double Width { get; set; }
    public int DisplayIndex { get; set; }
}

public interface IClientSettingsStore
{
    ClientSettings Current { get; }
    void Save();
}

[JsonSerializable(typeof(ClientSettings))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal sealed partial class ClientSettingsJsonContext : JsonSerializerContext;

public sealed partial class JsonClientSettingsStore : IClientSettingsStore
{
    private readonly string _path;
    private readonly ILogger<JsonClientSettingsStore> _logger;
    private readonly Lock _gate = new();

    public JsonClientSettingsStore(AppOptions options, ILogger<JsonClientSettingsStore> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger;
        _path = Path.Combine(options.DataFolder, "client-settings.json");
        Current = Load();
    }

    public ClientSettings Current { get; }

    public void Save()
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                string json = JsonSerializer.Serialize(Current, ClientSettingsJsonContext.Default.ClientSettings);
                File.WriteAllText(_path, json);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogSaveFailed(_logger, ex, _path);
            }
        }
    }

    private ClientSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                return JsonSerializer.Deserialize(File.ReadAllText(_path), ClientSettingsJsonContext.Default.ClientSettings)
                    ?? new ClientSettings();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            LogLoadFailed(_logger, ex, _path);
        }

        return new ClientSettings();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not save client settings to {Path}")]
    private static partial void LogSaveFailed(ILogger logger, Exception ex, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not load client settings from {Path}, using defaults")]
    private static partial void LogLoadFailed(ILogger logger, Exception ex, string path);
}

/// <summary>Non-persistent settings store for tests and the fake mode preview.</summary>
public sealed class InMemoryClientSettingsStore : IClientSettingsStore
{
    public ClientSettings Current { get; } = new();
    public int SaveCount { get; private set; }

    public void Save() => SaveCount++;
}
