using System.Security.Cryptography;
using System.Text.Json;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.SiteNode.Admin;

namespace Oadm.SiteNode.Config;

/// <summary>
/// Reads and writes node.json. Every change goes through <see cref="Update"/> (one lock, saved at once, then
/// <see cref="Changed"/>); readers get a copy.
/// </summary>
public sealed partial class NodeStore
{
    public const string FileName = "node.json";

    private static readonly JsonSerializerOptions Json = new(AdminJson.Options) { WriteIndented = true };

    private readonly Lock _lock = new();
    private readonly string _path;
    private readonly ILogger _logger;
    private NodeConfig _config;

    public NodeStore(string dataDirectory, ILogger<NodeStore>? logger = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataDirectory);
        Directory.CreateDirectory(dataDirectory);
        _path = Path.Combine(dataDirectory, FileName);
        _logger = logger ?? NullLogger<NodeStore>.Instance;
        _config = Load();
        if (string.IsNullOrEmpty(_config.Id))
        {
            _config.Id = NewId();
            Save(_config);
        }
    }

    /// <summary>Raised after every change (outside the lock).</summary>
    public event Action? Changed;

    /// <summary>A copy of the current configuration.</summary>
    public NodeConfig Snapshot()
    {
        lock (_lock)
            return Clone(_config);
    }

    /// <summary>Reads one value under the lock (no copy).</summary>
    public T Read<T>(Func<NodeConfig, T> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        lock (_lock)
            return read(_config);
    }

    /// <summary>Changes the configuration, saves it and raises <see cref="Changed"/>. <paramref name="change"/> returns false for no change.</summary>
    public bool TryUpdate(Func<NodeConfig, bool> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_lock)
        {
            var copy = Clone(_config);
            if (!change(copy))
                return false;
            Save(copy);
            _config = copy;
        }

        Changed?.Invoke();
        return true;
    }

    public void Update(Action<NodeConfig> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        TryUpdate(c =>
        {
            change(c);
            return true;
        });
    }

    public static string NewId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();

    // Absent = first start. A file that cannot be read fails the start (DataFiles); only a file that reads but is not a
    // node.json (damaged content) is kept aside as .unreadable and replaced by defaults.
    private NodeConfig Load()
    {
        if (!DataFiles.Exists(_path))
            return new NodeConfig();
        var bytes = DataFiles.ReadAllBytes(_path);
        try
        {
            return JsonSerializer.Deserialize<NodeConfig>(bytes, Json) ?? new NodeConfig();
        }
        catch (JsonException e)
        {
            LogUnreadable(_logger, e, _path);
            File.WriteAllBytes(_path + ".unreadable", bytes);
            return new NodeConfig();
        }
    }

    private void Save(NodeConfig config) => DataFiles.WriteDurable(_path, JsonSerializer.SerializeToUtf8Bytes(config, Json));

    private static NodeConfig Clone(NodeConfig config) =>
        JsonSerializer.Deserialize<NodeConfig>(JsonSerializer.SerializeToUtf8Bytes(config, Json), Json)!;

    [LoggerMessage(Level = LogLevel.Error, Message = "{Path} is damaged; the site node starts with defaults (a copy is kept as .unreadable)")]
    private static partial void LogUnreadable(ILogger logger, Exception error, string path);
}
