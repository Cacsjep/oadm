using System.Runtime.CompilerServices;

using Microsoft.Extensions.Logging;

using Oadm.Client.Api;
using Oadm.Client.Infrastructure;
using Oadm.Contracts.V1;

namespace Oadm.Client.Plugins;

/// <summary>
/// Cached result of TaskService.ListTaskPlugins. The runnable device ids depend on device state, so the
/// catalog is refreshed on connect and whenever the device table changes, throttled: at most one call per
/// <see cref="MinRefreshInterval"/>, the last request always answered. Scale: with 5,000 devices the
/// device table changes all the time; a debounce would starve, a call per change would load the server.
/// Runnable checks use a hash set per plugin (built once per refresh): O(selected) per plugin.
/// </summary>
public sealed partial class TaskPluginCatalog(IOadmApi api, IUiDispatcher ui, ILogger<TaskPluginCatalog> logger)
{
    private static readonly ConditionalWeakTable<TaskPluginInfo, RunnableSet> Sets = [];

    private readonly Lock _gate = new();
    private bool _scheduled;
    private DateTime _lastStartUtc = DateTime.MinValue;

    public IReadOnlyList<TaskPluginInfo> Plugins { get; private set; } = [];

    /// <summary>Raised on the UI thread after <see cref="Plugins"/> changed.</summary>
    public event EventHandler? Changed;

    /// <summary>Wait after the first request of a burst before the call, so the burst is answered by one call.</summary>
    public TimeSpan DebounceDelay { get; set; } = TimeSpan.FromMilliseconds(400);

    /// <summary>Minimum time between two calls started by <see cref="RequestRefresh"/>.</summary>
    public TimeSpan MinRefreshInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>ListTaskPlugins calls made (tests and diagnostics).</summary>
    public int RefreshCount { get; private set; }

    public async Task RefreshAsync(CancellationToken ct)
    {
        try
        {
            RefreshCount++;
            IReadOnlyList<TaskPluginInfo> plugins = await api.ListTaskPluginsAsync(ct).ConfigureAwait(false);
            foreach (TaskPluginInfo plugin in plugins)
            {
                _ = SetOf(plugin); // built off the UI thread
            }

            ui.Post(() =>
            {
                Plugins = plugins;
                Changed?.Invoke(this, EventArgs.Empty);
            });
        }
        catch (OperationCanceledException)
        {
            // superseded
        }
        catch (Exception ex)
        {
            LogRefreshFailed(logger, ex.Message);
        }
    }

    /// <summary>
    /// Coalesces bursts of device changes into one ListTaskPlugins call: a request while a call is already
    /// scheduled is covered by it; otherwise a call starts after <see cref="DebounceDelay"/>, but not
    /// earlier than <see cref="MinRefreshInterval"/> after the previous one.
    /// </summary>
    public void RequestRefresh()
    {
        TimeSpan wait;
        lock (_gate)
        {
            if (_scheduled)
            {
                return;
            }

            _scheduled = true;
            TimeSpan sinceLast = DateTime.UtcNow - _lastStartUtc;
            wait = sinceLast < MinRefreshInterval && MinRefreshInterval - sinceLast > DebounceDelay
                ? MinRefreshInterval - sinceLast
                : DebounceDelay;
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(wait).ConfigureAwait(false);
            lock (_gate)
            {
                // Requests from now on schedule the next call: nothing is lost.
                _scheduled = false;
                _lastStartUtc = DateTime.UtcNow;
            }

            await RefreshAsync(CancellationToken.None).ConfigureAwait(false);
        }, CancellationToken.None);
    }

    /// <summary>Plugins whose CanRun is true for every one of the given devices.</summary>
    public static IEnumerable<TaskPluginInfo> RunnableFor(IEnumerable<TaskPluginInfo> plugins, IReadOnlyCollection<string> deviceIds)
    {
        ArgumentNullException.ThrowIfNull(plugins);
        ArgumentNullException.ThrowIfNull(deviceIds);
        if (deviceIds.Count == 0)
        {
            return [];
        }

        return plugins.Where(p => SetOf(p).ContainsAll(deviceIds));
    }

    /// <summary>Whether the plugin can run on the device (full or compact runnable set).</summary>
    public static bool CanRun(TaskPluginInfo plugin, string deviceId)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        return SetOf(plugin).Contains(deviceId);
    }

    /// <summary>The runnable set of a plugin info, built once per instance (a refresh brings new instances).</summary>
    private static RunnableSet SetOf(TaskPluginInfo plugin) => Sets.GetValue(plugin, p => new RunnableSet(p));

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not load task plugins: {Reason}")]
    private static partial void LogRefreshFailed(ILogger logger, string reason);

    /// <summary>Hash set over runnable_device_ids, or over not_runnable_device_ids for the compact form.</summary>
    private sealed class RunnableSet
    {
        private readonly HashSet<string> _ids;
        private readonly bool _allExcept;

        public RunnableSet(TaskPluginInfo plugin)
        {
            _allExcept = plugin.RunnableOnAllExcept;
            _ids = new HashSet<string>(_allExcept ? plugin.NotRunnableDeviceIds : plugin.RunnableDeviceIds, StringComparer.Ordinal);
        }

        public bool Contains(string deviceId) => _ids.Contains(deviceId) != _allExcept;

        public bool ContainsAll(IReadOnlyCollection<string> deviceIds)
        {
            foreach (string id in deviceIds)
            {
                if (!Contains(id))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
