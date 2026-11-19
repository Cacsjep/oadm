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

    public abstract string StepName { get; }

    /// <summary>The id of the task plugin this node starts ("oadm.datetime").</summary>
    public abstract string TaskPluginId { get; }

    public abstract object DefaultSettings { get; }

    /// <summary>The one error of readable settings, or null.</summary>
    protected abstract string? CheckSettings(TSettings settings, ProfileCheckContext context);

    /// <summary>The task's payload JSON for this camera (the task validates it again before it writes).</summary>
    protected abstract string Payload(TSettings settings, NodeRun run);

    /// <summary>
    /// The payload when it needs a read first (a kept file as an upload, a secret): by default <see cref="Payload"/>. An
    /// <see cref="InvalidOperationException"/> is the node's Error with its message; nothing starts.
    /// </summary>
    protected virtual Task<string> PayloadAsync(TSettings settings, NodeRun run, CancellationToken ct) => Task.FromResult(Payload(settings, run));

    /// <summary>The task this camera's run starts (by default <see cref="TaskPluginId"/>; Certificate starts PKI's or EST's).</summary>
    protected virtual string TaskFor(TSettings settings) => TaskPluginId;

    /// <summary>Who starts the task: the plugin (default), or the user who turned provisioning on (their MyAxis sign-in); null: Error <see cref="NodeTexts.StartAgain"/>.</summary>
    protected virtual ITaskRunner? RunnerFor(NodeRun run) => run?.Host.Tasks;

    /// <summary>The result after the task: <paramref name="result"/> by default; a node with a second task runs it here.</summary>
    protected virtual Task<NodeResult> AfterAsync(TSettings settings, NodeRun run, Guid deviceId, NodeResult result, CancellationToken ct) => Task.FromResult(result);

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

        if (RunnerFor(run) is not { } runner)
        {
            return NodeResult.Error(NodeTexts.StartAgain);
        }

        string payload;
        try
        {
            payload = await PayloadAsync(settings, run, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return NodeResult.Error(ex.Message);
        }

        var outcome = await TaskRuns.RunTaskAsync(run.Host, runner, TaskFor(settings), deviceId, payload, ct).ConfigureAwait(false);
        var result = outcome is null ? NodeResult.Error(NodeTexts.TaskLost(Name)) : Map(outcome, settings, run);
        return await AfterAsync(settings, run, deviceId, result, ct).ConfigureAwait(false);
    }
}

/// <summary>Starts the task of a node and waits for its end.</summary>
public static class TaskRuns
{
    /// <summary>
    /// Starts <paramref name="pluginId"/> on the device and waits for its end (polling, <see cref="ProvisioningOptions.TaskPoll"/>,
    /// at most <see cref="ProvisioningOptions.TaskTimeout"/>). Null when the task ended without an outcome or did not start.
    /// </summary>
    public static Task<TaskOutcome?> RunTaskAsync(INodeHost host, string pluginId, Guid deviceId, string payloadJson, CancellationToken ct) =>
        RunTaskAsync(host, host?.Tasks ?? throw new ArgumentNullException(nameof(host)), pluginId, deviceId, payloadJson, ct);

    /// <summary>Like <see cref="RunTaskAsync(INodeHost, string, Guid, string, CancellationToken)"/> with another runner (the user who turned provisioning on).</summary>
    public static async Task<TaskOutcome?> RunTaskAsync(INodeHost host, ITaskRunner runner, string pluginId, Guid deviceId, string payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(runner);
        IReadOnlyList<Guid> ids;
        try
        {
            ids = await runner.RunAsync(pluginId, [deviceId], payloadJson, host.Owner, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException or UnauthorizedAccessException or Sdk.Devices.DevicesNotAvailableException)
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
            var active = runner.IsActive(taskId);
            if (await runner.GetOutcomeAsync(taskId, ct).ConfigureAwait(false) is { } outcome)
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
    public const string NotInOadm = "The camera is no longer in OADM.";

    /// <summary>Set password and the check of a camera OADM reaches by a name: they need its IP address.</summary>
    public const string NeedsIpAddress = "OADM reaches this camera by a name. Provisioning needs its IP address.";

    public static string TaskLost(string node) => $"The task of {node} ended without a result.";

    /// <summary>A node that needs the administrator who turned provisioning on, after a server restart.</summary>
    public const string StartAgain = "Turn provisioning off and on again: the server restarted since it was turned on.";

    /// <summary>The message of a task's end without its " Nothing was changed." tail.</summary>
    public static string Plain(string? message)
    {
        var text = message?.Trim() ?? string.Empty;
        foreach (var tail in (string[])[" Nothing was changed.", " Nothing was sent.", " Nothing was installed."])
        {
            if (text.EndsWith(tail, StringComparison.Ordinal))
            {
                text = text[..^tail.Length].TrimEnd();
            }
        }

        return text;
    }

    public static string TaskTooLong(TimeSpan timeout) => $"The task did not end within {timeout.TotalMinutes:0} minutes.";
}

/// <summary>The JSON options of the plugin's settings, page calls and events.</summary>
public static class ProvisioningJson
{
    public static readonly JsonSerializerOptions Options = Sdk.Plugins.PluginJson.CreateOptions(Sdk.Plugins.PluginJsonEnums.CamelCase);
}
