using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Grpc.Core;

using Oadm.Client.Dialogs;
using Oadm.Contracts.Logging;
using Oadm.Contracts.V1;
using Oadm.Sdk.Client;
using Oadm.Sdk.Plugins;

namespace Oadm.Client.Logging;

/// <summary>
/// What an export of a tab would hold: <see cref="Any"/> entry at all; <see cref="OverLimit"/>: more than
/// <see cref="LogExportLimits.MaxEntries"/>, of <see cref="Total"/> entries (null when the log has no total: the server log).
/// </summary>
public readonly record struct LogExportCount(bool Any, bool OverLimit, int? Total)
{
    /// <summary>The count of a log that has a total.</summary>
    public static LogExportCount Of(int total) => new(total > 0, total > LogExportLimits.MaxEntries, total);
}

/// <summary>
/// Export of one tab of the Logs page (docs/spec/main-window.md "Logs page"): what the tab shows (its range, search and level
/// filter) as a CSV or PDF file, the newest <see cref="LogExportLimits.MaxEntries"/> entries. It counts first; more than that
/// asks "Export 5,000 entries" in a message window. Then the save dialog, then the server writes the file (LogExportService)
/// into it with the progress row and Cancel. The permission "Export logs" decides whether the button shows.
/// </summary>
public sealed partial class LogExportViewModel : ObservableObject
{
    public const string ExportText = "Export 5,000 entries";

    internal static readonly FileType CsvFile = new("CSV file", "csv", "text/csv");
    internal static readonly FileType PdfFile = new("PDF file", "pdf", "application/pdf");

    private readonly string _log;
    private readonly string _title;
    private readonly IDialogService _dialogs;
    private readonly TimeProvider _time;
    private readonly Func<CancellationToken, Task<LogExportCount>> _count;
    private readonly Func<LogExportOptions, CancellationToken, Task<PluginDownload>> _open;
    private readonly Func<string> _rangeName;
    private CancellationTokenSource? _work;

    /// <param name="log">The log's part of the file name: <see cref="LogExportLimits.ServerLog"/>, ...</param>
    /// <param name="title">"Export server log": the title of the windows.</param>
    /// <param name="count">How many entries the tab's filter holds (cheap: a count, or a read past 5,000).</param>
    /// <param name="open">Starts the file of the tab's filter on the server.</param>
    /// <param name="rangeName">The range as the tab's button names it ("Last 24 hours"); empty for All and Custom.</param>
    public LogExportViewModel(string log, string title, IDialogService dialogs, TimeProvider time,
        Func<CancellationToken, Task<LogExportCount>> count, Func<LogExportOptions, CancellationToken, Task<PluginDownload>> open, Func<string> rangeName)
    {
        _log = log;
        _title = title;
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _count = count ?? throw new ArgumentNullException(nameof(count));
        _open = open ?? throw new ArgumentNullException(nameof(open));
        _rangeName = rangeName ?? throw new ArgumentNullException(nameof(rangeName));
    }

    /// <summary>The file is being made: the Export button is off, the progress row and Cancel show.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CsvCommand), nameof(PdfCommand), nameof(CancelCommand))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    public partial bool IsBusy { get; private set; }

    public bool IsIdle => !IsBusy;

    /// <summary>Percent of the file received (0 until its length is known).</summary>
    [ObservableProperty]
    public partial double Progress { get; private set; }

    /// <summary>"Making the PDF file".</summary>
    [ObservableProperty]
    public partial string ProgressText { get; private set; } = string.Empty;

    /// <summary>The text of the over-5,000 window: "The export holds the newest 5,000 of 12,345 entries. ...".</summary>
    public static string LimitMessage(LogExportCount count) => count.Total is { } total
        ? string.Create(CultureInfo.CurrentCulture, $"The export holds the newest {LogExportLimits.MaxEntries:N0} of {total:N0} entries. Narrow the dates or the search to get the rest.")
        : string.Create(CultureInfo.CurrentCulture, $"The export holds the newest {LogExportLimits.MaxEntries:N0} of more than {LogExportLimits.MaxEntries:N0} entries. Narrow the dates or the search to get the rest.");

    private bool CanExport() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanExport))]
    private Task CsvAsync() => ExportAsync(pdf: false);

    [RelayCommand(CanExecute = nameof(CanExport))]
    private Task PdfAsync() => ExportAsync(pdf: true);

    private bool CanCancel() => IsBusy;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _work?.Cancel();

    /// <summary>Counts, asks when there are more than 5,000, asks for the file, then writes it.</summary>
    public async Task ExportAsync(bool pdf)
    {
        if (IsBusy)
        {
            return;
        }

        using var work = new CancellationTokenSource();
        _work = work;
        IsBusy = true;
        Progress = 0;
        ProgressText = "Counting the entries";
        SavedFile? file = null;
        var saved = false;
        try
        {
            var count = await _count(work.Token).ConfigureAwait(true);
            if (!count.Any)
            {
                await _dialogs.ShowMessageAsync(_title, "There are no entries to export.").ConfigureAwait(true);
                return;
            }

            if (count.OverLimit)
            {
                if (!await _dialogs.ConfirmAsync(_title, LimitMessage(count), ExportText, "Cancel").ConfigureAwait(true))
                {
                    return;
                }
            }

            var now = _time.GetLocalNow();
            file = await _dialogs.CreateFileAsync(_title, LogExportLimits.FileName(_log, pdf, now), pdf ? PdfFile : CsvFile).ConfigureAwait(true);
            if (file is null || work.IsCancellationRequested)
            {
                return;
            }

            ProgressText = pdf ? "Making the PDF file" : "Making the CSV file";
            var options = new LogExportOptions
            {
                Format = pdf ? LogExportFormat.Pdf : LogExportFormat.Csv,
                TimeZone = _time.LocalTimeZone.Id,
                UtcOffsetMinutes = (int)now.Offset.TotalMinutes,
                RangeName = _rangeName(),
            };
            var download = await _open(options, work.Token).ConfigureAwait(true);
            await using (download.ConfigureAwait(true))
            {
                var buffer = new byte[256 * 1024];
                long copied = 0;
                int read;
                while ((read = await download.Content.ReadAsync(buffer, work.Token).ConfigureAwait(true)) > 0)
                {
                    await file.Content.WriteAsync(buffer.AsMemory(0, read), work.Token).ConfigureAwait(true);
                    copied += read;
                    if (download.Length is > 0 and var total)
                    {
                        Progress = Math.Min(100, 100.0 * copied / total);
                    }
                }
            }

            await file.Content.FlushAsync(work.Token).ConfigureAwait(true);
            saved = true;
        }
        catch (OperationCanceledException)
        {
            // Cancel: the cut file is removed below.
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled && work.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is RpcException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            await _dialogs.ShowMessageAsync(_title, "The file could not be made: " + ServerErrors.Message(ex)).ConfigureAwait(true);
        }
        finally
        {
            if (file is not null)
            {
                await CloseAsync(file, saved).ConfigureAwait(true);
            }

            _work = null;
            IsBusy = false;
            ProgressText = string.Empty;
        }
    }

    private static async Task CloseAsync(SavedFile file, bool saved)
    {
        await file.Content.DisposeAsync().ConfigureAwait(true);
        if (saved || file.Delete is null)
        {
            return;
        }

        try
        {
            await file.Delete().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The file stays cut; the user cancelled or saw the error.
        }
    }
}
