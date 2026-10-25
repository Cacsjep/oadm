using Oadm.Core.Devices;
using Oadm.Core.Plugins;

namespace Oadm.Server.Tasks;

/// <summary>
/// Devices a task plugin can run on (CanRun true, in device-table order) and the others, grouped by the reason
/// (<see cref="Sdk.Plugins.ITaskPlugin.NotSupportedReason"/>): <see cref="NotRunnable"/> is the concatenation of
/// <see cref="Reasons"/> in their order (most devices first).
/// </summary>
public sealed record TaskPluginRunnable(
    RegisteredTaskPlugin Plugin,
    IReadOnlyList<Guid> Runnable,
    IReadOnlyList<Guid> NotRunnable,
    IReadOnlyList<NotRunnableReason> Reasons)
{
    public TaskPluginRunnable(RegisteredTaskPlugin plugin, IReadOnlyList<Guid> runnable, IReadOnlyList<Guid> notRunnable)
        : this(plugin, runnable, notRunnable, notRunnable.Count == 0 ? [] : [new NotRunnableReason(Sdk.Plugins.TaskSupportReasons.Default, notRunnable)])
    {
    }
}

/// <summary>Not-runnable devices of one task plugin that share a reason.</summary>
public sealed record NotRunnableReason(string Reason, IReadOnlyList<Guid> DeviceIds);

/// <summary>
/// Scale: ListTaskPlugins asks every task plugin's CanRun for every device (5,000 devices x N plugins)
/// and every client calls it after device changes. The result is computed once per device-table
/// version (<see cref="DeviceChangeFeed.Version"/>) and plugin set, and reused for at most
/// <see cref="MaxAge"/> (CanRun may also depend on state outside the device row, e.g. a core plugin).
/// Concurrent callers share one computation.
/// </summary>
public sealed partial class TaskPluginRunnableCache(
    PluginRegistry registry,
    DeviceRepository devices,
    DeviceChangeFeed feed,
    TimeProvider time,
    ILogger<TaskPluginRunnableCache> logger,
    Core.DeviceGroups.DeviceGroupStore? groups = null) : IDisposable
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(10);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _reasonFailuresLogged = new(StringComparer.Ordinal);
    private Entry? _entry;

    // Per group (never per "none:" key of a user without devices); a deleted group's entry is dropped.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Scoped> _scoped = new(StringComparer.Ordinal);
    private int _watchingGroups;

    /// <summary>Number of computations so far (tests and diagnostics).</summary>
    public int Computations { get; private set; }

    /// <summary>
    /// The runnable sets of <paramref name="view"/> (docs/spec/device-scope.md, Tasks): for a user with all devices the
    /// cached sets as they are; for a user with a device group every set and every reason intersected with the view's
    /// devices before anything is encoded, so no id outside the view leaves the server. Cached per group while neither the
    /// sets nor the view's devices changed.
    /// </summary>
    public async Task<IReadOnlyList<TaskPluginRunnable>> GetAsync(Core.DeviceGroups.DeviceView view, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(view);
        var all = await GetAsync(ct).ConfigureAwait(false);
        if (view.HasAllDevices)
        {
            return all;
        }

        var ids = await view.IdsAsync(ct).ConfigureAwait(false);
        var key = view.ScopeKey;
        if (_scoped.TryGetValue(key, out var cached) && ReferenceEquals(cached.Source, all) && ReferenceEquals(cached.Ids, ids))
        {
            return cached.Results;
        }

        IReadOnlyList<TaskPluginRunnable> results = [.. all.Select(entry => Within(entry, ids))];
        if (view.GroupId is not null)
        {
            // A user without devices has a key of its own: such keys are never kept (one per user would never go).
            WatchGroups();
            _scoped[key] = new Scoped(all, ids, results);
        }

        return results;
    }

    /// <summary>The cached groups now (tests).</summary>
    internal int ScopedCount => _scoped.Count;

    private void WatchGroups()
    {
        if (groups is not null && Interlocked.Exchange(ref _watchingGroups, 1) == 0)
        {
            groups.Deleted += OnGroupDeleted;
        }
    }

    private void OnGroupDeleted(object? sender, Core.DeviceGroups.DeviceGroupDeleteResult deleted) => _scoped.TryRemove(deleted.Id.ToString(), out _);

    /// <summary>The entry with only the devices of <paramref name="ids"/>; reasons keep "most devices first".</summary>
    internal static TaskPluginRunnable Within(TaskPluginRunnable entry, IReadOnlySet<Guid> ids)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(ids);
        var runnable = entry.Runnable.Where(ids.Contains).ToList();
        var reasons = entry.Reasons
            .Select(r => new NotRunnableReason(r.Reason, [.. r.DeviceIds.Where(ids.Contains)]))
            .Where(r => r.DeviceIds.Count > 0)
            .OrderByDescending(r => r.DeviceIds.Count)
            .ThenBy(r => r.Reason, StringComparer.Ordinal)
            .ToList();
        var notRunnable = new List<Guid>(reasons.Sum(r => r.DeviceIds.Count));
        foreach (var reason in reasons)
        {
            notRunnable.AddRange(reason.DeviceIds);
        }

        return new TaskPluginRunnable(entry.Plugin, runnable, notRunnable, reasons);
    }

    private sealed record Scoped(IReadOnlyList<TaskPluginRunnable> Source, IReadOnlySet<Guid> Ids, IReadOnlyList<TaskPluginRunnable> Results);

    /// <summary>The runnable sets of every menu task plugin (ShowInMenus), cached.</summary>
    public async Task<IReadOnlyList<TaskPluginRunnable>> GetAsync(CancellationToken ct)
    {
        var plugins = registry.TaskPlugins;
        if (IsFresh(_entry, plugins))
        {
            return _entry!.Results;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsFresh(_entry, plugins))
            {
                return _entry!.Results;
            }

            // Read the version before the devices: a change in between makes the next call recompute.
            var version = feed.Version;
            var at = time.GetUtcNow();
            var all = await devices.ListDevicesAsync(ct).ConfigureAwait(false);
            var results = new List<TaskPluginRunnable>(plugins.Count);
            foreach (var plugin in plugins)
            {
                if (!SafeShowInMenus(plugin))
                {
                    continue; // started by its core plugin's page only
                }

                var runnable = new List<Guid>(all.Count);
                var byReason = new Dictionary<string, List<Guid>>(StringComparer.Ordinal);
                var logged = false;
                foreach (var device in all)
                {
                    if (device.Status == Sdk.Devices.DeviceStatus.SiteOffline)
                    {
                        // The host refuses it whatever the plugin says (third-party plugins never see the status).
                        Add(byReason, Sdk.Sites.SiteTexts.SiteOffline(device.SiteName), device.Id);
                        continue;
                    }

                    if (plugin.SiteRefusal(device) is { } site)
                    {
                        // A package that does not work at sites: greyed for devices there, whatever CanRun says.
                        Add(byReason, site, device.Id);
                        continue;
                    }

                    if (SafeCanRun(plugin, device, ref logged))
                    {
                        runnable.Add(device.Id);
                        continue;
                    }

                    Add(byReason, SafeReason(plugin, device), device.Id);
                }

                var reasons = byReason
                    .OrderByDescending(r => r.Value.Count)
                    .ThenBy(r => r.Key, StringComparer.Ordinal)
                    .Select(r => new NotRunnableReason(r.Key, r.Value))
                    .ToList();
                var notRunnable = new List<Guid>(all.Count - runnable.Count);
                foreach (var group in reasons)
                {
                    notRunnable.AddRange(group.DeviceIds);
                }

                results.Add(new TaskPluginRunnable(plugin, runnable, notRunnable, reasons));
            }

            Computations++;
            _entry = new Entry(version, at, plugins, results);
            return results;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void Add(Dictionary<string, List<Guid>> byReason, string reason, Guid deviceId)
    {
        if (!byReason.TryGetValue(reason, out var ids))
        {
            byReason[reason] = ids = [];
        }

        ids.Add(deviceId);
    }

    private bool IsFresh(Entry? entry, IReadOnlyList<RegisteredTaskPlugin> plugins) =>
        entry is not null
        && entry.Version == feed.Version
        && time.GetUtcNow() - entry.At < MaxAge
        && entry.Plugins.SequenceEqual(plugins);

    private bool SafeShowInMenus(RegisteredTaskPlugin plugin)
    {
        try
        {
            return plugin.Plugin.ShowInMenus;
        }
#pragma warning disable CA1031 // A plugin with a throwing property must not break the menu.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogPluginInfoFailed(ex, plugin.Id);
            return false;
        }
    }

    /// <summary>A throwing CanRun counts as false; logged once per plugin and computation (not 5,000 times).</summary>
    private bool SafeCanRun(RegisteredTaskPlugin plugin, Device device, ref bool logged)
    {
        try
        {
            return plugin.Plugin.CanRun(device);
        }
#pragma warning disable CA1031 // Plugin code is untrusted.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            if (!logged)
            {
                logged = true;
                LogCanRunFailed(ex, plugin.Id, device.Id);
            }

            return false;
        }
    }

    /// <summary>The plugin's reason (default text when it gives none); a throwing plugin is logged once per server run.</summary>
    private string SafeReason(RegisteredTaskPlugin plugin, Device device)
    {
        var reason = Sdk.Plugins.TaskSupportReasons.Of(plugin.Plugin, device, out var error);
        if (error is not null && _reasonFailuresLogged.Add(plugin.Id))
        {
            LogReasonFailed(error, plugin.Id, device.Id);
        }

        return reason;
    }

    public void Dispose()
    {
        if (groups is not null && Volatile.Read(ref _watchingGroups) == 1)
        {
            groups.Deleted -= OnGroupDeleted;
        }

        _gate.Dispose();
    }

    private sealed record Entry(long Version, DateTimeOffset At, IReadOnlyList<RegisteredTaskPlugin> Plugins, IReadOnlyList<TaskPluginRunnable> Results);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Task plugin {PluginId} could not be described")]
    private partial void LogPluginInfoFailed(Exception ex, string pluginId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Task plugin {PluginId}: CanRun threw for device {DeviceId}")]
    private partial void LogCanRunFailed(Exception ex, string pluginId, Guid deviceId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Task plugin {PluginId}: NotSupportedReason threw for device {DeviceId}; the default text is shown (logged once)")]
    private partial void LogReasonFailed(Exception ex, string pluginId, Guid deviceId);
}
