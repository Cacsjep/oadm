using Avalonia.Controls;

using Oadm.Plugins.Provisioning.Client;
using Oadm.Plugins.Provisioning.Engine;
using Oadm.Sdk.Client;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Provisioning.Tests.Client;

/// <summary>The Devices page's context as the Provisioning button and Run again see it: the plugin's context, messages.</summary>
internal sealed class FakeToolbarContext(FakePageContext plugin) : IToolbarContext
{
    public FakePageContext Plugin { get; } = plugin;

    public List<(string Title, string Message)> Messages { get; } = [];

    public IReadOnlyList<IDeviceInfo> SelectedDevices => [];

    public IReadOnlyList<IDeviceInfo> Devices => [];

    public event EventHandler? SelectionChanged { add { } remove { } }

    public event EventHandler? DevicesChanged { add { } remove { } }

    public IReadOnlyList<ToolbarTaskPlugin> TaskPlugins => [];

    public event EventHandler? TaskPluginsChanged { add { } remove { } }

    public Window? Owner => null;

    public bool CanRunTask(string pluginId) => false;

    public Task<IReadOnlyList<string>?> RunTaskAsync(string pluginId, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>?>(null);

    public Task OpenAsync(string hostPage) => Task.CompletedTask;

    public Task RemoveDevicesAsync(IReadOnlyCollection<Guid> deviceIds, CancellationToken ct) => Task.CompletedTask;

    public Task ShowMessageAsync(string title, string message)
    {
        Messages.Add((title, message));
        return Task.CompletedTask;
    }

    public Task<bool> ConfirmAsync(string title, string message, string confirmText) => Task.FromResult(true);

    public Task<string?> QueryAsync(string pluginId, Guid deviceId, string method, string? payloadJson, CancellationToken ct) => Task.FromResult<string?>(null);

    public Task<UploadedFile> UploadAsync(string localPath, IProgress<double>? progress, CancellationToken ct) => throw new NotSupportedException();

    public Task<string?> InvokePluginAsync(string pluginId, string method, string? payloadJson, CancellationToken ct)
    {
        Assert.Equal(ProvisioningInfo.PluginId, pluginId);
        return Plugin.InvokeAsync(method, payloadJson, ct);
    }

    public ICorePluginClientContext? CorePluginContext(string pluginId) => pluginId == ProvisioningInfo.PluginId ? Plugin : null;
}

/// <summary>Provisioning management as the button opens it: recorded, never shown.</summary>
internal sealed class FakeProvisioningDialogs : IProvisioningDialogs
{
    public List<ProvisioningDialogViewModel> Shown { get; } = [];

    public Task ShowAsync(ProvisioningDialogViewModel dialog)
    {
        Shown.Add(dialog);
        return Task.CompletedTask;
    }
}

/// <summary>The Devices toolbar's Provisioning button and Run again of the tasks pane.</summary>
public sealed class ToolbarAndRunAgainTests
{
    private static FakePageContext Context(bool on)
    {
        var ctx = Samples.Context(Samples.Status(on));
        ctx.Answers[ProvisioningMethods.State] = _ => Samples.Serialize(new StateReply(on));
        return ctx;
    }

    [Fact]
    public async Task The_button_reads_the_state_and_turns_green_with_the_status_events()
    {
        var ctx = Context(on: false);
        var vm = new ProvisioningButtonViewModel(ctx, new FakeProvisioningDialogs());
        using var cts = new CancellationTokenSource();
        var watch = vm.WatchAsync(cts.Token);
        await Oadm.Sdk.Testing.Eventually.TrueAsync(() => ctx.CallsOf(ProvisioningMethods.State) == 1, "the first read");
        Assert.False(vm.IsOn);
        Assert.Equal(DeviceTexts.Provisioning, vm.Text);

        ctx.Push(ProvisioningMethods.StatusTopic, Samples.Status(on: true));
        await Oadm.Sdk.Testing.Eventually.TrueAsync(() => vm.IsOn, "the pushed status");
        Assert.Equal(ProvisioningTexts.ButtonActive, vm.Text);

        ctx.Push(ProvisioningMethods.StatusTopic, Samples.Status(on: false));
        await Oadm.Sdk.Testing.Eventually.TrueAsync(() => !vm.IsOn, "off again");
        await cts.CancelAsync();
        await watch;
    }

    [Fact]
    public void Only_an_administrator_with_the_right_opens_provisioning_management()
    {
        var ctx = Context(on: true);
        var admin = new ProvisioningButtonViewModel(ctx, new FakeProvisioningDialogs());
        Assert.True(admin.OpenCommand.CanExecute(null));
        Assert.Equal(ProvisioningTexts.ButtonTip, admin.Tooltip);

        ctx.IsAdministrator = false;
        var user = new ProvisioningButtonViewModel(ctx, new FakeProvisioningDialogs());
        Assert.False(user.OpenCommand.CanExecute(null));
        Assert.Equal(ProvisioningTexts.AdministratorOnly, user.Tooltip);

        ctx.IsAdministrator = true;
        ctx.Denied.Add(ProvisioningButtonViewModel.ManagePermission);
        Assert.False(new ProvisioningButtonViewModel(ctx, new FakeProvisioningDialogs()).OpenCommand.CanExecute(null));
    }

    [Fact]
    public async Task The_button_opens_management_and_follows_what_it_changed()
    {
        var ctx = Context(on: false);
        ctx.Answers[ProvisioningMethods.GetStatus] = _ => Samples.Serialize(Samples.Status(on: false));
        var dialogs = new FakeProvisioningDialogs();
        var vm = new ProvisioningButtonViewModel(ctx, dialogs);
        await vm.OpenCommand.ExecuteAsync(null);
        var dialog = Assert.Single(dialogs.Shown);
        await dialog.LoadAsync();
        await dialog.ToggleCommand.ExecuteAsync(null);
        Assert.True(vm.IsOn);
    }

    [Fact]
    public void The_toolbar_entry_sits_right_of_export_for_users_who_may_see_provisioning()
    {
        var plugin = new ProvisioningToolbarPlugin();
        Assert.Equal(ToolbarGroup.Manage, plugin.Group);
        Assert.True(plugin.Order > new Oadm.Client.Devices.Toolbar.ExportToolbarPlugin().Order);
        Assert.Equal("oadm.provisioning:show", plugin.RequiredPermission);
    }

    private static TaskActionTask Task(TaskRunStatus status, string pluginId = ProvisioningInfo.TaskPluginId) =>
        new("t1", pluginId, Guid.Parse("11111111-2222-3333-4444-555555555555"), status, "Provision with Lobby cameras");

    [Fact]
    public void Run_again_fits_failed_provisioning_tasks_only()
    {
        var action = new RunAgainTaskAction();
        Assert.True(action.AppliesTo(Task(TaskRunStatus.Failed)));
        Assert.False(action.AppliesTo(Task(TaskRunStatus.Done)));
        Assert.False(action.AppliesTo(Task(TaskRunStatus.Running)));
        Assert.False(action.AppliesTo(Task(TaskRunStatus.Failed, "oadm.restart")));
        Assert.False(action.AppliesTo(Task(TaskRunStatus.Failed) with { DeviceId = Guid.Empty }));
        Assert.Equal("oadm.provisioning:manage", action.RequiredPermission);
    }

    [Fact]
    public async Task Run_again_asks_the_server_for_the_camera_and_says_when_nothing_started()
    {
        var page = Context(on: true);
        page.Answers[ProvisioningMethods.RunAgain] = _ => Samples.Serialize(new RunAgainReply(1, 0));
        var ctx = new FakeToolbarContext(page);
        var action = new RunAgainTaskAction();

        await action.RunAsync(ctx, [Task(TaskRunStatus.Failed), Task(TaskRunStatus.Done)], CancellationToken.None);
        var request = System.Text.Json.JsonSerializer.Deserialize<DeviceIdsRequest>(page.LastPayloadOf(ProvisioningMethods.RunAgain)!, Samples.Json)!;
        Assert.Equal([Task(TaskRunStatus.Failed).DeviceId], request.DeviceIds);
        Assert.Empty(ctx.Messages);

        page.Answers[ProvisioningMethods.RunAgain] = _ => Samples.Serialize(new RunAgainReply(0, 1));
        await action.RunAsync(ctx, [Task(TaskRunStatus.Failed)], CancellationToken.None);
        Assert.Equal((ProvisioningTexts.RunAgain, ProvisioningTexts.NothingToRunAgain), Assert.Single(ctx.Messages));

        await action.RunAsync(ctx, [Task(TaskRunStatus.Done)], CancellationToken.None);
        Assert.Equal(2, page.CallsOf(ProvisioningMethods.RunAgain)); // nothing that fits: no call
    }
}
