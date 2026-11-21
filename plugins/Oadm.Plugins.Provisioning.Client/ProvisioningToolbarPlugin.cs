using Avalonia.Controls;

using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Controls;

namespace Oadm.Plugins.Provisioning.Client;

/// <summary>
/// The Devices toolbar's Provisioning button, right of Export (docs/spec/provisioning.md "Where it lives"): opens
/// Provisioning management; green "Provisioning active" while provisioning is on. Shown to users who may see provisioning
/// (and while the plugin is on); the plugin has no rail page.
/// </summary>
public sealed class ProvisioningToolbarPlugin : IToolbarPlugin
{
    public string Id => "oadm.provisioning.toolbar";

    /// <summary>Right of Export (order 10) in the group of Remove, Refresh and Export.</summary>
    public int Order => 20;

    public ToolbarGroup Group => ToolbarGroup.Add;

    public string? RequiredPermission => ProvisioningButtonViewModel.ShowPermission;

    /// <summary>Replaces the window (tests).</summary>
    public IProvisioningDialogs? Dialogs { get; init; }

    public Control CreateControl(IToolbarContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var button = new ToolbarButton { Name = "ProvisioningButton", IconKey = ProvisioningInfo.IconKey, Text = DeviceTexts.Provisioning };
        ToolTip.SetShowOnDisabled(button, true);
        if (ctx.CorePluginContext(ProvisioningInfo.PluginId) is not { } plugin)
        {
            button.IsEnabled = false; // an older host: no way to open the plugin's dialogs
            return button;
        }

        var vm = new ProvisioningButtonViewModel(plugin, Dialogs ?? new WindowDialogs(ctx, button));
        Bind(button, vm);
        CancellationTokenSource? watch = null;
        button.AttachedToVisualTree += (_, _) =>
        {
            watch?.Cancel();
            watch = new CancellationTokenSource();
            _ = vm.WatchAsync(watch.Token);
        };
        button.DetachedFromVisualTree += (_, _) =>
        {
            watch?.Cancel();
            watch?.Dispose();
            watch = null;
        };
        return button;
    }

    /// <summary>Keeps the button's text, colour, tooltip and command in step with <paramref name="vm"/>.</summary>
    internal static void Bind(ToolbarButton button, ProvisioningButtonViewModel vm)
    {
        ArgumentNullException.ThrowIfNull(button);
        ArgumentNullException.ThrowIfNull(vm);
        button.DataContext = vm;
        button.Command = vm.OpenCommand;
        void Apply()
        {
            button.Text = vm.Text;
            button.Classes.Set("ok", vm.IsOn); // the green of the title bar's MyAxis cloud (Button.toolbar.ok)
            ToolTip.SetTip(button, vm.Tooltip);
        }

        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ProvisioningButtonViewModel.Text) or nameof(ProvisioningButtonViewModel.IsOn) or nameof(ProvisioningButtonViewModel.Tooltip))
            {
                Apply();
            }
        };
        Apply();
    }

    /// <summary>Provisioning management as a dialog over the main window.</summary>
    private sealed class WindowDialogs(IToolbarContext ctx, Control anchor) : IProvisioningDialogs
    {
        public async Task ShowAsync(ProvisioningDialogViewModel dialog)
        {
            var window = new ProvisioningWindow();
            window.Attach(dialog);
            if ((ctx.Owner ?? TopLevel.GetTopLevel(anchor) as Window) is { } owner)
            {
                await window.ShowDialog(owner).ConfigureAwait(true);
                return;
            }

            var closed = new TaskCompletionSource();
            window.Closed += (_, _) => closed.TrySetResult();
            window.Show();
            await closed.Task.ConfigureAwait(true);
        }
    }
}
