using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;

using Google.Protobuf.WellKnownTypes;

using Oadm.Client.Devices;
using Oadm.Contracts.V1;

namespace Oadm.Client.Tasks;

/// <summary>One step in the task details window. Updated in place so the grid keeps its scroll position.</summary>
public sealed partial class TaskStepRowViewModel : ObservableObject
{
    [ObservableProperty] public partial string Number { get; private set; } = "";
    [ObservableProperty] public partial string Name { get; private set; } = "";
    [ObservableProperty] public partial TaskStepState State { get; private set; }
    [ObservableProperty] public partial string StateText { get; private set; } = "";
    [ObservableProperty] public partial PillKind StateKind { get; private set; }
    [ObservableProperty] public partial string Detail { get; private set; } = "";
    [ObservableProperty] public partial string DurationText { get; private set; } = "";
    [ObservableProperty] public partial int Progress { get; private set; }
    [ObservableProperty] public partial string ProgressText { get; private set; } = "";

    /// <summary>Only the running step shows a progress bar.</summary>
    [ObservableProperty] public partial bool IsRunning { get; private set; }

    public bool IsStateOk => StateKind == PillKind.Ok;
    public bool IsStateWarning => StateKind == PillKind.Warning;
    public bool IsStateError => StateKind == PillKind.Error;
    public bool IsStateAccent => StateKind == PillKind.Accent;

    public void Update(TaskStep step, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(step);
        Number = (step.Index + 1).ToString(CultureInfo.CurrentCulture);
        Name = step.Name;
        State = step.State;
        StateText = ToText(step.State);
        StateKind = ToKind(step.State);
        Detail = step.Detail;
        IsRunning = step.State == TaskStepState.Running;
        Progress = Math.Clamp(step.Progress, 0, 100);
        ProgressText = string.Create(CultureInfo.CurrentCulture, $"{Progress} %");
        DateTime? started = ToUtc(step.Started);
        DateTime? finished = ToUtc(step.Finished);
        DurationText = started is { } s ? FormatDuration((finished ?? (IsRunning ? nowUtc : s)) - s) : "";
    }

    public static string ToText(TaskStepState state) => state switch
    {
        TaskStepState.Pending => "Pending",
        TaskStepState.Running => "Running",
        TaskStepState.Done => "Done",
        TaskStepState.Warning => "Warning",
        TaskStepState.Skipped => "Skipped",
        TaskStepState.Failed => "Failed",
        _ => "Unknown",
    };

    public static PillKind ToKind(TaskStepState state) => state switch
    {
        TaskStepState.Running => PillKind.Accent,
        TaskStepState.Done => PillKind.Ok,
        TaskStepState.Warning => PillKind.Warning,
        TaskStepState.Failed => PillKind.Error,
        _ => PillKind.Neutral,
    };

    /// <summary>"0.4 s", "12 s", "2 min 05 s", "1 h 03 min".</summary>
    public static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        return duration.TotalSeconds < 10
            ? string.Create(CultureInfo.CurrentCulture, $"{duration.TotalSeconds:0.0} s")
            : duration.TotalMinutes < 1
                ? string.Create(CultureInfo.CurrentCulture, $"{(int)duration.TotalSeconds} s")
                : duration.TotalHours < 1
                    ? string.Create(CultureInfo.CurrentCulture, $"{(int)duration.TotalMinutes} min {duration.Seconds:00} s")
                    : string.Create(CultureInfo.CurrentCulture, $"{(int)duration.TotalHours} h {duration.Minutes:00} min");
    }

    partial void OnStateKindChanged(PillKind value)
    {
        OnPropertyChanged(nameof(IsStateOk));
        OnPropertyChanged(nameof(IsStateWarning));
        OnPropertyChanged(nameof(IsStateError));
        OnPropertyChanged(nameof(IsStateAccent));
    }

    private static DateTime? ToUtc(Timestamp? ts) =>
        ts is null || (ts.Seconds == 0 && ts.Nanos == 0) ? null : ts.ToDateTime();
}
