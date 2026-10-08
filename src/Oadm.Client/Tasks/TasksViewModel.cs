using System.Collections.ObjectModel;
using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Microsoft.Extensions.Logging;

using Oadm.Client.Api;
using Oadm.Client.Devices;
using Oadm.Client.Dialogs;
using Oadm.Client.Infrastructure;

namespace Oadm.Client.Tasks;

/// <summary>The resizable, collapsible Tasks pane below the device grid.</summary>
public sealed partial class TasksViewModel : ObservableObject
{
    private readonly TaskStore _store;
    private readonly DeviceStore _devices;
    private readonly IOadmApi _api;
    private readonly IDialogService _dialogs;
    private readonly IClientSettingsStore _settings;
    private readonly ILogger<TasksViewModel> _logger;

    public const double DefaultPaneHeight = 260;
    public const double MinPaneHeight = 120;

    public TasksViewModel(TaskStore store, DeviceStore devices, IOadmApi api, IDialogService dialogs,
        IClientSettingsStore settings, ILogger<TasksViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(settings);
        _store = store;
        _devices = devices;
        _api = api;
        _dialogs = dialogs;
        _settings = settings;
        _logger = logger;
        IsExpanded = settings.Current.BottomPaneExpanded;
        PaneHeight = settings.Current.TasksPaneHeight >= MinPaneHeight ? settings.Current.TasksPaneHeight : DefaultPaneHeight;
        store.Tasks.CollectionChanged += (_, _) =>
        {
            DeleteAllCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(IsEmpty));
        };
        store.Changed += (_, task) =>
        {
            OnPropertyChanged(nameof(ActiveCount));
            OnPropertyChanged(nameof(HasActive));
            OnPropertyChanged(nameof(ActiveCountText));
            DeleteAllCommand.NotifyCanExecuteChanged();
            if (task is null || task == SelectedTask)
            {
                NotifySelectionCommands();
            }
        };
    }

    public ObservableCollection<TaskRowViewModel> Tasks => _store.Tasks;

    /// <summary>No task in the history: the grid shows a one-line hint.</summary>
    public bool IsEmpty => _store.Tasks.Count == 0;

    public int ActiveCount => _store.ActiveCount;
    public bool HasActive => ActiveCount > 0;
    public string ActiveCountText => ActiveCount.ToString(CultureInfo.CurrentCulture);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DetailsCommand), nameof(CancelCommand), nameof(DeleteCommand))]
    public partial TaskRowViewModel? SelectedTask { get; set; }

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    /// <summary>Height of the expanded pane, changed by the splitter above it. Persisted per client.</summary>
    [ObservableProperty]
    public partial double PaneHeight { get; set; }

    partial void OnIsExpandedChanged(bool value)
    {
        _settings.Current.BottomPaneExpanded = value;
        _settings.Save();
    }

    partial void OnPaneHeightChanged(double value)
    {
        _settings.Current.TasksPaneHeight = value;
        _settings.Save();
    }

    /// <summary>Called by the view when the user finished dragging the splitter.</summary>
    public void CommitPaneHeight(double height)
    {
        if (double.IsFinite(height))
        {
            PaneHeight = Math.Max(MinPaneHeight, Math.Round(height));
        }
    }

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    /// <summary>Makes the pane visible, e.g. after a task was started.</summary>
    [RelayCommand]
    private void ShowTasks() => IsExpanded = true;

    private bool HasSelection => SelectedTask is not null;
    private bool CanCancel => SelectedTask?.IsActive == true;
    private bool CanDelete => SelectedTask is { IsActive: false };

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DetailsAsync()
    {
        if (SelectedTask is { } task)
        {
            var details = new TaskDetailsViewModel(task, _devices);
            await details.LoadLogAsync(_api, CancellationToken.None).ConfigureAwait(true);
            await _dialogs.ShowTaskDetailsAsync(details).ConfigureAwait(true);
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

    private bool HasTasks => Tasks.Count > 0;

    [RelayCommand(CanExecute = nameof(HasTasks))]
    private async Task DeleteAllAsync()
    {
        int active = ActiveCount;
        string message = active > 0
            ? $"Delete all {Tasks.Count} tasks? Running tasks are cancelled first."
            : $"Delete all {Tasks.Count} tasks from the history? This cannot be undone.";
        if (!await _dialogs.ConfirmAsync("Delete all tasks", message, "Delete all").ConfigureAwait(true))
        {
            return;
        }

        try
        {
            await _api.DeleteAllTasksAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            LogActionFailed(_logger, ex, "delete", "all");
            await _dialogs.ShowMessageAsync("Delete all tasks", "The tasks could not be deleted: " + ex.Message).ConfigureAwait(true);
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
