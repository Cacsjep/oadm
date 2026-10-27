using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Collections;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Client.Validation;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.LoadTest.Client;

/// <summary>A choice of the phase duration.</summary>
public sealed record DurationChoice(int Minutes)
{
    public override string ToString() => string.Create(CultureInfo.CurrentCulture, $"{Minutes} min");
}

/// <summary>A stat card above the graphs: title, value, detail and the colour it needs.</summary>
public sealed record CardValue(string Title, string Value, string? Detail = null, bool IsOk = false, bool IsWarning = false, bool IsError = false)
{
    public static readonly CardValue Empty = new(string.Empty, string.Empty);
}

/// <summary>A chosen device the check leaves out, with the reason.</summary>
public sealed record LeftOutRow(string Name, string Reason);

/// <summary>
/// Page "Load test" (docs/spec/load-test.md "The page"). Nothing is read before a device choice; with a
/// choice the page asks the server for the plan (read only). Start asks first, then the server streams; the live events
/// (one per second, already a batch of new graph points and changed rows) are applied as one update each: graphs get new
/// lists, rows are updated in place by stream id and the table is re-sorted with one reset only when the order changed.
/// A finished or opened run stays on the page until the choice or the check settings change.
/// </summary>
public sealed partial class LoadTestViewModel : ValidatingViewModel, IDisposable
{
    private readonly ICorePluginClientContext _ctx;
    private readonly Dictionary<string, StreamRowViewModel> _rowsById = new(StringComparer.Ordinal);
    private readonly CheckGraphs _graphs = new();
    private CancellationTokenSource? _active;
    private CancellationTokenSource? _plan;
    private CancellationTokenSource? _stream;
    private Guid? _runId;
    private bool _loadingRun;

    public LoadTestViewModel(ICorePluginClientContext ctx)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        DeviceChoice = new PageDeviceChoice(ctx, "cameras");
        DeviceChoice.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PageDeviceChoice.Choice))
            {
                OnInputsChanged();
            }
        };
        SelectedDuration = Durations.First(d => d.Minutes == LoadTestInfo.DefaultPhaseMinutes);
        Validation.Rule(nameof(BatchSizeText), () => ParseBatchSize(BatchSizeText) is null
            ? string.Create(CultureInfo.CurrentCulture, $"Enter 1 to {LoadTestInfo.MaxBatchSize}.")
            : null);
        Validation.Validate();
        _ctx.PermissionsChanged += OnPermissionsChanged;
        UpdateTexts();
    }

    // ---- inputs ----

    /// <summary>The cameras the check streams: "Selected cameras: Tag Building A".</summary>
    public PageDeviceChoice DeviceChoice { get; }

    /// <summary>Opens the device picker (set by the view); true when a choice was made.</summary>
    public Func<Task<bool>>? PickDevices { get; set; }

    /// <summary>Opens the run list (set by the view).</summary>
    public Func<RunListViewModel, Task>? ShowRunList { get; set; }

    [ObservableProperty]
    public partial string BatchSizeText { get; set; } = LoadTestInfo.DefaultBatchSize.ToString(CultureInfo.CurrentCulture);

    public static IReadOnlyList<DurationChoice> Durations { get; } = [.. LoadTestInfo.PhaseMinutes.Select(m => new DurationChoice(m))];

    [ObservableProperty]
    public partial DurationChoice SelectedDuration { get; set; }

    public int? BatchSize => ParseBatchSize(BatchSizeText);

    partial void OnBatchSizeTextChanged(string value) => OnInputsChanged();

    partial void OnSelectedDurationChanged(DurationChoice value) => OnInputsChanged();

    // ---- role ----

    /// <summary>The role may start and cancel a check and delete runs.</summary>
    public bool MayRun => _ctx.Has(CheckTexts.RunPermission);

    // ---- state ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorText { get; private set; }

    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    [ObservableProperty] public partial bool IsPlanLoading { get; private set; }

    [ObservableProperty] public partial CheckPlan? Plan { get; private set; }

    [ObservableProperty] public partial RunSummary? Run { get; private set; }

    [ObservableProperty] public partial bool IsStarting { get; private set; }

    /// <summary>Nothing chosen: one line what to do first.</summary>
    public bool ShowChooseFirst => Run is null && DeviceChoice.IsEmpty;

    /// <summary>Chosen, no run on the page: the plan.</summary>
    public bool ShowPlan => Run is null && !DeviceChoice.IsEmpty;

    /// <summary>A run on the page (running, finished or opened).</summary>
    public bool ShowRun => Run is not null;

    /// <summary>The stat cards: the plan's numbers before a run, the run's after.</summary>
    public bool ShowCards => ShowRun || (ShowPlan && Plan is not null);

    public bool IsRunning => Run?.State == RunState.Running;

    /// <summary>The progress row shows from the click on Start (the server opens the streams) until the run ends.</summary>
    public bool ShowProgress => IsRunning || IsStarting;

    public bool IsResult => Run is { State: not RunState.Running };

    [ObservableProperty] public partial string PlanText { get; private set; } = string.Empty;

    [ObservableProperty] public partial string ProgressText { get; private set; } = string.Empty;

    [ObservableProperty] public partial double ProgressValue { get; private set; }

    /// <summary>At most three restore warnings by name, then a count (the server keeps them all).</summary>
    [ObservableProperty] public partial IReadOnlyList<string> RestoreWarnings { get; private set; } = [];

    public bool HasRestoreWarnings => RestoreWarnings.Count > 0;

    [ObservableProperty] public partial CardValue Card1 { get; private set; } = CardValue.Empty;

    [ObservableProperty] public partial CardValue Card2 { get; private set; } = CardValue.Empty;

    [ObservableProperty] public partial CardValue Card3 { get; private set; } = CardValue.Empty;

    [ObservableProperty] public partial CardValue Card4 { get; private set; } = CardValue.Empty;

    // ---- graphs ----

    [ObservableProperty] public partial IReadOnlyList<ChartSeries> RateSeries { get; private set; } = [];

    [ObservableProperty] public partial IReadOnlyList<ChartSeries> LossSeries { get; private set; } = [];

    [ObservableProperty] public partial IReadOnlyList<ChartSeries> JitterSeries { get; private set; } = [];

    [ObservableProperty] public partial IReadOnlyList<ChartBar> StepBars { get; private set; } = [];

    /// <summary>The phases over time, marked on the line charts.</summary>
    [ObservableProperty] public partial IReadOnlyList<ChartBand> PhaseBands { get; private set; } = [];

    public static double LossThreshold => LoadTestInfo.MaxPacketLossPercent;

    public static double JitterThreshold => LoadTestInfo.JitterWarningMs;

    public static string NoValues => CheckTexts.NoValues;

    // ---- tables ----

    /// <summary>One row per camera stream, worst first.</summary>
    public RangeObservableCollection<StreamRowViewModel> Rows { get; } = [];

    public RangeObservableCollection<LeftOutRow> LeftOut { get; } = [];

    [ObservableProperty] public partial string LeftOutTitle { get; private set; } = "Left out";

    public bool ShowNoneLeftOut => Plan is not null && LeftOut.Count == 0;

    public bool HasLeftOut => LeftOut.Count > 0;

    public static string NoneLeftOut => CheckTexts.NoneLeftOut;

    public static string ChooseFirst => CheckTexts.ChooseFirst;

    public static string ReadingCameras => CheckTexts.ReadingCameras;

    [ObservableProperty] public partial StreamRowViewModel? SelectedRow { get; set; }

    /// <summary>The selected stream's own graphs (side panel).</summary>
    [ObservableProperty] public partial StreamDetailViewModel? Detail { get; private set; }

    partial void OnSelectedRowChanged(StreamRowViewModel? value) => _ = OpenStreamAsync(value);

    // ---- commands ----

    private bool CanStart => MayRun && !IsRunning && !IsStarting && !IsPlanLoading && Plan is { Streams: > 0, Refusal: null } && Validation.IsValid && !DeviceChoice.IsEmpty;

    /// <summary>Why Start is disabled (its tooltip), null when it can run.</summary>
    public string? StartTip => !MayRun ? CheckTexts.NotAllowed
        : IsRunning ? "A check is running"
        : DeviceChoice.IsEmpty ? "Choose cameras first"
        : FormError is { } error ? error
        : Plan is { Refusal: { } refusal } ? refusal
        : Plan is { Streams: 0 } ? CheckTexts.NoStreams
        : "Stream the chosen cameras batch by batch";

    private bool CanCancel => MayRun && IsRunning;

    public string CancelTip => MayRun ? "Stop the check and restore the exposure" : CheckTexts.NotAllowed;

    /// <summary>The toolbar's inputs: locked while a check runs.</summary>
    public bool CanEditInputs => !IsRunning && !IsStarting;

    [RelayCommand]
    private async Task ChooseDevicesAsync()
    {
        if (PickDevices is { } pick)
        {
            await pick().ConfigureAwait(true);
        }
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        if (Plan is not { } plan || BatchSize is not { } batch)
        {
            return;
        }

        var minutes = SelectedDuration.Minutes;
        var message = string.Create(CultureInfo.CurrentCulture,
            $"{CheckTexts.Count(plan.Streams, "camera stream", "camera streams")} in {CheckTexts.Count(plan.Batches, "batch", "batches")}, about {CheckTexts.Duration(plan.Duration)}. Expected peak rate {CheckTexts.MbpsWhole(plan.PeakMbps)}, {CheckTexts.MbpsWhole(plan.LowLightMbps)} in low light.")
            + Environment.NewLine + Environment.NewLine + CheckTexts.ExposureWarning(minutes * plan.Batches);
        if (!await _ctx.ConfirmAsync("Start load test", message, "Start").ConfigureAwait(true))
        {
            return;
        }

        ProgressValue = 0;
        ProgressText = "Starting the check";
        IsStarting = true;
        try
        {
            var (ids, all) = ChosenIds();
            var json = await _ctx.InvokeAsync(LoadTestMethods.Start,
                LoadTestJson.Serialize(new StartRequest(ids, batch, minutes, all)), _active?.Token ?? CancellationToken.None).ConfigureAwait(true);
            var summary = LoadTestJson.Deserialize<RunSummary>(json);
            ErrorText = null;
            ShowNewRun(summary);
        }
        catch (OperationCanceledException)
        {
        }
#pragma warning disable CA1031 // Shown on the page (e.g. another check runs).
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ErrorText = ServerErrors.Message(ex);
        }
        finally
        {
            IsStarting = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private async Task CancelAsync()
    {
        try
        {
            var json = await _ctx.InvokeAsync(LoadTestMethods.Cancel, "{}", _active?.Token ?? CancellationToken.None).ConfigureAwait(true);
            if (!string.IsNullOrWhiteSpace(json) && json != "null")
            {
                ApplySummary(LoadTestJson.Deserialize<RunSummary>(json));
            }

            ErrorText = null;
        }
        catch (OperationCanceledException)
        {
        }
#pragma warning disable CA1031 // Shown on the page.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ErrorText = ServerErrors.Message(ex);
        }
    }

    [RelayCommand]
    private async Task ShowRunsAsync()
    {
        await LoadRunsAsync(_active?.Token ?? CancellationToken.None).ConfigureAwait(true);
        if (ShowRunList is { } show)
        {
            await show(RunsViewModel).ConfigureAwait(true);
        }
    }

    /// <summary>The run list the dialog shows.</summary>
    public RunListViewModel RunsViewModel => _runs ??= new RunListViewModel(this, _ctx);

    private RunListViewModel? _runs;

    [RelayCommand]
    private void CloseDetail() => SelectedRow = null;

    // ---- lifetime ----

    /// <summary>The page is shown: watch the live events and show a check that runs.</summary>
    public void Activate()
    {
        if (_active is not null)
        {
            return;
        }

        _active = new CancellationTokenSource();
        var token = _active.Token;
        _ = _ctx.RunAsync(HandleEvent, PluginEventWatch.DefaultRetryDelay, token);
        _ = StartupAsync(token);
    }

    /// <summary>The page is hidden: no more watching.</summary>
    public void Deactivate()
    {
        if (_active is not { } active)
        {
            return;
        }

        _active = null;
        active.Cancel();
        active.Dispose();
        _plan?.Cancel();
    }

    public void Dispose()
    {
        Deactivate();
        _ctx.PermissionsChanged -= OnPermissionsChanged;
        _plan?.Dispose();
        _stream?.Dispose();
    }

    private async Task StartupAsync(CancellationToken ct)
    {
        var running = await LoadRunsAsync(ct).ConfigureAwait(true);
        if (running is { } id && Run?.RunId != id)
        {
            await OpenRunAsync(id, ct).ConfigureAwait(true);
        }
        else if (Run is null)
        {
            await LoadPlanAsync().ConfigureAwait(true);
        }
    }

    /// <summary>Reads the run list; returns the running run's id.</summary>
    internal async Task<Guid?> LoadRunsAsync(CancellationToken ct)
    {
        try
        {
            var json = await _ctx.InvokeAsync(LoadTestMethods.ListRuns, "{}", ct).ConfigureAwait(true);
            var list = LoadTestJson.Deserialize<RunList>(json);
            RunsViewModel.Apply(list);
            return list.RunningId;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
#pragma warning disable CA1031 // Shown on the page.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ErrorText = ServerErrors.Message(ex);
            return null;
        }
    }

    // ---- events ----

    /// <summary>Applies one pushed event (tests call it directly).</summary>
    public void HandleEvent(PluginEvent item)
    {
        ArgumentNullException.ThrowIfNull(item);
        switch (item.Topic)
        {
            case LoadTestMethods.LiveTopic:
                ApplyLive(LoadTestJson.Deserialize<LiveEvent>(item.PayloadJson));
                break;
            case LoadTestMethods.RunsTopic:
                var list = LoadTestJson.Deserialize<RunList>(item.PayloadJson);
                RunsViewModel.Apply(list);
                if (list.Runs.FirstOrDefault(r => r.RunId == _runId) is { } shown)
                {
                    ApplySummary(shown);
                }

                break;
        }
    }

    /// <summary>One second of the running check: one update of the graphs, the rows and the progress.</summary>
    public void ApplyLive(LiveEvent live)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (_runId != live.Summary.RunId)
        {
            // Another run on the page (an opened one): only a check this page shows is followed; with no run on the
            // page the running check takes it over.
            if (Run is not null && Run.State != RunState.Running && _runId is not null)
            {
                return;
            }

            ShowNewRun(live.Summary);
        }

        if (_loadingRun)
        {
            return;
        }

        if (!_graphs.Append(live.FromSecond, live.NewPoints))
        {
            // Points missing (the page connected late): read the whole run once.
            _ = OpenRunAsync(live.Summary.RunId, _active?.Token ?? CancellationToken.None);
            return;
        }

        ApplyRows(live.ChangedRows);
        PublishGraphs();
        ApplySummary(live.Summary);
    }

    /// <summary>Changed rows updated in place by stream id, new ones added; one reset when the order changed.</summary>
    public void ApplyRows(IReadOnlyList<StreamRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0)
        {
            return;
        }

        var added = false;
        foreach (var row in rows)
        {
            if (_rowsById.TryGetValue(row.StreamId, out var vm))
            {
                vm.Apply(row);
            }
            else
            {
                _rowsById[row.StreamId] = new StreamRowViewModel(row);
                added = true;
            }
        }

        Resort(added);
    }

    private void Resort(bool force)
    {
        var sorted = _rowsById.Values.ToArray();
        Array.Sort(sorted, StreamRowViewModel.WorstFirst);
        if (!force && sorted.Length == Rows.Count)
        {
            var same = true;
            for (var i = 0; i < sorted.Length && same; i++)
            {
                same = ReferenceEquals(sorted[i], Rows[i]);
            }

            if (same)
            {
                return;
            }
        }

        var selected = SelectedRow;
        Rows.ReplaceAll(sorted);
        if (selected is not null && _rowsById.ContainsKey(selected.StreamId) && !ReferenceEquals(SelectedRow, selected))
        {
            _keepDetail = true;
            SelectedRow = selected;
            _keepDetail = false;
        }
    }

    private bool _keepDetail;

    // ---- runs ----

    /// <summary>"Reading the run".</summary>
    public const string ReadingRun = "Reading the run";

    /// <summary>The rows of an opened run arrive page by page; the progress row shows until the last page is in.</summary>
    [ObservableProperty] public partial bool IsRunLoading { get; private set; }

    [ObservableProperty] public partial double RunLoadValue { get; private set; }

    [ObservableProperty] public partial string RunLoadText { get; private set; } = ReadingRun;

    /// <summary>
    /// Opens a run (the run list, or the running one when the page is shown): the first page with the graphs, then the other
    /// pages of rows (<see cref="LoadTestInfo.MaxStreamsPerPage"/> each, worst first) added in one range each.
    /// </summary>
    internal async Task OpenRunAsync(Guid runId, CancellationToken ct)
    {
        _loadingRun = true;
        IsRunLoading = true;
        RunLoadValue = 0;
        RunLoadText = ReadingRun;
        try
        {
            var json = await _ctx.InvokeAsync(LoadTestMethods.GetRun, LoadTestJson.Serialize(new RunRequest(runId)), ct).ConfigureAwait(true);
            var detail = LoadTestJson.Deserialize<RunDetail>(json);
            ShowDetail(detail);
            ErrorText = null;
            var loaded = detail.Streams.Count;
            while (loaded < detail.TotalStreams && detail.Streams.Count > 0 && _runId == runId)
            {
                RunLoadValue = 100.0 * loaded / detail.TotalStreams;
                RunLoadText = string.Create(CultureInfo.CurrentCulture, $"{ReadingRun}: {loaded:N0} of {detail.TotalStreams:N0} cameras");
                json = await _ctx.InvokeAsync(LoadTestMethods.GetRun,
                    LoadTestJson.Serialize(new RunRequest(runId, loaded, LoadTestInfo.MaxStreamsPerPage)), ct).ConfigureAwait(true);
                var page = LoadTestJson.Deserialize<RunDetail>(json);
                if (page.Streams.Count == 0 || _runId != runId)
                {
                    break;
                }

                AppendRows(page.Streams);
                loaded += page.Streams.Count;
            }
        }
        catch (OperationCanceledException)
        {
        }
#pragma warning disable CA1031 // Shown on the page.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ErrorText = ServerErrors.Message(ex);
        }
        finally
        {
            _loadingRun = false;
            IsRunLoading = false;
        }
    }

    /// <summary>A further page of an opened run: new rows at the end in one range (the server sends them worst first).</summary>
    internal void AppendRows(IReadOnlyList<StreamRow> rows)
    {
        var added = new List<StreamRowViewModel>(rows.Count);
        foreach (var row in rows)
        {
            if (!_rowsById.ContainsKey(row.StreamId))
            {
                var vm = new StreamRowViewModel(row);
                _rowsById[row.StreamId] = vm;
                added.Add(vm);
            }
        }

        Rows.AddRange(added);
    }

    /// <summary>Shows a whole run (tests and the screenshots call it directly).</summary>
    public void ShowDetail(RunDetail detail)
    {
        ArgumentNullException.ThrowIfNull(detail);
        ResetRun(detail.Summary.RunId);
        _graphs.Replace(detail.Graphs);
        foreach (var row in detail.Streams)
        {
            _rowsById[row.StreamId] = new StreamRowViewModel(row);
        }

        Resort(force: true);
        PublishGraphs();
        ApplySummary(detail.Summary);
    }

    /// <summary>A run was deleted on the server: leave it.</summary>
    internal void RunDeleted(Guid runId)
    {
        if (_runId == runId)
        {
            ResetRun(null);
            Run = null;
            AfterRunChanged();
            _ = LoadPlanAsync();
        }
    }

    private void ShowNewRun(RunSummary summary)
    {
        ResetRun(summary.RunId);
        PublishGraphs();
        ApplySummary(summary);
    }

    private void ResetRun(Guid? runId)
    {
        _runId = runId;
        _graphs.Clear();
        _rowsById.Clear();
        SelectedRow = null;
        Rows.ReplaceAll([]);
    }

    private void PublishGraphs()
    {
        RateSeries = _graphs.Rate();
        LossSeries = _graphs.Loss();
        JitterSeries = _graphs.Jitter();
        StepBars = _graphs.Steps();
        PhaseBands = _graphs.Bands();
    }

    private void ApplySummary(RunSummary summary)
    {
        if (summary.RunId != _runId)
        {
            return;
        }

        Run = summary;
        AfterRunChanged();
    }

    private void AfterRunChanged()
    {
        OnPropertyChanged(nameof(ShowChooseFirst));
        OnPropertyChanged(nameof(ShowPlan));
        OnPropertyChanged(nameof(ShowRun));
        OnPropertyChanged(nameof(ShowCards));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(ShowProgress));
        OnPropertyChanged(nameof(IsResult));
        OnPropertyChanged(nameof(CanEditInputs));
        UpdateTexts();
        NotifyCommands();
    }

    // ---- stream detail ----

    private async Task OpenStreamAsync(StreamRowViewModel? row)
    {
        if (_keepDetail)
        {
            return;
        }

        _stream?.Cancel();
        _stream?.Dispose();
        _stream = null;
        if (row is null || _runId is not { } runId)
        {
            Detail = null;
            return;
        }

        var detail = new StreamDetailViewModel(row.Row);
        Detail = detail;
        _stream = new CancellationTokenSource();
        var ct = _stream.Token;
        try
        {
            var json = await _ctx.InvokeAsync(LoadTestMethods.GetStream, LoadTestJson.Serialize(new StreamRequest(runId, row.StreamId)), ct).ConfigureAwait(true);
            detail.Apply(LoadTestJson.Deserialize<StreamDetail>(json));
        }
        catch (OperationCanceledException)
        {
        }
#pragma warning disable CA1031 // Shown in the panel.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            detail.Fail(ServerErrors.Message(ex));
        }
    }

    // ---- plan ----

    private void OnInputsChanged()
    {
        if (IsRunning)
        {
            return;
        }

        if (Run is not null)
        {
            // New inputs: the page shows the plan again, the run stays in the run list.
            ResetRun(null);
            Run = null;
        }

        AfterRunChanged();
        _ = LoadPlanAsync();
    }

    /// <summary>Reads the plan of the chosen cameras (read only); a newer request takes over.</summary>
    internal async Task LoadPlanAsync()
    {
        _plan?.Cancel();
        _plan?.Dispose();
        _plan = null;
        Plan = null;
        LeftOut.ReplaceAll([]);
        OnPropertyChanged(nameof(ShowNoneLeftOut));
        OnPropertyChanged(nameof(HasLeftOut));
        if (DeviceChoice.IsEmpty || BatchSize is not { } batch || _active is null)
        {
            IsPlanLoading = false;
            UpdateTexts();
            NotifyCommands();
            return;
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(_active.Token);
        _plan = cts;
        IsPlanLoading = true;
        UpdateTexts();
        NotifyCommands();
        try
        {
            var (ids, all) = ChosenIds();
            var json = await _ctx.InvokeAsync(LoadTestMethods.Plan,
                LoadTestJson.Serialize(new PlanRequest(ids, batch, SelectedDuration.Minutes, all)), cts.Token).ConfigureAwait(true);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            ErrorText = null;
            ApplyPlan(LoadTestJson.Deserialize<CheckPlan>(json));
        }
        catch (OperationCanceledException)
        {
            return;
        }
#pragma warning disable CA1031 // Shown on the page.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            if (!cts.IsCancellationRequested)
            {
                ErrorText = ServerErrors.Message(ex);
            }
        }

        if (ReferenceEquals(_plan, cts))
        {
            IsPlanLoading = false;
            UpdateTexts();
            NotifyCommands();
        }
    }

    /// <summary>
    /// The chosen ids, or none with <c>all</c> for the choice "All devices" (the server takes the caller's video devices, so
    /// 30,000 ids never travel).
    /// </summary>
    private (List<Guid> Ids, bool All) ChosenIds() => DeviceChoice.Choice.Kind == DeviceChoiceKind.All
        ? ([], true)
        : ([.. DeviceChoice.Resolve().Select(d => d.Id)], false);

    /// <summary>Shows a plan (tests and the screenshots call it directly); a refusal (too many cameras) shows below the toolbar.</summary>
    public void ApplyPlan(CheckPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        IsPlanLoading = false;
        Plan = plan;
        if (plan.Refusal is { } refusal)
        {
            ErrorText = refusal;
        }
        LeftOut.ReplaceAll(plan.LeftOut.Select(d => new LeftOutRow(d.Name, d.Reason)));
        LeftOutTitle = plan.LeftOut.Count == 0 ? "Left out" : "Left out (" + plan.LeftOut.Count.ToString("N0", CultureInfo.CurrentCulture) + ")";
        OnPropertyChanged(nameof(ShowNoneLeftOut));
        OnPropertyChanged(nameof(HasLeftOut));
        OnPropertyChanged(nameof(ShowCards));
        UpdateTexts();
        NotifyCommands();
    }

    // ---- texts ----

    private void UpdateTexts()
    {
        if (Run is { } run)
        {
            UpdateRunTexts(run);
            return;
        }

        RestoreWarnings = [];
        OnPropertyChanged(nameof(HasRestoreWarnings));
        if (Plan is { } plan)
        {
            PlanText = plan.Streams == 0 ? CheckTexts.NoStreams
                : string.Create(CultureInfo.CurrentCulture,
                    $"{CheckTexts.Count(plan.Streams, "camera stream", "camera streams")} in {CheckTexts.Count(plan.Batches, "batch", "batches")} of at most {BatchSize ?? 0:N0}, {SelectedDuration.Minutes} min per phase.");
            Card1 = new CardValue("Camera streams", plan.Streams.ToString("N0", CultureInfo.CurrentCulture), CheckTexts.Count(plan.Batches, "batch", "batches"));
            Card2 = new CardValue("Time", CheckTexts.Duration(plan.Duration), "plus connect times");
            Card3 = new CardValue("Expected peak", CheckTexts.MbpsWhole(plan.PeakMbps), "largest batch, Normal");
            Card4 = new CardValue("Expected in low light", CheckTexts.MbpsWhole(plan.LowLightMbps), "largest batch, Low light");
        }
        else
        {
            PlanText = string.Empty;
        }
    }

    private void UpdateRunTexts(RunSummary run)
    {
        ProgressValue = run.Progress * 100;
        var phase = run.Phase is { } p ? CheckTexts.PhaseName(p) : "Connecting to the cameras";
        ProgressText = string.Create(CultureInfo.CurrentCulture, $"Batch {Math.Max(1, run.Batch)} of {run.Batches} · {phase}")
            + (run.Remaining is { } left ? " · " + CheckTexts.Duration(left) + " left" : string.Empty);
        var warnings = run.RestoreWarnings.Take(3).ToList();
        if (run.RestoreWarnings.Count > 3)
        {
            warnings.Add(string.Create(CultureInfo.CurrentCulture, $"and {run.RestoreWarnings.Count - 3:N0} more cameras whose exposure was not restored"));
        }

        RestoreWarnings = warnings;
        OnPropertyChanged(nameof(HasRestoreWarnings));
        Card1 = new CardValue("Cameras checked", run.Streams.ToString("N0", CultureInfo.CurrentCulture),
            run.FailedStreams > 0 ? CheckTexts.Count(run.FailedStreams, "failed", "failed") : CheckTexts.Count(run.Batches, "batch", "batches"));
        Card2 = new CardValue("Peak rate", CheckTexts.MbpsWhole(run.PeakMbps), "of a batch");
        var lossBad = run.WorstLossPercent >= LoadTestInfo.MaxPacketLossPercent;
        Card3 = new CardValue("Worst camera loss", CheckTexts.Percent(run.WorstLossPercent), "packets or frames", IsError: lossBad, IsOk: !lossBad && run.State == RunState.Completed);
        Card4 = run.Headroom is { } headroom
            ? new CardValue("Headroom", headroom, "last step every camera passed", IsOk: headroom == "100 %", IsWarning: headroom != "100 %")
            : new CardValue("Headroom", "–", run.State == RunState.Running ? "after the ramp" : "no step passed", IsError: run.State != RunState.Running);
    }

    private void NotifyCommands()
    {
        StartCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(StartTip));
        OnPropertyChanged(nameof(CancelTip));
        OnPropertyChanged(nameof(MayRun));
    }

    protected override void OnValidationChanged()
    {
        base.OnValidationChanged();
        NotifyCommands();
    }

    partial void OnIsStartingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanEditInputs));
        OnPropertyChanged(nameof(ShowProgress));
        NotifyCommands();
    }

    partial void OnIsPlanLoadingChanged(bool value) => NotifyCommands();

    private void OnPermissionsChanged(object? sender, EventArgs e)
    {
        NotifyCommands();
        RunsViewModel.PermissionsChanged();
    }

    internal static int? ParseBatchSize(string? text) =>
        int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var n) && n is >= 1 and <= LoadTestInfo.MaxBatchSize ? n : null;
}
