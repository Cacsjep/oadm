using System.Collections.Concurrent;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Core.Auth;
using Oadm.Core.Vapix;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Network;
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

/// <summary>Who may call a core plugin page method and whether the call goes to the audit log.</summary>
/// <param name="RequiredRole">The plugin's own answer (<see cref="ICorePlugin.RequiredRole"/>).</param>
public sealed record CorePluginMethodAccess(UserRole RequiredRole, bool Audited, string PluginName)
{
    /// <summary>The permission key the method needs ("oadm.dhcp-server:manage"); null with <see cref="AdminOnly"/> false: a login.</summary>
    public string? Permission { get; init; }

    /// <summary>Only the Administrator role (a plugin that names no usable permission for an Admin method, or that throws).</summary>
    public bool AdminOnly { get; init; }
}

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
    private readonly IFirewallRules? _firewall;
    private readonly string? _pluginDataRoot;
    private readonly IDeviceAutoAdd? _autoAdd;
    private readonly Func<string, Sdk.Events.IEventPublisher>? _eventPublishers;
    private readonly Func<string, Sdk.Events.IEventSubscriptions>? _eventSubscriptions;
    private readonly Func<Sites.PluginNetworkScope>? _networks;
    private readonly PluginServiceRegistry? _services;
    private readonly HostDeviceScopes? _deviceScopes;
    private readonly Devices.ProtectedTagRegistry? _protectedTags;
    private readonly DeviceGroups.DeviceViewWatcher? _viewWatcher;
    private readonly CallTaskOwners _taskOwners = new();
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
        IDeviceEventStreams? eventStreams = null,
        IFirewallRules? firewall = null,
        string? pluginDataRoot = null,
        IDeviceAutoAdd? autoAdd = null,
        Func<string, Sdk.Events.IEventPublisher>? eventPublishers = null,
        Func<string, Sdk.Events.IEventSubscriptions>? eventSubscriptions = null,
        Func<Sites.PluginNetworkScope>? networks = null,
        PluginServiceRegistry? services = null,
        PluginJobRegistry? jobs = null,
        HostDeviceScopes? deviceScopes = null,
        DeviceGroups.DeviceViewWatcher? viewWatcher = null,
        Devices.ProtectedTagRegistry? protectedTags = null)
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
        _firewall = firewall;
        _pluginDataRoot = pluginDataRoot;
        _autoAdd = autoAdd;
        _eventPublishers = eventPublishers;
        _eventSubscriptions = eventSubscriptions;
        _networks = networks;
        _services = services;
        _deviceScopes = deviceScopes;
        _viewWatcher = viewWatcher;
        _protectedTags = protectedTags;
        Jobs = jobs ?? new PluginJobRegistry();
        Events = events ?? new PluginEventHub();
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<CorePluginHost>();
    }

    /// <summary>Live events of the core plugins for their pages (<see cref="ICorePluginContext.Events"/>).</summary>
    public PluginEventHub Events { get; }

    /// <summary>The jobs the core plugins started for their users (<see cref="ICorePluginContext.Jobs"/>).</summary>
    public PluginJobRegistry Jobs { get; }

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
        if (_deviceScopes is not null)
        {
            await _deviceScopes.LoadAsync(ct).ConfigureAwait(false);
        }

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
                entry.Subscriptions?.Dispose();
                DisposePro(entry);
                entry.Subscriptions = _eventSubscriptions is null ? null : new PluginEventSubscriptions(_eventSubscriptions(registered.Id));
                await DisposeNetworkAsync(entry).ConfigureAwait(false);
                entry.Network = _networks?.Invoke();
                var siteSupport = registered.Origin.SiteSupport;
                var guard = new PluginCallGuard(registered.Id, _loggerFactory.CreateLogger("Oadm.Plugins." + registered.Id));
                entry.CallVapix = SiteSupportGuard.Guard(_vapix, _devices, siteSupport);
                entry.CallEventStreams = SiteSupportGuard.Guard(_eventStreams, _devices, siteSupport);

                // The context's full accesses refuse during the calls of users with a device group (PluginCallGuard).
                var context = new CorePluginContext(
                    guard.Devices(_devices),
                    guard.Vapix(entry.CallVapix),
                    guard.Tasks(_tasks),
                    _settings.GetSettings(registered.Id),
                    _loggerFactory.CreateLogger("Oadm.Plugins." + registered.Id),
                    registered.Origin.Directory,
                    _secrets,
                    Events.For(registered.Id),
                    _trustAnchors?.For(registered.Id),
                    guard.EventStreams(entry.CallEventStreams),
                    _firewall,
                    _pluginDataRoot is null ? null : Path.Combine(_pluginDataRoot, SafeFolderName(registered.Id)),
                    guard.AutoAdd(_autoAdd),
                    registered.Plugin is Sdk.Events.IEventSource ? _eventPublishers?.Invoke(registered.Id) : null,
                    entry.Subscriptions,
                    _registry.Licenses?.LicenseOf(registered.Origin.PackageId))
                {
                    // Not for devices at a site: no route into a site and no relays (docs/spec/sites.md, Declaration).
                    Network = SiteSupportGuard.Guard(entry.Network, siteSupport),
                    Pro = entry.Pro = _registry.Pro is { } pro ? new PluginProLicense(pro) : null,
                    Services = _services?.For(registered.Id),
                    Records = _settings.GetRecords(registered.Id),
                    Sites = siteSupport == Sdk.Sites.SiteSupport.Full ? entry.Network?.Sites : null,
                    System = new HostSystemAccess(guard, _devices, entry.CallVapix, _tasks, entry.CallEventStreams),
                    Jobs = Jobs.For(registered.Id),
                    DeviceScopes = guard.DeviceScopes(_deviceScopes), // only the caller's own scope in a group user's call
                    ProtectedTags = _protectedTags?.For(registered.Id),
                };
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
                    entry.Subscriptions?.Dispose();
                    DisposePro(entry);
                    await DisposeNetworkAsync(entry).ConfigureAwait(false);
                    _services?.RemoveOwner(registered.Id);
                    _protectedTags?.RemoveOwner(registered.Id);
                    throw;
                }
#pragma warning disable CA1031 // One faulty core plugin must not stop the others or the host.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    entry.State = CorePluginState.Faulted;
                    entry.Error = ex.Message;
                    entry.Subscriptions?.Dispose();
                    DisposePro(entry);
                    await DisposeNetworkAsync(entry).ConfigureAwait(false);
                    _services?.RemoveOwner(registered.Id);
                    _protectedTags?.RemoveOwner(registered.Id);
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
                await StopOneAsync(_startOrder[i], ct).ConfigureAwait(false);
            }

            _startOrder.Clear();
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>
    /// After a package was turned on or off (<see cref="PluginRegistry.SetEnabled"/>): stops the running core plugins of
    /// disabled packages, then starts the enabled ones that are not running. Errors are logged, never thrown.
    /// </summary>
    public async Task SyncEnabledAsync(CancellationToken ct)
    {
        var enabled = _registry.CorePlugins.Select(c => c.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        await _lifecycle.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            for (var i = _startOrder.Count - 1; i >= 0; i--)
            {
                var id = _startOrder[i];
                if (!enabled.Contains(id))
                {
                    await StopOneAsync(id, ct).ConfigureAwait(false);
                    _startOrder.RemoveAt(i);
                }
            }
        }
        finally
        {
            _lifecycle.Release();
        }

        await StartAllAsync(ct).ConfigureAwait(false);
    }

    private async Task StopOneAsync(string id, CancellationToken ct)
    {
        if (!_entries.TryGetValue(id, out var entry) || entry.State != CorePluginState.Running)
        {
            return;
        }

        entry.State = CorePluginState.Stopping;
        _services?.RemoveOwner(id); // nobody reaches a stopping plugin's services
        _protectedTags?.RemoveOwner(id); // a stopping plugin protects no tags
        Jobs.CancelPlugin(id); // a stopping plugin's jobs end
        try
        {
            await entry.Plugin.StopAsync(ct).ConfigureAwait(false);
            entry.Subscriptions?.Dispose(); // a stopped plugin receives no more events
            DisposePro(entry);
            await DisposeNetworkAsync(entry).ConfigureAwait(false); // its listeners and relays close
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
            entry.Subscriptions?.Dispose();
            DisposePro(entry);
            await DisposeNetworkAsync(entry).ConfigureAwait(false);
            LogStopFailed(ex, id);
        }
    }

    private static void DisposePro(Entry entry)
    {
        entry.Pro?.Dispose();
        entry.Pro = null;
    }

    /// <summary>Closes the listeners and relays a plugin run left open.</summary>
    private static async Task DisposeNetworkAsync(Entry entry)
    {
        if (entry.Network is { } network)
        {
            entry.Network = null;
            await network.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Routes a user's page call to a running core plugin with the user's call (<see cref="ICallContext"/>,
    /// docs/spec/device-scope.md, Plugins). Throws <see cref="KeyNotFoundException"/> for an unknown plugin and
    /// <see cref="InvalidOperationException"/> when it is not running; for a user with a device group
    /// <see cref="DeviceAccessRefusedException"/> before the plugin runs when the package is older than the call context or
    /// the method is for users with all devices only (<see cref="ICorePlugin.RequiresAllDevices"/>). During the call the
    /// context's full accesses refuse for such a user. Exceptions thrown by the plugin are logged and rethrown so the gRPC
    /// layer can map them to an error status.
    /// </summary>
    public async Task<string?> InvokeAsync(PluginCaller caller, string pluginId, string method, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        var (registered, entry) = RunningEntry(pluginId);
        var (call, mark) = Prepare(caller, registered, entry, method);
        try
        {
            using (CallerContext.UsePluginCall(mark))
            {
                return await registered.Plugin.InvokeAsync(call, method, payloadJson, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not DeviceAccessRefusedException and not DevicesNotAvailableException)
        {
            LogInvokeFailed(ex, pluginId, method);
            throw;
        }
    }

    /// <summary>
    /// A call without a user: in-process work of the host itself and tests (background work: the plugin's old overload, the
    /// context's full accesses work). Never for a user's call; <c>PluginService</c> always passes the caller.
    /// </summary>
    public async Task<string?> InvokeAsync(string pluginId, string method, string? payloadJson, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        var (registered, _) = RunningEntry(pluginId);
        try
        {
            using (CallerContext.Suppress())
            {
                return await registered.Plugin.InvokeAsync(method, payloadJson, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogInvokeFailed(ex, pluginId, method);
            throw;
        }
    }

    /// <summary>
    /// Opens a file of a running core plugin (<see cref="ICorePluginDownloads"/>, gRPC PluginService.Download) with the
    /// user's call. Throws like <see cref="InvokeAsync"/>; <see cref="NotSupportedException"/> for a plugin without downloads.
    /// </summary>
    public async Task<PluginDownload> OpenDownloadAsync(PluginCaller caller, string pluginId, string method, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        var (registered, entry) = RunningEntry(pluginId);
        if (registered.Plugin is not ICorePluginDownloads downloads)
        {
            throw new NotSupportedException($"The plugin '{pluginId}' has no downloads.");
        }

        var (call, mark) = Prepare(caller, registered, entry, method);
        try
        {
            using (CallerContext.UsePluginCall(mark))
            {
                return await downloads.OpenDownloadAsync(call, method, payloadJson, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not DeviceAccessRefusedException and not DevicesNotAvailableException)
        {
            LogInvokeFailed(ex, pluginId, method);
            throw;
        }
    }

    /// <summary>
    /// Whether <paramref name="caller"/> may use the plugin at all: always with all devices; with a device group only when
    /// its package is built for the call context (<see cref="PluginOrigin.SupportsCallContext"/>). False for an unknown plugin.
    /// </summary>
    public bool IsCallable(PluginCaller caller, string pluginId)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return _registry.TryGetCorePlugin(pluginId, out var registered) && PluginCallRules.IsCallable(caller, registered.Origin);
    }

    /// <summary>
    /// The user's call for the plugin and the mark of the call: refuses (before the plugin runs) what a user with a device
    /// group may not call.
    /// </summary>
    private (ICallContext Call, PluginCallMark Mark) Prepare(PluginCaller caller, RegisteredCorePlugin registered, Entry entry, string method)
    {
        PluginCallRules.RequireCallable(caller, registered.Origin);
        var systemAllowed = false;
        if (!caller.HasAllDevices)
        {
            if (Answer(registered, method, p => p.RequiresAllDevices(method), fallback: true))
            {
                throw new DeviceAccessRefusedException(DeviceAccessRefusedException.AllDevicesOnly);
            }

            systemAllowed = !string.IsNullOrWhiteSpace(Answer(registered, method, p => p.SystemAccessReason(method), fallback: null));
        }

        var call = new HostCallContext(caller, entry.CallVapix ?? _vapix, _tasks, entry.CallEventStreams, _viewWatcher, _taskOwners);
        return (call, new PluginCallMark(registered.Id, method, caller.HasAllDevices, systemAllowed) { ScopeKey = caller.View.ScopeKey });
    }

    /// <summary>The plugin's answer, or <paramref name="fallback"/> when it throws (fail closed).</summary>
    private T Answer<T>(RegisteredCorePlugin registered, string method, Func<ICorePlugin, T> ask, T fallback)
    {
        try
        {
            return ask(registered.Plugin);
        }
#pragma warning disable CA1031 // A broken plugin answer must not open the method.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogInvokeFailed(ex, registered.Id, method);
            return fallback;
        }
    }

    /// <summary>The running plugin of <paramref name="pluginId"/>; KeyNotFound for an unknown one, InvalidOperation when off or stopped.</summary>
    private (RegisteredCorePlugin Registered, Entry Entry) RunningEntry(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        if (!_registry.TryGetCorePlugin(pluginId, out var registered))
        {
            if (_registry.AllCorePlugins.FirstOrDefault(c => string.Equals(c.Id, pluginId, StringComparison.OrdinalIgnoreCase)) is { } off)
            {
                throw new InvalidOperationException($"The {off.Origin.DisplayName} plugin is turned off.");
            }

            throw new KeyNotFoundException($"Unknown core plugin '{pluginId}'.");
        }

        if (!_entries.TryGetValue(registered.Id, out var entry) || entry.State != CorePluginState.Running)
        {
            throw new InvalidOperationException($"Core plugin '{pluginId}' is not running.");
        }

        return (registered, entry);
    }

    /// <summary>
    /// The access rule of a page method: the permission it needs, whether calls are audited
    /// (<see cref="ICorePlugin.IsAudited"/>) and the plugin's display name. The permission is the plugin's
    /// <see cref="ICorePlugin.RequiredPermission"/> when it names a declared one or "show"; without an answer the role
    /// decides: Operator methods need "show" (a login for a plugin without a page), Admin methods "manage" (the automatic
    /// permission of a plugin that declares none). Anything else, and a plugin that throws, is Administrator only (fail
    /// closed). Null for an unknown plugin.
    /// </summary>
    public CorePluginMethodAccess? AccessOf(string pluginId, string method)
    {
        if (!_registry.TryGetCorePlugin(pluginId, out var registered))
        {
            return null;
        }

        var plugin = registered.Plugin;
        try
        {
            var role = plugin.RequiredRole(method);
            var access = new CorePluginMethodAccess(role, plugin.IsAudited(method), plugin.DisplayName ?? registered.Id);
            var declared = Auth.PermissionCatalog.DeclaredOf(plugin);
            var named = plugin.RequiredPermission(method);
            string? key = named switch
            {
                PermissionKeys.Show when plugin.HasPage => PermissionKeys.Show,
                null when role == UserRole.Operator => plugin.HasPage ? PermissionKeys.Show : string.Empty,
                null when declared.Count == 0 || declared.Any(p => p.Key == PermissionKeys.Manage) => PermissionKeys.Manage,
                not null when declared.Any(p => p.Key == named) => named,
                _ => null,
            };
            return key switch
            {
                null => access with { AdminOnly = true },
                "" => access,
                _ => access with { Permission = PermissionKeys.Of(registered.Id, key) },
            };
        }
#pragma warning disable CA1031 // A broken plugin answer must not open the method.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogInvokeFailed(ex, pluginId, method);
            return new CorePluginMethodAccess(UserRole.Admin, true, registered.Id) { AdminOnly = true };
        }
    }

    /// <summary>The "show" permission of a plugin's page ("oadm.io-monitor:show"), or null for a plugin without a page.</summary>
    public string? ShowPermissionOf(string pluginId)
    {
        if (!_registry.TryGetCorePlugin(pluginId, out var registered))
        {
            return null;
        }

        try
        {
            return registered.Plugin.HasPage ? PermissionKeys.Of(registered.Id, PermissionKeys.Show) : null;
        }
#pragma warning disable CA1031 // Like the rail: a broken plugin has its page.
        catch (Exception)
#pragma warning restore CA1031
        {
            return PermissionKeys.Of(registered.Id, PermissionKeys.Show);
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

    /// <summary>A plugin id as one folder name: characters that are not allowed in file names become '_'.</summary>
    internal static string SafeFolderName(string pluginId)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var name = new string([.. pluginId.Select(c => invalid.Contains(c) || c is '/' or '\\' or ':' ? '_' : c)]);
        return name is "" or "." or ".." ? "_" : name;
    }

    private sealed class Entry(ICorePlugin plugin)
    {
        public ICorePlugin Plugin { get; } = plugin;

        public volatile CorePluginState State;

        public volatile string? Error;

        /// <summary>The event subscriptions of the current run; ended when the plugin stops or fails.</summary>
        public PluginEventSubscriptions? Subscriptions;

        /// <summary>The network of the current run (listeners, relays); closed when the plugin stops or fails.</summary>
        public Sites.PluginNetworkScope? Network;

        /// <summary>The OADM PRO license of the current run; its handlers are dropped when the plugin stops or fails.</summary>
        public PluginProLicense? Pro;

        /// <summary>The plugin's VAPIX factory without the guard (site rules applied): what the call contexts scope.</summary>
        public IVapixClientFactory? CallVapix;

        /// <summary>The plugin's event streams without the guard (site rules applied).</summary>
        public IDeviceEventStreams? CallEventStreams;
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
    IDeviceEventStreams? eventStreams = null,
    IFirewallRules? firewall = null,
    string? dataDirectory = null,
    IDeviceAutoAdd? autoAdd = null,
    Sdk.Events.IEventPublisher? eventPublisher = null,
    Sdk.Events.IEventSubscriptions? eventSubscriptions = null,
    Sdk.Licensing.IPluginLicense? license = null) : ICorePluginContext
{
    public Sdk.Licensing.IPluginLicense? License { get; } = license;

    public Sdk.Licensing.IProLicense? Pro { get; init; }

    private readonly Lazy<string?> _dataDirectory = new(() =>
    {
        if (dataDirectory is not null)
        {
            Directory.CreateDirectory(dataDirectory);
        }

        return dataDirectory;
    });

    public IDeviceRepository Devices { get; } = devices;

    public IVapixClientFactory Vapix { get; } = vapix;

    public ITaskRunner Tasks { get; } = tasks;

    public IPluginSettings Settings { get; } = settings;

    public IPluginRecords? Records { get; init; }

    public ILogger Logger { get; } = logger;

    public string? PluginDirectory { get; } = pluginDirectory;

    public ISecretProtector? Secrets { get; } = secrets;

    public IPluginEvents? Events { get; } = events;

    public ITrustAnchors? TrustAnchors { get; } = trustAnchors;

    public IDeviceEventStreams? EventStreams { get; } = eventStreams;

    public IFirewallRules? Firewall { get; } = firewall;

    public IDeviceAutoAdd? AutoAdd { get; } = autoAdd;

    public Sdk.Events.IEventPublisher? EventPublisher { get; } = eventPublisher;

    public Sdk.Events.IEventSubscriptions? EventSubscriptions { get; } = eventSubscriptions;

    public Sdk.Sites.IPluginNetwork? Network { get; init; }

    public IPluginServices? Services { get; init; }

    public Sdk.Sites.ISites? Sites { get; init; }

    public ISystemAccess System { get; init; } = null!;

    public IPluginJobs? Jobs { get; init; }

    public IDeviceScopes? DeviceScopes { get; init; }

    public IProtectedTags? ProtectedTags { get; init; }

    /// <summary>Created on first use, so plugins that never ask leave no empty folder behind.</summary>
    public string? DataDirectory => _dataDirectory.Value;
}
