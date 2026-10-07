using System.Collections.ObjectModel;
using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;

using Oadm.Client.Api;
using Oadm.Client.Devices;
using Oadm.Contracts.V1;

namespace Oadm.Client.Tasks;

public sealed record TaskDeviceRow(string Device, string Serial, string Model, string StateText, PillKind StateKind, string Message, int Progress)
{
    public bool IsStateOk => StateKind == PillKind.Ok;
    public bool IsStateWarning => StateKind == PillKind.Warning;
    public bool IsStateError => StateKind == PillKind.Error;
    public bool IsStateAccent => StateKind == PillKind.Accent;
    public bool IsStateNeutral => StateKind == PillKind.Neutral;
}

/// <summary>One line of the task log in the details window.</summary>
public sealed record TaskLogRow(DateTime Time, string LevelText, PillKind LevelKind, string Device, string Message)
{
    public string TimeText => Time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);
    public bool IsWarning => LevelKind == PillKind.Warning;
    public bool IsError => LevelKind == PillKind.Error;
}

/// <summary>Per-device results and the log of one task, shown by the Details button.</summary>
public sealed partial class TaskDetailsViewModel : ObservableObject
{
    private readonly DeviceStore _devices;

    public TaskDetailsViewModel(TaskRowViewModel task, DeviceStore devices)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(devices);
        Task = task;
        _devices = devices;
        Rows = task.DeviceResults.Select(r =>
        {
            DeviceRowViewModel? device = devices.Find(r.DeviceId);
            return new TaskDeviceRow(TaskDeviceLabels.Label(r.DeviceId, devices), device?.Serial ?? "", device?.Model ?? "",
                TaskRowViewModel.ToText(r.State), TaskRowViewModel.ToKind(r.State), r.Message, Math.Clamp(r.Progress, 0, 100));
        }).ToList();
    }

    public TaskRowViewModel Task { get; }
    public IReadOnlyList<TaskDeviceRow> Rows { get; }
    public string Title => $"{Task.Name} - {Task.StateText}";

    /// <summary>Task log, oldest first. Filled by <see cref="LoadLogAsync"/>.</summary>
    public ObservableCollection<TaskLogRow> Log { get; } = [];

    [ObservableProperty] public partial string LogStatus { get; private set; } = "";

    /// <summary>Loads the task log from the server. Failures are shown in <see cref="LogStatus"/>, never thrown.</summary>
    public async System.Threading.Tasks.Task LoadLogAsync(IOadmApi api, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(api);
        try
        {
            IReadOnlyList<TaskLogEntry> entries = await api.GetTaskLogAsync(Task.Id, ct).ConfigureAwait(true);
            Log.Clear();
            foreach (TaskLogEntry entry in entries)
            {
                Log.Add(ToRow(entry));
            }

            LogStatus = Log.Count == 0 ? "No log entries." : string.Create(CultureInfo.CurrentCulture, $"{Log.Count} log entries");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogStatus = "The log could not be loaded: " + ex.Message;
        }
    }

    public static string ToText(TaskLogLevel level) => level switch
    {
        TaskLogLevel.Warning => "Warning",
        TaskLogLevel.Error => "Error",
        _ => "Info",
    };

    public static PillKind ToKind(TaskLogLevel level) => level switch
    {
        TaskLogLevel.Warning => PillKind.Warning,
        TaskLogLevel.Error => PillKind.Error,
        _ => PillKind.Neutral,
    };

    private TaskLogRow ToRow(TaskLogEntry entry)
    {
        string device = "";
        if (!string.IsNullOrEmpty(entry.DeviceId))
        {
            device = _devices.Find(entry.DeviceId)?.DisplayAddress ?? entry.DeviceId;
        }

        DateTime time = entry.Time is null ? DateTime.MinValue : entry.Time.ToDateTime().ToLocalTime();
        return new TaskLogRow(time, ToText(entry.Level), ToKind(entry.Level), device, entry.Message);
    }
}
