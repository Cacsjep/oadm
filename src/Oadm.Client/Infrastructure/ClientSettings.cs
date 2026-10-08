using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Logging;

namespace Oadm.Client.Infrastructure;

/// <summary>Client-side settings, stored as JSON in the client data folder.</summary>
public sealed class ClientSettings
{
    /// <summary>Server of a new client; without a scheme the client connects over TLS (https).</summary>
    public const string DefaultServerAddress = "localhost:5080";

    /// <summary>The client's address before TLS; read as <see cref="DefaultServerAddress"/>.</summary>
    public const string LegacyDefaultServerAddress = "http://localhost:5080";

    /// <summary>Most entries of <see cref="RecentServers"/>.</summary>
    public const int MaxRecentServers = 10;

    /// <summary>Server of the last login (the login window preselects it; <c>--server</c> overrides it).</summary>
    public string ServerAddress { get; set; } = DefaultServerAddress;

    /// <summary>Servers logged in to, newest first (login window list).</summary>
    public List<string> RecentServers { get; set; } = [];

    /// <summary>Pinned server certificates: "host:port" -> SHA-256 fingerprint (trust on first use).</summary>
    public Dictionary<string, string> PinnedServers { get; set; } = [];

    /// <summary>"Remember me": "host:port" -> user name and session token (never the password).</summary>
    public Dictionary<string, RememberedLogin> RememberedLogins { get; set; } = [];

    /// <summary>Device grid column layout, in display order.</summary>
    public List<ColumnLayoutEntry> DeviceColumns { get; set; } = [];

    public bool BottomPaneExpanded { get; set; } = true;

    /// <summary>Height of the tasks pane set with the splitter, in pixels. 0 = default.</summary>
    public double TasksPaneHeight { get; set; }

    /// <summary>Navigation rail shows labels (expanded, the default; user decision 2026-10-08) or icons only (collapsed).</summary>
    public bool NavRailExpanded { get; set; } = true;
}

/// <summary>A remembered session of one server.</summary>
public sealed class RememberedLogin
{
    public string UserName { get; set; } = "";

    public string Token { get; set; } = "";
}

/// <summary>Pinned server certificates in the client settings file.</summary>
public sealed class ClientPinStore(IClientSettingsStore settings) : Oadm.Contracts.Security.IServerPinStore
{
    private readonly Lock _gate = new();

    public string? GetPin(string serverKey)
    {
        lock (_gate)
        {
            return settings.Current.PinnedServers.TryGetValue(serverKey, out string? pin) ? pin : null;
        }
    }

    public void SetPin(string serverKey, string fingerprint)
    {
        lock (_gate)
        {
            settings.Current.PinnedServers[serverKey] = fingerprint;
        }

        settings.Save();
    }

    public void RemovePin(string serverKey)
    {
        lock (_gate)
        {
            settings.Current.PinnedServers.Remove(serverKey);
            settings.Current.RememberedLogins.Remove(serverKey);
        }

        settings.Save();
    }
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
                ClientSettings loaded = JsonSerializer.Deserialize(File.ReadAllText(_path), ClientSettingsJsonContext.Default.ClientSettings)
                    ?? new ClientSettings();
                if (string.Equals(loaded.ServerAddress, ClientSettings.LegacyDefaultServerAddress, StringComparison.OrdinalIgnoreCase))
                {
                    loaded.ServerAddress = ClientSettings.DefaultServerAddress;
                }

                return loaded;
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
