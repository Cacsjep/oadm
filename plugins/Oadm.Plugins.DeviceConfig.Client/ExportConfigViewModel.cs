using System.Globalization;
using System.IO.Compression;
using System.Text;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Sdk.Client.Collections;
using Oadm.Sdk.Devices;

namespace Oadm.Plugins.DeviceConfig.Client;

/// <summary>Where the export goes: the chosen file, opened only when there is something to write.</summary>
public sealed record ExportTarget(Func<Task<Stream>> OpenAsync, string DisplayPath);

/// <summary>One device of the export window: address, MAC, model and its state chip, updated in place.</summary>
public sealed partial class ExportDeviceRow : ObservableObject
{
    public ExportDeviceRow(IDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        DeviceId = device.Id;
        Address = device.Address;
        Serial = device.Serial;
        Model = device.Model ?? string.Empty;
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

    public bool IsFinished => IsOk || IsError;

    public void Exporting() => (StateText, Detail, IsAccent) = ("Exporting", null, true);

    public void Done(long bytes) => (StateText, Detail, IsOk, IsError, IsAccent) = ("Done", FileSizeText(bytes), true, false, false);

    public void Failed(string reason) => (StateText, Detail, IsOk, IsError, IsAccent) = ("Failed", reason, false, true, false);

    /// <summary>The export ended before this device was reached.</summary>
    public void NotRun()
    {
        if (!IsFinished)
        {
            (StateText, Detail, IsAccent) = ("Not exported", null, false);
        }
    }

    internal static string FileSizeText(long bytes) => bytes < 1024 * 1024
        ? string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0.#} KB")
        : string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024):0.#} MB");
}

/// <summary>
/// "Export configuration": reads the configuration of every selected device through the plugin's read-only query (at most
/// <see cref="DeviceConfigIds.ExportParallelism"/> at a time) and saves it: one device = its <c>.json</c>, several devices =
/// one ZIP with a file per device (named with address, MAC and model, which the ZIP import matches on) plus
/// <c>summary.txt</c> when some failed. Answers are written as they arrive; nothing is held for all devices at once.
/// </summary>
public sealed partial class ExportConfigViewModel : ObservableObject
{
    public const string Title = DeviceConfigIds.ExportName;

    public const string SummaryFileName = "summary.txt";

    private readonly Func<Guid, CancellationToken, Task<string?>> _query;
    private readonly Func<DateTime> _now;
    private CancellationTokenSource? _cts;

    /// <param name="query">The export query of one device (<c>ITaskDialogContext.QueryAsync</c> with <see cref="DeviceConfigIds.ExportQuery"/>).</param>
    public ExportConfigViewModel(Func<Guid, CancellationToken, Task<string?>> query, IReadOnlyList<IDeviceInfo> devices, Func<DateTime>? now = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(devices);
        _query = query;
        _now = now ?? (() => DateTime.Now);
        var seen = new HashSet<Guid>();
        Devices = [.. devices.Where(d => seen.Add(d.Id))];
        Rows.ReplaceAll(Devices.Select(d => new ExportDeviceRow(d)));
        Summary = DeviceConfigIds.Count(Rows.Count, "device");
    }

    /// <summary>The selected devices, each once.</summary>
    public IReadOnlyList<IDeviceInfo> Devices { get; }

    public RangeObservableCollection<ExportDeviceRow> Rows { get; } = new();

    /// <summary>One device: a single file; several: a ZIP.</summary>
    public bool IsZip => Rows.Count != 1;

    /// <summary>Name the save dialog suggests.</summary>
    public string SuggestedFileName => IsZip
        ? ConfigFileNames.ForZip(_now())
        : ConfigFileNames.ForDevice(Devices[0].Address, Devices[0].Serial, Devices[0].Model);

    [ObservableProperty] public partial string Summary { get; private set; }
    [ObservableProperty] public partial double Progress { get; private set; }
    [ObservableProperty] public partial string ProgressText { get; private set; } = "Starting";
    [ObservableProperty] public partial bool IsBusy { get; private set; }
    [ObservableProperty] public partial string CancelText { get; private set; } = "Cancel";
    [ObservableProperty] public partial string? ErrorMessage { get; private set; }
    [ObservableProperty] public partial string? ResultText { get; private set; }
    [ObservableProperty] public partial bool HasFailures { get; private set; }

    public bool HasError => ErrorMessage is not null;

    public bool HasResult => ResultText is not null;

    public event EventHandler? CloseRequested;

    /// <summary>Exports every device into <paramref name="target"/>. Never throws; errors show in the window.</summary>
    public async Task RunAsync(ExportTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var cts = new CancellationTokenSource();
        _cts = cts;
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            if (IsZip)
            {
                await ExportZipAsync(target, cts.Token).ConfigureAwait(true);
            }
            else
            {
                await ExportOneAsync(target, cts.Token).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            ProgressText = IsZip ? "Cancelled. The file is incomplete; delete it." : "Cancelled.";
        }
#pragma warning disable CA1031 // Shown in the window.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ErrorMessage = "The configuration could not be saved: " + ErrorText(ex);
            ProgressText = "Stopped.";
        }
        finally
        {
            IsBusy = false;
            _cts = null;
            cts.Dispose();
            CancelText = "Close";
            foreach (var row in Rows)
            {
                row.NotRun();
            }
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

    private async Task ExportOneAsync(ExportTarget target, CancellationToken ct)
    {
        var row = Rows[0];
        ProgressText = "Exporting the configuration";
        var text = await ReadAsync(row, ct).ConfigureAwait(true);
        if (text is null)
        {
            ProgressText = "Nothing was saved.";
            HasFailures = true;
            ResultText = "The configuration could not be exported: " + row.Detail;
            return;
        }

        var stream = await target.OpenAsync().ConfigureAwait(true);
        await using (stream.ConfigureAwait(true))
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            await stream.WriteAsync(bytes, ct).ConfigureAwait(true);
            await stream.FlushAsync(ct).ConfigureAwait(true);
        }

        Progress = 100;
        ProgressText = "Saved " + target.DisplayPath;
        ResultText = "Configuration saved.";
        Summary = "1 device · 1 done";
    }

    private async Task ExportZipAsync(ExportTarget target, CancellationToken ct)
    {
        var stream = await target.OpenAsync().ConfigureAwait(true);
        await using (stream.ConfigureAwait(true))
        {
            var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var done = 0;
            var failed = 0;
            var gate = new SemaphoreSlim(DeviceConfigIds.ExportParallelism);
            var writeLock = new object();
            try
            {
                UpdateProgress(0, 0);

                async Task ExportAsync(ExportDeviceRow row)
                {
                    await gate.WaitAsync(ct).ConfigureAwait(true);
                    try
                    {
                        var text = await ReadAsync(row, ct).ConfigureAwait(true);
                        ct.ThrowIfCancellationRequested();

                        // One entry open at a time: written in one go under the lock (no await in between).
                        lock (writeLock)
                        {
                            if (text is not null)
                            {
                                WriteEntry(zip, UniqueName(names, ConfigFileNames.ForDevice(row.Address, row.Serial, row.Model)), text);
                                done++;
                            }
                            else
                            {
                                failed++;
                            }

                            UpdateProgress(done, failed);
                        }
                    }
                    finally
                    {
                        gate.Release();
                    }
                }

                await Task.WhenAll(Rows.ToList().Select(ExportAsync)).ConfigureAwait(true);
                if (failed > 0)
                {
                    WriteEntry(zip, SummaryFileName, SummaryText(done, failed));
                }
            }
            finally
            {
                gate.Dispose();
                zip.Dispose();
            }

            await stream.FlushAsync(ct).ConfigureAwait(true);
            Progress = 100;
            ProgressText = "Saved " + target.DisplayPath;
            ResultText = failed == 0
                ? DeviceConfigIds.Count(done, "configuration") + " saved."
                : $"{DeviceConfigIds.Count(done, "configuration")} saved, {failed} failed. {SummaryFileName} in the file lists the failures.";
        }
    }

    private static void WriteEntry(ZipArchive zip, string name, string text)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var content = entry.Open();
        content.Write(Encoding.UTF8.GetBytes(text));
    }

    /// <summary>The device's export, or null with the row marked Failed.</summary>
    private async Task<string?> ReadAsync(ExportDeviceRow row, CancellationToken ct)
    {
        row.Exporting();
        try
        {
            var reply = ExportReply.Parse(await _query(row.DeviceId, ct).ConfigureAwait(true));
            if (reply.Text is { Length: > 0 } text)
            {
                row.Done(Encoding.UTF8.GetByteCount(text));
                return text;
            }

            row.Failed(reply.Error ?? "The device sent no configuration.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // One failing device never stops the others.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            row.Failed(ErrorText(ex));
        }

        HasFailures = true;
        return null;
    }

    private void UpdateProgress(int done, int failed)
    {
        var total = Rows.Count;
        var finished = done + failed;
        Progress = total == 0 ? 0 : 95.0 * finished / total;
        ProgressText = string.Create(CultureInfo.InvariantCulture, $"Exporting configurations {Math.Min(finished + 1, total):N0} of {total:N0}");
        Summary = failed > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{DeviceConfigIds.Count(total, "device")} · {done:N0} done · {failed:N0} failed")
            : string.Create(CultureInfo.InvariantCulture, $"{DeviceConfigIds.Count(total, "device")} · {done:N0} done");
    }

    private string SummaryText(int done, int failed)
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"OADM configuration export, {_now():yyyy-MM-dd HH:mm}\r\n");
        text.Append(CultureInfo.InvariantCulture, $"{done} saved, {failed} failed\r\n\r\nFailed:\r\n");
        foreach (var row in Rows.Where(r => r.IsError))
        {
            text.Append(CultureInfo.InvariantCulture, $"{row.Address}  {row.Serial}  {row.Model}  {row.Detail}\r\n");
        }

        return text.ToString();
    }

    private static string UniqueName(HashSet<string> names, string name)
    {
        if (names.Add(name))
        {
            return name;
        }

        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        for (var i = 2; ; i++)
        {
            var candidate = string.Create(CultureInfo.InvariantCulture, $"{stem}-{i}{extension}");
            if (names.Add(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>The message of an error; for gRPC errors the status detail (the server's text).</summary>
    public static string ErrorText(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        var detail = ex.GetType().GetProperty("Status")?.GetValue(ex) is { } status
            ? status.GetType().GetProperty("Detail")?.GetValue(status) as string
            : null;
        if (!string.IsNullOrWhiteSpace(detail))
        {
            return detail;
        }

        return string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;
    }
}
