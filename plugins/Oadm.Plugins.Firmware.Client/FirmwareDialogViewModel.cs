using System.Collections.ObjectModel;
using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Sdk.Client;
using Oadm.Sdk.Devices;

namespace Oadm.Plugins.Firmware.Client;

/// <summary>Local file access for the dialog, replaceable in tests.</summary>
public interface IFirmwareFileSource
{
    /// <summary>Lets the user choose a .bin file; null when cancelled.</summary>
    Task<string?> PickAsync();

    /// <summary>Size of the file in bytes. The content is never inspected (see <see cref="FirmwareImageInspector"/>).</summary>
    Task<long> GetSizeAsync(string path, CancellationToken ct);
}

public sealed record FactoryDefaultModeOption(FactoryDefaultMode Mode, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One selected device in the dialog table.</summary>
public sealed partial class FirmwareDeviceRow(IDeviceInfo device) : ObservableObject
{
    public IDeviceInfo Device { get; } = device;

    public string Address => Device.Address;

    public string Model => Device.Model ?? string.Empty;

    [ObservableProperty]
    public partial string? CurrentVersion { get; set; } = device.FirmwareVersion;

    /// <summary>Rollback image / commit state from the fwmgr status query.</summary>
    [ObservableProperty]
    public partial string? FirmwareState { get; set; }

    /// <summary>False when the device does not offer fwmgr 1.x (from the status query).</summary>
    [ObservableProperty]
    public partial bool Supported { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VerdictText), nameof(Message), nameof(IsOk), nameof(IsWarn), nameof(IsError), nameof(WillInstall))]
    public partial FirmwareCheck? Check { get; set; }

    public string VerdictText => Check?.Verdict switch
    {
        null => "Choose a file",
        FirmwareVerdict.Upgrade => "Upgrade",
        FirmwareVerdict.DeviceValidates => "Device checks",
        FirmwareVerdict.AlreadyUpToDate => "Up to date",
        FirmwareVerdict.Downgrade => "Downgrade",
        FirmwareVerdict.DowngradeBlocked => "Downgrade blocked",
        FirmwareVerdict.WrongProduct => "Wrong product",
        FirmwareVerdict.InvalidFile => "Invalid file",
        FirmwareVerdict.UnknownVersionRefused => "Refused",
        _ => "Not supported",
    };

    public string Message => Check?.Message ?? string.Empty;

    public bool IsOk => Check?.Verdict is FirmwareVerdict.Upgrade;

    public bool IsWarn => Check?.Verdict is FirmwareVerdict.Downgrade or FirmwareVerdict.DeviceValidates;

    public bool IsError => Check is { WillInstall: false, IsNoOp: false };

    public bool WillInstall => Check?.WillInstall == true;

    /// <summary>The status query was started for this row (preloaded or because the grid showed it).</summary>
    internal bool StatusRequested { get; set; }
}

/// <summary>
/// "Upgrade firmware" dialog: pick a .bin file, preview per device what will happen, choose the
/// options, upload the file to the server and return the task payload.
/// </summary>
public sealed partial class FirmwareDialogViewModel : ObservableObject, IDisposable
{
    private readonly ITaskDialogContext _ctx;
    private readonly IFirmwareFileSource _files;
    private readonly CancellationTokenSource _lifetime = new();

    // Never disposed: status reads still in flight release it after the dialog closed (it has no wait handle).
    private readonly SemaphoreSlim _statusSlots = new(MaxParallelStatusQueries);
    private CancellationTokenSource? _uploadCts;
    private FirmwareImageInfo? _image;

    public FirmwareDialogViewModel(ITaskDialogContext ctx, IReadOnlyList<IDeviceInfo> devices, IFirmwareFileSource files)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(files);
        _ctx = ctx;
        _files = files;
        Devices = new ObservableCollection<FirmwareDeviceRow>(devices.Select(d => new FirmwareDeviceRow(d)));
        SelectedMode = Modes[0];
    }

    /// <summary>Raised with the payload JSON (or null when cancelled) when the dialog should close.</summary>
    public event EventHandler<string?>? CloseRequested;

    public ObservableCollection<FirmwareDeviceRow> Devices { get; }

    public IReadOnlyList<FactoryDefaultModeOption> Modes { get; } =
    [
        new(FactoryDefaultMode.None, "None, keep all settings (recommended)"),
        new(FactoryDefaultMode.Soft, "Soft, reset settings but keep network settings"),
        new(FactoryDefaultMode.Hard, "Hard, reset everything including the IP address"),
    ];

    public string Title { get; } = "Upgrade firmware";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    public partial string? FilePath { get; set; }

    [ObservableProperty]
    public partial string FileName { get; set; } = "No file selected";

    [ObservableProperty]
    public partial string? FileDetails { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FileRowError))]
    public partial string? FileError { get; set; }

    [ObservableProperty]
    public partial FactoryDefaultModeOption SelectedMode { get; set; }

    [ObservableProperty]
    public partial bool AllowDowngrade { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(BrowseCommand))]
    public partial bool IsUploading { get; set; }

    /// <summary>0-100.</summary>
    [ObservableProperty]
    public partial double UploadProgress { get; set; }

    [ObservableProperty]
    public partial string? UploadText { get; set; }

    /// <summary>Why the upload of the chosen file failed (shown at the file row, like a rejected file).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FileRowError))]
    public partial string? Error { get; set; }

    /// <summary>The error at the file row: the file is rejected, or its upload failed.</summary>
    public string? FileRowError => FileError ?? Error;

    /// <summary>Why Upgrade is disabled (its tooltip); null when it is enabled or an upload runs.</summary>
    public string? StartBlockedReason =>
        IsUploading ? null
        : _image is null || FilePath is null ? FileError ?? "Choose an AXIS OS .bin file."
        : _image.IsRejected ? FileError
        : !Devices.Any(d => d.WillInstall) ? "None of the selected devices can install this file (see Result)."
        : null;

    [ObservableProperty]
    public partial string Summary { get; set; } = "Choose an AXIS OS .bin file.";

    public string? ModeWarning => SelectedMode.Mode switch
    {
        FactoryDefaultMode.Soft => "Soft factory default: all settings, users and passwords are reset; the IP address and network settings are kept. OADM loses access until a new password is set.",
        FactoryDefaultMode.Hard => "Hard factory default: everything is reset, including the IP address. Devices with a static address fall back to DHCP or 192.168.0.90 and may become unreachable for OADM.",
        _ => null,
    };

    public bool HasModeWarning => ModeWarning is not null;

    /// <summary>Set after a successful start.</summary>
    public string? Result { get; private set; }

    /// <summary>Rows whose status is read when the dialog opens; the others are read when the grid shows them.</summary>
    public const int StatusPreloadCount = 50;

    /// <summary>Status queries in flight at most (each one reaches a device through the server).</summary>
    public const int MaxParallelStatusQueries = 4;

    /// <summary>
    /// Reads the current firmware state through the plugin's read-only "status" query, for the first
    /// <see cref="StatusPreloadCount"/> devices only (at most <see cref="MaxParallelStatusQueries"/> at a time): with
    /// thousands of selected devices the other rows are read lazily when the grid shows them
    /// (<see cref="EnsureStatusAsync"/>); until then they use the cached firmware version.
    /// </summary>
    public async Task LoadStatusAsync(CancellationToken ct)
    {
        var rows = new List<FirmwareDeviceRow>(Math.Min(StatusPreloadCount, Devices.Count));
        for (var i = 0; i < Devices.Count && rows.Count < StatusPreloadCount; i++)
        {
            if (!Devices[i].StatusRequested)
            {
                Devices[i].StatusRequested = true;
                rows.Add(Devices[i]);
            }
        }

        await Task.WhenAll(rows.Select(row => ReadStatusAsync(row, ct))).ConfigureAwait(true);
        Evaluate();
    }

    /// <summary>Reads the status of a row the grid shows, once; re-evaluates that row and the summary.</summary>
    public async Task EnsureStatusAsync(FirmwareDeviceRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.StatusRequested || _lifetime.IsCancellationRequested)
        {
            return;
        }

        row.StatusRequested = true;
        try
        {
            await ReadStatusAsync(row, _lifetime.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return; // dialog closed
        }

        EvaluateRow(row);
        UpdateSummary();
    }

    private async Task ReadStatusAsync(FirmwareDeviceRow row, CancellationToken ct)
    {
        await _statusSlots.WaitAsync(ct).ConfigureAwait(true);
        try
        {
            var json = await _ctx.QueryAsync(row.Device.Id, FirmwareTaskPluginIds.StatusQuery, null, ct).ConfigureAwait(true);
            if (FirmwareStatusInfo.FromJson(json) is not { } status)
            {
                return;
            }

            row.Supported = status.Supported;
            row.CurrentVersion = status.ActiveVersion ?? row.CurrentVersion;
            row.FirmwareState = DescribeState(status);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // A failed status read only means the table shows the cached version.
        catch (Exception)
#pragma warning restore CA1031
        {
            row.FirmwareState = "-"; // unknown: the Rollback column stays quiet
        }
        finally
        {
            _statusSlots.Release();
        }
    }

    public static string DescribeState(FirmwareStatusInfo status)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (!status.Supported)
        {
            return "Firmware management not supported";
        }

        if (status.IsCommitted == false || status.TimeToRollback is not null)
        {
            return status.TimeToRollback is { } s
                ? string.Create(CultureInfo.InvariantCulture, $"Not committed, rolls back to {status.InactiveVersion} in {s} s")
                : $"Not committed (previous {status.InactiveVersion})";
        }

        return status.InactiveVersion is { } inactive ? $"Rollback to {inactive} possible" : "No rollback image";
    }

    /// <summary>Uses a file without the picker (tests, drag and drop).</summary>
    public async Task SelectFileAsync(string path, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Error = null;
        try
        {
            var size = await _files.GetSizeAsync(path, ct).ConfigureAwait(true);
            _image = FirmwareImageInspector.Inspect(path, size);
            FilePath = path;
            FileName = _image.FileName;
            FileError = _image.RejectReason;
            FileDetails = FileSizeText.Format(size) + (_image.IsIdentified
                ? $"  ·  for {_image.Product}, AXIS OS {_image.Version}"
                : "  ·  product and version not recognized from the file name; each device checks the image itself");
        }
        catch (IOException ex)
        {
            _image = null;
            FilePath = null;
            FileName = FirmwareImageInspector.FileNameOf(path);
            FileDetails = null;
            FileError = "The file could not be read: " + ex.Message;
        }
        catch (UnauthorizedAccessException ex)
        {
            _image = null;
            FilePath = null;
            FileName = FirmwareImageInspector.FileNameOf(path);
            FileDetails = null;
            FileError = "The file could not be read: " + ex.Message;
        }

        Evaluate();
    }

    [RelayCommand(CanExecute = nameof(CanBrowse))]
    private async Task BrowseAsync()
    {
        var path = await _files.PickAsync().ConfigureAwait(true);
        if (!string.IsNullOrEmpty(path))
        {
            await SelectFileAsync(path, CancellationToken.None).ConfigureAwait(true);
        }
    }

    private bool CanBrowse() => !IsUploading;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        if (_image is null || FilePath is null)
        {
            return;
        }

        Error = null;
        IsUploading = true;
        UploadProgress = 0;
        UploadText = "Uploading firmware to the server";
        var uploadCts = new CancellationTokenSource();
        _uploadCts = uploadCts;
        try
        {
            var finished = false;
            var progress = new Progress<double>(p =>
            {
                if (finished)
                {
                    return;
                }

                // The SDK does not specify the unit: accept a fraction (0..1) or a percentage.
                var percent = p <= 1.0 ? p * 100 : p;
                UploadProgress = Math.Clamp(percent, 0, 100);
                UploadText = string.Create(CultureInfo.InvariantCulture, $"Uploading firmware to the server ({UploadProgress:0} %)");
            });
            var uploaded = await _ctx.UploadAsync(FilePath, progress, uploadCts.Token).ConfigureAwait(true);
            Result = new FirmwarePayload
            {
                FileId = uploaded.Id,
                FileName = _image.FileName,
                FactoryDefaultMode = SelectedMode.Mode,
                AllowDowngrade = AllowDowngrade,
                Direction = Direction(),
            }.ToJson();
            finished = true;
            UploadProgress = 100;
            CloseRequested?.Invoke(this, Result);
        }
        catch (OperationCanceledException)
        {
            UploadText = null;
            Error = "Upload cancelled.";
        }
#pragma warning disable CA1031 // Shown to the user; the dialog stays open so they can retry.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            UploadText = null;
            Error = "The firmware file could not be uploaded to the server: " + ex.Message;
        }
        finally
        {
            IsUploading = false;
            _uploadCts = null;
            uploadCts.Dispose();
        }
    }

    /// <summary>Upgrade or Downgrade when every device that will install agrees (names the task), else Unknown.</summary>
    public FirmwareDirection Direction()
    {
        var verdicts = Devices.Where(d => d.WillInstall).Select(d => d.Check!.Verdict).Distinct().ToList();
        return verdicts.Count != 1 ? FirmwareDirection.Unknown : verdicts[0] switch
        {
            FirmwareVerdict.Upgrade => FirmwareDirection.Upgrade,
            FirmwareVerdict.Downgrade => FirmwareDirection.Downgrade,
            _ => FirmwareDirection.Unknown,
        };
    }

    public bool CanStart() => !IsUploading && _image is { IsRejected: false } && FilePath is not null && Devices.Any(d => d.WillInstall);

    [RelayCommand]
    private void Cancel()
    {
        if (_uploadCts is { } cts)
        {
            cts.Cancel();
            return;
        }

        CloseRequested?.Invoke(this, null);
    }

    partial void OnSelectedModeChanged(FactoryDefaultModeOption value)
    {
        OnPropertyChanged(nameof(ModeWarning));
        OnPropertyChanged(nameof(HasModeWarning));
        Evaluate();
    }

    partial void OnAllowDowngradeChanged(bool value) => Evaluate();

    public void Dispose()
    {
        _uploadCts?.Cancel();
        _uploadCts?.Dispose();
        _uploadCts = null;
        if (!_lifetime.IsCancellationRequested)
        {
            _lifetime.Cancel();
        }

        _lifetime.Dispose();
    }

    /// <summary>One pass over all rows (thousands of devices: no nested loops, one summary).</summary>
    private void Evaluate()
    {
        foreach (var row in Devices)
        {
            EvaluateRow(row);
        }

        UpdateSummary();
    }

    private void EvaluateRow(FirmwareDeviceRow row)
    {
        if (_image is null)
        {
            row.Check = null;
        }
        else if (!row.Supported)
        {
            row.Check = new FirmwareCheck(FirmwareVerdict.InvalidFile, "This device's firmware cannot be updated from OADM.");
        }
        else
        {
            row.Check = FirmwareCompatibility.Evaluate(row.Model, row.CurrentVersion, _image, SelectedMode.Mode, AllowDowngrade);
        }
    }

    private void UpdateSummary()
    {
        if (_image is null)
        {
            Summary = "Choose an AXIS OS .bin file.";
        }
        else
        {
            int install = 0, upToDate = 0;
            foreach (var row in Devices)
            {
                if (row.WillInstall)
                {
                    install++;
                }
                else if (row.Check?.IsNoOp == true)
                {
                    upToDate++;
                }
            }

            var skipped = Devices.Count - install - upToDate;
            Summary = string.Create(CultureInfo.InvariantCulture, $"{install:N0} of {Devices.Count:N0} device(s) will be updated, {upToDate:N0} already up to date, {skipped:N0} not possible.");
        }

        StartCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(StartBlockedReason));
    }

    partial void OnIsUploadingChanged(bool value) => OnPropertyChanged(nameof(StartBlockedReason));
}

/// <summary>Ids shared with the server part (kept here so the client does not reference the server assembly).</summary>
public static class FirmwareTaskPluginIds
{
    public const string PluginId = "oadm.firmware";
    public const string StatusQuery = "status";
}
