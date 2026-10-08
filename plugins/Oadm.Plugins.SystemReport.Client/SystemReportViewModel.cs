using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Collections;
using Oadm.Sdk.Devices;

namespace Oadm.Plugins.SystemReport.Client;

/// <summary>Where the bundle goes (the file the user chose). Dispose closes it.</summary>
public sealed record ReportTarget(Stream Stream, string DisplayPath) : IDisposable
{
    public void Dispose() => Stream.Dispose();
}

/// <summary>One device of the dialog: address, MAC, model and its state chip, updated in place.</summary>
public sealed partial class DeviceReportRow : ObservableObject
{
    public DeviceReportRow(Guid deviceId, string address, string serial, string? model)
    {
        DeviceId = deviceId;
        Address = address;
        Serial = serial;
        Model = model ?? string.Empty;
    }

    public Guid DeviceId { get; }
    public string Address { get; }
    public string Serial { get; }
    public string Model { get; }

    [ObservableProperty] public partial string StateText { get; private set; } = "Waiting";
    [ObservableProperty] public partial string? Detail { get; private set; }
    [ObservableProperty] public partial bool IsOk { get; private set; }
    [ObservableProperty] public partial bool IsError { get; private set; }
    [ObservableProperty] public partial bool IsAccent { get; private set; }

    /// <summary>Applies the server's state of the device.</summary>
    public void Apply(DeviceReportStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        (StateText, Detail, IsOk, IsError, IsAccent) = status.State switch
        {
            DeviceReportStates.Downloading => ("Downloading", (string?)null, false, false, true),
            DeviceReportStates.Done => ("Done", FileSizeText.Format(status.Size), true, false, false),
            DeviceReportStates.Failed => ("Failed", status.Error, false, true, false),
            _ => ("Waiting", null, false, false, false),
        };
    }

    /// <summary>The job ended before this device was reached (cancelled or failed).</summary>
    public void MarkNotRun()
    {
        if (!IsOk && !IsError)
        {
            (StateText, Detail, IsOk, IsError, IsAccent) = ("Not downloaded", null, false, false, false);
        }
    }
}

/// <summary>
/// The "System report" progress dialog: starts the server job for the selected devices, shows each device's state
/// (virtualized list, rows updated in place from the status deltas), then downloads the bundled ZIP in chunks into the
/// chosen file. Cancel stops the job on the server and deletes its files.
/// </summary>
public sealed partial class SystemReportViewModel : ObservableObject
{
    public const string Title = "System report";

    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(500);

    private readonly Func<string, string?, CancellationToken, Task<string?>> _invoke;
    private readonly Dictionary<Guid, DeviceReportRow> _rows;
    private CancellationTokenSource? _cts;
    private string? _jobId;
    private (int Reports, int Failed) _finished;

    /// <param name="invoke">The plugin's server part (<c>IToolbarContext.InvokePluginAsync</c> bound to the plugin id).</param>
    public SystemReportViewModel(Func<string, string?, CancellationToken, Task<string?>> invoke, IReadOnlyList<IDeviceInfo> devices, TimeSpan? pollInterval = null)
    {
        ArgumentNullException.ThrowIfNull(invoke);
        ArgumentNullException.ThrowIfNull(devices);
        _invoke = invoke;
        PollInterval = pollInterval ?? DefaultPollInterval;
        var rows = new List<DeviceReportRow>(devices.Count);
        _rows = new Dictionary<Guid, DeviceReportRow>(devices.Count);
        foreach (var device in devices)
        {
            if (_rows.TryAdd(device.Id, new DeviceReportRow(device.Id, device.Address, device.Serial, device.Model)))
            {
                rows.Add(_rows[device.Id]);
            }
        }

        Rows.ReplaceAll(rows);
        Summary = Count(rows.Count, "device");
    }

    public TimeSpan PollInterval { get; }

    /// <summary>One row per selected device, in selection order.</summary>
    public RangeObservableCollection<DeviceReportRow> Rows { get; } = new();

    [ObservableProperty] public partial string Summary { get; private set; }
    [ObservableProperty] public partial double Progress { get; private set; }
    [ObservableProperty] public partial string ProgressText { get; private set; } = "Starting";
    [ObservableProperty] public partial bool IsBusy { get; private set; }
    [ObservableProperty] public partial string CancelText { get; private set; } = "Cancel";
    [ObservableProperty] public partial string? ErrorMessage { get; private set; }

    /// <summary>The finished file: "Saved ..." with the counts.</summary>
    [ObservableProperty] public partial string? ResultText { get; private set; }

    [ObservableProperty] public partial bool HasFailures { get; private set; }

    public bool HasError => ErrorMessage is not null;

    public bool HasResult => ResultText is not null;

    /// <summary>The dialog wants to close.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Runs the job and writes the bundle into <paramref name="target"/>. Never throws; errors show in the dialog.</summary>
    public async Task RunAsync(ReportTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var cts = new CancellationTokenSource();
        _cts = cts;
        IsBusy = true;
        ErrorMessage = null;
        var completed = false;
        try
        {
            using (target)
            {
                await DownloadAsync(target.Stream, cts.Token).ConfigureAwait(true);
            }

            completed = true;
            ShowResult(target.DisplayPath);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            ProgressText = "Cancelled. The file is incomplete; delete it.";
        }
        catch (Exception ex)
        {
            ErrorMessage = "The system reports could not be downloaded: " + ErrorText(ex);
            ProgressText = "Stopped.";
        }
        finally
        {
            IsBusy = false;
            _cts = null;
            cts.Dispose();
            CancelText = "Close";
            if (!completed)
            {
                foreach (var row in Rows)
                {
                    row.MarkNotRun();
                }
            }

            await DeleteJobAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        if (IsBusy)
        {
            _cts?.Cancel();
            return;
        }

        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    partial void OnErrorMessageChanged(string? value) => OnPropertyChanged(nameof(HasError));

    partial void OnResultTextChanged(string? value) => OnPropertyChanged(nameof(HasResult));

    private async Task DownloadAsync(Stream output, CancellationToken ct)
    {
        var status = SystemReportJson.Deserialize<JobStatus>(await _invoke(
            SystemReportMethods.Start,
            SystemReportJson.Serialize(new StartRequest { DeviceIds = [.. Rows.Select(r => r.DeviceId)] }),
            ct).ConfigureAwait(true));
        _jobId = status.JobId;
        Apply(status);
        while (status.State is JobStates.Running or JobStates.Packing)
        {
            await Task.Delay(PollInterval, ct).ConfigureAwait(true);
            status = SystemReportJson.Deserialize<JobStatus>(await _invoke(
                SystemReportMethods.Status,
                SystemReportJson.Serialize(new StatusRequest { JobId = status.JobId, SinceVersion = status.Version }),
                ct).ConfigureAwait(true));
            Apply(status);
        }

        if (status.State != JobStates.Done)
        {
            throw new InvalidOperationException(status.Error ?? "The server could not build the system reports.");
        }

        long offset = 0;
        while (true)
        {
            ProgressText = string.Create(CultureInfo.InvariantCulture, $"Saving the file {offset * 100 / Math.Max(1, status.Size)} %");
            var chunk = SystemReportJson.Deserialize<ReportChunk>(await _invoke(
                SystemReportMethods.Read,
                SystemReportJson.Serialize(new ReadRequest { JobId = status.JobId, Offset = offset }),
                ct).ConfigureAwait(true));
            var data = Convert.FromBase64String(chunk.DataBase64);
            await output.WriteAsync(data, ct).ConfigureAwait(true);
            offset += data.Length;
            if (chunk.Eof || data.Length == 0)
            {
                break;
            }
        }

        await output.FlushAsync(ct).ConfigureAwait(true);
        _finished = (status.Total - status.Failed, status.Failed);
    }

    /// <summary>Applies a job status: O(changed devices), rows are looked up by id and updated in place.</summary>
    public void Apply(JobStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        foreach (var device in status.Devices)
        {
            if (_rows.TryGetValue(device.DeviceId, out var row))
            {
                row.Apply(device);
            }
        }

        Progress = status.Total == 0 ? 0 : 95.0 * status.Finished / status.Total;
        ProgressText = status.State == JobStates.Packing
            ? "Putting the reports into one file"
            : string.Create(CultureInfo.InvariantCulture, $"Downloading system reports {Math.Min(status.Finished + 1, status.Total)} of {status.Total}");
        HasFailures = status.Failed > 0;
        Summary = status.Failed > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{Count(status.Total, "device")} · {status.Finished - status.Failed} done · {status.Failed} failed")
            : string.Create(CultureInfo.InvariantCulture, $"{Count(status.Total, "device")} · {status.Finished} done");
    }

    private void ShowResult(string path)
    {
        Progress = 100;
        var (reports, failed) = _finished;
        ProgressText = "Saved " + path;
        ResultText = failed == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{Count(reports, "system report")} saved.")
            : string.Create(CultureInfo.InvariantCulture, $"{Count(reports, "system report")} saved, {failed} failed. summary.txt in the file lists the failures.");
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
            await _invoke(SystemReportMethods.Delete, SystemReportJson.Serialize(new JobRequest { JobId = id }), CancellationToken.None).ConfigureAwait(true);
        }
#pragma warning disable CA1031 // Cleanup only; the server drops old jobs by itself.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    private static string Count(int n, string noun) =>
        string.Create(CultureInfo.InvariantCulture, $"{n:N0} {noun}{(n == 1 ? string.Empty : "s")}");

    /// <summary>The message of an error; for gRPC errors the status detail (the server's text).</summary>
    public static string ErrorText(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        var detail = ex.GetType().GetProperty("Status")?.GetValue(ex) is { } status
            ? status.GetType().GetProperty("Detail")?.GetValue(status) as string
            : null;
        return string.IsNullOrWhiteSpace(detail) ? ex.Message : detail;
    }
}
