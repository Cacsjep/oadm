using System.Globalization;
using System.Text.Json;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Collections;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Client.Validation;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.EdgeRecordingExport.Client;

/// <summary>
/// The Edge Recording Export page: choose cameras (the shared device picker, video devices only), all recordings or a
/// range in this computer's time, Search (one server call per camera, <see cref="SearchParallelism"/> at a time), then
/// Download into a chosen folder: one recording after the other (the camera's link is the limit), a folder per camera,
/// overall progress, Stop. The view model lives as long as the rail page, so a download keeps running while the user is on
/// other pages; it stops when the page is disposed (log out, plugin turned off).
/// </summary>
public sealed partial class EdgeRecordingViewModel : ValidatingViewModel, IDisposable
{
    /// <summary>Cameras searched at the same time.</summary>
    public const int SearchParallelism = 4;

    private readonly ICorePluginClientContext _ctx;
    private readonly SynchronizationContext? _ui;
    private DeviceChoice _choice = DeviceChoice.Devices([]);
    private CancellationTokenSource? _work;
    private long _progressPosted;
    private bool _disposed;

    public EdgeRecordingViewModel(ICorePluginClientContext ctx)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        _ui = SynchronizationContext.Current;
        var today = new DateTimeOffset(DateTime.Today);
        StartDate = today;
        StartTime = TimeSpan.Zero;
        StopDate = today.AddDays(1);
        StopTime = TimeSpan.Zero;
        Validation
            .Rule(nameof(StartDate), StartError)
            .Rule(nameof(StopDate), StopError);
        Validation.Validate();
        _ctx.PermissionsChanged += OnPermissionsChanged;
    }

    /// <summary>Opens the device picker; set by the view (the window owner), tests replace it.</summary>
    public Func<IReadOnlyList<IDeviceInfo>, DeviceChoice, Task<DeviceChoice?>>? PickCameras { get; set; }

    /// <summary>Asks for the target folder; set by the view, tests replace it. Null = cancelled.</summary>
    public Func<Task<string?>>? PickFolder { get; set; }

    public RangeObservableCollection<CameraRowViewModel> Rows { get; } = [];

    /// <summary>The button text like the device buttons of the event rules: "Choose cameras", "Selected cameras: Tag Building A".</summary>
    public string CamerasText => _choice.Kind == DeviceChoiceKind.All
        ? "Selected cameras: All"
        : _choice.IsEmpty ? "Choose cameras" : "Selected cameras: " + DeviceSelectorTexts.Brief(_choice.DeviceIds.Count, _choice.Tags, SingleAddress());

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRange))]
    public partial bool AllRecordings { get; set; }

    public bool IsRange
    {
        get => !AllRecordings;
        set => AllRecordings = !value;
    }

    /// <summary>The start day (date picker; the time of day comes from <see cref="StartTime"/>).</summary>
    [ObservableProperty]
    public partial DateTimeOffset? StartDate { get; set; }

    /// <summary>The start time of day (time picker, 24 hours).</summary>
    [ObservableProperty]
    public partial TimeSpan? StartTime { get; set; }

    [ObservableProperty]
    public partial DateTimeOffset? StopDate { get; set; }

    [ObservableProperty]
    public partial TimeSpan? StopTime { get; set; }

    /// <summary>The start in this computer's time, as UTC; null while a part is missing.</summary>
    public DateTimeOffset? RangeStart => EdgeRecordingFormat.Combine(StartDate, StartTime);

    public DateTimeOffset? RangeStop => EdgeRecordingFormat.Combine(StopDate, StopTime);

    /// <summary>The visible error of the From row (below its pickers).</summary>
    public string? StartErrorText => ErrorOf(nameof(StartDate));

    public string? StopErrorText => ErrorOf(nameof(StopDate));

    // A time picker belongs to the date picker of its row: the row's error shows once either was changed.
    partial void OnStartTimeChanged(TimeSpan? value) => Validation.Touch(nameof(StartDate));

    partial void OnStopTimeChanged(TimeSpan? value) => Validation.Touch(nameof(StopDate));

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle), nameof(IsBusy))]
    public partial bool IsSearching { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle), nameof(IsBusy))]
    public partial bool IsDownloading { get; set; }

    public bool IsBusy => IsSearching || IsDownloading;

    public bool IsIdle => !IsBusy;

    [ObservableProperty]
    public partial double ProgressValue { get; set; }

    [ObservableProperty]
    public partial string ProgressText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProgress))]
    public partial bool ShowProgress { get; set; }

    public bool HasProgress => ShowProgress;

    [ObservableProperty]
    public partial string SummaryText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorText { get; set; }

    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    public bool ShowEmpty => Rows.Count == 0;

    public static string EmptyText => "Choose cameras and click Search.";

    /// <summary>The role may download recordings.</summary>
    public bool CanExport => _ctx.Has(PermissionKeys.Of(EdgeRecordingInfo.PluginId, EdgeRecordingInfo.ExportKey));

    public bool CanSearch => IsIdle && !_choice.IsEmpty && Validation.IsValid;

    public string? SearchTip => IsBusy ? null : _choice.IsEmpty ? "Choose cameras first" : Validation.FirstError;

    public bool CanDownload => IsIdle && CanExport && Rows.Any(r => r.RecordingCount > 0);

    public string? DownloadTip => !CanExport ? "Your role does not allow exporting recordings." : IsIdle && !Rows.Any(r => r.RecordingCount > 0) ? "Search first" : null;

    /// <summary>The video devices the picker lists.</summary>
    public IReadOnlyList<IDeviceInfo> VideoDevices() => [.. _ctx.Devices.Where(d => d.HasVideo)];

    /// <summary>The chosen cameras now: the chosen devices and the video devices with a chosen tag (all of them for All).</summary>
    public IReadOnlyList<IDeviceInfo> ChosenCameras()
    {
        var video = VideoDevices();
        if (_choice.Kind == DeviceChoiceKind.All)
        {
            return video;
        }

        var ids = _choice.DeviceIds.ToHashSet();
        var tags = new HashSet<string>(_choice.Tags, StringComparer.OrdinalIgnoreCase);
        return [.. video.Where(d => ids.Contains(d.Id) || d.Tags.Any(tags.Contains))];
    }

    /// <summary>Sets the choice (the picker's result; tests).</summary>
    public void SetChoice(DeviceChoice choice)
    {
        _choice = choice ?? throw new ArgumentNullException(nameof(choice));
        OnPropertyChanged(nameof(CamerasText));
        RaiseCommands();
    }

    [RelayCommand]
    private async Task ChooseCamerasAsync()
    {
        if (PickCameras is null || IsBusy)
        {
            return;
        }

        if (await PickCameras(VideoDevices(), _choice).ConfigureAwait(true) is { } choice)
        {
            SetChoice(choice);
        }
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        Validation.ShowAll(nameof(StartDate), nameof(StopDate));
        if (!CanSearch)
        {
            return;
        }

        var cameras = ChosenCameras();
        if (cameras.Count == 0)
        {
            ErrorText = "None of the chosen devices is a video device.";
            return;
        }

        var start = AllRecordings ? null : RangeStart;
        var stop = AllRecordings ? null : RangeStop;
        var rows = cameras.Select(d => new CameraRowViewModel(d)).OrderBy(r => r.AddressSortKey, StringComparer.Ordinal).ToList();
        Rows.ReplaceAll(rows);
        OnPropertyChanged(nameof(ShowEmpty));
        ErrorText = null;
        using var work = Begin();
        IsSearching = true;
        ShowProgress = true;
        var done = 0;
        UpdateSearchProgress(0, rows.Count);
        using var gate = new SemaphoreSlim(SearchParallelism);
        try
        {
            await Task.WhenAll(rows.Select(async row =>
            {
                await gate.WaitAsync(work.Token).ConfigureAwait(true);
                try
                {
                    row.SetSearching();
                    row.SetResult(await SearchOneAsync(row, start, stop, work.Token).ConfigureAwait(true));
                }
                finally
                {
                    gate.Release();
                }

                UpdateSearchProgress(Interlocked.Increment(ref done), rows.Count);
            })).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (work.IsCancellationRequested)
        {
            foreach (var row in rows.Where(r => r.Result is null))
            {
                row.SetNotChecked();
            }
        }
        finally
        {
            IsSearching = false;
            ShowProgress = false;
            End(work);
            UpdateSummary(stopped: work.IsCancellationRequested);
        }
    }

    [RelayCommand]
    private async Task DownloadAsync()
    {
        if (!CanDownload || PickFolder is null)
        {
            return;
        }

        var folder = await PickFolder().ConfigureAwait(true);
        if (string.IsNullOrEmpty(folder))
        {
            return;
        }

        var rows = Rows.Where(r => r.RecordingCount > 0).ToList();
        var totalFiles = rows.Sum(r => r.RecordingCount);
        long? totalBytes = rows.All(r => r.EstimatedBytes is not null) ? rows.Sum(r => r.EstimatedBytes!.Value) : null;
        var files = new RecordingFiles(folder);
        ErrorText = null;
        using var work = Begin();
        IsDownloading = true;
        ShowProgress = true;
        var fileIndex = 0;
        long doneBytes = 0;
        var stopped = false;
        try
        {
            foreach (var row in rows)
            {
                row.BeginDownload();
                string? lastError = null;
                var index = 0;
                foreach (var recording in row.Recordings)
                {
                    work.Token.ThrowIfCancellationRequested();
                    index++;
                    fileIndex++;
                    row.SetDownloading(index);
                    var current = fileIndex;
                    var before = doneBytes;
                    UpdateDownloadProgress(current, totalFiles, before, totalBytes, null);
                    try
                    {
                        var written = await DownloadOneAsync(files, row, recording, bytes =>
                            Post(() => UpdateDownloadProgress(current, totalFiles, before + bytes, totalBytes, null)), work.Token).ConfigureAwait(true);
                        doneBytes += written ?? 0;
                        if (written is null)
                        {
                            row.Skipped++;
                        }
                        else
                        {
                            row.Downloaded++;
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && !work.IsCancellationRequested)
                    {
                        // The folder cannot be written (disk full, removed drive): every further file would fail the same way.
                        row.Failed++;
                        row.EndDownload(stopped: true, ex.Message);
                        ErrorText = "The download stopped: " + ex.Message;
                        stopped = true;
                        return;
                    }
#pragma warning disable CA1031 // One recording that fails (deleted meanwhile, camera gone) must not stop the others; shown in its row.
                    catch (Exception ex) when (ex is not OperationCanceledException && !work.IsCancellationRequested)
#pragma warning restore CA1031
                    {
                        row.Failed++;
                        lastError = ServerErrors.Message(ex);
                    }
                }

                row.EndDownload(stopped: false, lastError);
            }
        }
#pragma warning disable CA1031 // Stop: the call ends with whatever the cancelled read throws.
        catch (Exception) when (work.IsCancellationRequested)
#pragma warning restore CA1031
        {
            stopped = true;
            foreach (var row in rows.Where(r => r.IsBusy))
            {
                row.EndDownload(stopped: true, null);
            }
        }
        finally
        {
            IsDownloading = false;
            ShowProgress = false;
            End(work);
            var downloaded = rows.Sum(r => r.Downloaded);
            var skipped = rows.Sum(r => r.Skipped);
            var failed = rows.Sum(r => r.Failed);
            SummaryText = string.Create(CultureInfo.CurrentCulture, $"{(stopped ? "Download stopped" : "Download finished")}: {downloaded:N0} downloaded")
                + (skipped > 0 ? string.Create(CultureInfo.CurrentCulture, $", {skipped:N0} already there") : string.Empty)
                + (failed > 0 ? string.Create(CultureInfo.CurrentCulture, $", {failed:N0} failed") : string.Empty)
                + " · " + EdgeRecordingFormat.Size(doneBytes) + " in " + folder;
        }
    }

    /// <summary>Stops the search or the download; files already complete stay.</summary>
    [RelayCommand]
    private void Stop() => _work?.Cancel();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _ctx.PermissionsChanged -= OnPermissionsChanged;
        _work?.Cancel();
    }

    protected override void OnValidationChanged()
    {
        base.OnValidationChanged();
        OnPropertyChanged(nameof(StartErrorText));
        OnPropertyChanged(nameof(StopErrorText));
        RaiseCommands();
    }

    private async Task<SearchResult> SearchOneAsync(CameraRowViewModel row, DateTimeOffset? start, DateTimeOffset? stop, CancellationToken ct)
    {
        try
        {
            var json = await _ctx.InvokeAsync(EdgeRecordingMethods.Search, JsonSerializer.Serialize(new SearchRequest(row.Device.Id, start, stop), EdgeRecordingInfo.Json), ct).ConfigureAwait(true);
            return JsonSerializer.Deserialize<SearchResult>(json ?? string.Empty, EdgeRecordingInfo.Json)
                ?? new SearchResult(row.Device.Id, [], null, null, "The server sent no result");
        }
        catch (JsonException)
        {
            return new SearchResult(row.Device.Id, [], null, null, "The server sent a result OADM cannot read");
        }
#pragma warning disable CA1031 // Shown in the camera's row.
        catch (Exception ex) when (ex is not OperationCanceledException && !ct.IsCancellationRequested)
#pragma warning restore CA1031
        {
            return new SearchResult(row.Device.Id, [], null, null, ServerErrors.Message(ex));
        }
    }

    /// <summary>One recording into its camera folder: the bytes written, or null when the file was already there.</summary>
    private async Task<long?> DownloadOneAsync(RecordingFiles files, CameraRowViewModel row, RecordingEntry recording, Action<long> progress, CancellationToken ct)
    {
        var request = new ExportRequest(row.Device.Id, recording.Id, recording.DiskId, recording.Start, recording.Stop, row.Result?.Format);
        var download = await _ctx.OpenDownloadAsync(EdgeRecordingMethods.Export, JsonSerializer.Serialize(request, EdgeRecordingInfo.Json), ct).ConfigureAwait(true);
        await using (download.ConfigureAwait(true))
        {
            if (files.TargetFor(row.Device, download.FileName) is not { } path)
            {
                return null; // complete from an earlier download; disposing ends the transfer
            }

            return await RecordingFiles.WriteAsync(download.Content, path, progress, ct).ConfigureAwait(true);
        }
    }

    private CancellationTokenSource Begin()
    {
        _work = new CancellationTokenSource();
        RaiseCommands();
        return _work;
    }

    private void End(CancellationTokenSource work)
    {
        if (ReferenceEquals(_work, work))
        {
            _work = null;
        }

        RaiseCommands();
    }

    private void UpdateSearchProgress(int done, int total)
    {
        ProgressValue = total == 0 ? 0 : done * 100.0 / total;
        ProgressText = string.Create(CultureInfo.CurrentCulture, $"Searching cameras: {done:N0} of {total:N0}");
    }

    private void UpdateDownloadProgress(int file, int totalFiles, long bytes, long? totalBytes, string? _)
    {
        ProgressValue = totalBytes is > 0 ? Math.Min(99.9, bytes * 100.0 / totalBytes.Value) : (file - 1) * 100.0 / Math.Max(1, totalFiles);
        ProgressText = string.Create(CultureInfo.CurrentCulture, $"Downloading recording {file:N0} of {totalFiles:N0} · {EdgeRecordingFormat.Size(bytes)}")
            + (totalBytes is { } t ? " of " + EdgeRecordingFormat.AboutSize(t) : string.Empty);
    }

    /// <summary>Runs <paramref name="action"/> on the page's thread, at most every 200 ms (byte progress of a file).</summary>
    private void Post(Action action)
    {
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _progressPosted) < 200)
        {
            return;
        }

        Interlocked.Exchange(ref _progressPosted, now);
        if (_ui is null)
        {
            action();
        }
        else
        {
            _ui.Post(_ => action(), null);
        }
    }

    private void UpdateSummary(bool stopped)
    {
        var found = Rows.Where(r => r.Result is { Problem: null }).ToList();
        var recordings = found.Sum(r => r.RecordingCount);
        var withRecordings = found.Where(r => r.RecordingCount > 0).ToList();
        var problems = Rows.Count(r => r.Result is { Problem: not null });
        var text = (stopped ? "Search stopped: " : string.Empty)
            + EdgeRecordingFormat.Count(Rows.Count, "camera") + " · " + EdgeRecordingFormat.Count(recordings, "recording");
        if (withRecordings.Count > 0)
        {
            text += " · " + (withRecordings.All(r => r.EstimatedBytes is not null)
                ? EdgeRecordingFormat.AboutSize(withRecordings.Sum(r => r.EstimatedBytes!.Value))
                : "size unknown");
        }

        if (problems > 0)
        {
            text += string.Create(CultureInfo.CurrentCulture, $" · {problems:N0} not searched");
        }

        SummaryText = text;
    }

    private string? StartError() =>
        AllRecordings ? null : StartDate is null ? "Choose the start date." : StartTime is null ? "Choose the start time." : null;

    private string? StopError()
    {
        if (AllRecordings)
        {
            return null;
        }

        if (StopDate is null || StopTime is null)
        {
            return StopDate is null ? "Choose the stop date." : "Choose the stop time.";
        }

        return RangeStart is { } start && RangeStop is { } stop && start >= stop ? "The stop must be after the start." : null;
    }

    private string? SingleAddress() =>
        _choice.DeviceIds.Count == 1 ? _ctx.Devices.FirstOrDefault(d => d.Id == _choice.DeviceIds[0])?.Address : null;

    private void OnPermissionsChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(CanExport));
        RaiseCommands();
    }

    partial void OnAllRecordingsChanged(bool value) => Validation.Validate();

    partial void OnIsSearchingChanged(bool value) => RaiseCommands();

    partial void OnIsDownloadingChanged(bool value) => RaiseCommands();

    private void RaiseCommands()
    {
        OnPropertyChanged(nameof(CanSearch));
        OnPropertyChanged(nameof(SearchTip));
        OnPropertyChanged(nameof(CanDownload));
        OnPropertyChanged(nameof(DownloadTip));
    }
}
