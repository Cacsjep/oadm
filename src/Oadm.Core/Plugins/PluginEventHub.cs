using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Sdk.Plugins;

namespace Oadm.Core.Plugins;

/// <summary>
/// Fan-out of core plugin live events (<see cref="IPluginEvents"/>) to the watchers of gRPC <c>PluginService.Watch</c>.
/// Every watcher has its own bounded queue (<see cref="QueueCapacity"/>, oldest dropped), so a slow client never
/// blocks a plugin or another client. Events are not stored: a watcher sees the events published after it subscribed.
/// </summary>
public sealed partial class PluginEventHub
{
    /// <summary>Events buffered per watcher before the oldest are dropped.</summary>
    public const int QueueCapacity = 256;

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Channel<PluginEvent>, byte>> _watchers =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ILogger _logger;

    public PluginEventHub(ILogger<PluginEventHub>? logger = null)
    {
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>Number of active watchers of a plugin (tests, diagnostics).</summary>
    public int WatcherCount(string pluginId) => _watchers.TryGetValue(pluginId, out var set) ? set.Count : 0;

    /// <summary>The publisher handed to the plugin through <see cref="ICorePluginContext.Events"/>.</summary>
    public IPluginEvents For(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        return new Publisher(this, pluginId);
    }

    public void Publish(string pluginId, string topic, string? payloadJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        if (payloadJson is { Length: > IPluginEvents.MaxPayloadLength })
        {
            LogTooLarge(pluginId, topic, payloadJson.Length);
            return;
        }

        if (!_watchers.TryGetValue(pluginId, out var set) || set.IsEmpty)
        {
            return;
        }

        var item = new PluginEvent(topic, payloadJson);
        foreach (var channel in set.Keys)
        {
            channel.Writer.TryWrite(item);
        }
    }

    /// <summary>Streams the events of one plugin until <paramref name="ct"/> is cancelled.</summary>
    public async IAsyncEnumerable<PluginEvent> WatchAsync(string pluginId, [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        var channel = Channel.CreateBounded<PluginEvent>(new BoundedChannelOptions(QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
        var set = _watchers.GetOrAdd(pluginId, _ => new ConcurrentDictionary<Channel<PluginEvent>, byte>());
        set[channel] = 0;
        try
        {
            while (await channel.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (channel.Reader.TryRead(out var item))
                {
                    yield return item;
                }
            }
        }
        finally
        {
            set.TryRemove(channel, out _);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Core plugin {PluginId} event {Topic} dropped: {Length} characters is too large")]
    private partial void LogTooLarge(string pluginId, string topic, int length);

    private sealed class Publisher(PluginEventHub hub, string pluginId) : IPluginEvents
    {
        public void Publish(string topic, string? payloadJson) => hub.Publish(pluginId, topic, payloadJson);
    }
}
