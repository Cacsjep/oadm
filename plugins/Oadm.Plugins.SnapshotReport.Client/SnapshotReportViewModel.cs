using System.Collections.ObjectModel;
using System.Globalization;

using Avalonia.Media;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Sdk.Client;

namespace Oadm.Plugins.SnapshotReport.Client;

/// <summary>Dialogs and image decoding of the page; the view implements it, tests fake it.</summary>
public interface ISnapshotReportUi
{
    /// <summary>Decodes a JPEG to a bitmap at most <paramref name="decodeWidth"/> pixels wide (0 = full size). Any thread.</summary>
    IImage? Decode(byte[] jpeg, int decodeWidth);

    /// <summary>Shows the large preview of a tile.</summary>
    void ShowPreview(SnapshotTileViewModel tile);

    /// <summary>Shows the export dialog (modal) until the report is saved or cancelled.</summary>
    Task ShowExportAsync(ExportReportViewModel dialog);
}

/// <summary>One line of the virtualized tile grid: as many tiles as fit the page width.</summary>
public sealed class SnapshotTileRow(int index, IReadOnlyList<SnapshotTileViewModel> tiles)
{
    /// <summary>Position in <see cref="SnapshotReportViewModel.Rows"/>.</summary>
    public int Index { get; } = index;

    public IReadOnlyList<SnapshotTileViewModel> Tiles { get; } = tiles;
}

/// <summary>
/// Page "Snapshot report": a grid of current snapshots of every video source of the managed video devices,
/// for the yearly maintenance check, and the PDF export of the selected tiles.
/// Scales to thousands of tiles: the grid is virtualized by rows (<see cref="Rows"/>), snapshots load only for the
/// rows the view shows (<see cref="SetRowRealized"/>) with at most <see cref="LoadParallelism"/> requests in flight,
/// loads of rows scrolled away are cancelled, and at most <see cref="MaxCachedImages"/> decoded pictures of hidden
/// tiles are kept.
/// </summary>
public sealed partial class SnapshotReportViewModel : ObservableObject, IDisposable
{
    public const double MinTileWidth = 200;
    public const double MaxTileWidth = 720;

    /// <summary>Horizontal space of a tile besides its width (right margin of the theme's Border.tile).</summary>
    public const double TileSpacing = 12;

    /// <summary>Snapshots the page loads at the same time (the server limits all callers to 4 as well).</summary>
    public const int LoadParallelism = 4;

    /// <summary>Tiles loaded right after a refresh, before the view reports which rows it shows (and without a view).</summary>
    public const int InitialLoadCount = 24;

    /// <summary>Decoded pictures kept for tiles that are not shown; older ones are dropped and reloaded when shown again.</summary>
    public const int MaxCachedImages = 150;

    private readonly ICorePluginClientContext _ctx;
    private readonly ISnapshotReportUi _ui;
    private readonly ExportSettingsStore _settings;
    private readonly TimeProvider _time;
    private readonly Lock _sync = new();
    private readonly HashSet<SnapshotTileRow> _realizedRows = [];
    private readonly LinkedList<SnapshotTileViewModel> _imageCache = new();
    private CancellationTokenSource? _loadCts;
    private CancellationToken _currentToken;
    private TaskCompletionSource _idle = NewIdle(completed: true);
    private bool _eagerFirst = true;
    private int _inFlight;
    private int _loadedSinceRefresh;
    private int _deviceCount;
    private bool _syncingSelectAll;

    public SnapshotReportViewModel(ICorePluginClientContext ctx, ISnapshotReportUi ui, ExportSettingsStore? settings = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(ui);
        _ctx = ctx;
        _ui = ui;
        _settings = settings ?? new ExportSettingsStore();
        _time = time ?? TimeProvider.System;
    }

    public ObservableCollection<SnapshotTileViewModel> Tiles { get; } = [];

    /// <summary>Tiles matching the search, in grid order (replaced as a whole).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<SnapshotTileViewModel> FilteredTiles { get; private set; } = [];

    /// <summary><see cref="FilteredTiles"/> in rows of <see cref="Columns"/> tiles: the items of the virtualized grid.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<SnapshotTileRow> Rows { get; private set; } = [];

    /// <summary>Tiles per row, from <see cref="AvailableWidth"/> and <see cref="TileWidth"/>.</summary>
    public int Columns => AvailableWidth <= 0 ? 1 : Math.Max(1, (int)((AvailableWidth + 0.5) / (TileWidth + TileSpacing)));

    /// <summary>Width the view has for the grid (set by the view).</summary>
    [ObservableProperty]
    public partial double AvailableWidth { get; set; }

    [ObservableProperty]
    public partial string? SearchText { get; set; }

    /// <summary>Tile width in pixels (size slider).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PictureHeight))]
    public partial double TileWidth { get; set; } = 320;

    /// <summary>Height of the picture area: 16:9 of the tile content width.</summary>
    public double PictureHeight => Math.Round((TileWidth - 22) * 9 / 16);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    public partial double Progress { get; private set; }

    [ObservableProperty]
    public partial string? ProgressText { get; private set; }

    [ObservableProperty]
    public partial bool IsProgressVisible { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; private set; }

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    /// <summary>"Select all" check box: true when every shown tile is selected.</summary>
    [ObservableProperty]
    public partial bool IsAllSelected { get; set; }

    public bool IsEmpty => HasCreated && !IsLoading && Tiles.Count == 0 && !HasError;

    /// <summary>
    /// False until the user pressed Create snapshots: opening the page contacts no device (a site can have hundreds of
    /// cameras; user decision 2026-10-09).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty), nameof(IsWaitingForCreate), nameof(StatusLine))]
    [NotifyCanExecuteChangedFor(nameof(CreateSnapshotsCommand), nameof(RefreshAllCommand))]
    public partial bool HasCreated { get; private set; }

    /// <summary>The page before Create snapshots: shows the prompt instead of the grid.</summary>
    public bool IsWaitingForCreate => !HasCreated;

    public IEnumerable<SnapshotTileViewModel> SelectedTiles => Tiles.Where(t => t.IsSelected);

    /// <summary>Snapshot requests running now (tests).</summary>
    public int LoadsInFlight
    {
        get
        {
            lock (_sync)
            {
                return _inFlight;
            }
        }
    }

    /// <summary>Tiles holding a decoded picture now.</summary>
    public int CachedImageCount
    {
        get
        {
            lock (_sync)
            {
                return _imageCache.Count;
            }
        }
    }

    /// <summary>"14 pictures from 9 cameras · 12 selected". One pass, no allocations per tile.</summary>
    public string StatusLine
    {
        get
        {
            if (!HasCreated)
            {
                return string.Empty;
            }

            int selected = 0, failed = 0;
            foreach (var tile in Tiles)
            {
                selected += tile.IsSelected ? 1 : 0;
                failed += tile.IsError ? 1 : 0;
            }

            var devices = _deviceCount;
            var text = string.Create(CultureInfo.InvariantCulture, $"{Tiles.Count:N0} {(Tiles.Count == 1 ? "picture" : "pictures")} from {devices:N0} {(devices == 1 ? "camera" : "cameras")} · {selected:N0} selected");
            if (failed > 0)
            {
                text += string.Create(CultureInfo.InvariantCulture, $" · {failed:N0} failed");
            }

            if (FilteredTiles.Count != Tiles.Count)
            {
                text += string.Create(CultureInfo.InvariantCulture, $" · {FilteredTiles.Count:N0} shown");
            }

            return text;
        }
    }

    /// <summary>The first load, started only by the user (the page itself never contacts devices).</summary>
    [RelayCommand(CanExecute = nameof(IsWaitingForCreate))]
    private Task CreateSnapshotsAsync() => RefreshAllAsync();

    /// <summary>
    /// Lists the sources and loads the snapshots of the shown tiles (also "Refresh all"). Completes when every shown
    /// tile has its snapshot; the others load when they are scrolled into view.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasCreated))]
    public async Task RefreshAllAsync()
    {
        HasCreated = true;
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        var cts = new CancellationTokenSource();
        _loadCts = cts;
        _currentToken = cts.Token;
        ErrorMessage = null;
        IsLoading = true;
        IsProgressVisible = true;
        Progress = 0;
        ProgressText = "Reading video sources...";
        try
        {
            var json = await _ctx.InvokeAsync(SnapshotReportMethods.ListSources, SnapshotReportJson.Serialize(new ListSourcesRequest()), cts.Token).ConfigureAwait(true);
            var result = SnapshotReportJson.Deserialize<ListSourcesResult>(json);
            var selection = new Dictionary<(Guid, int), bool>(Tiles.Count);
            foreach (var old in Tiles)
            {
                selection[(old.DeviceId, old.Camera)] = old.IsSelected;
                old.PropertyChanged -= OnTilePropertyChanged;
                old.Dispose();
            }

            lock (_sync)
            {
                _imageCache.Clear();
                _realizedRows.Clear();
                _eagerFirst = true;
                _loadedSinceRefresh = 0;
                _idle = NewIdle(completed: false);
            }

            var tiles = new List<SnapshotTileViewModel>(result.Tiles.Count);
            var devices = new HashSet<Guid>();
            foreach (var tile in result.Tiles)
            {
                var vm = new SnapshotTileViewModel(tile, RefreshTileAsync, _ui.ShowPreview);
                if (selection.TryGetValue((vm.DeviceId, vm.Camera), out var selected))
                {
                    vm.IsSelected = selected;
                }

                if (!vm.IsDeviceError)
                {
                    vm.SetWaiting();
                    vm.NeedsLoad = true;
                }

                vm.PropertyChanged += OnTilePropertyChanged;
                tiles.Add(vm);
                devices.Add(vm.DeviceId);
            }

            _deviceCount = devices.Count;
            Tiles.Clear();
            foreach (var tile in tiles)
            {
                Tiles.Add(tile);
            }

            ApplyFilter();
            Pump(cts.Token);
            UpdateProgress();
            Task idle;
            lock (_sync)
            {
                idle = _idle.Task;
            }

            await idle.WaitAsync(cts.Token).ConfigureAwait(true);
            if (_loadCts == cts)
            {
                IsProgressVisible = false;
                Progress = 100;
                ProgressText = string.Create(CultureInfo.InvariantCulture, $"{Volatile.Read(ref _loadedSinceRefresh):N0} snapshots loaded");
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // A newer refresh took over.
        }
        catch (Exception ex)
        {
            ErrorMessage = "Could not read the video devices: " + ErrorText(ex);
            IsProgressVisible = false;
        }
        finally
        {
            if (_loadCts == cts)
            {
                IsLoading = false;
            }

            OnPropertyChanged(nameof(StatusLine));
            OnPropertyChanged(nameof(IsEmpty));
            ExportPdfCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>Completes when no shown tile waits for its snapshot and no request runs.</summary>
    public Task WhenIdleAsync()
    {
        lock (_sync)
        {
            return _idle.Task;
        }
    }

    /// <summary>
    /// The view realized (<paramref name="realized"/> true) or recycled a row of the virtualized grid. Shown rows load
    /// their snapshots; loads of a recycled row are cancelled.
    /// </summary>
    public void SetRowRealized(SnapshotTileRow row, bool realized)
    {
        ArgumentNullException.ThrowIfNull(row);
        lock (_sync)
        {
            if (realized)
            {
                if (!_realizedRows.Add(row))
                {
                    return;
                }

                _eagerFirst = false;
                foreach (var tile in row.Tiles)
                {
                    Touch(tile);
                }
            }
            else
            {
                if (!_realizedRows.Remove(row))
                {
                    return;
                }

                var wanted = WantedSet();
                foreach (var tile in row.Tiles)
                {
                    if (tile.LoadCts is { } loading && !wanted.Contains(tile))
                    {
                        loading.Cancel();
                    }
                }
            }
        }

        Pump(_currentToken);
    }

    /// <summary>Takes a new snapshot of one tile (a device error tile re-reads all sources).</summary>
    public async Task RefreshTileAsync(SnapshotTileViewModel tile)
    {
        ArgumentNullException.ThrowIfNull(tile);
        if (tile.IsDeviceError)
        {
            await RefreshAllAsync().ConfigureAwait(true);
            return;
        }

        await LoadOneAsync(tile, CancellationToken.None).ConfigureAwait(true);
        tile.NeedsLoad = false;
        OnPropertyChanged(nameof(StatusLine));
    }

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportPdfAsync()
    {
        var selected = SelectedTiles.ToList();
        if (selected.Count == 0)
        {
            return;
        }

        var export = new ExportReportViewModel(_ctx, selected, _settings, _time);
        await _ui.ShowExportAsync(export).ConfigureAwait(true);
    }

    private bool CanExport() => !IsLoading && Tiles.Any(t => t.IsSelected);

    partial void OnSearchTextChanged(string? value) => ApplyFilter();

    partial void OnTileWidthChanged(double oldValue, double newValue)
    {
        var clamped = Math.Clamp(newValue, MinTileWidth, MaxTileWidth);
        if (Math.Abs(clamped - newValue) > 0.1)
        {
            TileWidth = clamped;
            return;
        }

        if (ColumnsFor(oldValue) != Columns)
        {
            BuildRows();
        }
    }

    partial void OnAvailableWidthChanged(double oldValue, double newValue)
    {
        if (ColumnsFor(TileWidth, oldValue) != Columns)
        {
            BuildRows();
        }
    }

    partial void OnIsLoadingChanged(bool value) => ExportPdfCommand.NotifyCanExecuteChanged();

    partial void OnIsAllSelectedChanged(bool value)
    {
        if (_syncingSelectAll)
        {
            return;
        }

        _syncingSelectAll = true;
        try
        {
            foreach (var tile in FilteredTiles)
            {
                tile.IsSelected = value;
            }
        }
        finally
        {
            _syncingSelectAll = false;
        }

        OnPropertyChanged(nameof(StatusLine));
        ExportPdfCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;
        foreach (var tile in Tiles)
        {
            tile.Dispose();
        }
    }

    private static TaskCompletionSource NewIdle(bool completed)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (completed)
        {
            tcs.SetResult();
        }

        return tcs;
    }

    private int ColumnsFor(double tileWidth, double? availableWidth = null)
    {
        var width = availableWidth ?? AvailableWidth;
        return width <= 0 ? 1 : Math.Max(1, (int)((width + 0.5) / (tileWidth + TileSpacing)));
    }

    /// <summary>One pass over the tiles; the list is replaced as a whole (one notification, not one per tile).</summary>
    private void ApplyFilter()
    {
        var terms = (SearchText ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var filtered = new List<SnapshotTileViewModel>(terms.Length == 0 ? Tiles.Count : 16);
        foreach (var tile in Tiles)
        {
            if (Matches(tile.SearchText, terms))
            {
                filtered.Add(tile);
            }
        }

        FilteredTiles = filtered;
        lock (_sync)
        {
            _eagerFirst = true;
        }

        BuildRows();
        SyncSelectAll();
        OnPropertyChanged(nameof(StatusLine));
    }

    private static bool Matches(string text, string[] terms)
    {
        foreach (var term in terms)
        {
            if (!text.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private void BuildRows()
    {
        var columns = Columns;
        var tiles = FilteredTiles;
        var rows = new SnapshotTileRow[(tiles.Count + columns - 1) / columns];
        for (var r = 0; r < rows.Length; r++)
        {
            var start = r * columns;
            var count = Math.Min(columns, tiles.Count - start);
            var line = new SnapshotTileViewModel[count];
            for (var c = 0; c < count; c++)
            {
                line[c] = tiles[start + c];
            }

            rows[r] = new SnapshotTileRow(r, line);
        }

        lock (_sync)
        {
            // The view realizes the new rows; until then the first tiles count as shown.
            _realizedRows.Clear();
            _eagerFirst = true;
        }

        Rows = rows;
        OnPropertyChanged(nameof(Columns));
        Pump(_currentToken);
    }

    private void OnTilePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SnapshotTileViewModel.IsSelected) && !_syncingSelectAll)
        {
            SyncSelectAll();
            OnPropertyChanged(nameof(StatusLine));
            ExportPdfCommand.NotifyCanExecuteChanged();
        }
    }

    private void SyncSelectAll()
    {
        if (_syncingSelectAll)
        {
            return;
        }

        _syncingSelectAll = true;
        try
        {
            var all = FilteredTiles.Count > 0;
            foreach (var tile in FilteredTiles)
            {
                if (!tile.IsSelected)
                {
                    all = false;
                    break;
                }
            }

            IsAllSelected = all;
        }
        finally
        {
            _syncingSelectAll = false;
        }
    }

    /// <summary>The tiles the page wants pictures for, in grid order: the realized rows, plus the first tiles after a refresh.</summary>
    private List<SnapshotTileViewModel> WantedTiles()
    {
        var wanted = new List<SnapshotTileViewModel>();
        if (_eagerFirst)
        {
            var first = FilteredTiles;
            for (var i = 0; i < first.Count && i < InitialLoadCount; i++)
            {
                wanted.Add(first[i]);
            }
        }

        foreach (var row in _realizedRows.OrderBy(r => r.Index))
        {
            wanted.AddRange(row.Tiles);
        }

        return wanted;
    }

    private HashSet<SnapshotTileViewModel> WantedSet() => [.. WantedTiles()];

    /// <summary>Starts loads for shown tiles that wait, up to <see cref="LoadParallelism"/> at a time; completes the idle task when done.</summary>
    private void Pump(CancellationToken ct)
    {
        var start = new List<(SnapshotTileViewModel Tile, CancellationTokenSource Cts)>();
        lock (_sync)
        {
            var waiting = false;
            foreach (var tile in WantedTiles())
            {
                if (!tile.NeedsLoad || tile.LoadCts is not null)
                {
                    continue;
                }

                if (_inFlight + start.Count >= LoadParallelism || ct.IsCancellationRequested)
                {
                    waiting = true;
                    break;
                }

                var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                tile.LoadCts = cts;
                start.Add((tile, cts));
            }

            _inFlight += start.Count;
            if (_inFlight == 0 && !waiting)
            {
                _idle.TrySetResult();
            }
            else if (_idle.Task.IsCompleted)
            {
                _idle = NewIdle(completed: false);
            }
        }

        foreach (var (tile, cts) in start)
        {
            _ = RunLoadAsync(tile, cts, ct);
        }
    }

    private async Task RunLoadAsync(SnapshotTileViewModel tile, CancellationTokenSource cts, CancellationToken refreshToken)
    {
        try
        {
            await LoadOneAsync(tile, cts.Token).ConfigureAwait(true);
            tile.NeedsLoad = false;
            Interlocked.Increment(ref _loadedSinceRefresh);
        }
        catch (OperationCanceledException)
        {
            // Scrolled away or a newer refresh: the tile waits until it is shown again.
            if (!refreshToken.IsCancellationRequested)
            {
                tile.SetWaiting();
            }
        }
        finally
        {
            lock (_sync)
            {
                tile.LoadCts = null;
                _inFlight--;
            }

            cts.Dispose();
        }

        if (!refreshToken.IsCancellationRequested)
        {
            UpdateProgress();
        }

        // The current refresh (maybe a newer one) gets the free slot.
        Pump(_currentToken);
    }

    private void UpdateProgress()
    {
        if (!IsLoading)
        {
            return;
        }

        int remaining;
        lock (_sync)
        {
            remaining = 0;
            foreach (var tile in WantedTiles())
            {
                remaining += tile.NeedsLoad ? 1 : 0;
            }
        }

        var loaded = Volatile.Read(ref _loadedSinceRefresh);
        var total = loaded + remaining;
        Progress = total == 0 ? 100 : 100.0 * loaded / total;
        ProgressText = remaining > 0
            ? string.Create(CultureInfo.InvariantCulture, $"Loading snapshots {loaded + 1:N0} of {total:N0}")
            : string.Create(CultureInfo.InvariantCulture, $"{total:N0} snapshots loaded");
        OnPropertyChanged(nameof(StatusLine));
    }

    private async Task LoadOneAsync(SnapshotTileViewModel tile, CancellationToken ct)
    {
        tile.SetLoading();
        try
        {
            var request = new SnapshotRequest { DeviceId = tile.DeviceId, Camera = tile.Camera };
            var json = await _ctx.InvokeAsync(SnapshotReportMethods.Snapshot, SnapshotReportJson.Serialize(request), ct).ConfigureAwait(true);
            var result = SnapshotReportJson.Deserialize<SnapshotResult>(json);
            if (result.Error is not null || string.IsNullOrEmpty(result.JpegBase64))
            {
                tile.SetError(result.Error ?? "The server sent no picture");
                return;
            }

            var jpeg = Convert.FromBase64String(result.JpegBase64);
            var decodeWidth = (int)Math.Min(MaxTileWidth, Math.Max(result.Width, 1));
            var image = await Task.Run(() => _ui.Decode(jpeg, decodeWidth), ct).ConfigureAwait(true);
            if (ct.IsCancellationRequested)
            {
                (image as IDisposable)?.Dispose();
                ct.ThrowIfCancellationRequested();
            }

            tile.SetImage(jpeg, image, result.Width, result.Height, result.CapturedUtc);
            CacheImage(tile);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            tile.SetError(ErrorText(ex));
        }
    }

    /// <summary>Records a decoded picture and drops the oldest pictures of hidden tiles beyond <see cref="MaxCachedImages"/>.</summary>
    private void CacheImage(SnapshotTileViewModel tile)
    {
        var evict = new List<SnapshotTileViewModel>();
        lock (_sync)
        {
            Touch(tile);
            if (_imageCache.Count <= MaxCachedImages)
            {
                return;
            }

            var wanted = WantedSet();
            for (var node = _imageCache.First; node is not null && _imageCache.Count - evict.Count > MaxCachedImages; node = node.Next)
            {
                if (!wanted.Contains(node.Value))
                {
                    evict.Add(node.Value);
                }
            }

            foreach (var old in evict)
            {
                _imageCache.Remove(old.CacheNode!);
                old.CacheNode = null;
                old.NeedsLoad = true;
            }
        }

        foreach (var old in evict)
        {
            old.Unload();
        }
    }

    /// <summary>Marks a tile with a picture as recently shown (end of the cache list). Call under the lock.</summary>
    private void Touch(SnapshotTileViewModel tile)
    {
        if (tile.CacheNode is { } node)
        {
            _imageCache.Remove(node);
            _imageCache.AddLast(node);
        }
        else if (tile.HasImage)
        {
            tile.CacheNode = _imageCache.AddLast(tile);
        }
    }

    /// <summary>gRPC errors carry the readable message in Status.Detail; take it without referencing Grpc.</summary>
    internal static string ErrorText(Exception ex)
    {
        var detail = ex.GetType().GetProperty("Status")?.GetValue(ex) is { } status
            ? status.GetType().GetProperty("Detail")?.GetValue(status) as string
            : null;
        return string.IsNullOrWhiteSpace(detail) ? ex.Message : detail;
    }
}
