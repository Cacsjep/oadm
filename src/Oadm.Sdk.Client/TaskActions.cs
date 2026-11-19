namespace Oadm.Sdk.Client;

/// <summary>
/// SDK 0.15.0: a command of a client plugin on the selected task of the tasks pane, e.g. Run again of Provisioning on a failed
/// "Provision with" task. The host shows it in the tasks pane's header while <see cref="AppliesTo"/> holds for the selected
/// task and the user's role has <see cref="RequiredPermission"/> (the plugin package must be on). Found like the other client
/// plugin parts: in a <c>*.Client.dll</c> with a public parameterless constructor.
/// </summary>
public interface ITaskAction
{
    /// <summary>Unique id, e.g. "oadm.provisioning.run-again".</summary>
    string Id { get; }

    /// <summary>The button text ("Run again").</summary>
    string Text { get; }

    /// <summary>Theme icon key of the button ("refresh"); null: the plugin icon.</summary>
    string? IconKey => null;

    /// <summary>The button's tooltip (at most about 8 words); null: none.</summary>
    string? Tooltip => null;

    /// <summary>The permission the action needs ("oadm.provisioning:manage"); null (the default): every user.</summary>
    string? RequiredPermission => null;

    /// <summary>Whether the action fits <paramref name="task"/>; called on the UI thread whenever the selection or its state changes: keep it cheap.</summary>
    bool AppliesTo(TaskActionTask task);

    /// <summary>
    /// Runs the action on <paramref name="tasks"/> (those it applies to) on the UI thread, with the Devices page's context for
    /// server calls and dialogs. A failure is shown by the host in its message window.
    /// </summary>
    Task RunAsync(IToolbarContext ctx, IReadOnlyList<TaskActionTask> tasks, CancellationToken ct);
}

/// <summary>A task of the tasks pane as a <see cref="ITaskAction"/> sees it (SDK 0.15.0).</summary>
/// <param name="TaskId">The task's id on the server.</param>
/// <param name="PluginId">The task plugin that runs it ("oadm.provisioning.run").</param>
/// <param name="DeviceId">The task's device.</param>
/// <param name="Status">Where it stands.</param>
/// <param name="Name">The task's name as the tasks pane shows it.</param>
public sealed record TaskActionTask(string TaskId, string PluginId, Guid DeviceId, TaskRunStatus Status, string Name);
