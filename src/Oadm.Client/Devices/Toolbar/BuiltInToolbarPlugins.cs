using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;

using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Devices;

namespace Oadm.Client.Devices.Toolbar;

/// <summary>
/// "Add" (the primary button): a menu with the ways to add devices, each opening the add page in its mode: Discovery
/// (zero-configuration scan), Network range (IPv4 range scan), Add manually (an address) and Import from file (CSV).
/// </summary>
public sealed class AddToolbarPlugin : IToolbarPlugin
{
    /// <summary>The menu entries: header, icon key, host page and tooltip.</summary>
    public static IReadOnlyList<(string Header, string IconKey, string HostPage, string Tooltip)> Entries { get; } =
    [
        ("Discovery", "search", HostPages.AddScan, "Find Axis devices on the local network"),
        ("Network range", "range", HostPages.AddIpRange, "Scan an IPv4 address range"),
        ("Add manually", "add", HostPages.AddManually, "Add a device by IP address or host name"),
        ("Import from file", "download", HostPages.AddImport, "Add devices from a CSV or address list"),
    ];

    /// <summary>The Add menu (toolbar button and the empty Devices page).</summary>
    public static MenuFlyout CreateMenu(IToolbarContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        foreach (var (header, iconKey, hostPage, tooltip) in Entries)
        {
            var item = new MenuItem { Header = header, Icon = new OadmIcon { Data = ToolbarButton.ResolveIcon(iconKey) } };
            ToolTip.SetTip(item, tooltip);
            item.Click += async (_, _) => await ctx.OpenAsync(hostPage).ConfigureAwait(true);
            menu.Items.Add(item);
        }

        return menu;
    }

    public string Id => "oadm.toolbar.add";
    public int Order => 0;
    public ToolbarGroup Group => ToolbarGroup.Add;

    public Control CreateControl(IToolbarContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var button = new ToolbarButton { Text = "Add", IconKey = "add", IsPrimary = true, Name = "AddButton", Flyout = CreateMenu(ctx) };
        ToolTip.SetTip(button, "Add devices: discovery, network range, manually or from a file");
        return button;
    }
}

/// <summary>"Refresh": reads the selected devices again now (one call for the whole selection). Disabled without a selection.</summary>
public sealed class RefreshToolbarPlugin : IToolbarPlugin
{
    public const string Tooltip = "Read the selected devices again";

    public string Id => "oadm.toolbar.refresh";
    public int Order => 5;
    public ToolbarGroup Group => ToolbarGroup.Manage;

    public Control CreateControl(IToolbarContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var button = new ToolbarButton { Text = "Refresh", IconKey = "refresh", IsEnabled = ctx.SelectedDevices.Count > 0 };
        ToolTip.SetTip(button, Tooltip);
        ToolTip.SetShowOnDisabled(button, true);
        ctx.SelectionChanged += (_, _) => button.IsEnabled = ctx.SelectedDevices.Count > 0;
        button.Click += async (_, _) => await RefreshSelectionAsync(ctx).ConfigureAwait(true);
        return button;
    }

    /// <summary>The refresh flow shared by the toolbar button and the context menu.</summary>
    public static async Task RefreshSelectionAsync(IToolbarContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var ids = ctx.SelectedDevices.Select(d => d.Id).ToList();
        if (ids.Count == 0)
        {
            return;
        }

        try
        {
            await ctx.RefreshDevicesAsync(ids, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await ctx.ShowMessageAsync("Refresh", "The devices could not be refreshed: " + ex.Message).ConfigureAwait(true);
        }
    }
}

/// <summary>"Remove": removes the selected devices from OADM after a confirmation. Disabled without a selection.</summary>
public sealed class RemoveToolbarPlugin : IToolbarPlugin
{
    public string Id => "oadm.toolbar.remove";
    public int Order => 0;
    public ToolbarGroup Group => ToolbarGroup.Manage;

    public Control CreateControl(IToolbarContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var button = new ToolbarButton { Text = "Remove", IconKey = "remove", IsEnabled = ctx.SelectedDevices.Count > 0 };
        ctx.SelectionChanged += (_, _) => button.IsEnabled = ctx.SelectedDevices.Count > 0;
        button.Click += async (_, _) => await ConfirmAndRemoveAsync(ctx).ConfigureAwait(true);
        return button;
    }

    /// <summary>The remove flow shared by the toolbar button and the context menu.</summary>
    /// <returns>True when devices were removed.</returns>
    public static async Task<bool> ConfirmAndRemoveAsync(IToolbarContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        IReadOnlyList<IDeviceInfo> selected = ctx.SelectedDevices;
        if (selected.Count == 0)
        {
            return false;
        }

        string what = selected.Count == 1 ? $"the device {DisplayAddress(selected[0])} ({selected[0].Serial})" : $"{selected.Count} devices";
        if (!await ctx.ConfirmAsync("Remove devices", $"Remove {what} from OADM? The devices themselves are not changed.", "Remove").ConfigureAwait(true))
        {
            return false;
        }

        try
        {
            await ctx.RemoveDevicesAsync(selected.Select(d => d.Id).ToList(), CancellationToken.None).ConfigureAwait(true);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await ctx.ShowMessageAsync("Remove devices", "The devices could not be removed: " + ex.Message).ConfigureAwait(true);
            return false;
        }
    }

    private static string DisplayAddress(IDeviceInfo device) => device is DeviceRowViewModel row ? row.DisplayAddress : device.Address;
}

/// <summary>
/// "Export devices" (icon button, next to Remove): saves the selected devices, or every device the search
/// shows when none is selected, as a CSV file (<see cref="HostPages.ExportDevices"/>). The tooltip says which.
/// </summary>
public sealed class ExportToolbarPlugin : IToolbarPlugin
{
    public const string Text = "Export";

    public string Id => "oadm.toolbar.export";
    public int Order => 10;
    public ToolbarGroup Group => ToolbarGroup.Manage;

    public Control CreateControl(IToolbarContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var button = new ToolbarButton { Text = Text, IconKey = "export", Name = "ExportButton" };
        ToolTip.SetTip(button, Tooltip(ctx.SelectedDevices.Count));
        ctx.SelectionChanged += (_, _) => ToolTip.SetTip(button, Tooltip(ctx.SelectedDevices.Count));
        button.Click += async (_, _) => await ctx.OpenAsync(HostPages.ExportDevices).ConfigureAwait(true);
        return button;
    }

    /// <summary>Says which devices the export writes.</summary>
    public static string Tooltip(int selected) => selected switch
    {
        0 => "Save the shown devices as CSV",
        _ => "Save the selected devices as CSV",
    };
}

/// <summary>
/// The task plugin actions: one button per task plugin that declares <c>ShowInToolbar</c>, enabled
/// when it can run on the whole selection (disabled with the reason as tooltip when the selection cannot run it),
/// rebuilt when the server's task plugins change.
/// </summary>
public sealed class TaskActionsToolbarPlugin : IToolbarPlugin
{
    public string Id => "oadm.toolbar.tasks";
    public int Order => 0;
    public ToolbarGroup Group => ToolbarGroup.Tasks;

    public Control CreateControl(IToolbarContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        var buttons = new Dictionary<string, ToolbarButton>(StringComparer.OrdinalIgnoreCase);

        void Rebuild()
        {
            panel.Children.Clear();
            buttons.Clear();
            foreach (ToolbarTaskPlugin plugin in ctx.TaskPlugins.Where(p => p.ShowInToolbar))
            {
                var button = new ToolbarButton { Text = Oadm.Sdk.Plugins.TaskPluginNames.Normalize(plugin.DisplayName), IconKey = plugin.IconKey ?? "plugin", Name = "task:" + plugin.Id };
                string id = plugin.Id;
                button.Click += async (_, _) => await ctx.RunTaskAsync(id, CancellationToken.None).ConfigureAwait(true);
                buttons[plugin.Id] = button;
                panel.Children.Add(button);
            }

            panel.IsVisible = buttons.Count > 0;
            UpdateEnabled();
        }

        void UpdateEnabled()
        {
            bool selected = ctx.SelectedDevices.Count > 0;
            foreach ((string id, ToolbarButton button) in buttons)
            {
                bool canRun = ctx.CanRunTask(id);
                button.IsEnabled = canRun;
                ToolTip.SetTip(button, canRun || !selected ? null : ctx.CannotRunTaskReason(id));
                ToolTip.SetShowOnDisabled(button, true);
            }
        }

        ctx.TaskPluginsChanged += (_, _) => Rebuild();
        ctx.SelectionChanged += (_, _) => UpdateEnabled();
        Rebuild();
        return panel;
    }
}

/// <summary>
/// "AXIS OS - Release Notes": opens the AXIS OS release notes on help.axis.com in the default browser. An icon-only link
/// on the right of the toolbar, next to Columns (class <c>trailing</c>), so it does not compete with the device actions.
/// </summary>
public sealed class ReleaseNotesToolbarPlugin : IToolbarPlugin
{
    public static readonly Uri ReleaseNotesUri = new("https://help.axis.com/en-us/axis-os-release-notes");

    public string Id => "oadm.toolbar.release-notes";
    public int Order => 1000;
    public ToolbarGroup Group => ToolbarGroup.Plugins;

    public Control CreateControl(IToolbarContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var button = new ToolbarButton { Text = "AXIS OS - Release Notes", IconKey = "externalLink", IsIconOnly = true, Classes = { "trailing" } };
        ToolTip.SetTip(button, "AXIS OS release notes (help.axis.com)");
        button.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(button)?.Launcher is { } launcher && !await launcher.LaunchUriAsync(ReleaseNotesUri).ConfigureAwait(true))
            {
                await ctx.ShowMessageAsync("AXIS OS - Release Notes", "The browser could not be opened. The release notes are at " + ReleaseNotesUri).ConfigureAwait(true);
            }
        };
        return button;
    }
}

/// <summary>The built-in toolbar plugins, registered like plugin ones.</summary>
public static class BuiltInToolbarPlugins
{
    public static IReadOnlyList<IToolbarPlugin> All { get; } =
    [
        new AddToolbarPlugin(),
        new RemoveToolbarPlugin(),
        new RefreshToolbarPlugin(),
        new ExportToolbarPlugin(),
        new TaskActionsToolbarPlugin(),
        new ReleaseNotesToolbarPlugin(),
    ];
}
