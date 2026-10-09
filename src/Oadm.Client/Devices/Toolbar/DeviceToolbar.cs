using Avalonia;
using Avalonia.Controls;

using Microsoft.Extensions.Logging;

using Oadm.Client.Plugins;
using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Controls;

namespace Oadm.Client.Devices.Toolbar;

/// <summary>
/// The toolbar plugins of the Devices page: built-in ones (registered in the container, their ids
/// are reserved) and those of client plugins, ordered by group, order and id. Creates each control
/// once (a failing plugin is logged and left out) and keeps a separator between two groups that
/// show something.
/// </summary>
public sealed partial class DeviceToolbar
{
    private readonly ILogger<DeviceToolbar> _logger;
    private readonly IClientPluginRegistry _registry;
    private readonly Dictionary<IToolbarPlugin, Control> _byPlugin = [];
    private Func<string?, bool> _isPackageEnabled = _ => true;
    private List<Control>? _controls;

    public DeviceToolbar(IEnumerable<IToolbarPlugin> builtIn, IClientPluginRegistry registry, ILogger<DeviceToolbar> logger)
    {
        ArgumentNullException.ThrowIfNull(builtIn);
        ArgumentNullException.ThrowIfNull(registry);
        _logger = logger;
        _registry = registry;
        var plugins = builtIn.ToList();
        foreach (IToolbarPlugin plugin in registry.ToolbarPlugins)
        {
            if (plugins.Exists(p => string.Equals(p.Id, plugin.Id, StringComparison.OrdinalIgnoreCase)))
            {
                LogDuplicate(_logger, plugin.Id);
                continue;
            }

            plugins.Add(plugin);
        }

        Plugins = Arrange(plugins);
    }

    /// <summary>All toolbar plugins in display order.</summary>
    public IReadOnlyList<IToolbarPlugin> Plugins { get; }

    /// <summary>Display order: group, then order, then id.</summary>
    public static IReadOnlyList<IToolbarPlugin> Arrange(IEnumerable<IToolbarPlugin> plugins) =>
        plugins.OrderBy(p => p.Group).ThenBy(p => p.Order).ThenBy(p => p.Id, StringComparer.Ordinal).ToList();

    /// <summary>
    /// Puts the toolbar into <paramref name="panel"/>: the plugin controls in order with a
    /// <see cref="ToolbarSeparator"/> before each new group. A control with the class <c>trailing</c> (a link such as
    /// the AXIS OS release notes) goes to <paramref name="trailing"/> on the right instead. Controls are created on the
    /// first call and moved on later calls (a page view is created again after navigating).
    /// </summary>
    public void AttachTo(Panel panel, IToolbarContext context, Panel? trailing = null)
    {
        ArgumentNullException.ThrowIfNull(panel);
        ArgumentNullException.ThrowIfNull(context);
        _controls ??= Create(context);
        foreach (Control control in _controls)
        {
            Panel target = trailing is not null && control.Classes.Contains("trailing") ? trailing : panel;
            if (control.Parent is Panel old && !ReferenceEquals(old, target))
            {
                old.Children.Remove(control);
            }

            if (!target.Children.Contains(control))
            {
                target.Children.Add(control);
            }
        }
    }

    /// <summary>
    /// Hides the entries of plugin packages that are off (Settings page, Plugins) and shows them again when turned on.
    /// Built-in entries have no package and stay.
    /// </summary>
    public void ApplyPackageStates(Func<string?, bool> isPackageEnabled)
    {
        ArgumentNullException.ThrowIfNull(isPackageEnabled);
        _isPackageEnabled = isPackageEnabled;
        foreach ((IToolbarPlugin plugin, Control control) in _byPlugin)
        {
            if (_registry.PackageOf(plugin) is { } package)
            {
                control.IsVisible = isPackageEnabled(package);
            }
        }
    }

    private List<Control> Create(IToolbarContext context)
    {
        var result = new List<Control>();
        var groups = new List<(ToolbarSeparator? Separator, List<Control> Controls)>();
        foreach (IGrouping<ToolbarGroup, IToolbarPlugin> group in Plugins.GroupBy(p => p.Group))
        {
            var controls = new List<Control>();
            foreach (IToolbarPlugin plugin in group)
            {
                try
                {
                    Control control = plugin.CreateControl(context);
                    control.Name ??= plugin.Id;
                    controls.Add(control);
                    _byPlugin[plugin] = control;
                    if (!_isPackageEnabled(_registry.PackageOf(plugin)))
                    {
                        control.IsVisible = false;
                    }
                }
                catch (Exception ex)
                {
                    // The entry is left out; the client keeps running (production hardening 4).
                    LogCreateFailed(_logger, ex, plugin.Id);
                    _ = ReportAsync(context, plugin.Id, ex);
                }
            }

            if (controls.Count == 0)
            {
                continue;
            }

            ToolbarSeparator? separator = groups.Count == 0 ? null : new ToolbarSeparator();
            if (separator is not null)
            {
                result.Add(separator);
            }

            result.AddRange(controls);
            groups.Add((separator, controls));
            foreach (Control control in controls)
            {
                control.PropertyChanged += (_, e) =>
                {
                    if (e.Property == Visual.IsVisibleProperty)
                    {
                        UpdateSeparators(groups);
                    }
                };
            }
        }

        UpdateSeparators(groups);
        return result;
    }

    /// <summary>A separator shows only between a visible group and an earlier visible group.</summary>
    private static void UpdateSeparators(List<(ToolbarSeparator? Separator, List<Control> Controls)> groups)
    {
        bool anyBefore = false;
        foreach ((ToolbarSeparator? separator, List<Control> controls) in groups)
        {
            bool visible = controls.Exists(c => c.IsVisible);
            if (separator is not null)
            {
                separator.IsVisible = visible && anyBefore;
            }

            anyBefore |= visible;
        }
    }

    private async Task ReportAsync(IToolbarContext context, string id, Exception ex)
    {
        try
        {
            await context.ShowMessageAsync("Toolbar", $"The toolbar entry {id} failed: {ex.Message}").ConfigureAwait(true);
        }
#pragma warning disable CA1031 // Reporting a broken plugin must never break the toolbar.
        catch (Exception showFailed)
#pragma warning restore CA1031
        {
            LogCreateFailed(_logger, showFailed, id);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Toolbar plugin {Id} is ignored: a built-in toolbar entry has the same id")]
    private static partial void LogDuplicate(ILogger logger, string id);

    [LoggerMessage(Level = LogLevel.Error, Message = "Toolbar plugin {Id} failed to create its control")]
    private static partial void LogCreateFailed(ILogger logger, Exception ex, string id);
}
