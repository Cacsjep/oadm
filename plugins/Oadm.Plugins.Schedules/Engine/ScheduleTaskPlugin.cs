using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Schedules.Engine;

/// <summary>
/// The task "Run &lt;schedule&gt;" (docs/spec/schedules.md "A run"): one per device and run, one named step per action
/// (<see cref="ScheduleEngine.ExecuteAsync"/>). Only Schedules starts it (hidden from the menus, a page task: never through
/// TaskService.Run or the task tools). The server setting Parallel tasks per plugin decides how many run at once. The payload (the
/// run, the actions as they were) lives in memory only.
/// </summary>
internal sealed class ScheduleTaskPlugin(Func<ScheduleEngine?> engine) : ITaskPlugin
{
    public const string NotRunning = "Schedules is not running. Turn the plugin on, or restart the OADM server.";

    public string Id => SchedulesInfo.TaskPluginId;

    public string DisplayName => "Run schedule";

    public string? IconKey => SchedulesInfo.IconKey;

    public bool ShowInToolbar => false;

    public bool RequiresDialog => false;

    public bool ShowInMenus => false;

    /// <summary>The device is checked when its turn comes (Ok only); the actions' tasks check their APIs.</summary>
    public bool CanRun(IDeviceInfo device) => true;

    /// <summary>"Run Firmware upgrade".</summary>
    public string GetTaskName(string? payloadJson) =>
        PluginJson.TryParse<RunTaskPayload>(payloadJson, SchedulesJson.Options) is { } payload ? TaskNameOf(payload.Name) : DisplayName;

    /// <summary>"Run Firmware upgrade": the task name and the target of its audit entry.</summary>
    public static string TaskNameOf(string? schedule) => string.IsNullOrWhiteSpace(schedule) ? "Run schedule" : "Run " + schedule.Trim();

    public Task ExecuteAsync(ITaskExecutionContext ctx, IDeviceInfo device, string? payloadJson, CancellationToken ct) =>
        (engine() ?? throw new InvalidOperationException(NotRunning)).ExecuteAsync(ctx, device, payloadJson, ct);
}
