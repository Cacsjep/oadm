using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;

using Google.Protobuf.WellKnownTypes;

using Oadm.Client.Devices;
using Oadm.Contracts.V1;

namespace Oadm.Client.Tasks;

/// <summary>One row of the Tasks grid.</summary>
public sealed partial class TaskRowViewModel : ObservableObject
{
    public TaskRowViewModel(TaskInfo task)
    {
        ArgumentNullException.ThrowIfNull(task);
        Id = task.Id;
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
    [ObservableProperty] public partial IReadOnlyList<TaskDeviceResult> DeviceResults { get; private set; } = [];

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
