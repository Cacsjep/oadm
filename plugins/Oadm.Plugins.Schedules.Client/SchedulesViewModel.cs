using Avalonia.Controls;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Collections;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Schedules.Client;

/// <summary>A schedule in the page's grid, updated in place.</summary>
public sealed partial class ScheduleRow : ObservableObject
{
    internal ScheduleRow(ScheduleSummary summary, DateTimeOffset now)
    {
        Id = summary.Id;
        Apply(summary, now);
    }

    public Guid Id { get; }

    public ScheduleSummary Summary { get; private set; } = new();

    [ObservableProperty] public partial string Name { get; private set; } = string.Empty;

    [ObservableProperty] public partial bool On { get; private set; }

    [ObservableProperty] public partial string DevicesText { get; private set; } = string.Empty;

    [ObservableProperty] public partial string WhenText { get; private set; } = string.Empty;

    [ObservableProperty] public partial string ThenText { get; private set; } = string.Empty;

    [ObservableProperty] public partial string NextRunText { get; private set; } = string.Empty;

    [ObservableProperty] public partial string LastRunText { get; private set; } = string.Empty;

    [ObservableProperty] public partial bool LastRunOk { get; private set; }

    [ObservableProperty] public partial bool LastRunWarning { get; private set; }

    [ObservableProperty] public partial bool LastRunError { get; private set; }

    [ObservableProperty] public partial bool LastRunAccent { get; private set; }

    [ObservableProperty] public partial string Owner { get; private set; } = string.Empty;

    /// <summary>Sort key of Next run (none last).</summary>
    public DateTimeOffset NextRunSortKey => Summary.NextRun ?? DateTimeOffset.MaxValue;

    /// <summary>Sort key of Last run.</summary>
    public DateTimeOffset LastRunSortKey => Summary.LastRun?.Started ?? DateTimeOffset.MinValue;

    internal void Apply(ScheduleSummary s, DateTimeOffset now)
    {
        Summary = s;
        Name = s.Name;
        On = s.On;
        DevicesText = SchedulesTexts.DevicesText(s.DevicesKind, s.DeviceCount, s.Tags);
        WhenText = ScheduleFormat.WhenText(s.When);
        ThenText = string.Join(", ", s.Actions.Select(SchedulesTexts.ActionName));
        NextRunText = s.On ? ScheduleFormat.Moment(s.NextRun, now) : SchedulesTexts.TurnedOff;
        Owner = s.Owner ?? string.Empty;

        // A schedule that will not run as it should shows why, as a warning, until it is fixed.
        if (s.Warning is { Length: > 0 } warning)
        {
            (LastRunText, LastRunOk, LastRunWarning, LastRunError, LastRunAccent) = (warning, false, true, false, false);
        }
        else if (s.LastRun is { } run)
        {
            LastRunText = ScheduleFormat.Summary(run);
            (LastRunOk, LastRunWarning, LastRunError, LastRunAccent) = RunLook.Of(run.Result);
        }
        else
        {
            (LastRunText, LastRunOk, LastRunWarning, LastRunError, LastRunAccent) = (string.Empty, false, false, false, false);
        }

        OnPropertyChanged(nameof(NextRunSortKey));
        OnPropertyChanged(nameof(LastRunSortKey));
    }
}

/// <summary>The chip state of a run result.</summary>
internal static class RunLook
{
    public static (bool Ok, bool Warning, bool Error, bool Accent) Of(RunResult result) => result switch
    {
        RunResult.Done => (true, false, false, false),
        RunResult.DoneWithWarnings or RunResult.Missed or RunResult.Skipped => (false, true, false, false),
        RunResult.Failed or RunResult.Stopped => (false, false, true, false),
        RunResult.Running => (false, false, false, true),
        _ => (false, false, false, false),
    };
}

/// <summary>One run of the selected schedule.</summary>
public sealed record RunRow(
    Guid Id,
    string StartedText,
    string EndedText,
    string ResultText,
    string Detail,
    bool IsOk,
    bool IsWarning,
    bool IsError,
    bool IsAccent,
    bool HasFailures,
    DateTimeOffset StartedSortKey,
    DateTimeOffset EndedSortKey)
{
    internal static RunRow Of(ScheduleRun run, DateTimeOffset now)
    {
        var (ok, warning, error, accent) = RunLook.Of(run.Result);
        return new RunRow(
            run.Id,
            ScheduleFormat.Moment(run.Started, now),
            ScheduleFormat.Moment(run.Ended, now),
            // "Failed: 212 done, 3 failed"; a run that stopped or never ran says why ("Stopped at stage 3: 1 failed. 1 done, ...").
            HasReason(run) ? ScheduleFormat.Summary(run) : SchedulesTexts.ResultText(run.Result),
            HasReason(run) ? string.Empty : ScheduleFormat.Counts(run),
            ok,
            warning,
            error,
            accent,
            run.Failed > 0,
            run.Started,
            run.Ended ?? DateTimeOffset.MaxValue);
    }

    /// <summary>The run still runs: it is never deleted.</summary>
    public bool IsRunning => IsAccent;

    private static bool HasReason(ScheduleRun run) => run.Result is RunResult.Stopped or RunResult.Failed && run.Reason is { Length: > 0 };
}

/// <summary>One failed device of a run.</summary>
public sealed record FailureRow(string Device, string Step, string Message);

/// <summary>
/// Page "Schedules" (docs/spec/schedules.md "The page"): the schedules with their next and last run, New schedule / Edit /
/// Run now / Turn on or off / Delete (with "manage"; the server refuses others), the runs of the selected schedule and the
/// failed devices of a run. Live through the plugin's "changed" events: one reload at a time, once more when changes came
/// meanwhile.
/// </summary>
public sealed partial class SchedulesViewModel : ObservableObject, IDisposable
{
    private readonly ICorePluginClientContext _ctx;
    private readonly TimeProvider _time;
    private readonly Dictionary<Guid, ScheduleRow> _rows = [];
    private List<ScheduleRow> _all = [];
    private CancellationTokenSource? _active;
    private bool _reloading;
    private bool _reloadAgain;
    private int _runsVersion;
    private int _failuresVersion;

    public SchedulesViewModel(ICorePluginClientContext ctx, TimeProvider? time = null)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        _time = time ?? TimeProvider.System;
        _ctx.PermissionsChanged += (_, _) => OnPermissionsChanged();
    }

    /// <summary>The role may change schedules ("oadm.schedules:manage").</summary>
    public bool MayManage => _ctx.Has(PermissionKeys.Of(SchedulesInfo.PluginId, PermissionKeys.Manage));

    /// <summary>Opens the wizard; true when the schedule was saved (tests replace it).</summary>
    public Func<ScheduleEditorViewModel, Task<bool>> ShowEditor { get; set; } = async editor =>
    {
        var window = new ScheduleEditorWindow();
        window.Attach(editor);
        _ = editor.LoadAsync();
        return editor.Owner is { } owner && await window.ShowDialog<bool>(owner).ConfigureAwait(true);
    };

    /// <summary>How long the page waits before reading again when the host has no live events (fake mode, older hosts).</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    public RangeObservableCollection<ScheduleRow> Schedules { get; } = [];

    public RangeObservableCollection<RunRow> Runs { get; } = [];

    public RangeObservableCollection<FailureRow> Failures { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(ToggleText), nameof(ToggleIcon), nameof(ShowNoRuns), nameof(ShowChooseSchedule))]
    [NotifyCanExecuteChangedFor(nameof(EditCommand), nameof(RunNowCommand), nameof(ToggleCommand), nameof(DeleteCommand), nameof(DeleteRunsCommand), nameof(DeleteAllRunsCommand))]
    public partial ScheduleRow? SelectedSchedule { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowFailures), nameof(RunsSpan))]
    public partial RunRow? SelectedRun { get; set; }

    [ObservableProperty] public partial string SearchText { get; set; } = string.Empty;

    /// <summary>The first read of the page (the overlay).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty), nameof(ShowChooseSchedule))]
    public partial bool IsLoading { get; private set; } = true;

    /// <summary>The runs of the selected schedule are read (a ring over the runs).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoRuns))]
    public partial bool IsRunsLoading { get; private set; }

    /// <summary>The failed devices of the selected run are read (a ring over the list).</summary>
    [ObservableProperty] public partial bool IsFailuresLoading { get; private set; }

    /// <summary>A change runs (Run now, turn on or off, delete): the buttons wait.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NewScheduleCommand), nameof(EditCommand), nameof(RunNowCommand), nameof(ToggleCommand), nameof(DeleteCommand), nameof(DeleteRunsCommand), nameof(DeleteAllRunsCommand))]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorText { get; private set; }

    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    public bool HasSelection => SelectedSchedule is not null;

    public bool ShowEmpty => !IsLoading && _all.Count == 0;

    public bool ShowChooseSchedule => !IsLoading && _all.Count > 0 && SelectedSchedule is null;

    public bool ShowNoRuns => SelectedSchedule is not null && !IsRunsLoading && Runs.Count == 0;

    /// <summary>The selected run has failed devices: their list shows beside the runs.</summary>
    public bool ShowFailures => SelectedRun is { HasFailures: true };

    /// <summary>The runs take the whole card unless the failed devices show beside them.</summary>
    public int RunsSpan => ShowFailures ? 1 : 2;

    /// <summary>"Turn off" for a schedule that is on, else "Turn on".</summary>
    public string ToggleText => SelectedSchedule is { On: true } ? SchedulesTexts.TurnOff : SchedulesTexts.TurnOn;

    public string ToggleIcon => SelectedSchedule is { On: true } ? "cancel" : "check";

    /// <summary>The page is shown: read and follow the changes.</summary>
    public void Activate()
    {
        if (_active is not null)
        {
            return;
        }

        _active = new CancellationTokenSource();
        RequestReload();
        _ = WatchAsync(_active.Token);
    }

    public void Deactivate()
    {
        if (_active is { } active)
        {
            _active = null;
            active.Cancel();
            active.Dispose();
        }
    }

    public void Dispose() => Deactivate();

    /// <summary>The reload that runs now (tests wait for it).</summary>
    public Task ReloadTask { get; private set; } = Task.CompletedTask;

    /// <summary>The read of the runs that runs now (tests wait for it).</summary>
    public Task RunsTask { get; private set; } = Task.CompletedTask;

    /// <summary>The read of a run's failed devices that runs now (tests wait for it).</summary>
    public Task FailuresTask { get; private set; } = Task.CompletedTask;

    /// <summary>Reads the list (and the selected schedule's runs) again: one read at a time, once more when asked meanwhile.</summary>
    public void RequestReload()
    {
        if (_reloading)
        {
            _reloadAgain = true;
            return;
        }

        ReloadTask = ReloadLoopAsync();
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnSelectedScheduleChanged(ScheduleRow? value)
    {
        SelectedRun = null;
        RunsTask = LoadRunsAsync();
    }

    partial void OnSelectedRunChanged(RunRow? value) => FailuresTask = LoadFailuresAsync();

    /// <summary>The runs chosen in the runs grid (several with Ctrl or Shift); the view hands them over.</summary>
    public IReadOnlyList<RunRow> SelectedRuns { get; private set; } = [];

    /// <summary>The grid's choice changed (the view, tests).</summary>
    public void SetSelectedRuns(IEnumerable<RunRow> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        SelectedRuns = [.. runs];
        RefreshRunCommands();
    }

    private bool CanDeleteRuns() => CanChange() && SelectedRuns.Any(r => !r.IsRunning);

    private bool CanDeleteAllRuns() => CanChange() && Runs.Any(r => !r.IsRunning);

    /// <summary>Delete: the chosen runs that ended (a running run is never deleted).</summary>
    [RelayCommand(CanExecute = nameof(CanDeleteRuns))]
    private Task DeleteRunsAsync() => ChangeAsync(async row =>
    {
        var ids = SelectedRuns.Where(r => !r.IsRunning).Select(r => r.Id).ToList();
        ApplyRuns(Parse<RunsReply>(await _ctx.InvokeAsync(SchedulesMethods.DeleteRuns, Serialize(new DeleteRunsRequest(row.Id, ids)), CancellationToken.None).ConfigureAwait(true)).Runs, null);
    });

    /// <summary>Delete all: every run of the schedule that ended, after a confirmation with a red Delete.</summary>
    [RelayCommand(CanExecute = nameof(CanDeleteAllRuns))]
    private async Task DeleteAllRunsAsync()
    {
        if (SelectedSchedule is not { } schedule
            || !await _ctx.ConfirmAsync(SchedulesTexts.DeleteAllRunsTitle, SchedulesTexts.DeleteAllRunsMessage(schedule.Name), SchedulesTexts.Delete).ConfigureAwait(true))
        {
            return;
        }

        await ChangeAsync(async row =>
            ApplyRuns(Parse<RunsReply>(await _ctx.InvokeAsync(SchedulesMethods.DeleteRuns, Serialize(new DeleteRunsRequest(row.Id, [], All: true)), CancellationToken.None).ConfigureAwait(true)).Runs, null))
            .ConfigureAwait(true);
    }

    private void ApplyRuns(IReadOnlyList<ScheduleRun> runs, Guid? selectedRun)
    {
        var now = _time.GetUtcNow();
        Runs.ReplaceAll([.. runs.OrderByDescending(r => r.Started).Select(r => RunRow.Of(r, now))]);
        SelectedRun = selectedRun is { } id ? Runs.FirstOrDefault(r => r.Id == id) : null;
        SelectedRuns = SelectedRun is { } kept ? [kept] : [];
        OnPropertyChanged(nameof(ShowNoRuns));
        RefreshRunCommands();
    }

    private void RefreshRunCommands()
    {
        DeleteRunsCommand.NotifyCanExecuteChanged();
        DeleteAllRunsCommand.NotifyCanExecuteChanged();
    }

    private bool CanCreate() => MayManage && !IsBusy;

    private bool CanChange() => MayManage && !IsBusy && SelectedSchedule is not null;

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private Task NewScheduleAsync() => OpenEditorAsync(null);

    [RelayCommand(CanExecute = nameof(CanChange))]
    private Task EditAsync() => SelectedSchedule is { } row ? OpenEditorAsync(row.Id) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanChange))]
    private Task RunNowAsync() => ChangeAsync(async row =>
    {
        await _ctx.InvokeAsync(SchedulesMethods.RunNow, Serialize(new IdRequest(row.Id)), CancellationToken.None).ConfigureAwait(true);
        RequestReload();
    });

    [RelayCommand(CanExecute = nameof(CanChange))]
    private Task ToggleAsync() => ChangeAsync(async row =>
    {
        var summary = Parse<ScheduleSummary>(await _ctx.InvokeAsync(SchedulesMethods.TurnOn, Serialize(new TurnOnRequest(row.Id, !row.On)), CancellationToken.None).ConfigureAwait(true));
        row.Apply(summary, _time.GetUtcNow());
        OnPropertyChanged(nameof(ToggleText));
        OnPropertyChanged(nameof(ToggleIcon));
    });

    [RelayCommand(CanExecute = nameof(CanChange))]
    private async Task DeleteAsync()
    {
        if (SelectedSchedule is not { } row
            || !await _ctx.ConfirmAsync(SchedulesTexts.DeleteTitle, SchedulesTexts.DeleteMessage(row.Name), SchedulesTexts.Delete).ConfigureAwait(true))
        {
            return;
        }

        await ChangeAsync(async _ =>
        {
            var reply = Parse<SchedulesReply>(await _ctx.InvokeAsync(SchedulesMethods.Delete, Serialize(new IdRequest(row.Id)), CancellationToken.None).ConfigureAwait(true));
            ApplyList(reply.Schedules);
        }).ConfigureAwait(true);
    }

    private async Task OpenEditorAsync(Guid? id)
    {
        var editor = new ScheduleEditorViewModel(_ctx, id, _time);
        if (await ShowEditor(editor).ConfigureAwait(true))
        {
            RequestReload(); // joins a reload a live event started (one at a time), which then reads once more
            await ReloadTask.ConfigureAwait(true);
            if (editor.Saved is { } saved && _rows.TryGetValue(saved.Id, out var row))
            {
                SelectedSchedule = row;
            }
        }
    }

    private async Task ChangeAsync(Func<ScheduleRow, Task> change)
    {
        if (SelectedSchedule is not { } row)
        {
            return;
        }

        IsBusy = true;
        ErrorText = null;
        try
        {
            var error = await GuardedRun.RunAsync(() => change(row)).ConfigureAwait(true);
            if (error is not null)
            {
                ErrorText = error;
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ReloadLoopAsync()
    {
        _reloading = true;
        try
        {
            do
            {
                _reloadAgain = false;
                var error = await GuardedRun.RunAsync(async () =>
                {
                    var reply = Parse<SchedulesReply>(await _ctx.InvokeAsync(SchedulesMethods.List, null, CancellationToken.None).ConfigureAwait(true));
                    ApplyList(reply.Schedules);
                    if (SelectedSchedule is not null)
                    {
                        RunsTask = LoadRunsAsync();
                        await RunsTask.ConfigureAwait(true);
                    }
                }).ConfigureAwait(true);
                ErrorText = error;
                IsLoading = false;
            }
            while (_reloadAgain);
        }
        finally
        {
            _reloading = false;
        }
    }

    private void ApplyList(IReadOnlyList<ScheduleSummary> summaries)
    {
        var now = _time.GetUtcNow();
        var all = new List<ScheduleRow>(summaries.Count);
        var seen = new HashSet<Guid>();
        foreach (var summary in summaries)
        {
            seen.Add(summary.Id);
            if (_rows.TryGetValue(summary.Id, out var row))
            {
                row.Apply(summary, now);
            }
            else
            {
                row = new ScheduleRow(summary, now);
                _rows[summary.Id] = row;
            }

            all.Add(row);
        }

        foreach (var gone in _rows.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _rows.Remove(gone);
        }

        var structural = all.Count != _all.Count || all.Where((r, i) => !ReferenceEquals(r, _all[i])).Any();
        _all = all;
        if (structural)
        {
            ApplyFilter();
        }

        if (SelectedSchedule is { } selected && !_rows.ContainsKey(selected.Id))
        {
            SelectedSchedule = null;
        }

        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(ShowChooseSchedule));
        OnPropertyChanged(nameof(ToggleText));
        OnPropertyChanged(nameof(ToggleIcon));
    }

    private void ApplyFilter()
    {
        var text = SearchText.Trim();
        var selected = SelectedSchedule;
        Schedules.ReplaceAll(text.Length == 0
            ? _all
            : [.. _all.Where(r => r.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
                || r.ThenText.Contains(text, StringComparison.OrdinalIgnoreCase)
                || r.DevicesText.Contains(text, StringComparison.OrdinalIgnoreCase)
                || r.Owner.Contains(text, StringComparison.OrdinalIgnoreCase))]);
        if (selected is not null && Schedules.Contains(selected))
        {
            SelectedSchedule = selected;
        }
    }

    private async Task LoadRunsAsync()
    {
        var version = ++_runsVersion;
        if (SelectedSchedule is not { } row)
        {
            ApplyRuns([], null);
            return;
        }

        var selectedRun = SelectedRun?.Id;
        IsRunsLoading = true;
        try
        {
            var error = await GuardedRun.RunAsync(async () =>
            {
                var reply = Parse<RunsReply>(await _ctx.InvokeAsync(SchedulesMethods.Runs, Serialize(new IdRequest(row.Id)), CancellationToken.None).ConfigureAwait(true));
                if (version != _runsVersion)
                {
                    return;
                }

                ApplyRuns(reply.Runs, selectedRun);
            }).ConfigureAwait(true);
            if (error is not null && version == _runsVersion)
            {
                ErrorText = error;
            }
        }
        finally
        {
            if (version == _runsVersion)
            {
                IsRunsLoading = false;
                OnPropertyChanged(nameof(ShowNoRuns));
            }
        }
    }

    private async Task LoadFailuresAsync()
    {
        var version = ++_failuresVersion;
        if (SelectedSchedule is not { } schedule || SelectedRun is not { HasFailures: true } run)
        {
            Failures.ReplaceAll([]);
            return;
        }

        IsFailuresLoading = true;
        try
        {
            var error = await GuardedRun.RunAsync(async () =>
            {
                var reply = Parse<RunFailuresReply>(
                    await _ctx.InvokeAsync(SchedulesMethods.RunFailures, Serialize(new RunRequest(schedule.Id, run.Id)), CancellationToken.None).ConfigureAwait(true));
                if (version == _failuresVersion)
                {
                    Failures.ReplaceAll([.. reply.Failures.Select(f => new FailureRow(f.Device, f.Step, f.Message))]);
                }
            }).ConfigureAwait(true);
            if (error is not null && version == _failuresVersion)
            {
                ErrorText = error;
            }
        }
        finally
        {
            if (version == _failuresVersion)
            {
                IsFailuresLoading = false;
            }
        }
    }

    private async Task WatchAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await foreach (var item in _ctx.WatchEventsAsync(ct).ConfigureAwait(true))
                {
                    if (item.Topic == SchedulesMethods.ChangedTopic)
                    {
                        RequestReload();
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // The connection dropped: read again and watch again.
            catch (Exception)
#pragma warning restore CA1031
            {
            }

            try
            {
                await Task.Delay(PollInterval, _time, ct).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            RequestReload(); // no events (fake mode, older host) or a dropped stream: read again
        }
    }

    private void OnPermissionsChanged()
    {
        OnPropertyChanged(nameof(MayManage));
        NewScheduleCommand.NotifyCanExecuteChanged();
        EditCommand.NotifyCanExecuteChanged();
        RunNowCommand.NotifyCanExecuteChanged();
        ToggleCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        RefreshRunCommands();
    }

    private static string Serialize<T>(T value) => PluginJson.Serialize(value, SchedulesJson.Options);

    private static T Parse<T>(string? json) => PluginJson.Parse<T>(json, SchedulesJson.Options);

    /// <summary>Owner for the wizard.</summary>
    internal Window? Owner => _ctx.Owner;
}
