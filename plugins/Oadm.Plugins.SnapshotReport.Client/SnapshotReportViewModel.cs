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

/// <summary>
/// Page "Snapshot report": a grid of current snapshots of every video source of the managed video devices,
/// for the yearly maintenance check, and the PDF export of the selected tiles.
/// </summary>
public sealed partial class SnapshotReportViewModel : ObservableObject, IDisposable
{
    public const double MinTileWidth = 200;
    public const double MaxTileWidth = 720;

    /// <summary>Snapshots the page loads at the same time (the server limits all callers to 4 as well).</summary>
    public const int LoadParallelism = 4;

    private readonly ICorePluginClientContext _ctx;
    private readonly ISnapshotReportUi _ui;
    private readonly ExportSettingsStore _settings;
    private readonly TimeProvider _time;
    private CancellationTokenSource? _loadCts;
    private int _loaded;
    private int _toLoad;
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

    /// <summary>Tiles matching the search, in grid order.</summary>
    public ObservableCollection<SnapshotTileViewModel> FilteredTiles { get; } = [];

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

    public bool IsEmpty => !IsLoading && Tiles.Count == 0 && !HasError;

    public IEnumerable<SnapshotTileViewModel> SelectedTiles => Tiles.Where(t => t.IsSelected);

    /// <summary>"14 pictures from 9 cameras · 12 selected".</summary>
    public string StatusLine
    {
        get
        {
            var devices = Tiles.Select(t => t.DeviceId).Distinct().Count();
            var selected = Tiles.Count(t => t.IsSelected);
            var failed = Tiles.Count(t => t.IsError);
            var text = string.Create(CultureInfo.InvariantCulture, $"{Tiles.Count} {(Tiles.Count == 1 ? "picture" : "pictures")} from {devices} {(devices == 1 ? "camera" : "cameras")} · {selected} selected");
            if (failed > 0)
            {
                text += string.Create(CultureInfo.InvariantCulture, $" · {failed} failed");
            }

            if (FilteredTiles.Count != Tiles.Count)
            {
                text += string.Create(CultureInfo.InvariantCulture, $" · {FilteredTiles.Count} shown");
            }

            return text;
        }
    }

    /// <summary>Lists the sources and loads every snapshot (also "Refresh all").</summary>
    [RelayCommand]
    public async Task RefreshAllAsync()
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        var cts = new CancellationTokenSource();
        _loadCts = cts;
        ErrorMessage = null;
        IsLoading = true;
        IsProgressVisible = true;
        Progress = 0;
        ProgressText = "Reading video sources...";
        try
        {
            var json = await _ctx.InvokeAsync(SnapshotReportMethods.ListSources, SnapshotReportJson.Serialize(new ListSourcesRequest()), cts.Token).ConfigureAwait(true);
            var result = SnapshotReportJson.Deserialize<ListSourcesResult>(json);
            var selection = Tiles.ToDictionary(t => (t.DeviceId, t.Camera), t => t.IsSelected);
            foreach (var old in Tiles)
            {
                old.PropertyChanged -= OnTilePropertyChanged;
                old.Dispose();
            }

            Tiles.Clear();
            foreach (var tile in result.Tiles)
            {
                var vm = new SnapshotTileViewModel(tile, RefreshTileAsync, _ui.ShowPreview);
                if (selection.TryGetValue((vm.DeviceId, vm.Camera), out var selected))
                {
                    vm.IsSelected = selected;
                }

                vm.PropertyChanged += OnTilePropertyChanged;
                Tiles.Add(vm);
            }

            ApplyFilter();
            await LoadSnapshotsAsync([.. Tiles.Where(t => !t.IsDeviceError)], cts.Token).ConfigureAwait(true);
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

    partial void OnTileWidthChanged(double value)
    {
        var clamped = Math.Clamp(value, MinTileWidth, MaxTileWidth);
        if (Math.Abs(clamped - value) > 0.1)
        {
            TileWidth = clamped;
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

    private void ApplyFilter()
    {
        var terms = (SearchText ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        FilteredTiles.Clear();
        foreach (var tile in Tiles.Where(t => terms.All(term => t.SearchText.Contains(term, StringComparison.OrdinalIgnoreCase))))
        {
            FilteredTiles.Add(tile);
        }

        SyncSelectAll();
        OnPropertyChanged(nameof(StatusLine));
    }

    private void OnTilePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SnapshotTileViewModel.IsSelected))
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
            IsAllSelected = FilteredTiles.Count > 0 && FilteredTiles.All(t => t.IsSelected);
        }
        finally
        {
            _syncingSelectAll = false;
        }
    }

    private async Task LoadSnapshotsAsync(IReadOnlyList<SnapshotTileViewModel> tiles, CancellationToken ct)
    {
        _loaded = 0;
        _toLoad = tiles.Count;
        foreach (var tile in tiles)
        {
            tile.SetWaiting();
        }

        UpdateProgress();
        var queue = new System.Collections.Concurrent.ConcurrentQueue<SnapshotTileViewModel>(tiles);
        async Task WorkerAsync()
        {
            while (queue.TryDequeue(out var tile))
            {
                ct.ThrowIfCancellationRequested();
                await LoadOneAsync(tile, ct).ConfigureAwait(true);
                Interlocked.Increment(ref _loaded);
                UpdateProgress();
            }
        }

        // Workers continue on the UI thread in the app; the concurrent queue keeps tests without one safe.
        await Task.WhenAll(Enumerable.Range(0, Math.Min(LoadParallelism, Math.Max(1, tiles.Count))).Select(_ => WorkerAsync())).ConfigureAwait(true);
        IsProgressVisible = false;
    }

    private void UpdateProgress()
    {
        var loaded = Volatile.Read(ref _loaded);
        Progress = _toLoad == 0 ? 100 : 100.0 * loaded / _toLoad;
        ProgressText = loaded < _toLoad
            ? string.Create(CultureInfo.InvariantCulture, $"Loading snapshots {loaded + 1} of {_toLoad}")
            : string.Create(CultureInfo.InvariantCulture, $"{_toLoad} snapshots loaded");
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
            tile.SetImage(jpeg, image, result.Width, result.Height, result.CapturedUtc);
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

    /// <summary>gRPC errors carry the readable message in Status.Detail; take it without referencing Grpc.</summary>
    internal static string ErrorText(Exception ex)
    {
        var detail = ex.GetType().GetProperty("Status")?.GetValue(ex) is { } status
            ? status.GetType().GetProperty("Detail")?.GetValue(status) as string
            : null;
        return string.IsNullOrWhiteSpace(detail) ? ex.Message : detail;
    }
}
