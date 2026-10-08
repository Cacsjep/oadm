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

/// <summary>
/// "Import devices" (icon button after Add manually): a CSV file of addresses, optionally with user name
/// and password per device, opens the add page with every address (<see cref="HostPages.AddImport"/>).
/// </summary>
public sealed class ImportToolbarPlugin : IToolbarPlugin
{
    public const string Text = "Import devices";

    public string Id => "oadm.toolbar.import";
    public int Order => 30;
    public ToolbarGroup Group => ToolbarGroup.Add;

    public Control CreateControl(IToolbarContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        // Icon only: the toolbar must fit the 1280 px minimum window with the rail expanded.
        var button = new ToolbarButton { Text = Text, IconKey = "download", IsIconOnly = true, Name = "ImportButton" };
        ToolTip.SetTip(button, "Import devices: add the addresses of a CSV file (an export, or one address per line; optional User name and Password columns)");
        button.Click += async (_, _) => await ctx.OpenAsync(HostPages.AddImport).ConfigureAwait(true);
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
/// "Export devices" (icon button, next to Remove): saves the selected devices, or every device the search
/// shows when none is selected, as a CSV file (<see cref="HostPages.ExportDevices"/>). The tooltip says which.
/// </summary>
public sealed class ExportToolbarPlugin : IToolbarPlugin
{
    public const string Text = "Export devices";

    public string Id => "oadm.toolbar.export";
    public int Order => 10;
    public ToolbarGroup Group => ToolbarGroup.Manage;

    public Control CreateControl(IToolbarContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        // Icon only: the toolbar must fit the 1280 px minimum window with the rail expanded.
        var button = new ToolbarButton { Text = Text, IconKey = "export", IsIconOnly = true, Name = "ExportButton" };
        ToolTip.SetTip(button, Tooltip(ctx.SelectedDevices.Count));
        ctx.SelectionChanged += (_, _) => ToolTip.SetTip(button, Tooltip(ctx.SelectedDevices.Count));
        button.Click += async (_, _) => await ctx.OpenAsync(HostPages.ExportDevices).ConfigureAwait(true);
        return button;
    }

    /// <summary>Says which devices the export writes.</summary>
    public static string Tooltip(int selected) => selected switch
    {
        0 => "Export devices: save every device shown as a CSV file",
        1 => "Export devices: save the selected device as a CSV file",
        _ => string.Create(System.Globalization.CultureInfo.CurrentCulture, $"Export devices: save the {selected} selected devices as a CSV file"),
    };
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

/// <summary>"AXIS OS - Release Notes": opens the AXIS OS release notes on help.axis.com in the default browser. Last on the toolbar.</summary>
public sealed class ReleaseNotesToolbarPlugin : IToolbarPlugin
{
    public static readonly Uri ReleaseNotesUri = new("https://help.axis.com/en-us/axis-os-release-notes");

    public string Id => "oadm.toolbar.release-notes";
    public int Order => 1000;
    public ToolbarGroup Group => ToolbarGroup.Plugins;

    public Control CreateControl(IToolbarContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var button = new ToolbarButton { Text = "AXIS OS - Release Notes", IconKey = "externalLink" };
        ToolTip.SetTip(button, "Open the AXIS OS release notes on help.axis.com in your browser");
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
        new ScanToolbarPlugin(),
        new ScanRangeToolbarPlugin(),
        new AddManuallyToolbarPlugin(),
        new ImportToolbarPlugin(),
        new RemoveToolbarPlugin(),
        new ExportToolbarPlugin(),
        new TaskActionsToolbarPlugin(),
        new ReleaseNotesToolbarPlugin(),
    ];
}
