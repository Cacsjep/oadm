using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Validation;

namespace Oadm.Plugins.SnapshotReport.Client;

/// <summary>A file the user chose for the report. Dispose closes it.</summary>
public sealed record ReportTarget(Stream Stream, string DisplayPath, string? Folder) : IDisposable
{
    public void Dispose() => Stream.Dispose();
}

/// <summary>The save dialog of the export window (tests fake it).</summary>
public interface IReportSavePicker
{
    /// <summary>Asks where to save the PDF; null = cancelled.</summary>
    Task<ReportTarget?> PickAsync(string suggestedFileName, string? folder);
}

/// <summary>
/// Export dialog: site / customer, technician and date (remembered per client except the date), then the
/// server builds the PDF from fresh full-HD snapshots of the selected tiles and the dialog saves it. Field errors show
/// below their input once edited (<see cref="ValidatingViewModel"/>); Export stays disabled with the reason as tooltip.
/// </summary>
public sealed partial class ExportReportViewModel : ValidatingViewModel
{
    /// <summary>Longest site and technician text (one line in the PDF footer).</summary>
    public const int MaxTextLength = 120;

    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    private readonly ICorePluginClientContext _ctx;
    private readonly ExportSettingsStore _store;
    private readonly ExportSettings _settings;
    private readonly IReadOnlyList<ReportItem> _items;
    private CancellationTokenSource? _cts;
    private string? _jobId;

    public ExportReportViewModel(ICorePluginClientContext ctx, IReadOnlyList<SnapshotTileViewModel> tiles, ExportSettingsStore store, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(tiles);
        ArgumentNullException.ThrowIfNull(store);
        _ctx = ctx;
        _store = store;
        _settings = store.Load();
        _items = [.. tiles.Select(t => new ReportItem { DeviceId = t.DeviceId, Camera = t.Camera })];
        Site = _settings.Site;
        Technician = _settings.Technician;
        DateText = DateOnly.FromDateTime((time ?? TimeProvider.System).GetLocalNow().Date).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        Validation
            .Rule(nameof(Site), () => string.IsNullOrWhiteSpace(Site) ? "Enter the site or customer name."
                : Site.Trim().Length > MaxTextLength ? $"Use at most {MaxTextLength} characters." : null)
            .Rule(nameof(Technician), () => (Technician ?? string.Empty).Trim().Length > MaxTextLength ? $"Use at most {MaxTextLength} characters." : null)
            .Rule(nameof(DateText), () => Date is null ? "Enter the date as yyyy-MM-dd." : null);
        Validation.Validate();
        Validation.Reset(); // remembered values: nothing shown before the user edits a field
        var devices = tiles.Select(t => t.DeviceId).Distinct().Count();
        Summary = string.Create(CultureInfo.InvariantCulture,
            $"{tiles.Count} {(tiles.Count == 1 ? "snapshot" : "snapshots")} from {devices} {(devices == 1 ? "camera" : "cameras")}. The server takes new snapshots (up to {SnapshotReportPluginInfo.ReportMaxWidth}x{SnapshotReportPluginInfo.ReportMaxHeight}) and builds an A4 PDF with two snapshots per page.");
    }

    public static string Title => "Export maintenance report";

    /// <summary>Set by the window: its save file dialog.</summary>
    public IReportSavePicker? SavePicker { get; set; }

    /// <summary>Raised when the dialog should close (saved path, or null when cancelled).</summary>
    public event EventHandler<string?>? CloseRequested;

    public string Summary { get; }

    public int ItemCount => _items.Count;

    [ObservableProperty]
    public partial string Site { get; set; }

    [ObservableProperty]
    public partial string Technician { get; set; }

    /// <summary>Report date as typed (yyyy-MM-dd), today by default.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Date), nameof(SuggestedFileName))]
    public partial string DateText { get; set; }

    /// <summary>The parsed report date; null while <see cref="DateText"/> is not a valid yyyy-MM-dd date.</summary>
    public DateOnly? Date => DateOnly.TryParseExact(DateText?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

    /// <summary>The date error (shown below the field once edited).</summary>
    public string? DateError => Validation.ErrorOf(nameof(DateText));

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    [NotifyPropertyChangedFor(nameof(IsEditable))]
    public partial bool IsBusy { get; private set; }

    public bool IsEditable => !IsBusy;

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

    /// <summary>The report was saved, but some snapshots failed (the dialog stays open to say so).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWarning))]
    public partial string? WarningMessage { get; private set; }

    public bool HasWarning => !string.IsNullOrEmpty(WarningMessage);

    /// <summary>"Cancel", or "Close" once a report was saved.</summary>
    [ObservableProperty]
    public partial string CancelText { get; private set; } = "Cancel";

    /// <summary>Path of the saved report once done.</summary>
    [ObservableProperty]
    public partial string? SavedPath { get; private set; }

    /// <summary>The site error (shown below the field once edited).</summary>
    public string? SiteError => Validation.ErrorOf(nameof(Site));

    /// <summary>Why Export is disabled (tooltip).</summary>
    public string? ExportBlockedReason => IsBusy ? null : FormError ?? (_items.Count == 0 ? "Select at least one snapshot." : null);

    protected override void OnValidationChanged()
    {
        OnPropertyChanged(nameof(SiteError));
        OnPropertyChanged(nameof(DateError));
        OnPropertyChanged(nameof(ExportBlockedReason));
        ExportCommand.NotifyCanExecuteChanged();
    }

    /// <summary>"Maintenance report - Site - 2026-10-07.pdf" without characters file systems refuse.</summary>
    public string SuggestedFileName
    {
        get
        {
            var date = (Date ?? DateOnly.FromDateTime(DateTime.Today)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var site = new string([.. (Site ?? string.Empty).Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' ? '_' : c)]);
            return string.IsNullOrEmpty(site) ? $"Maintenance report - {date}.pdf" : $"Maintenance report - {site} - {date}.pdf";
        }
    }

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportAsync()
    {
        ErrorMessage = null;
        WarningMessage = null;
        var picker = SavePicker ?? throw new InvalidOperationException("No save dialog attached.");
        var target = await picker.PickAsync(SuggestedFileName, _settings.LastFolder).ConfigureAwait(true);
        if (target is null)
        {
            return;
        }

        _settings.Site = Site.Trim();
        _settings.Technician = (Technician ?? string.Empty).Trim();
        _settings.LastFolder = target.Folder ?? _settings.LastFolder;
        _store.Save(_settings);

        var cts = new CancellationTokenSource();
        _cts = cts;
        IsBusy = true;
        IsProgressVisible = true;
        Progress = 0;
        ProgressText = "Starting...";
        var saved = false;
        try
        {
            using (target)
            {
                await GenerateAsync(target.Stream, cts.Token).ConfigureAwait(true);
            }

            saved = true;
            SavedPath = target.DisplayPath;
            ProgressText = "Saved " + target.DisplayPath;
            Progress = 100;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            ProgressText = "Cancelled.";
        }
        catch (Exception ex)
        {
            ErrorMessage = "The report could not be created: " + SnapshotReportViewModel.ErrorText(ex);
            IsProgressVisible = false;
        }
        finally
        {
            IsBusy = false;
            _cts = null;
            cts.Dispose();
            await DeleteJobAsync().ConfigureAwait(true);
        }

        if (saved && !HasWarning)
        {
            CloseRequested?.Invoke(this, SavedPath);
        }
        else if (saved)
        {
            CancelText = "Close";
        }
    }

    private bool CanExport() => !IsBusy && Validation.IsValid && _items.Count > 0;

    [RelayCommand]
    private async Task CancelAsync()
    {
        if (IsBusy)
        {
            _cts?.Cancel();
            return;
        }

        await DeleteJobAsync().ConfigureAwait(true);
        CloseRequested?.Invoke(this, SavedPath);
    }

    partial void OnSiteChanged(string value) => OnPropertyChanged(nameof(SuggestedFileName));


    private async Task GenerateAsync(Stream output, CancellationToken ct)
    {
        var request = new ReportRequest
        {
            Site = Site.Trim(),
            Technician = (Technician ?? string.Empty).Trim(),
            Date = Date ?? DateOnly.FromDateTime(DateTime.Today),
            Items = [.. _items],
        };
        var status = SnapshotReportJson.Deserialize<ReportJobStatus>(
            await _ctx.InvokeAsync(SnapshotReportMethods.GenerateReport, SnapshotReportJson.Serialize(request), ct).ConfigureAwait(true));
        _jobId = status.JobId;
        while (status.State == ReportJobStates.Running)
        {
            ShowStatus(status);
            await Task.Delay(PollInterval, ct).ConfigureAwait(true);
            status = SnapshotReportJson.Deserialize<ReportJobStatus>(
                await _ctx.InvokeAsync(SnapshotReportMethods.ReportStatus, SnapshotReportJson.Serialize(new ReportJobRequest { JobId = status.JobId }), ct).ConfigureAwait(true));
        }

        if (status.State != ReportJobStates.Done)
        {
            throw new InvalidOperationException(status.Error ?? "The server could not build the report.");
        }

        long offset = 0;
        while (true)
        {
            ProgressText = string.Create(CultureInfo.InvariantCulture, $"Download report {offset * 100 / Math.Max(1, status.Size)} %");
            var chunk = SnapshotReportJson.Deserialize<ReportChunk>(
                await _ctx.InvokeAsync(SnapshotReportMethods.ReadReport, SnapshotReportJson.Serialize(new ReadReportRequest { JobId = status.JobId, Offset = offset }), ct).ConfigureAwait(true));
            var data = Convert.FromBase64String(chunk.DataBase64);
            await output.WriteAsync(data, ct).ConfigureAwait(true);
            offset += data.Length;
            if (chunk.Eof || data.Length == 0)
            {
                break;
            }
        }

        await output.FlushAsync(ct).ConfigureAwait(true);
        if (status.Failed > 0)
        {
            WarningMessage = string.Create(CultureInfo.InvariantCulture, $"{status.Failed} of {status.Total} snapshots failed; the report shows their errors.");
        }
    }

    private void ShowStatus(ReportJobStatus status)
    {
        // Snapshots are 90 % of the work, the PDF the rest.
        Progress = status.Total == 0 ? 0 : 90.0 * status.Done / status.Total;
        ProgressText = status.Message ?? "Working...";
    }

    private async Task DeleteJobAsync()
    {
        if (_jobId is not { } id)
        {
            return;
        }

        _jobId = null;
        try
        {
            await _ctx.InvokeAsync(SnapshotReportMethods.DeleteReport, SnapshotReportJson.Serialize(new ReportJobRequest { JobId = id }), CancellationToken.None).ConfigureAwait(true);
        }
#pragma warning disable CA1031 // Cleanup only; the server drops old reports by itself.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }
}
