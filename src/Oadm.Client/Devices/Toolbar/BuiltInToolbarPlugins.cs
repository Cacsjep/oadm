using Avalonia.Controls;
using Avalonia.Layout;

using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Devices;

namespace Oadm.Client.Devices.Toolbar;

/// <summary>"Scan": opens the add page and starts zero-configuration (mDNS) discovery right away. The primary button.</summary>
public sealed class ScanToolbarPlugin : IToolbarPlugin
{
    public string Id => "oadm.toolbar.scan";
    public int Order => 0;
    public ToolbarGroup Group => ToolbarGroup.Add;

    public Control CreateControl(IToolbarContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var button = new ToolbarButton { Text = "Scan", IconKey = "search", IsPrimary = true };
        ToolTip.SetTip(button, "Find devices on the network with zero-configuration (Bonjour) and log in with your known credentials");
        button.Click += async (_, _) => await ctx.OpenAsync(HostPages.AddScan).ConfigureAwait(true);
        return button;
    }
}

/// <summary>"Scan IP range": the add page with the range input.</summary>
public sealed class ScanRangeToolbarPlugin : IToolbarPlugin
{
    public string Id => "oadm.toolbar.range";
    public int Order => 10;
    public ToolbarGroup Group => ToolbarGroup.Add;

    public Control CreateControl(IToolbarContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var button = new ToolbarButton { Text = "Scan IP range", IconKey = "range" };
        ToolTip.SetTip(button, "Scan an IPv4 address range");
        button.Click += async (_, _) => await ctx.OpenAsync(HostPages.AddIpRange).ConfigureAwait(true);
        return button;
    }
}

/// <summary>"Add manually": the add page with the address input.</summary>
public sealed class AddManuallyToolbarPlugin : IToolbarPlugin
{
    public string Id => "oadm.toolbar.manual";
    public int Order => 20;
    public ToolbarGroup Group => ToolbarGroup.Add;

    public Control CreateControl(IToolbarContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var button = new ToolbarButton { Text = "Add manually", IconKey = "add" };
        ToolTip.SetTip(button, "Add a device by IP address or host name");
        button.Click += async (_, _) => await ctx.OpenAsync(HostPages.AddManually).ConfigureAwait(true);
        return button;
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
/// The task plugin actions: one button per task plugin that declares <c>ShowInToolbar</c>, enabled
/// when it can run on the whole selection, rebuilt when the server's task plugins change.
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
                var button = new ToolbarButton { Text = plugin.DisplayName, IconKey = plugin.IconKey ?? "plugin", Name = "task:" + plugin.Id };
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
            foreach ((string id, ToolbarButton button) in buttons)
            {
                button.IsEnabled = ctx.CanRunTask(id);
            }
        }

        ctx.TaskPluginsChanged += (_, _) => Rebuild();
        ctx.SelectionChanged += (_, _) => UpdateEnabled();
        Rebuild();
        return panel;
    }
}

/// <summary>The built-in toolbar plugins, registered like plugin ones.</summary>
public static class BuiltInToolbarPlugins
{
    public static IReadOnlyList<IToolbarPlugin> All { get; } =
    [
        new ScanToolbarPlugin(),
        new ScanRangeToolbarPlugin(),
        new AddManuallyToolbarPlugin(),
        new RemoveToolbarPlugin(),
        new TaskActionsToolbarPlugin(),
    ];
}
