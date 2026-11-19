using CommunityToolkit.Mvvm.Input;

using Oadm.Sdk.Client;

namespace Oadm.Client.Tasks;

/// <summary>A plugin's command on the selected task (<see cref="ITaskAction"/>) as a button of the tasks pane's header.</summary>
public sealed class TaskActionItem
{
    internal TaskActionItem(ITaskAction action, TaskActionTask task, Func<TaskActionItem, Task> run)
    {
        ArgumentNullException.ThrowIfNull(run);
        Action = action ?? throw new ArgumentNullException(nameof(action));
        Task = task ?? throw new ArgumentNullException(nameof(task));
        RunCommand = new AsyncRelayCommand(() => run(this));
    }

    public ITaskAction Action { get; }

    public TaskActionTask Task { get; }

    public string Text => Action.Text;

    public string? IconKey => Action.IconKey ?? "plugin";

    public string? Tooltip => Action.Tooltip;

    /// <summary>Runs the action; disabled while it runs.</summary>
    public IAsyncRelayCommand RunCommand { get; }
}
