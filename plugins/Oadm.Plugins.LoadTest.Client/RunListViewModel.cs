using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Collections;

namespace Oadm.Plugins.LoadTest.Client;

/// <summary>One kept run in the run list.</summary>
public sealed record RunRowViewModel(RunSummary Summary)
{
    public Guid RunId => Summary.RunId;

    public string DateText => CheckTexts.Date(Summary.StartedUtc);

    public DateTimeOffset StartedUtc => Summary.StartedUtc;

    public string StartedBy => Summary.StartedBy;

    public string CamerasText => Summary.Streams.ToString("N0", CultureInfo.CurrentCulture);

    public int Cameras => Summary.Streams;

    /// <summary>"50 at once, 2 min".</summary>
    public string SettingsText => string.Create(CultureInfo.CurrentCulture, $"{Summary.BatchSize:N0} at once, {Summary.PhaseMinutes} min");

    public string VerdictText => Summary.State switch
    {
        RunState.Running => "Running",
        RunState.Cancelled => "Cancelled. " + Summary.Verdict,
        _ => Summary.Verdict,
    };

    public bool IsOk => Summary.State == RunState.Completed && Summary.Passed;

    public bool IsError => Summary.State is RunState.Completed or RunState.Failed && !Summary.Passed;

    public bool IsAccent => Summary.State == RunState.Running;

    public bool IsRunning => Summary.State == RunState.Running;
}

/// <summary>The run list dialog: the kept runs, newest first; Open shows one on the page, Delete removes it on the server.</summary>
public sealed partial class RunListViewModel : ObservableObject
{
    private readonly LoadTestViewModel _page;
    private readonly ICorePluginClientContext _ctx;

    public RunListViewModel(LoadTestViewModel page, ICorePluginClientContext ctx)
    {
        _page = page ?? throw new ArgumentNullException(nameof(page));
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
    }

    public static string Title => "Runs";

    public static string EmptyText => CheckTexts.NoRuns;

    public RangeObservableCollection<RunRowViewModel> Runs { get; } = [];

    public bool ShowEmpty => Runs.Count == 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    [NotifyPropertyChangedFor(nameof(DeleteTip))]
    public partial RunRowViewModel? Selected { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorText { get; private set; }

    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    /// <summary>Closes the dialog (set by the window).</summary>
    public Action? Close { get; set; }

    public bool MayDelete => _page.MayRun;

    public string DeleteTip => !MayDelete ? CheckTexts.DeleteNotAllowed
        : Selected is { IsRunning: true } ? "Cancel the check first"
        : Selected is null ? "Choose a run first" : "Delete this run";

    public void Apply(RunList list)
    {
        ArgumentNullException.ThrowIfNull(list);
        var selected = Selected?.RunId;
        Runs.ReplaceAll(list.Runs.OrderByDescending(r => r.StartedUtc).Select(r => new RunRowViewModel(r)));
        Selected = Runs.FirstOrDefault(r => r.RunId == selected);
        OnPropertyChanged(nameof(ShowEmpty));
    }

    internal void PermissionsChanged()
    {
        OnPropertyChanged(nameof(MayDelete));
        OnPropertyChanged(nameof(DeleteTip));
        DeleteCommand.NotifyCanExecuteChanged();
    }

    private bool CanOpen => Selected is not null;

    [RelayCommand(CanExecute = nameof(CanOpen))]
    private async Task OpenAsync()
    {
        if (Selected is not { } row)
        {
            return;
        }

        Close?.Invoke();
        await _page.OpenRunAsync(row.RunId, CancellationToken.None).ConfigureAwait(true);
    }

    private bool CanDelete => MayDelete && Selected is { IsRunning: false };

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private async Task DeleteAsync()
    {
        if (Selected is not { } row)
        {
            return;
        }

        var message = string.Create(CultureInfo.CurrentCulture, $"The run of {row.DateText} with {CheckTexts.Count(row.Cameras, "camera", "cameras")} is deleted on the server.");
        if (!await _ctx.ConfirmAsync("Delete run", message, "Delete").ConfigureAwait(true))
        {
            return;
        }

        try
        {
            await _ctx.InvokeAsync(LoadTestMethods.DeleteRun, LoadTestJson.Serialize(new RunRequest(row.RunId)), CancellationToken.None).ConfigureAwait(true);
            Runs.RemoveAll(r => r.RunId == row.RunId);
            Selected = null;
            OnPropertyChanged(nameof(ShowEmpty));
            ErrorText = null;
            _page.RunDeleted(row.RunId);
        }
#pragma warning disable CA1031 // Shown below the list.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ErrorText = ServerErrors.Message(ex);
        }
    }

    [RelayCommand]
    private void Cancel() => Close?.Invoke();
}
