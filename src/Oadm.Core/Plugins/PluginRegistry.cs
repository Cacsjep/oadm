using System.Diagnostics.CodeAnalysis;
using System.Reflection;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Sdk.Plugins;

namespace Oadm.Core.Plugins;

/// <summary>Where a plugin came from: a plugin folder (manifest) or an assembly compiled into the host.</summary>
public sealed record PluginOrigin(string PackageId, string Version, string? Directory)
{
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
}

public sealed record RegisteredCorePlugin(ICorePlugin Plugin, PluginOrigin Origin)
{
    public string Id => Plugin.Id;
}

/// <summary>A plugin that could not be loaded or registered. Shown to admins, never fatal.</summary>
public sealed record PluginLoadError(string Source, string Message, Exception? Exception = null);

/// <summary>
/// All task and core plugins known to the host. Contributed task plugins are registered exactly
/// like standalone ones, with their owning core plugin attached. Plugin ids are unique
/// (case-insensitive); the first registration wins. Thread-safe.
/// </summary>
public sealed partial class PluginRegistry
{
    private readonly Lock _sync = new();
    private readonly List<RegisteredTaskPlugin> _tasks = [];
    private readonly List<RegisteredCorePlugin> _cores = [];
    private readonly List<PluginLoadError> _errors = [];
    private readonly ILogger _logger;

    public PluginRegistry(ILogger<PluginRegistry>? logger = null)
    {
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    public IReadOnlyList<RegisteredTaskPlugin> TaskPlugins
    {
        get
        {
            lock (_sync)
            {
                return [.. _tasks];
            }
        }
    }

    public IReadOnlyList<RegisteredCorePlugin> CorePlugins
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

    public bool TryGetTaskPlugin(string id, [NotNullWhen(true)] out RegisteredTaskPlugin? plugin)
    {
        lock (_sync)
        {
            plugin = _tasks.Find(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
            return plugin is not null;
        }
    }

    public bool TryGetCorePlugin(string id, [NotNullWhen(true)] out RegisteredCorePlugin? plugin)
    {
        lock (_sync)
        {
            plugin = _cores.Find(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));
            return plugin is not null;
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
        try
        {
            id = plugin.Id;
        }
#pragma warning disable CA1031 // Plugin code is untrusted; isolate its failures.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            AddError(new PluginLoadError(origin.PackageId, $"Task plugin {plugin.GetType().FullName} could not be inspected: {ex.Message}", ex));
            return false;
        }

        lock (_sync)
        {
            if (!IsValidId(id) || IdTaken(id))
            {
                AddErrorLocked(new PluginLoadError(origin.PackageId, $"Task plugin id '{id}' is invalid or already registered."));
                return false;
            }

            _tasks.Add(new RegisteredTaskPlugin(plugin, owner, origin));
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

    [LoggerMessage(Level = LogLevel.Error, Message = "Plugin error in {Source}: {Message}")]
    private partial void LogPluginError(Exception? ex, string source, string message);
}
