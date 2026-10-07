using System.Collections.ObjectModel;
using System.ComponentModel;
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

/// <summary>
/// The device, the steps and the log of one task, shown by the Details button. Follows the task live:
/// steps and the device row update with every task change, the log is re-read when a step or the task
/// state changes. Dispose (the window does on close) to stop following.
/// </summary>
public sealed partial class TaskDetailsViewModel : ObservableObject, IDisposable
{
    private readonly DeviceStore _devices;
    private readonly TimeProvider _time;
    private IOadmApi? _api;
    private bool _logLoading;
    private bool _logReloadPending;
    private bool _disposed;

    public TaskDetailsViewModel(TaskRowViewModel task, DeviceStore devices, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(devices);
        Task = task;
        _devices = devices;
        _time = timeProvider ?? TimeProvider.System;
        RefreshRows();
        RefreshSteps();
        task.PropertyChanged += OnTaskChanged;
    }

    public TaskRowViewModel Task { get; }

    [ObservableProperty] public partial IReadOnlyList<TaskDeviceRow> Rows { get; private set; } = [];

    public string Title => $"{Task.Name} - {Task.StateText}";

    /// <summary>The task's steps in order, updated live.</summary>
    public ObservableCollection<TaskStepRowViewModel> Steps { get; } = [];

    /// <summary>"3 of 6 steps finished", "No steps reported."</summary>
    [ObservableProperty] public partial string StepsStatus { get; private set; } = "";

    /// <summary>Task log, oldest first. Filled by <see cref="LoadLogAsync"/>.</summary>
    public ObservableCollection<TaskLogRow> Log { get; } = [];

    [ObservableProperty] public partial string LogStatus { get; private set; } = "";

    /// <summary>
    /// Loads the task log from the server and remembers <paramref name="api"/> to re-read it while the task
    /// runs. Failures are shown in <see cref="LogStatus"/>, never thrown.
    /// </summary>
    public async System.Threading.Tasks.Task LoadLogAsync(IOadmApi api, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(api);
        _api = api;
        _logLoading = true;
        try
        {
            IReadOnlyList<TaskLogEntry> entries = await api.GetTaskLogAsync(Task.Id, ct).ConfigureAwait(true);
            // The log only grows: append what is new, so the grid keeps its scroll position.
            if (entries.Count < Log.Count)
            {
                Log.Clear();
            }

            foreach (TaskLogEntry entry in entries.Skip(Log.Count))
            {
                Log.Add(ToRow(entry));
            }

            LogStatus = Log.Count == 0 ? "No log entries." : string.Create(CultureInfo.CurrentCulture, $"{Log.Count} log entries");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogStatus = "The log could not be loaded: " + ex.Message;
        }
        finally
        {
            _logLoading = false;
        }

        if (_logReloadPending && !_disposed)
        {
            _logReloadPending = false;
            await LoadLogAsync(api, ct).ConfigureAwait(true);
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            Task.PropertyChanged -= OnTaskChanged;
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

    private void OnTaskChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(TaskRowViewModel.Steps):
                RefreshSteps();
                break;
            case nameof(TaskRowViewModel.DeviceResults):
            case nameof(TaskRowViewModel.DeviceText):
                RefreshRows();
                break;
            case nameof(TaskRowViewModel.StateText):
                OnPropertyChanged(nameof(Title));
                ReloadLog();
                break;
            case nameof(TaskRowViewModel.CurrentStepIndex):
                ReloadLog();
                break;
        }
    }

    private void ReloadLog()
    {
        if (_api is not { } api || _disposed)
        {
            return;
        }

        if (_logLoading)
        {
            _logReloadPending = true;
            return;
        }

        _ = LoadLogAsync(api, CancellationToken.None);
    }

    private void RefreshRows()
    {
        Rows = [.. Task.DeviceResults.Select(r =>
        {
            DeviceRowViewModel? device = _devices.Find(r.DeviceId);
            return new TaskDeviceRow(TaskDeviceLabels.Label(r.DeviceId, _devices), device?.Serial ?? "", device?.Model ?? "",
                TaskRowViewModel.ToText(r.State), TaskRowViewModel.ToKind(r.State), r.Message, Math.Clamp(r.Progress, 0, 100));
        })];
    }

    private void RefreshSteps()
    {
        IReadOnlyList<TaskStep> steps = Task.Steps;
        DateTime now = _time.GetUtcNow().UtcDateTime;
        while (Steps.Count > steps.Count)
        {
            Steps.RemoveAt(Steps.Count - 1);
        }

        for (int i = 0; i < steps.Count; i++)
        {
            if (i == Steps.Count)
            {
                Steps.Add(new TaskStepRowViewModel());
            }

            Steps[i].Update(steps[i], now);
        }

        int finished = steps.Count(s => s.State is TaskStepState.Done or TaskStepState.Warning or TaskStepState.Skipped or TaskStepState.Failed);
        StepsStatus = steps.Count == 0
            ? "No steps reported."
            : string.Create(CultureInfo.CurrentCulture, $"{finished} of {steps.Count} steps finished");
    }

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
