using System.Globalization;
using System.IO.Compression;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Grpc.Core;

using Oadm.Client.Api;
using Oadm.Client.Dialogs;
using Oadm.Contracts.Logging;
using Oadm.Sdk.Client;

namespace Oadm.Client.Support;

/// <summary>
/// The Settings page's card "Support" (administrators): Create support report saves one ZIP on this computer with the
/// server's report (its logs and summary.txt) and this client's log files under <c>client-logs/</c>. The server's ZIP is
/// downloaded to a temporary file first, then both are written into the chosen file; nothing large is held in memory.
/// </summary>
public sealed partial class SupportViewModel : ObservableObject
{
    public static readonly FileType ZipFileType = new("ZIP file", "zip", "application/zip");

    private readonly IOadmApi _api;
    private readonly IDialogService _dialogs;
    private readonly string _clientLogFolder;

    /// <param name="clientLogFolder">This client's log folder (<c>&lt;client data folder&gt;/logs</c>).</param>
    public SupportViewModel(IOadmApi api, IDialogService dialogs, string clientLogFolder)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _clientLogFolder = clientLogFolder ?? throw new ArgumentNullException(nameof(clientLogFolder));
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand))]
    [NotifyPropertyChangedFor(nameof(ShowStatus))]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    public partial double Progress { get; private set; }

    /// <summary>What runs now ("Making the support report"), or the outcome ("Saved oadm-support-....zip.").</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowStatus))]
    public partial string? Status { get; private set; }

    [ObservableProperty]
    public partial bool StatusIsError { get; private set; }

    /// <summary>The outcome next to the button (not while the progress row shows).</summary>
    public bool ShowStatus => !IsBusy && !string.IsNullOrEmpty(Status);

    private bool CanCreate() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private async Task CreateAsync()
    {
        var suggested = $"oadm-support-{DateTime.Now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture)}.zip";
        var file = await _dialogs.CreateFileAsync("Save support report", suggested, ZipFileType).ConfigureAwait(true);
        if (file is null)
        {
            return;
        }

        await SaveAsync(file).ConfigureAwait(true);
    }

    /// <summary>Makes the report into <paramref name="file"/>; a failure removes the cut file (tests call it directly).</summary>
    public async Task SaveAsync(SavedFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        IsBusy = true;
        Progress = 0;
        Show("Making the support report");
        var temp = Path.Combine(Path.GetTempPath(), "oadm-support-" + Guid.NewGuid().ToString("N") + ".zip");
        var saved = false;
        try
        {
            var download = await _api.CreateSupportReportAsync(CancellationToken.None).ConfigureAwait(true);
            await using (download.ConfigureAwait(true))
            {
                var target = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, useAsync: true);
                await using (target.ConfigureAwait(true))
                {
                    var buffer = new byte[256 * 1024];
                    long copied = 0;
                    int read;
                    while ((read = await download.Content.ReadAsync(buffer).ConfigureAwait(true)) > 0)
                    {
                        await target.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(true);
                        copied += read;
                        if (download.Length is > 0 and var total)
                        {
                            Progress = Math.Min(90, 90.0 * copied / total);
                        }
                    }
                }
            }

            Show("Adding the logs of this computer");
            // Off the UI thread: the logs can be large.
            await Task.Run(() => CombineAsync(temp, file.Content)).ConfigureAwait(true);
            Progress = 100;
            saved = true;
            Show($"Saved {file.Name}.");
        }
        catch (Exception ex) when (ex is RpcException or OperationCanceledException or IOException or HttpRequestException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
        {
            Show(ServerErrors.Message(ex), error: true);
        }
        finally
        {
            IsBusy = false;
            File.Delete(temp);
            if (saved || file.Delete is null)
            {
                await file.Content.DisposeAsync().ConfigureAwait(true);
            }
            else
            {
                try
                {
                    await file.Delete().ConfigureAwait(true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // The message already says the save failed.
                }
            }
        }
    }

    /// <summary>Every entry of the server's report, then this client's logs under <c>client-logs/</c>.</summary>
    private async Task CombineAsync(string serverZip, Stream target)
    {
        var source = new FileStream(serverZip, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        await using (source.ConfigureAwait(false))
        {
            using var server = new ZipArchive(source, ZipArchiveMode.Read);
            using var combined = new ZipArchive(target, ZipArchiveMode.Create, leaveOpen: true);
            foreach (var entry in server.Entries)
            {
                var copy = combined.CreateEntry(entry.FullName, CompressionLevel.Optimal);
                copy.LastWriteTime = entry.LastWriteTime;
                var from = entry.Open();
                await using (from.ConfigureAwait(false))
                {
                    var to = copy.Open();
                    await using (to.ConfigureAwait(false))
                    {
                        await from.CopyToAsync(to).ConfigureAwait(false);
                    }
                }
            }

            await LogArchive.AddAsync(combined, _clientLogFolder, "client-*.log", "client-logs", LogArchive.MaxBytes, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private void Show(string? text, bool error = false)
    {
        Status = text;
        StatusIsError = error;
    }
}
