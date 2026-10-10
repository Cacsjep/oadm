using System.Diagnostics.CodeAnalysis;

using Microsoft.Extensions.Logging;

using Oadm.Sdk.Actions;
using Oadm.Sdk.Events;
using Oadm.Sdk.Plugins;

namespace Oadm.Core.Plugins;

/// <summary>An action of the event rules as known to the host. <see cref="Owner"/> is set when a core plugin contributed it.</summary>
public sealed record RegisteredActionPlugin(IActionPlugin Plugin, ICorePlugin? Owner, PluginOrigin Origin)
{
    public string Id => Plugin.Id;
}

/// <summary>An event type a core plugin publishes (<see cref="IEventSource"/>); <see cref="SourceId"/> is the core plugin id.</summary>
public sealed record RegisteredEventType(EventTypeInfo Type, string SourceId, PluginOrigin Origin)
{
    public string Id => Type.Id;
}

/// <summary>Actions and event types of the event rules (contributed by plugins).</summary>
public sealed partial class PluginRegistry
{
    private readonly List<RegisteredActionPlugin> _actions = [];
    private readonly List<RegisteredEventType> _eventTypes = [];

    /// <summary>Actions of enabled packages.</summary>
    public IReadOnlyList<RegisteredActionPlugin> ActionPlugins
    {
        get
        {
            lock (_sync)
            {
                return [.. _actions.Where(a => IsEnabledLocked(a.Origin))];
            }
        }
    }

    /// <summary>Every registered action, enabled or not.</summary>
    public IReadOnlyList<RegisteredActionPlugin> AllActionPlugins
    {
        get
        {
            lock (_sync)
            {
                return [.. _actions];
            }
        }
    }

    /// <summary>Every event type of a plugin, enabled or not (the engine tracks which sources are on).</summary>
    public IReadOnlyList<RegisteredEventType> AllEventTypes
    {
        get
        {
            lock (_sync)
            {
                return [.. _eventTypes];
            }
        }
    }

    /// <summary>Whether the package of a registered event type or action is on.</summary>
    public bool IsEnabled(PluginOrigin origin)
    {
        ArgumentNullException.ThrowIfNull(origin);
        lock (_sync)
        {
            return IsEnabledLocked(origin);
        }
    }

    /// <summary>An action of an enabled package.</summary>
    public bool TryGetActionPlugin(string id, [NotNullWhen(true)] out RegisteredActionPlugin? action)
    {
        lock (_sync)
        {
            action = _actions.Find(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase) && IsEnabledLocked(a.Origin));
            return action is not null;
        }
    }

    /// <summary>Registers a standalone action. Returns false (and records an error) on a duplicate or invalid id.</summary>
    public bool RegisterActionPlugin(IActionPlugin plugin, PluginOrigin origin) => RegisterAction(plugin, owner: null, origin);

    /// <summary>Actions and event types a core plugin contributes (called from <see cref="RegisterCorePlugin"/>).</summary>
    private void RegisterContributions(ICorePlugin plugin, PluginOrigin origin)
    {
        if (plugin is IActionSource actionSource)
        {
            IReadOnlyList<IActionPlugin> actions;
            try
            {
                actions = actionSource.ActionPlugins ?? [];
            }
#pragma warning disable CA1031 // Plugin code is untrusted.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                AddError(new PluginLoadError(origin.PackageId, $"Actions of {plugin.Id} could not be read: {ex.Message}", ex));
                actions = [];
            }

            foreach (var action in actions.Where(a => a is not null))
            {
                RegisterAction(action, plugin, origin);
            }
        }

        if (plugin is IEventSource eventSource)
        {
            ReadEventTypes(plugin, eventSource, origin);
            if (eventSource is IDynamicEventSource dynamic)
            {
                dynamic.EventTypesChanged += (_, _) =>
                {
                    ReadEventTypes(plugin, eventSource, origin);
                    EventTypesChanged?.Invoke(this, EventArgs.Empty);
                };
            }
        }
    }

    /// <summary>The event types of a dynamic source changed (<see cref="IDynamicEventSource"/>); raised on the plugin's thread.</summary>
    public event EventHandler? EventTypesChanged;

    /// <summary>Replaces the event types of one core plugin with what it declares now.</summary>
    private void ReadEventTypes(ICorePlugin plugin, IEventSource eventSource, PluginOrigin origin)
    {
        IReadOnlyList<EventTypeInfo> types;
        try
        {
            types = eventSource.EventTypes ?? [];
        }
#pragma warning disable CA1031 // Plugin code is untrusted.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            AddError(new PluginLoadError(origin.PackageId, $"Event types of {plugin.Id} could not be read: {ex.Message}", ex));
            return;
        }

        lock (_sync)
        {
            _eventTypes.RemoveAll(e => string.Equals(e.SourceId, plugin.Id, StringComparison.OrdinalIgnoreCase));
            foreach (var type in types.Where(t => t is not null))
            {
                if (string.IsNullOrWhiteSpace(type.Id) || string.IsNullOrWhiteSpace(type.DisplayName)
                    || _eventTypes.Exists(e => string.Equals(e.Id, type.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    AddErrorLocked(new PluginLoadError(origin.PackageId, $"Event type id '{type.Id}' is invalid or already registered."));
                    continue;
                }

                _eventTypes.Add(new RegisteredEventType(type, plugin.Id, origin));
            }
        }
    }

    private bool RegisterAction(IActionPlugin plugin, ICorePlugin? owner, PluginOrigin origin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        ArgumentNullException.ThrowIfNull(origin);
        string id;
        try
        {
            id = plugin.Id;
            _ = plugin.DisplayName;
            _ = plugin.Fields;
        }
#pragma warning disable CA1031 // Plugin code is untrusted.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            AddError(new PluginLoadError(origin.PackageId, $"Action {plugin.GetType().FullName} could not be inspected: {ex.Message}", ex));
            return false;
        }

        lock (_sync)
        {
            if (!IsValidId(id) || _actions.Exists(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase)))
            {
                AddErrorLocked(new PluginLoadError(origin.PackageId, $"Action id '{id}' is invalid or already registered."));
                return false;
            }

            _actions.Add(new RegisteredActionPlugin(plugin, owner, origin));
        }

        LogRegisteredAction(id, owner?.Id ?? "-", origin.PackageId);
        return true;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Registered action {ActionId} (owner {Owner}) from {Package}")]
    private partial void LogRegisteredAction(string actionId, string owner, string package);
}
