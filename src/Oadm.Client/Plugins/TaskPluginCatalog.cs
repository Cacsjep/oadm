using System.Runtime.CompilerServices;

using Microsoft.Extensions.Logging;

using Oadm.Client.Api;
using Oadm.Client.Infrastructure;
using Oadm.Contracts.V1;
using Oadm.Sdk.Plugins;

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

    /// <summary>
    /// Why the plugin cannot run on the given devices (tooltip of its greyed menu entry or toolbar button), or null
    /// when it can run on all of them (or none are given). One device: its reason ("Needs AXIS OS 11.11 or later (this
    /// device has 10.12.338)"). Several: the most common reason without device details and how many of the devices it
    /// concerns, plus how many other reasons there are ("Needs AXIS OS 11.11 or later: 3 of 5 selected devices (+1 other
    /// reason)"). O(devices) with one hash lookup each.
    /// </summary>
    public static string? NotRunnableReason(TaskPluginInfo plugin, IReadOnlyCollection<string> deviceIds)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        ArgumentNullException.ThrowIfNull(deviceIds);
        if (deviceIds.Count == 0)
        {
            return null;
        }

        RunnableSet set = SetOf(plugin);
        Dictionary<string, int>? counts = null;
        string? single = null;
        foreach (string id in deviceIds)
        {
            int index = set.ReasonIndex(id);
            if (index == RunnableSet.Runnable)
            {
                continue;
            }

            single = set.Reason(index);
            counts ??= new Dictionary<string, int>(StringComparer.Ordinal);
            string general = set.General(index);
            counts[general] = counts.GetValueOrDefault(general) + 1;
        }

        if (counts is null)
        {
            return null;
        }

        if (deviceIds.Count == 1)
        {
            return single;
        }

        KeyValuePair<string, int> top = counts
            .OrderByDescending(c => c.Value)
            .ThenBy(c => c.Key, StringComparer.Ordinal)
            .First();
        string text = $"{top.Key}: {top.Value} of {deviceIds.Count} selected devices";
        int others = counts.Count - 1;
        return others switch
        {
            0 => text,
            1 => text + " (+1 other reason)",
            _ => text + $" (+{others} other reasons)",
        };
    }

    /// <summary>The runnable set of a plugin info, built once per instance (a refresh brings new instances).</summary>
    private static RunnableSet SetOf(TaskPluginInfo plugin) => Sets.GetValue(plugin, p => new RunnableSet(p));

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not load task plugins: {Reason}")]
    private static partial void LogRefreshFailed(ILogger logger, string reason);

    /// <summary>
    /// Hash set over runnable_device_ids, or over not_runnable_device_ids for the compact form, plus the reason of every
    /// not-runnable device (not_runnable_groups, see tasks.proto) as an index into the few distinct reasons.
    /// </summary>
    private sealed class RunnableSet
    {
        public const int Runnable = -1;
        private const int NoReason = -2;

        private readonly HashSet<string> _ids;
        private readonly bool _allExcept;
        private readonly Dictionary<string, int> _reasonOf = new(StringComparer.Ordinal);
        private readonly List<string> _reasons = [];
        private readonly List<string> _general = [];
        private readonly int _otherReason = NoReason;

        public RunnableSet(TaskPluginInfo plugin)
        {
            _allExcept = plugin.RunnableOnAllExcept;
            _ids = new HashSet<string>(_allExcept ? plugin.NotRunnableDeviceIds : plugin.RunnableDeviceIds, StringComparer.Ordinal);
            int next = 0;
            foreach (NotRunnableGroup group in plugin.NotRunnableGroups)
            {
                int index = AddReason(group.Reason);
                if (_allExcept)
                {
                    // not_runnable_device_ids are sorted by group: this group takes the next `count` ids.
                    int end = Math.Min(plugin.NotRunnableDeviceIds.Count, next + Math.Max(0, group.Count));
                    for (; next < end; next++)
                    {
                        _reasonOf[plugin.NotRunnableDeviceIds[next]] = index;
                    }
                }
                else if (group.OtherDevices)
                {
                    _otherReason = index;
                }
                else
                {
                    foreach (string id in group.DeviceIds)
                    {
                        _reasonOf[id] = index;
                    }
                }
            }
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

        /// <summary>Index of the device's reason, <see cref="Runnable"/> when the plugin can run on it.</summary>
        public int ReasonIndex(string deviceId)
        {
            if (Contains(deviceId))
            {
                return Runnable;
            }

            return _reasonOf.TryGetValue(deviceId, out int index) ? index : _otherReason;
        }

        /// <summary>The full reason; the default text when the server sent none (older servers).</summary>
        public string Reason(int index) => index >= 0 ? _reasons[index] : TaskSupportReasons.Default;

        /// <summary>The reason without its device detail.</summary>
        public string General(int index) => index >= 0 ? _general[index] : TaskSupportReasons.Default;

        private int AddReason(string reason)
        {
            string text = string.IsNullOrWhiteSpace(reason) ? TaskSupportReasons.Default : reason;
            _reasons.Add(text);
            _general.Add(TaskSupportReasons.General(text));
            return _reasons.Count - 1;
        }
    }
}
