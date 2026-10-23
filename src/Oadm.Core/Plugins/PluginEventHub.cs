using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Sdk.Plugins;

namespace Oadm.Core.Plugins;

/// <summary>
/// Who watches a plugin's events (gRPC <c>PluginService.Watch</c>): the user, the session, the device scope key, whether the
/// user has all devices, and (for a user with a device group) the ids of the devices the user can see now.
/// </summary>
/// <param name="VisibleIds">The ids of the user's view (cached by the view per device table version); unused with all devices.</param>
public sealed record PluginWatcher(string UserName, string SessionId, string ScopeKey, bool HasAllDevices, Func<CancellationToken, Task<IReadOnlySet<Guid>>> VisibleIds)
{
    /// <summary>The host's own watcher (tests, diagnostics): every event an all-devices user would get.</summary>
    public static PluginWatcher System { get; } = new("", "", ICallContext.AllDevicesScopeKey, true, _ => Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>()));

    /// <summary>
    /// The ids of the user's view when they are at hand without a read (the view's cache is current), else null: the hub
    /// leaves out device events of other devices before it queues them, so they never push the user's own events out of the
    /// queue. The check when the watcher reads stays.
    /// </summary>
    public Func<IReadOnlySet<Guid>?>? CachedIds { get; init; }

    /// <summary>The watcher of a user's call.</summary>
    public static PluginWatcher Of(PluginCaller caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var view = caller.View;
        return new PluginWatcher(caller.Caller.UserName, caller.Caller.SessionId, view.ScopeKey, caller.HasAllDevices, view.IdsAsync)
        {
            CachedIds = view.CachedIds,
        };
    }
}

/// <summary>
/// Fan-out of core plugin live events (<see cref="IPluginEvents"/>) to the watchers of gRPC <c>PluginService.Watch</c>.
/// Every watcher has its own bounded queue (<see cref="QueueCapacity"/>, oldest dropped), so a slow client never
/// blocks a plugin or another client. Events are not stored: a watcher sees the events published after it subscribed.
/// Each event goes only to the watchers its audience covers (docs/spec/device-scope.md, Plugins): the old
/// <c>Publish(topic, json)</c> and <see cref="PluginEventAudience.AllDevices"/> to watchers with all devices, a device or
/// device rows to the users who can see them (rows filtered per watcher, in order), a user, a session or a device scope
/// key to their watchers. The device checks of a group watcher run in its own reading loop; the publisher only leaves out
/// device events of other devices when the watcher's ids are cached (never a read in the publisher).
/// </summary>
public sealed partial class PluginEventHub
{
    /// <summary>Events buffered per watcher before the oldest are dropped.</summary>
    public const int QueueCapacity = 256;

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Subscription, byte>> _watchers =
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

    /// <summary>The old publish: only watchers with all devices receive it.</summary>
    public void Publish(string pluginId, string topic, string? payloadJson) =>
        Publish(pluginId, topic, payloadJson, PluginEventAudience.AllDevices);

    /// <summary>Sends the event to the watchers <paramref name="audience"/> covers; never blocks.</summary>
    public void Publish(string pluginId, string topic, string? payloadJson, PluginEventAudience audience)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        ArgumentNullException.ThrowIfNull(audience);
        if (payloadJson is { Length: > IPluginEvents.MaxPayloadLength })
        {
            LogTooLarge(pluginId, topic, payloadJson.Length);
            return;
        }

        if (!_watchers.TryGetValue(pluginId, out var set) || set.IsEmpty)
        {
            return;
        }

        var item = new Queued(new PluginEvent(topic, payloadJson), audience);
        foreach (var subscription in set.Keys)
        {
            if (Reaches(audience, subscription.Watcher))
            {
                subscription.Channel.Writer.TryWrite(item);
            }
        }
    }

    /// <summary>
    /// Whether the event can reach the watcher: true for watchers it covers by user, session, scope and for watchers with all
    /// devices; device audiences of group watchers are checked against the view's cached ids when they are at hand (no read
    /// in the publisher) and always again when the watcher reads.
    /// </summary>
    private static bool Reaches(PluginEventAudience audience, PluginWatcher watcher) => audience.Kind switch
    {
        PluginEventAudienceKind.AllDevices => watcher.HasAllDevices,
        PluginEventAudienceKind.Device or PluginEventAudienceKind.DeviceRows =>
            watcher.HasAllDevices || watcher.CachedIds?.Invoke() is not { } ids || audience.DeviceIds.Any(ids.Contains),
        PluginEventAudienceKind.User => string.Equals(audience.UserName, watcher.UserName, StringComparison.OrdinalIgnoreCase),
        PluginEventAudienceKind.Session => string.Equals(audience.SessionId, watcher.SessionId, StringComparison.Ordinal),
        PluginEventAudienceKind.DeviceScope => string.Equals(audience.ScopeKey, watcher.ScopeKey, StringComparison.OrdinalIgnoreCase),
        _ => false, // an audience this host does not know: nobody (fail closed)
    };

    /// <summary>
    /// Streams every event of one plugin, as a watcher with all devices sees them: for in-process host work and tests.
    /// Never for a user: <c>PluginService.Watch</c> passes the caller's <see cref="PluginWatcher"/>.
    /// </summary>
    public IAsyncEnumerable<PluginEvent> WatchAsync(string pluginId, CancellationToken ct) => WatchAsync(pluginId, PluginWatcher.System, ct);

    /// <summary>Streams the events of one plugin <paramref name="watcher"/> may see until <paramref name="ct"/> is cancelled.</summary>
    public async IAsyncEnumerable<PluginEvent> WatchAsync(string pluginId, PluginWatcher watcher, [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentNullException.ThrowIfNull(watcher);
        var subscription = new Subscription(watcher, Channel.CreateBounded<Queued>(new BoundedChannelOptions(QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        }));
        var set = _watchers.GetOrAdd(pluginId, _ => new ConcurrentDictionary<Subscription, byte>());
        set[subscription] = 0;
        try
        {
            var reader = subscription.Channel.Reader;
            while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (reader.TryRead(out var item))
                {
                    var shown = watcher.HasAllDevices ? item.Event : await ForGroupWatcherAsync(pluginId, item, watcher, ct).ConfigureAwait(false);
                    if (shown is not null)
                    {
                        yield return shown;
                    }
                }
            }
        }
        finally
        {
            set.TryRemove(subscription, out _);
        }
    }

    /// <summary>The event as a watcher with a device group sees it: a device it can see, its rows of a row event, else nothing.</summary>
    private async Task<PluginEvent?> ForGroupWatcherAsync(string pluginId, Queued item, PluginWatcher watcher, CancellationToken ct)
    {
        var audience = item.Audience;
        switch (audience.Kind)
        {
            case PluginEventAudienceKind.Device:
                var ids = await watcher.VisibleIds(ct).ConfigureAwait(false);
                return audience.DeviceIds.Count == 1 && ids.Contains(audience.DeviceIds[0]) ? item.Event : null;
            case PluginEventAudienceKind.DeviceRows:
                return FilterRows(pluginId, item.Event, audience.DeviceIds, await watcher.VisibleIds(ct).ConfigureAwait(false));
            case PluginEventAudienceKind.AllDevices:
                return null;
            default:
                return item.Event; // user, session, scope: checked when queued
        }
    }

    /// <summary>
    /// The elements of a JSON array whose device <paramref name="visible"/> holds, in order; null when none is visible or the
    /// payload is not an array with one element per device (logged: the plugin's mistake, nothing is sent).
    /// </summary>
    private PluginEvent? FilterRows(string pluginId, PluginEvent item, IReadOnlyList<Guid> rowDevices, IReadOnlySet<Guid> visible)
    {
        JsonArray? rows = null;
        try
        {
            rows = item.PayloadJson is null ? null : JsonNode.Parse(item.PayloadJson) as JsonArray;
        }
        catch (JsonException)
        {
        }

        if (rows is null || rows.Count != rowDevices.Count)
        {
            LogBadRows(pluginId, item.Topic, rowDevices.Count);
            return null;
        }

        var kept = new JsonArray();
        for (var i = 0; i < rowDevices.Count; i++)
        {
            if (visible.Contains(rowDevices[i]))
            {
                kept.Add(rows[i]?.DeepClone());
            }
        }

        return kept.Count == 0 ? null : item with { PayloadJson = kept.ToJsonString() };
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Core plugin {PluginId} event {Topic} dropped: {Length} characters is too large")]
    private partial void LogTooLarge(string pluginId, string topic, int length);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Core plugin {PluginId} event {Topic} dropped for users with a device group: the payload is not a JSON array of {Count} rows")]
    private partial void LogBadRows(string pluginId, string topic, int count);

    private sealed record Queued(PluginEvent Event, PluginEventAudience Audience);

    private sealed class Subscription(PluginWatcher watcher, Channel<Queued> channel)
    {
        public PluginWatcher Watcher { get; } = watcher;

        public Channel<Queued> Channel { get; } = channel;
    }

    private sealed class Publisher(PluginEventHub hub, string pluginId) : IPluginEvents
    {
        public void Publish(string topic, string? payloadJson) => hub.Publish(pluginId, topic, payloadJson);

        public void Publish(string topic, string? payloadJson, PluginEventAudience audience) => hub.Publish(pluginId, topic, payloadJson, audience);
    }
}
