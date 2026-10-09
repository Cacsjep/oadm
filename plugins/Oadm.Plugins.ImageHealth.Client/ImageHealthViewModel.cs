using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Collections;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.ImageHealth.Client;

/// <summary>
/// Page "Image Health Dashboard". <see cref="Activate"/> when the page is shown watches the pushed rows and starts one
/// check (the server asks every video device once); Refresh checks again; <see cref="AutoRefresh"/> (off by default)
/// checks every 10 s while the page is shown. <see cref="Deactivate"/> stops watching and the auto refresh: nothing is
/// asked while the page is hidden. Rows are updated in place by device id; search, new and removed rows rebuild the
/// shown list with one reset (O(n)).
/// </summary>
public sealed partial class ImageHealthViewModel : ObservableObject, IDisposable
{
    private readonly ICorePluginClientContext _ctx;
    private readonly Dictionary<Guid, ImageHealthRowViewModel> _byId = [];
    private CancellationTokenSource? _active;
    private CancellationTokenSource? _autoRefresh;
    private ImageHealthState? _state;

    public ImageHealthViewModel(ICorePluginClientContext ctx)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
    }

    /// <summary>Wait before watching the events again after the stream broke.</summary>
    public static TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Auto refresh period (tests shorten it).</summary>
    public static TimeSpan AutoRefreshInterval { get; set; } = ImageHealthPluginInfo.AutoRefreshInterval;

    public RangeObservableCollection<ImageHealthRowViewModel> Rows { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorText { get; private set; }

    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    public partial bool IsChecking { get; private set; }

    [ObservableProperty] public partial double ProgressValue { get; private set; }

    [ObservableProperty] public partial string ProgressText { get; private set; } = string.Empty;

    [ObservableProperty] public partial string SummaryText { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    public partial string EmptyText { get; private set; } = "Checking the cameras.";

    public bool ShowEmpty => Rows.Count == 0;

    /// <summary>Check every 10 s while the page is shown. Off by default (user decision 2026-10-09).</summary>
    [ObservableProperty]
    public partial bool AutoRefresh { get; set; }

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnAutoRefreshChanged(bool value) => SyncAutoRefresh();

    /// <summary>The page is shown: watch the events and check once.</summary>
    public void Activate()
    {
        if (_active is not null)
        {
            return;
        }

        _active = new CancellationTokenSource();
        var token = _active.Token;
        _ = WatchAsync(token);
        _ = CheckAsync(token);
        SyncAutoRefresh();
    }

    /// <summary>The page is hidden: no more checks, no watch.</summary>
    public void Deactivate()
    {
        if (_active is not { } active)
        {
            return;
        }

        _active = null;
        active.Cancel();
        active.Dispose();
        SyncAutoRefresh();
    }

    public void Dispose()
    {
        Deactivate();
        _autoRefresh?.Dispose();
    }

    private bool CanRefresh => !IsChecking;

    /// <summary>Checks every camera again (new cameras, apps started or stopped, detections).</summary>
    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task RefreshAsync() => CheckAsync(_active?.Token ?? CancellationToken.None);

    /// <summary>Applies one pushed event (tests call it directly).</summary>
    public void HandleEvent(PluginEvent item)
    {
        ArgumentNullException.ThrowIfNull(item);
        switch (item.Topic)
        {
            case ImageHealthMethods.RowsTopic:
                var rows = ImageHealthJson.Deserialize<RowsEvent>(item.PayloadJson);
                ApplyRows(rows.Rows ?? [], rows.Removed);
                break;
            case ImageHealthMethods.StateTopic:
                ApplyState(ImageHealthJson.Deserialize<ImageHealthState>(item.PayloadJson), replaceRows: false);
                break;
        }
    }

    /// <summary>A full state (check / getState reply), or the counts only.</summary>
    public void ApplyState(ImageHealthState state, bool replaceRows)
    {
        ArgumentNullException.ThrowIfNull(state);
        _state = state;
        if (replaceRows)
        {
            var keep = new Dictionary<Guid, ImageHealthRowViewModel>(_byId);
            _byId.Clear();
            foreach (var row in state.Rows)
            {
                if (keep.TryGetValue(row.DeviceId, out var existing))
                {
                    existing.Apply(row);
                    _byId[row.DeviceId] = existing;
                }
                else
                {
                    _byId[row.DeviceId] = new ImageHealthRowViewModel(row);
                }
            }

            ApplyFilter();
        }

        IsChecking = state.Checking;
        ProgressValue = state.Total == 0 ? 0 : 100.0 * state.Checked / state.Total;
        ProgressText = string.Create(CultureInfo.CurrentCulture, $"Checking cameras {state.Checked:N0} of {state.Total:N0}");
        UpdateSummary();
    }

    /// <summary>Changed rows: updated in place; new and removed rows rebuild the shown list once.</summary>
    public void ApplyRows(IReadOnlyList<ImageHealthRow> rows, IReadOnlyList<Guid>? removed = null)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var structural = false;
        foreach (var id in removed ?? [])
        {
            structural |= _byId.Remove(id);
        }

        foreach (var row in rows)
        {
            if (_byId.TryGetValue(row.DeviceId, out var existing))
            {
                existing.Apply(row);
            }
            else
            {
                _byId[row.DeviceId] = new ImageHealthRowViewModel(row);
                structural = true;
            }
        }

        if (structural)
        {
            ApplyFilter();
        }

        UpdateSummary();
    }

    private void SyncAutoRefresh()
    {
        var want = AutoRefresh && _active is not null;
        if (want && _autoRefresh is null)
        {
            _autoRefresh = new CancellationTokenSource();
            _ = AutoRefreshAsync(_autoRefresh.Token);
        }
        else if (!want && _autoRefresh is { } running)
        {
            _autoRefresh = null;
            running.Cancel();
            running.Dispose();
        }
    }

    private async Task AutoRefreshAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(AutoRefreshInterval, ct).ConfigureAwait(true);
                if (!IsChecking)
                {
                    await CheckAsync(ct).ConfigureAwait(true);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // auto refresh off or page hidden
        }
    }

    private async Task CheckAsync(CancellationToken ct)
    {
        try
        {
            var json = await _ctx.InvokeAsync(ImageHealthMethods.Check, null, ct).ConfigureAwait(true);
            ApplyState(ImageHealthJson.Deserialize<ImageHealthState>(json), replaceRows: true);
            ErrorText = null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
#pragma warning disable CA1031 // Shown on the page.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ErrorText = Message(ex);
        }
    }

    private void ApplyFilter()
    {
        var search = SearchText.Trim();
        IEnumerable<ImageHealthRowViewModel> shown = _byId.Values;
        if (search.Length > 0)
        {
            shown = shown.Where(r => r.SearchText.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        Rows.ReplaceAll(shown.OrderBy(r => r.AddressSortKey, StringComparer.Ordinal));
        OnPropertyChanged(nameof(ShowEmpty));
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        var state = _state;
        if (state is null)
        {
            SummaryText = string.Empty;
            return;
        }

        var detected = _byId.Values.Count(r => r.AnyDetected);
        var pending = _byId.Values.Count(r => r.AnyPending);
        var parts = new List<string> { Count(state.Running, "camera with the app running", "cameras with the app running") };
        if (detected > 0)
        {
            parts.Add(Count(detected, "with a detection", "with a detection"));
        }

        if (pending > 0)
        {
            parts.Add(Count(pending, "pending", "pending"));
        }

        if (state.NotRunning > 0)
        {
            parts.Add(Count(state.NotRunning, "not running", "not running"));
        }

        if (state.WithoutApp > 0)
        {
            parts.Add(Count(state.WithoutApp, "without the app", "without the app"));
        }

        if (state.Failed > 0)
        {
            parts.Add(Count(state.Failed, "could not be checked", "could not be checked"));
        }

        if (Rows.Count != _byId.Count)
        {
            parts.Add(string.Create(CultureInfo.CurrentCulture, $"{Rows.Count:N0} shown"));
        }

        if (state.CheckedUtc is { } at && !state.Checking)
        {
            parts.Add("checked " + at.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture));
        }

        SummaryText = string.Join(" · ", parts);
        EmptyText = state.Checking
            ? "Checking the cameras."
            : _byId.Count == 0
                ? state.Total == 0 ? "No video devices yet." : "No camera has AXIS Image Health Analytics."
                : "No camera matches the search.";
    }

    private static string Count(int n, string one, string many) =>
        string.Create(CultureInfo.CurrentCulture, $"{n:N0} {(n == 1 ? one : many)}");

    private async Task WatchAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var events = _ctx.WatchEventsAsync(ct).GetAsyncEnumerator(ct);
                try
                {
                    while (await events.MoveNextAsync().ConfigureAwait(true))
                    {
                        HandleEvent(events.Current);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
#pragma warning disable CA1031 // The connection dropped: watch again after a short delay.
                catch (Exception)
#pragma warning restore CA1031
                {
                }
                finally
                {
                    await events.DisposeAsync().ConfigureAwait(true);
                }

                await Task.Delay(ReconnectDelay, ct).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
            // page hidden
        }
    }

    /// <summary>gRPC errors carry the user message in Status.Detail; read it without a Grpc reference.</summary>
    private static string Message(Exception ex)
    {
        var detail = ex.GetType().GetProperty("Status")?.GetValue(ex) is { } status
            ? status.GetType().GetProperty("Detail")?.GetValue(status) as string
            : null;
        return string.IsNullOrEmpty(detail) ? ex.Message : detail;
    }
}
