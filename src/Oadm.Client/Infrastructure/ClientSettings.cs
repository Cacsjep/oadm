using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Logging;

namespace Oadm.Client.Infrastructure;

/// <summary>Client-side settings, stored as JSON in the client data folder.</summary>
public sealed class ClientSettings
{
    /// <summary>Server of a new client; without a scheme the client connects over TLS (https).</summary>
    public const string DefaultServerAddress = "localhost:5080";

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

    /// <summary>The optional plugins dialog: "server/user" -> the state it was shown for (<c>AddOnNotice</c>).</summary>
    public Dictionary<string, string> AddOnNotices { get; set; } = [];

    /// <summary>Add page "Use DNS names instead of IP addresses" (Discovery and Network range).</summary>
    public bool AddByDnsName { get; set; }

    /// <summary>Texts plugins keep on this computer ("plugin id/key" -> value), e.g. a page's last device choice.</summary>
    public Dictionary<string, string> PluginSettings { get; set; } = [];

    /// <summary>Device grid group mode: a group per tag.</summary>
    public bool GroupDevicesByTag { get; set; }

    /// <summary>Device grid: one column per compliance check instead of the one Compliance column.</summary>
    public bool ExpandCompliance { get; set; }

    /// <summary>Compliance page: the result text next to each icon (else the icon only, the text on hover).</summary>
    public bool ComplianceShowText { get; set; } = true;

    /// <summary>Compliance page: a group per tag.</summary>
    public bool ComplianceGroupByTag { get; set; }

    /// <summary>Compliance page: the columns the user turned off (Model, Tags, Result, "check:id").</summary>
    public List<string> ComplianceHiddenColumns { get; set; } = [];

    /// <summary>Settings "Tell me about new versions": the new version dialog at client start; off with its "Don't tell me".</summary>
    public bool TellAboutNewVersions { get; set; } = true;

    /// <summary>Settings "Decode video on the graphics card" (live view); on by default, tested at every start.</summary>
    public bool GpuDecoding { get; set; } = true;

    /// <summary>Settings "Show video details over the picture" (codec, resolution, frame rate, bitrate, decoder).</summary>
    public bool ShowVideoDetails { get; set; }

    /// <summary>Settings "Frame rate (adaptive quality)", 1..30.</summary>
    public int AdaptiveFps { get; set; } = 20;

    /// <summary>Settings "Appearance": dark (default) or light; read once at the start (<see cref="JsonClientSettingsStore.ReadAppearance"/>).</summary>
    [JsonConverter(typeof(AppearanceJsonConverter))]
    public Oadm.Theme.Appearance Appearance { get; set; }

    /// <summary>Height of the tasks pane set with the splitter, in pixels. 0 = default.</summary>
    public double TasksPaneHeight { get; set; }

    /// <summary>Navigation rail shows labels (expanded, the default) or icons only (collapsed).</summary>
    public bool NavRailExpanded { get; set; } = true;

    /// <summary>Ids of "server key replaced" notices the user dismissed on this client.</summary>
    public List<string> DismissedKeyNotices { get; set; } = [];

    /// <summary>The version of the terms of use accepted on this computer (<c>TermsViewModel.CurrentVersion</c>); empty = not yet.</summary>
    public string AcceptedTerms { get; set; } = "";
}

/// <summary>
/// "Dark" / "Light" in the settings file; anything else (a number, a name of a later version) reads as Dark, so a hand-edited
/// or newer file never stops the client or resets the other settings.
/// </summary>
public sealed class AppearanceJsonConverter : JsonConverter<Oadm.Theme.Appearance>
{
    public override Oadm.Theme.Appearance Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            reader.Skip();
            return default;
        }

        return Enum.TryParse<Oadm.Theme.Appearance>(reader.GetString(), ignoreCase: true, out var value) && Enum.IsDefined(value)
            && !int.TryParse(reader.GetString(), out _)
            ? value
            : default;
    }

    public override void Write(Utf8JsonWriter writer, Oadm.Theme.Appearance value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.ToString());
    }
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
    private readonly TimeProvider _time;

    /// <summary>
    /// A temporary file younger than this may belong to another client that saves right now (two clients share the
    /// data folder): it stays at the start.
    /// </summary>
    internal static readonly TimeSpan LeftoverAge = TimeSpan.FromMinutes(1);

    public JsonClientSettingsStore(AppOptions options, ILogger<JsonClientSettingsStore> logger, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _path = Path.Combine(options.DataFolder, "client-settings.json");
        Current = Load();
    }

    public ClientSettings Current { get; }

    /// <summary>
    /// The appearance saved in the settings file of a data folder, read before the app loads its theme (the store itself
    /// is created later, with the services); dark when the file is missing or unreadable.
    /// </summary>
    public static Oadm.Theme.Appearance ReadAppearance(string dataFolder)
    {
        try
        {
            string path = Path.Combine(dataFolder, "client-settings.json");
            return File.Exists(path)
                ? JsonSerializer.Deserialize(File.ReadAllText(path), ClientSettingsJsonContext.Default.ClientSettings)?.Appearance ?? default
                : default;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return default;
        }
    }

    public void Save()
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                byte[] json = JsonSerializer.SerializeToUtf8Bytes(Current, ClientSettingsJsonContext.Default.ClientSettings);
                Replace(_path, stream => stream.Write(json));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogSaveFailed(_logger, ex, _path);
            }
        }
    }

    private const UnixFileMode OwnerReadWrite = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>The files an interrupted <see cref="Replace"/> leaves next to <paramref name="path"/>.</summary>
    private static string TempPattern(string path) => Path.GetFileName(path) + ".*.tmp";

    /// <summary>
    /// Writes the file as a whole or not at all: into a temporary file in the same folder, which then takes the place of
    /// <paramref name="path"/> (a crash or a full disk while writing leaves the old file, never half of the new one). The
    /// file holds session tokens and the server pins: on Linux and macOS it is created for its owner only (0600); on
    /// Windows it has the rights of the user's data folder.
    /// </summary>
    internal static void Replace(string path, Action<Stream> write)
    {
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = OwnerReadWrite;
            }

            using (var stream = new FileStream(temp, options))
            {
                write(stream);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Removed at a later start (Load).
            }

            throw;
        }
    }

    private ClientSettings Load()
    {
        try
        {
            RemoveLeftovers();
            if (File.Exists(_path))
            {
                OwnerOnly(_path);
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

    /// <summary>
    /// Linux and macOS: a settings file written by an older version (readable by others) becomes its owner's only, once at
    /// the start. A file that cannot be changed is still read.
    /// </summary>
    private void OwnerOnly(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            if (File.GetUnixFileMode(path) != OwnerReadWrite)
            {
                File.SetUnixFileMode(path, OwnerReadWrite);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogRightsFailed(_logger, ex, path);
        }
    }

    /// <summary>
    /// The temporary files of saves that never finished (the client was ended meanwhile): they hold the tokens too. Only
    /// those older than <see cref="LeftoverAge"/>: a younger one may be the save of another client, which would fail.
    /// </summary>
    private void RemoveLeftovers()
    {
        string? folder = Path.GetDirectoryName(_path);
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            return;
        }

        string[] leftovers;
        try
        {
            leftovers = Directory.GetFiles(folder, TempPattern(_path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return; // the settings are still read
        }

        DateTime oldest = _time.GetUtcNow().UtcDateTime - LeftoverAge;
        foreach (string leftover in leftovers)
        {
            try
            {
                if (File.GetLastWriteTimeUtc(leftover) < oldest)
                {
                    File.Delete(leftover);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // In use by another client that saves right now.
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not save client settings to {Path}")]
    private static partial void LogSaveFailed(ILogger logger, Exception ex, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not limit {Path} to its owner")]
    private static partial void LogRightsFailed(ILogger logger, Exception ex, string path);

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
