using System.Text.Json;

using Oadm.Plugins.Provisioning.Model;
using Oadm.Sdk.Tasks;

namespace Oadm.Plugins.Provisioning.Nodes;

/// <summary>
/// A node that starts the existing task of its area with a payload (README "Adding a node"): the task's own checks (API
/// requirements, validation before the first write) apply, the node maps its outcome. Done: Success with
/// <see cref="Done"/>; Done with warnings: Warning with the task's message; Failed or Cancelled: Error with it. The task is
/// never cancelled by the flow (a Stop only stops waiting): a device change runs to its end.
/// </summary>
/// <typeparam name="TSettings">The node's settings record.</typeparam>
public abstract class TaskNode<TSettings> : IProvisioningNode
    where TSettings : class
{
    /// <summary>"The settings of this node cannot be read."</summary>
    public const string Unreadable = "The settings of this node cannot be read.";

    public abstract NodeKind Kind { get; }

    public abstract string Name { get; }

    /// <summary>The id of the task plugin this node starts ("oadm.datetime").</summary>
    public abstract string TaskPluginId { get; }

    public abstract object DefaultSettings { get; }

    /// <summary>The one error of readable settings, or null.</summary>
    protected abstract string? CheckSettings(TSettings settings, ProfileCheckContext context);

    /// <summary>The task's payload JSON for this camera (the task validates it again before it writes).</summary>
    protected abstract string Payload(TSettings settings, NodeRun run);

    /// <summary>The message of a task that ended Done ("Time zone Europe/Vienna, NTP 10.0.0.17.").</summary>
    protected abstract string Done(TSettings settings, NodeRun run);

    /// <summary>A result without a task (the node does not apply to this camera), or null to run the task.</summary>
    protected virtual Task<NodeResult?> BeforeAsync(TSettings settings, NodeRun run, CancellationToken ct) => Task.FromResult<NodeResult?>(null);

    /// <summary>The result of an ended task.</summary>
    protected virtual NodeResult Map(TaskOutcome outcome, TSettings settings, NodeRun run)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        var message = string.IsNullOrWhiteSpace(outcome.Message) ? null : outcome.Message.Trim();
        return outcome.State switch
        {
            TaskEndState.Done => NodeResult.Success(Done(settings, run)),
            TaskEndState.DoneWithWarnings => NodeResult.Warning(message ?? Done(settings, run)),
            TaskEndState.Cancelled => NodeResult.Error(message ?? "The task was cancelled."),
            _ => NodeResult.Error(message ?? "The task failed."),
        };
    }

    public string? Check(JsonElement? settings, ProfileCheckContext context) =>
        NodeSettings.Read<TSettings>(settings) is { } read ? CheckSettings(read, context) : Unreadable;

    public virtual string? Warning(JsonElement? settings, ProfileCheckContext context) => null;

    public async Task<NodeResult> RunAsync(NodeRun run, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (run.SettingsAs<TSettings>() is not { } settings)
        {
            return NodeResult.Error(Unreadable);
        }

        if (CheckSettings(settings, ProfileCheckContext.Empty) is { } error)
        {
            return NodeResult.Error(error);
        }

        if (run.Camera.DeviceId is not { } deviceId)
        {
            return NodeResult.Error(NodeTexts.NotInOadm);
        }

        if (await BeforeAsync(settings, run, ct).ConfigureAwait(false) is { } early)
        {
            return early;
        }

        var outcome = await TaskRuns.RunTaskAsync(run.Host, TaskPluginId, deviceId, Payload(settings, run), ct).ConfigureAwait(false);
        return outcome is null ? NodeResult.Error(NodeTexts.TaskLost(Name)) : Map(outcome, settings, run);
    }
}

/// <summary>Starts the task of a node and waits for its end.</summary>
public static class TaskRuns
{
    /// <summary>
    /// Starts <paramref name="pluginId"/> on the device and waits for its end (polling, <see cref="ProvisioningOptions.TaskPoll"/>,
    /// at most <see cref="ProvisioningOptions.TaskTimeout"/>). Null when the task ended without an outcome or did not start.
    /// </summary>
    public static async Task<TaskOutcome?> RunTaskAsync(INodeHost host, string pluginId, Guid deviceId, string payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host);
        IReadOnlyList<Guid> ids;
        try
        {
            ids = await host.Tasks.RunAsync(pluginId, [deviceId], payloadJson, host.Owner, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            return new TaskOutcome(deviceId, TaskEndState.Failed, ex.Message);
        }

        if (ids.Count == 0)
        {
            return null;
        }

        var taskId = ids[0];
        var end = host.Time.GetUtcNow() + host.Options.TaskTimeout;
        while (true)
        {
            // Not active is read before the outcome: a task that ends between the two reads is seen with its outcome.
            var active = host.Tasks.IsActive(taskId);
            if (await host.Tasks.GetOutcomeAsync(taskId, ct).ConfigureAwait(false) is { } outcome)
            {
                return outcome;
            }

            if (active == false)
            {
                return null;
            }

            if (host.Time.GetUtcNow() >= end)
            {
                return new TaskOutcome(deviceId, TaskEndState.Failed, NodeTexts.TaskTooLong(host.Options.TaskTimeout));
            }

            await Task.Delay(host.Options.TaskPoll, host.Time, ct).ConfigureAwait(false);
        }
    }
}

/// <summary>Texts the nodes share.</summary>
public static class NodeTexts
{
    public const string NotInOadm = "The camera is not in OADM. Add to OADM did not work.";

    public static string TaskLost(string node) => $"The task of {node} ended without a result.";

    public static string TaskTooLong(TimeSpan timeout) => $"The task did not end within {timeout.TotalMinutes:0} minutes.";
}

/// <summary>The JSON options of the plugin's settings, page calls and events.</summary>
public static class ProvisioningJson
{
    public static readonly JsonSerializerOptions Options = Sdk.Plugins.PluginJson.CreateOptions(Sdk.Plugins.PluginJsonEnums.CamelCase);
}
