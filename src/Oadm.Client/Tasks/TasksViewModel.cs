using System.Collections.ObjectModel;
using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Microsoft.Extensions.Logging;

using Oadm.Client.Api;
using Oadm.Client.Devices;
using Oadm.Client.Dialogs;
using Oadm.Client.Infrastructure;
using Oadm.Client.Logging;

namespace Oadm.Client.Tasks;

/// <summary>The Tasks / Log pane below the device grid, also used as the full Tasks page.</summary>
public sealed partial class TasksViewModel : ObservableObject
{
    private readonly TaskStore _store;
    private readonly DeviceStore _devices;
    private readonly IOadmApi _api;
    private readonly IDialogService _dialogs;
    private readonly IClientSettingsStore _settings;
    private readonly ILogger<TasksViewModel> _logger;

    public TasksViewModel(TaskStore store, DeviceStore devices, IOadmApi api, IDialogService dialogs, LogStore log,
        IClientSettingsStore settings, ILogger<TasksViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(settings);
        _store = store;
        _devices = devices;
        _api = api;
        _dialogs = dialogs;
        _settings = settings;
        _logger = logger;
        LogEntries = log.Entries;
        IsExpanded = settings.Current.BottomPaneExpanded;
        IsLogTab = settings.Current.BottomPaneTab == 1;
        store.Changed += (_, task) =>
        {
            OnPropertyChanged(nameof(ActiveCount));
            OnPropertyChanged(nameof(HasActive));
            OnPropertyChanged(nameof(ActiveCountText));
            if (task is not null && task == SelectedTask)
            {
                NotifySelectionCommands();
            }
        };
    }

    public ObservableCollection<TaskRowViewModel> Tasks => _store.Tasks;
    public ObservableCollection<LogEntry> LogEntries { get; }

    public int ActiveCount => _store.ActiveCount;
    public bool HasActive => ActiveCount > 0;
    public string ActiveCountText => ActiveCount.ToString(CultureInfo.CurrentCulture);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DetailsCommand), nameof(CancelCommand), nameof(DeleteCommand))]
    public partial TaskRowViewModel? SelectedTask { get; set; }

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTasksTab))]
    public partial bool IsLogTab { get; set; }

    public bool IsTasksTab
    {
        get => !IsLogTab;
        set => IsLogTab = !value;
    }

    partial void OnIsExpandedChanged(bool value)
    {
        _settings.Current.BottomPaneExpanded = value;
        _settings.Save();
    }

    partial void OnIsLogTabChanged(bool value)
    {
        _settings.Current.BottomPaneTab = value ? 1 : 0;
        _settings.Save();
    }

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    [RelayCommand]
    private void ShowTasks()
    {
        IsLogTab = false;
        IsExpanded = true;
    }

    [RelayCommand]
    private void ShowLog()
    {
        IsLogTab = true;
        IsExpanded = true;
    }

    private bool HasSelection => SelectedTask is not null;
    private bool CanCancel => SelectedTask?.IsActive == true;
    private bool CanDelete => SelectedTask is { IsActive: false };

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DetailsAsync()
    {
        if (SelectedTask is { } task)
        {
            await _dialogs.ShowTaskDetailsAsync(new TaskDetailsViewModel(task, _devices)).ConfigureAwait(true);
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private async Task CancelAsync()
    {
        if (SelectedTask is not { } task)
        {
            return;
        }

        try
        {
            await _api.CancelTaskAsync(task.Id, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            LogActionFailed(_logger, ex, "cancel", task.Name);
            await _dialogs.ShowMessageAsync("Cancel task", "The task could not be cancelled: " + ex.Message).ConfigureAwait(true);
        }
    }

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private async Task DeleteAsync()
    {
        if (SelectedTask is not { } task)
        {
            return;
        }

        try
        {
            await _api.DeleteTaskAsync(task.Id, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            LogActionFailed(_logger, ex, "delete", task.Name);
            await _dialogs.ShowMessageAsync("Delete task", "The task could not be deleted: " + ex.Message).ConfigureAwait(true);
        }
    }

    private void NotifySelectionCommands()
    {
        DetailsCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not {Action} task {Name}")]
    private static partial void LogActionFailed(ILogger logger, Exception ex, string action, string name);
}
