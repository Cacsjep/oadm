using Oadm.Plugins.Provisioning.Engine;
using Oadm.Plugins.Provisioning.Nodes;
using Oadm.Sdk.Client;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Provisioning.Client;

/// <summary>
/// Run again on a failed "Provision with" task in the tasks pane (docs/spec/provisioning.md "Where it lives"): a new task for
/// that camera with the same profile that runs only the failed steps and those that never ran. Administrators who may run
/// provisioning only (the server checks it again).
/// </summary>
public sealed class RunAgainTaskAction : ITaskAction
{
    public string Id => "oadm.provisioning.run-again";

    public string Text => ProvisioningTexts.RunAgain;

    public string? IconKey => "refresh";

    public string? Tooltip => ProvisioningTexts.RunAgainTip;

    public string? RequiredPermission => ProvisioningButtonViewModel.ManagePermission;

    public bool AppliesTo(TaskActionTask task) =>
        task is { Status: TaskRunStatus.Failed } && task.DeviceId != Guid.Empty
        && string.Equals(task.PluginId, ProvisioningInfo.TaskPluginId, StringComparison.OrdinalIgnoreCase);

    public async Task RunAsync(IToolbarContext ctx, IReadOnlyList<TaskActionTask> tasks, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(tasks);
        var devices = tasks.Where(AppliesTo).Select(t => t.DeviceId).Distinct().ToList();
        if (devices.Count == 0)
        {
            return;
        }

        var json = await ctx.InvokePluginAsync(
            ProvisioningInfo.PluginId,
            ProvisioningMethods.RunAgain,
            PluginJson.Serialize(new DeviceIdsRequest(devices), ProvisioningJson.Options),
            ct).ConfigureAwait(true);
        if (PluginJson.Parse<RunAgainReply>(json, ProvisioningJson.Options).Started == 0)
        {
            await ctx.ShowMessageAsync(Text, ProvisioningTexts.NothingToRunAgain).ConfigureAwait(true);
        }
    }
}
