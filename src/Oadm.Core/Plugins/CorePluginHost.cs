using System.Collections.Concurrent;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Core.Vapix;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Tasks;
using Oadm.Sdk.Vapix;

namespace Oadm.Core.Plugins;

public enum CorePluginState
{
    Stopped = 0,
    Starting = 1,
    Running = 2,
    Stopping = 3,
    Faulted = 4,
}

public sealed record CorePluginStatus(string PluginId, CorePluginState State, string? Error);

/// <summary>
/// Starts and stops the registered core plugins and routes UI-page calls (<see cref="InvokeAsync"/>).
/// A plugin that throws on start is marked <see cref="CorePluginState.Faulted"/>; the others keep running.
/// </summary>
public sealed partial class CorePluginHost : IAsyncDisposable
{
    private readonly PluginRegistry _registry;
    private readonly IDeviceRepository _devices;
    private readonly IVapixClientFactory _vapix;
    private readonly ITaskRunner _tasks;
    private readonly IPluginSettingsProvider _settings;
    private readonly ISecretProtector? _secrets;
    private readonly TrustAnchorRegistry? _trustAnchors;
    private readonly IDeviceEventStreams? _eventStreams;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly List<string> _startOrder = [];

    public CorePluginHost(
        PluginRegistry registry,
        IDeviceRepository devices,
        IVapixClientFactory vapix,
        ITaskRunner tasks,
        IPluginSettingsProvider settings,
        ILoggerFactory? loggerFactory = null,
        ISecretProtector? secrets = null,
        PluginEventHub? events = null,
        TrustAnchorRegistry? trustAnchors = null,
        IDeviceEventStreams? eventStreams = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(vapix);
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(settings);
        _registry = registry;
        _devices = devices;
        _vapix = vapix;
        _tasks = tasks;
        _settings = settings;
        _secrets = secrets;
        _trustAnchors = trustAnchors;
        _eventStreams = eventStreams;
        Events = events ?? new PluginEventHub();
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<CorePluginHost>();
    }

    /// <summary>Live events of the core plugins for their pages (<see cref="ICorePluginContext.Events"/>).</summary>
    public PluginEventHub Events { get; }

    public IReadOnlyList<CorePluginStatus> Statuses =>
        [.. _registry.CorePlugins.Select(c => GetStatus(c.Id))];

    public CorePluginStatus GetStatus(string pluginId)
    {
        return _entries.TryGetValue(pluginId, out var entry)
            ? new CorePluginStatus(pluginId, entry.State, entry.Error)
            : new CorePluginStatus(pluginId, CorePluginState.Stopped, null);
    }

    /// <summary>Starts every registered core plugin that is not running yet, in registration order.</summary>
    public async Task StartAllAsync(CancellationToken ct)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var registered in _registry.CorePlugins)
            {
                ct.ThrowIfCancellationRequested();
                var entry = _entries.GetOrAdd(registered.Id, _ => new Entry(registered.Plugin));
                if (entry.State is CorePluginState.Running)
                {
                    continue;
                }

                entry.State = CorePluginState.Starting;
                entry.Error = null;
                var context = new CorePluginContext(
                    _devices,
                    _vapix,
                    _tasks,
                    _settings.GetSettings(registered.Id),
                    _loggerFactory.CreateLogger("Oadm.Plugins." + registered.Id),
                    registered.Origin.Directory,
                    _secrets,
                    Events.For(registered.Id),
                    _trustAnchors?.For(registered.Id),
                    _eventStreams);
                try
                {
                    await registered.Plugin.StartAsync(context, ct).ConfigureAwait(false);
                    entry.State = CorePluginState.Running;
                    _startOrder.Remove(registered.Id);
                    _startOrder.Add(registered.Id);
                    LogStarted(registered.Id);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    entry.State = CorePluginState.Stopped;
                    throw;
                }
#pragma warning disable CA1031 // One faulty core plugin must not stop the others or the host.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    entry.State = CorePluginState.Faulted;
                    entry.Error = ex.Message;
                    LogStartFailed(ex, registered.Id);
                }
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>Stops running core plugins in reverse start order. Errors are logged, never thrown.</summary>
    public async Task StopAllAsync(CancellationToken ct)
    {
        await _lifecycle.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            for (var i = _startOrder.Count - 1; i >= 0; i--)
            {
                var id = _startOrder[i];
                if (!_entries.TryGetValue(id, out var entry) || entry.State != CorePluginState.Running)
                {
                    continue;
                }

                entry.State = CorePluginState.Stopping;
                try
                {
                    await entry.Plugin.StopAsync(ct).ConfigureAwait(false);
                    entry.State = CorePluginState.Stopped;
                    _trustAnchors?.Remove(id); // a stopped plugin's CAs are no longer vouched for
                    LogStopped(id);
                }
#pragma warning disable CA1031 // Keep stopping the remaining plugins.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    entry.State = CorePluginState.Faulted;
                    entry.Error = ex.Message;
                    LogStopFailed(ex, id);
                }
            }

            _startOrder.Clear();
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>
    /// Routes a UI-page call to a running core plugin.
    /// Throws <see cref="KeyNotFoundException"/> for an unknown plugin and
    /// <see cref="InvalidOperationException"/> when it is not running. Exceptions thrown by the
    /// plugin are logged and rethrown so the gRPC layer can map them to an error status.
    /// </summary>
    public async Task<string?> InvokeAsync(string pluginId, string method, string? payloadJson, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentException.ThrowIfNullOrWhiteSpace(method);

        if (!_registry.TryGetCorePlugin(pluginId, out var registered))
        {
            throw new KeyNotFoundException($"Unknown core plugin '{pluginId}'.");
        }

        if (!_entries.TryGetValue(registered.Id, out var entry) || entry.State != CorePluginState.Running)
        {
            throw new InvalidOperationException($"Core plugin '{pluginId}' is not running.");
        }

        try
        {
            return await entry.Plugin.InvokeAsync(method, payloadJson, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogInvokeFailed(ex, pluginId, method);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAllAsync(CancellationToken.None).ConfigureAwait(false);
        _lifecycle.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Core plugin {PluginId} started")]
    private partial void LogStarted(string pluginId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Core plugin {PluginId} stopped")]
    private partial void LogStopped(string pluginId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Core plugin {PluginId} failed to start")]
    private partial void LogStartFailed(Exception ex, string pluginId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Core plugin {PluginId} failed to stop")]
    private partial void LogStopFailed(Exception ex, string pluginId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Core plugin {PluginId} failed on {Method}")]
    private partial void LogInvokeFailed(Exception ex, string pluginId, string method);

    private sealed class Entry(ICorePlugin plugin)
    {
        public ICorePlugin Plugin { get; } = plugin;

        public volatile CorePluginState State;

        public volatile string? Error;
    }
}

internal sealed class CorePluginContext(
    IDeviceRepository devices,
    IVapixClientFactory vapix,
    ITaskRunner tasks,
    IPluginSettings settings,
    ILogger logger,
    string? pluginDirectory = null,
    ISecretProtector? secrets = null,
    IPluginEvents? events = null,
    ITrustAnchors? trustAnchors = null,
    IDeviceEventStreams? eventStreams = null) : ICorePluginContext
{
    public IDeviceRepository Devices { get; } = devices;

    public IVapixClientFactory Vapix { get; } = vapix;

    public ITaskRunner Tasks { get; } = tasks;

    public IPluginSettings Settings { get; } = settings;

    public ILogger Logger { get; } = logger;

    public string? PluginDirectory { get; } = pluginDirectory;

    public ISecretProtector? Secrets { get; } = secrets;

    public IPluginEvents? Events { get; } = events;

    public ITrustAnchors? TrustAnchors { get; } = trustAnchors;

    public IDeviceEventStreams? EventStreams { get; } = eventStreams;
}
