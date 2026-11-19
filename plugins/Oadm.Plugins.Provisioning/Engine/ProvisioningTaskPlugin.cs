using Oadm.Plugins.Provisioning.Model;
using Oadm.Plugins.Provisioning.Nodes;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Provisioning.Engine;

/// <summary>
/// The task "Provision with &lt;profile&gt;" (docs/spec/provisioning.md rule 4): one per factory-new camera, one named step per
/// node (<see cref="ProvisioningService.ExecuteAsync"/>). Only Provisioning starts it (hidden from the menus, a page task: never
/// through TaskService.Run or the task tools), at most <see cref="ProvisioningOptions.MaxParallel"/> at once. The payload (the
/// profile as it was, the nodes to run) lives in memory only.
/// </summary>
public sealed class ProvisioningTaskPlugin(Func<ProvisioningService?> service, int maxParallel) : ITaskPlugin
{
    public const string NotRunning = "Provisioning is not running. Turn the Provisioning plugin on.";

    public string Id => ProvisioningInfo.TaskPluginId;

    public string DisplayName => "Provision";

    public string? IconKey => ProvisioningInfo.IconKey;

    public bool ShowInToolbar => false;

    public bool RequiresDialog => false;

    public bool ShowInMenus => false;

    public int? MaxParallelDevices => Math.Max(1, maxParallel);

    /// <summary>Any camera: the task's first step asks the camera itself whether it is new.</summary>
    public bool CanRun(IDeviceInfo device) => true;

    /// <summary>"Provision with Lobby".</summary>
    public string GetTaskName(string? payloadJson) =>
        PluginJson.TryParse<RunPayload>(payloadJson, ProvisioningJson.Options)?.Profile.Name?.Trim() is { Length: > 0 } name
            ? "Provision with " + name
            : DisplayName;

    public Task ExecuteAsync(ITaskExecutionContext ctx, IDeviceInfo device, string? payloadJson, CancellationToken ct) =>
        (service() ?? throw new InvalidOperationException(NotRunning)).ExecuteAsync(ctx, device, payloadJson, ct);
}
