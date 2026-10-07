using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;

using Google.Protobuf.WellKnownTypes;

using Oadm.Client.Devices;
using Oadm.Contracts.V1;

namespace Oadm.Client.Tasks;

/// <summary>One row of the Tasks grid.</summary>
public sealed partial class TaskRowViewModel : ObservableObject
{
    private readonly DeviceStore _devices;

    public TaskRowViewModel(TaskInfo task, DeviceStore devices)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(devices);
        Id = task.Id;
        _devices = devices;
        Update(task);
    }

    public string Id { get; }

    [ObservableProperty] public partial string PluginId { get; private set; } = "";
    [ObservableProperty] public partial string Name { get; private set; } = "";
    [ObservableProperty] public partial TaskState State { get; private set; }
    [ObservableProperty] public partial string StateText { get; private set; } = "";
    [ObservableProperty] public partial PillKind StateKind { get; private set; }
    [ObservableProperty] public partial string Owner { get; private set; } = "";
    [ObservableProperty] public partial DateTime? StartTime { get; private set; }
    [ObservableProperty] public partial string StartTimeText { get; private set; } = "";
    [ObservableProperty] public partial DateTime Created { get; private set; }
    [ObservableProperty] public partial int Progress { get; private set; }
    [ObservableProperty] public partial string ProgressText { get; private set; } = "";
    [ObservableProperty] public partial bool IsActive { get; private set; }

    /// <summary>Shown right of the status icon: the error or warning message, empty otherwise (icon only).</summary>
    [ObservableProperty] public partial string StatusMessage { get; private set; } = "";

    [ObservableProperty] public partial string StatusTooltip { get; private set; } = "";
    [ObservableProperty] public partial IReadOnlyList<TaskDeviceResult> DeviceResults { get; private set; } = [];

    /// <summary>The task's device (a task always targets exactly one device); empty for a malformed task.</summary>
    [ObservableProperty] public partial string DeviceId { get; private set; } = "";

    /// <summary>Shared by the tasks started together (one per selected device).</summary>
    [ObservableProperty] public partial string BatchId { get; private set; } = "";

    /// <summary>The Device cell: the address as in the device grid, see <see cref="TaskDeviceLabels"/>. Also the sort key.</summary>
    [ObservableProperty] public partial string DeviceText { get; private set; } = "";

    /// <summary>"10.0.0.48: Failed - Connection refused". Null without a device.</summary>
    [ObservableProperty] public partial string? DeviceTooltip { get; private set; }

    /// <summary>The task's named steps, in order (empty for plugins without steps).</summary>
    [ObservableProperty] public partial IReadOnlyList<TaskStep> Steps { get; private set; } = [];

    /// <summary>Index of the running step, else of the last started one; -1 without one.</summary>
    [ObservableProperty] public partial int CurrentStepIndex { get; private set; } = -1;

    /// <summary>The Current step cell: "Step 3/6 · Upload firmware · 45 %"; empty without steps.</summary>
    [ObservableProperty] public partial string CurrentStepText { get; private set; } = "";

    /// <summary>The full current step text plus its detail (progress detail, result or error); null without steps.</summary>
    [ObservableProperty] public partial string? CurrentStepTooltip { get; private set; }

    public bool IsStateOk => StateKind == PillKind.Ok;
    public bool IsStateWarning => StateKind == PillKind.Warning;
    public bool IsStateError => StateKind == PillKind.Error;
    public bool IsStateAccent => StateKind == PillKind.Accent;
    public bool IsStateNeutral => StateKind == PillKind.Neutral;

    public void Update(TaskInfo task)
    {
        ArgumentNullException.ThrowIfNull(task);
        PluginId = task.PluginId;
        Name = task.Name;
        State = task.State;
        StateText = ToText(task.State);
        StateKind = ToKind(task.State);
        Owner = task.Owner;
        Created = ToLocal(task.Created) ?? DateTime.MinValue;
        StartTime = ToLocal(task.Started);
        StartTimeText = StartTime?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture) ?? "";
        Progress = Math.Clamp(task.Progress, 0, 100);
        ProgressText = string.Create(CultureInfo.CurrentCulture, $"{Progress} %");
        IsActive = task.State is TaskState.Queued or TaskState.Running;
        DeviceResults = task.Devices.ToList();
        DeviceId = !string.IsNullOrEmpty(task.DeviceId) ? task.DeviceId : DeviceResults.Count > 0 ? DeviceResults[0].DeviceId : "";
        BatchId = task.BatchId;
        string message = DeviceResults.Count > 0 ? DeviceResults[0].Message : "";
        StatusMessage = task.State is TaskState.Failed or TaskState.DoneWithWarnings ? message : "";
        StatusTooltip = string.IsNullOrEmpty(message) ? StateText : $"{StateText}: {message}";
        Steps = task.Steps.ToList();
        CurrentStepIndex = Steps.Count == 0 ? -1 : task.CurrentStepIndex;
        TaskStep? current = CurrentStepIndex >= 0 && CurrentStepIndex < Steps.Count ? Steps[CurrentStepIndex] : null;
        CurrentStepText = FormatCurrentStep(current, Steps.Count);
        CurrentStepTooltip = CurrentStepText.Length == 0 ? null
            : string.IsNullOrEmpty(current?.Detail) ? CurrentStepText : $"{CurrentStepText}\n{current.Detail}";
        ResolveDevices();
    }

    /// <summary>"Step 3/6 · Upload firmware", plus " · 45 %" while a running step reports progress.</summary>
    public static string FormatCurrentStep(TaskStep? step, int count)
    {
        if (step is null || count == 0)
        {
            return count == 0 ? "" : string.Create(CultureInfo.CurrentCulture, $"{count} steps planned");
        }

        string text = string.Create(CultureInfo.CurrentCulture, $"Step {step.Index + 1}/{count} \u00B7 {step.Name}");
        return step.State == TaskStepState.Running && step.Progress is > 0 and < 100
            ? string.Create(CultureInfo.CurrentCulture, $"{text} \u00B7 {step.Progress} %")
            : text;
    }

    /// <summary>Re-reads the device address from the device store (the device was added, removed or changed).</summary>
    public void ResolveDevices()
    {
        DeviceText = DeviceId.Length == 0 ? "" : TaskDeviceLabels.Label(DeviceId, _devices);
        DeviceTooltip = TaskDeviceLabels.Tooltip(DeviceResults.Count > 0 ? DeviceResults[0] : null, DeviceText);
    }

    public static string ToText(TaskState state) => state switch
    {
        TaskState.Queued => "Queued",
        TaskState.Running => "Running",
        TaskState.Done => "Done",
        TaskState.Failed => "Failed",
        TaskState.Cancelled => "Cancelled",
        TaskState.DoneWithWarnings => "Done with warnings",
        _ => "Unknown",
    };

    public static PillKind ToKind(TaskState state) => state switch
    {
        TaskState.Running => PillKind.Accent,
        TaskState.Done => PillKind.Ok,
        TaskState.DoneWithWarnings => PillKind.Warning,
        TaskState.Failed => PillKind.Error,
        _ => PillKind.Neutral,
    };

    partial void OnStateKindChanged(PillKind value)
    {
        OnPropertyChanged(nameof(IsStateOk));
        OnPropertyChanged(nameof(IsStateWarning));
        OnPropertyChanged(nameof(IsStateError));
        OnPropertyChanged(nameof(IsStateAccent));
        OnPropertyChanged(nameof(IsStateNeutral));
    }

    private static DateTime? ToLocal(Timestamp? ts) =>
        ts is null || (ts.Seconds == 0 && ts.Nanos == 0) ? null : ts.ToDateTime().ToLocalTime();
}
