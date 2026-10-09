using System.Diagnostics.CodeAnalysis;
using System.Reflection;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Sdk.Plugins;

namespace Oadm.Core.Plugins;

/// <summary>Where a plugin came from: a plugin folder (manifest) or an assembly compiled into the host.</summary>
public sealed record PluginOrigin(string PackageId, string Version, string? Directory)
{
    /// <summary>Name of the package in the plugin list (manifest displayName, else the id).</summary>
    public string DisplayName { get; init; } = PackageId;

    /// <summary>Manifest <c>enabledByDefault</c>: the state until an administrator changes it.</summary>
    public bool EnabledByDefault { get; init; } = true;

    public static PluginOrigin FromAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var name = assembly.GetName();
        return new PluginOrigin(name.Name ?? "builtin", name.Version?.ToString() ?? "0.0.0", null);
    }
}

/// <summary>A task plugin as known to the host. <see cref="Owner"/> is set when a core plugin contributed it.</summary>
public sealed record RegisteredTaskPlugin(ITaskPlugin Plugin, ICorePlugin? Owner, PluginOrigin Origin)
{
    public string Id => Plugin.Id;

    /// <summary>The name the host shows: no trailing ellipsis, at most <see cref="TaskPluginNames.MaxDisplayNameLength"/> characters.</summary>
    public string DisplayName { get; init; } = TaskPluginNames.Normalize(Plugin.DisplayName);

    /// <summary>Context menu group (<see cref="ITaskPlugin.Group"/>), never empty.</summary>
    public string Group { get; init; } = TaskPluginNames.NormalizeGroup(Plugin.Group);
}

public sealed record RegisteredCorePlugin(ICorePlugin Plugin, PluginOrigin Origin)
{
    public string Id => Plugin.Id;
}

/// <summary>
/// One plugin package (plugin folder) in the plugin list: its core plugins and task plugins are turned on and off
/// together. <see cref="HasPage"/>: a core plugin with a rail page; <see cref="MenuTaskCount"/>: context menu entries.
/// </summary>
public sealed record PluginPackageState(PluginOrigin Origin, bool Enabled, bool HasPage, int MenuTaskCount)
{
    public string Id => Origin.PackageId;
}

/// <summary>A plugin that could not be loaded or registered. Shown to admins, never fatal.</summary>
public sealed record PluginLoadError(string Source, string Message, Exception? Exception = null);

/// <summary>
/// All task and core plugins known to the host. Contributed task plugins are registered exactly
/// like standalone ones, with their owning core plugin attached. Plugin ids are unique
/// (case-insensitive); the first registration wins. Thread-safe.
/// Packages can be turned off (Settings page, Plugins): <see cref="TaskPlugins"/>, <see cref="CorePlugins"/> and the
/// lookups only see enabled packages; <see cref="AllTaskPlugins"/> and <see cref="AllCorePlugins"/> see every one.
/// </summary>
public sealed partial class PluginRegistry
{
    private readonly Lock _sync = new();
    private readonly List<RegisteredTaskPlugin> _tasks = [];
    private readonly List<RegisteredCorePlugin> _cores = [];
    private readonly List<PluginLoadError> _errors = [];
    private readonly Dictionary<string, bool> _enabledOverrides = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger _logger;

    public PluginRegistry(ILogger<PluginRegistry>? logger = null)
    {
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>Task plugins of enabled packages.</summary>
    public IReadOnlyList<RegisteredTaskPlugin> TaskPlugins
    {
        get
        {
            lock (_sync)
            {
                return [.. _tasks.Where(t => IsEnabledLocked(t.Origin))];
            }
        }
    }

    /// <summary>Core plugins of enabled packages.</summary>
    public IReadOnlyList<RegisteredCorePlugin> CorePlugins
    {
        get
        {
            lock (_sync)
            {
                return [.. _cores.Where(c => IsEnabledLocked(c.Origin))];
            }
        }
    }

    /// <summary>Every registered task plugin, enabled or not.</summary>
    public IReadOnlyList<RegisteredTaskPlugin> AllTaskPlugins
    {
        get
        {
            lock (_sync)
            {
                return [.. _tasks];
            }
        }
    }

    /// <summary>Every registered core plugin, enabled or not.</summary>
    public IReadOnlyList<RegisteredCorePlugin> AllCorePlugins
    {
        get
        {
            lock (_sync)
            {
                return [.. _cores];
            }
        }
    }

    public IReadOnlyList<PluginLoadError> Errors
    {
        get
        {
            lock (_sync)
            {
                return [.. _errors];
            }
        }
    }

    /// <summary>Every package with at least one registered plugin, in registration order, with its state.</summary>
    public IReadOnlyList<PluginPackageState> Packages
    {
        get
        {
            lock (_sync)
            {
                var origins = new List<PluginOrigin>();
                foreach (var origin in _cores.Select(c => c.Origin).Concat(_tasks.Select(t => t.Origin)))
                {
                    if (!origins.Exists(o => SamePackage(o, origin)))
                    {
                        origins.Add(origin);
                    }
                }

                return [.. origins.Select(o => new PluginPackageState(
                    o,
                    IsEnabledLocked(o),
                    _cores.Exists(c => SamePackage(c.Origin, o) && SafeHasPage(c.Plugin)),
                    _tasks.Count(t => SamePackage(t.Origin, o) && SafeShowInMenus(t.Plugin))))];
            }
        }
    }

    /// <summary>Raised after <see cref="SetEnabled"/> or <see cref="ApplyEnabledStates"/>.</summary>
    public event EventHandler? EnabledChanged;

    /// <summary>Whether a package is known and on: the administrator's choice, else the manifest default.</summary>
    public bool IsEnabled(string packageId)
    {
        lock (_sync)
        {
            var origin = FindOriginLocked(packageId);
            return origin is not null && IsEnabledLocked(origin);
        }
    }

    /// <summary>Whether a package with this id has registered plugins.</summary>
    public bool HasPackage(string packageId)
    {
        lock (_sync)
        {
            return FindOriginLocked(packageId) is not null;
        }
    }

    /// <summary>The stored choices (package id -> on), e.g. at server start. Packages not listed use their default.</summary>
    public void ApplyEnabledStates(IReadOnlyDictionary<string, bool> states)
    {
        ArgumentNullException.ThrowIfNull(states);
        lock (_sync)
        {
            _enabledOverrides.Clear();
            foreach (var (id, enabled) in states)
            {
                _enabledOverrides[id] = enabled;
            }
        }

        EnabledChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Turns a package on or off. Returns every choice to store (package id -> on).</summary>
    public IReadOnlyDictionary<string, bool> SetEnabled(string packageId, bool enabled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        Dictionary<string, bool> snapshot;
        lock (_sync)
        {
            _enabledOverrides[packageId] = enabled;
            snapshot = new Dictionary<string, bool>(_enabledOverrides, StringComparer.OrdinalIgnoreCase);
        }

        EnabledChanged?.Invoke(this, EventArgs.Empty);
        return snapshot;
    }

    /// <summary>A task plugin of an enabled package.</summary>
    public bool TryGetTaskPlugin(string id, [NotNullWhen(true)] out RegisteredTaskPlugin? plugin)
    {
        lock (_sync)
        {
            plugin = _tasks.Find(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase) && IsEnabledLocked(t.Origin));
            return plugin is not null;
        }
    }

    /// <summary>A core plugin of an enabled package.</summary>
    public bool TryGetCorePlugin(string id, [NotNullWhen(true)] out RegisteredCorePlugin? plugin)
    {
        lock (_sync)
        {
            plugin = _cores.Find(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase) && IsEnabledLocked(c.Origin));
            return plugin is not null;
        }
    }

    private PluginOrigin? FindOriginLocked(string packageId) =>
        _cores.Select(c => c.Origin).Concat(_tasks.Select(t => t.Origin))
            .FirstOrDefault(o => string.Equals(o.PackageId, packageId, StringComparison.OrdinalIgnoreCase));

    private bool IsEnabledLocked(PluginOrigin origin) =>
        _enabledOverrides.TryGetValue(origin.PackageId, out var enabled) ? enabled : origin.EnabledByDefault;

    private static bool SamePackage(PluginOrigin a, PluginOrigin b) =>
        string.Equals(a.PackageId, b.PackageId, StringComparison.OrdinalIgnoreCase);

    private static bool SafeHasPage(ICorePlugin plugin)
    {
        try
        {
            return plugin.HasPage;
        }
#pragma warning disable CA1031 // Plugin code is untrusted.
        catch (Exception)
#pragma warning restore CA1031
        {
            return false;
        }
    }

    private static bool SafeShowInMenus(ITaskPlugin plugin)
    {
        try
        {
            return plugin.ShowInMenus;
        }
#pragma warning disable CA1031 // Plugin code is untrusted.
        catch (Exception)
#pragma warning restore CA1031
        {
            return false;
        }
    }

    /// <summary>Registers a standalone task plugin. Returns false (and records an error) on a duplicate or invalid id.</summary>
    public bool RegisterTaskPlugin(ITaskPlugin plugin, PluginOrigin origin)
    {
        return RegisterTask(plugin, owner: null, origin);
    }

    /// <summary>
    /// Registers a core plugin and every task plugin it contributes. Returns false when the core
    /// plugin itself was rejected; a rejected contributed task is recorded as an error only.
    /// </summary>
    public bool RegisterCorePlugin(ICorePlugin plugin, PluginOrigin origin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        ArgumentNullException.ThrowIfNull(origin);

        string id;
        IReadOnlyList<ITaskPlugin> contributed;
        try
        {
            id = plugin.Id;
            contributed = plugin.TaskPlugins ?? [];
        }
#pragma warning disable CA1031 // Plugin code is untrusted; isolate its failures.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            AddError(new PluginLoadError(origin.PackageId, $"Core plugin {plugin.GetType().FullName} could not be inspected: {ex.Message}", ex));
            return false;
        }

        lock (_sync)
        {
            if (!IsValidId(id) || IdTaken(id))
            {
                AddErrorLocked(new PluginLoadError(origin.PackageId, $"Core plugin id '{id}' is invalid or already registered."));
                return false;
            }

            _cores.Add(new RegisteredCorePlugin(plugin, origin));
        }

        LogRegisteredCore(id, origin.PackageId);
        foreach (var task in contributed)
        {
            if (task is not null)
            {
                RegisterTask(task, plugin, origin);
            }
        }

        return true;
    }

    public void AddError(PluginLoadError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        lock (_sync)
        {
            AddErrorLocked(error);
        }
    }

    private bool RegisterTask(ITaskPlugin plugin, ICorePlugin? owner, PluginOrigin origin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        ArgumentNullException.ThrowIfNull(origin);

        string id;
        string rawName;
        string? rawGroup;
        try
        {
            id = plugin.Id;
            rawName = plugin.DisplayName ?? string.Empty;
            rawGroup = plugin.Group;
        }
#pragma warning disable CA1031 // Plugin code is untrusted; isolate its failures.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            AddError(new PluginLoadError(origin.PackageId, $"Task plugin {plugin.GetType().FullName} could not be inspected: {ex.Message}", ex));
            return false;
        }

        var displayName = TaskPluginNames.Normalize(rawName);
        var stripped = TaskPluginNames.StripEllipsis(rawName);
        if (stripped.Length != rawName.Trim().Length)
        {
            LogTrailingEllipsis(id, rawName);
        }

        if (stripped.Length > TaskPluginNames.MaxDisplayNameLength)
        {
            LogNameTooLong(id, stripped, TaskPluginNames.MaxDisplayNameLength, displayName);
        }

        lock (_sync)
        {
            if (!IsValidId(id) || IdTaken(id))
            {
                AddErrorLocked(new PluginLoadError(origin.PackageId, $"Task plugin id '{id}' is invalid or already registered."));
                return false;
            }

            _tasks.Add(new RegisteredTaskPlugin(plugin, owner, origin)
            {
                DisplayName = displayName,
                Group = TaskPluginNames.NormalizeGroup(rawGroup),
            });
        }

        LogRegisteredTask(id, owner?.Id ?? "-", origin.PackageId);
        return true;
    }

    private static bool IsValidId(string? id) => !string.IsNullOrWhiteSpace(id);

    // Task and core plugins share one id space: the client addresses both by id.
    private bool IdTaken(string id) =>
        _tasks.Exists(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase))
        || _cores.Exists(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));

    private void AddErrorLocked(PluginLoadError error)
    {
        _errors.Add(error);
        LogPluginError(error.Exception, error.Source, error.Message);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Registered core plugin {PluginId} from {Package}")]
    private partial void LogRegisteredCore(string pluginId, string package);

    [LoggerMessage(Level = LogLevel.Information, Message = "Registered task plugin {PluginId} (owner {Owner}) from {Package}")]
    private partial void LogRegisteredTask(string pluginId, string owner, string package);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Task plugin {PluginId}: display name '{Name}' ends with an ellipsis; the host shows it without")]
    private partial void LogTrailingEllipsis(string pluginId, string name);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Task plugin {PluginId}: display name '{Name}' is longer than {Max} characters; shown as '{Shown}'")]
    private partial void LogNameTooLong(string pluginId, string name, int max, string shown);

    [LoggerMessage(Level = LogLevel.Error, Message = "Plugin error in {Source}: {Message}")]
    private partial void LogPluginError(Exception? ex, string source, string message);
}
