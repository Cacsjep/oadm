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
    ILogger<TaskPluginRunnableCache> logger) : IDisposable
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(10);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _reasonFailuresLogged = new(StringComparer.Ordinal);
    private Entry? _entry;

    /// <summary>Number of computations so far (tests and diagnostics).</summary>
    public int Computations { get; private set; }

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
                    if (SafeCanRun(plugin, device, ref logged))
                    {
                        runnable.Add(device.Id);
                        continue;
                    }

                    var reason = SafeReason(plugin, device);
                    if (!byReason.TryGetValue(reason, out var ids))
                    {
                        byReason[reason] = ids = [];
                    }

                    ids.Add(device.Id);
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

    public void Dispose() => _gate.Dispose();

    private sealed record Entry(long Version, DateTimeOffset At, IReadOnlyList<RegisteredTaskPlugin> Plugins, IReadOnlyList<TaskPluginRunnable> Results);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Task plugin {PluginId} could not be described")]
    private partial void LogPluginInfoFailed(Exception ex, string pluginId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Task plugin {PluginId}: CanRun threw for device {DeviceId}")]
    private partial void LogCanRunFailed(Exception ex, string pluginId, Guid deviceId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Task plugin {PluginId}: NotSupportedReason threw for device {DeviceId}; the default text is shown (logged once)")]
    private partial void LogReasonFailed(Exception ex, string pluginId, Guid deviceId);
}
