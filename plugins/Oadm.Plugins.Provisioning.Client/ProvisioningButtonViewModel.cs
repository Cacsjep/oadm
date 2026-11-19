using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Plugins.Provisioning.Nodes;
using Oadm.Sdk.Client;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Provisioning.Client;

/// <summary>Opens Provisioning management over the main window; tests fake it.</summary>
public interface IProvisioningDialogs
{
    /// <summary>Shows Provisioning management until it closes.</summary>
    Task ShowAsync(ProvisioningDialogViewModel dialog);
}

/// <summary>
/// The Devices toolbar's Provisioning button (docs/spec/provisioning.md "Where it lives"): "Provisioning", or green
/// "Provisioning active" while provisioning is on, live from the plugin's status event. Users who may see provisioning see
/// the button; only an Administrator with the right to run provisioning opens Provisioning management (the others see why
/// in its tooltip).
/// </summary>
public sealed partial class ProvisioningButtonViewModel : ObservableObject
{
    /// <summary>The right to change provisioning ("oadm.provisioning:manage").</summary>
    public static readonly string ManagePermission = PermissionKeys.Of(ProvisioningInfo.PluginId, PermissionKeys.Manage);

    /// <summary>The right to see provisioning ("oadm.provisioning:show"): the button shows.</summary>
    public static readonly string ShowPermission = PermissionKeys.Of(ProvisioningInfo.PluginId, PermissionKeys.Show);

    private readonly ICorePluginClientContext _ctx;
    private readonly IProvisioningDialogs _dialogs;

    public ProvisioningButtonViewModel(ICorePluginClientContext ctx, IProvisioningDialogs dialogs)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _ctx.PermissionsChanged += (_, _) => UpdateCanManage();
        UpdateCanManage();
    }

    /// <summary>The waits of the status watch; tests pass their own.</summary>
    public PluginEventWatchOptions WatchOptions { get; init; } = new() { MaxRetryDelay = TimeSpan.FromMinutes(1) };

    /// <summary>Provisioning is on: the button is green and reads "Provisioning active".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Text))]
    public partial bool IsOn { get; private set; }

    public string Text => IsOn ? ProvisioningTexts.ButtonActive : DeviceTexts.Provisioning;

    /// <summary>The user may open Provisioning management.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Tooltip))]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand))]
    public partial bool CanManage { get; private set; }

    public string Tooltip => CanManage ? ProvisioningTexts.ButtonTip : ProvisioningTexts.AdministratorOnly;

    private void UpdateCanManage() => CanManage = _ctx.IsAdministrator && _ctx.Has(ManagePermission);

    /// <summary>
    /// Follows whether provisioning is on until <paramref name="ct"/> is cancelled (the button left the window): reads it
    /// (every signed-in user may), then applies the status events (users who may see provisioning get them).
    /// </summary>
    public Task WatchAsync(CancellationToken ct) => _ctx.RunAsync(ReadAsync, OnEvent, WatchOptions, ct);

    private async Task ReadAsync(CancellationToken ct) =>
        IsOn = PluginJson.Parse<StateReply>(await _ctx.InvokeAsync(ProvisioningMethods.State, null, ct).ConfigureAwait(true), ProvisioningJson.Options).On;

    /// <summary>One event of the plugin: the status after On, Off and profile changes.</summary>
    internal void OnEvent(PluginEvent item)
    {
        if (item.Topic == ProvisioningMethods.StatusTopic && PluginJson.TryParse<StatusReply>(item.PayloadJson, ProvisioningJson.Options) is { } status)
        {
            IsOn = status.On;
        }
    }

    /// <summary>Opens Provisioning management; the button follows what it changed.</summary>
    [RelayCommand(CanExecute = nameof(CanManage))]
    public async Task OpenAsync()
    {
        var dialog = new ProvisioningDialogViewModel(_ctx);
        dialog.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ProvisioningDialogViewModel.Status))
            {
                IsOn = dialog.IsOn;
            }
        };
        await _dialogs.ShowAsync(dialog).ConfigureAwait(true);
    }
}
