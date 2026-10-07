using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Sdk.Client;
using Oadm.Sdk.Devices;

namespace Oadm.Plugins.Acap.Client;

/// <summary>Picks a local .eap file; implemented by the window with the platform storage provider.</summary>
public interface IEapFilePicker
{
    Task<string?> PickEapAsync();
}

public sealed record DeviceChoice(IDeviceInfo Device)
{
    public string Label => string.IsNullOrWhiteSpace(Device.Model) ? Device.Address : $"{Device.Model} - {Device.Address}";
}

/// <summary>One row of the installed applications table.</summary>
public sealed record ApplicationRow(InstalledApplication App)
{
    public string Name => App.DisplayName;

    public string PackageName => App.Name;

    public string Vendor => App.Vendor ?? string.Empty;

    public string Version => App.Version ?? string.Empty;

    public string Status => App.Status ?? "Unknown";

    public bool IsRunning => App.IsRunning;

    public string License => App.License switch
    {
        null or "" => string.Empty,
        "Valid" when App.LicenseExpirationDate is { Length: > 0 } d && d != "0" => $"Valid until {d}",
        var l => l,
    };

    public string Signature => App.SignatureStatus ?? string.Empty;
}

/// <summary>Per-device compatibility of the picked package.</summary>
public sealed partial class DeviceCompatibilityRow(DeviceChoice device) : ObservableObject
{
    public DeviceChoice Device { get; } = device;

    public string DeviceLabel => Device.Label;

    [ObservableProperty]
    public partial string Result { get; set; } = "Checking...";

    [ObservableProperty]
    public partial bool IsOk { get; set; }

    [ObservableProperty]
    public partial bool IsError { get; set; }
}

/// <summary>
/// Applications (ACAP) dialog: shows the installed applications of one selected device and builds
/// the task payload for install/upgrade, remove, start or stop on all selected devices. Reads
/// device state only through the plugin query; never talks to devices.
/// </summary>
public sealed partial class AcapDialogViewModel : ObservableObject
{
    private readonly ITaskDialogContext _ctx;
    private readonly IEapFilePicker _picker;
    private readonly Dictionary<Guid, Task<ListApplicationsResult>> _cache = [];
    private Action? _cancelUpload;
    private bool _initialized;

    public AcapDialogViewModel(ITaskDialogContext ctx, IReadOnlyList<IDeviceInfo> devices, IEapFilePicker picker)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(picker);
        if (devices.Count == 0)
        {
            throw new ArgumentException("At least one device is required.", nameof(devices));
        }

        _ctx = ctx;
        _picker = picker;
        Devices = devices.Select(d => new DeviceChoice(d)).ToList();
        SelectedDevice = Devices[0];
        _initialized = true;
    }

    /// <summary>Raised with the payload JSON, or null when the dialog was cancelled.</summary>
    public event EventHandler<string?>? CloseRequested;

    public IReadOnlyList<DeviceChoice> Devices { get; }

    public bool ShowDevicePicker => Devices.Count > 1;

    public string Title { get; } = "Applications (ACAP)";

    public string ScopeText => Devices.Count == 1
        ? $"Actions apply to {Devices[0].Label}."
        : $"Actions apply to all {Devices.Count} selected devices.";

    public ObservableCollection<ApplicationRow> Applications { get; } = [];

    public ObservableCollection<DeviceCompatibilityRow> Compatibility { get; } = [];

    /// <summary>The payload when the dialog completed, null otherwise.</summary>
    public string? Result { get; private set; }

    [ObservableProperty]
    public partial DeviceChoice SelectedDevice { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand), nameof(RemoveCommand))]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    public partial ApplicationRow? SelectedApplication { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError), nameof(IsEmpty))]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty]
    public partial bool ConfirmRemoveVisible { get; set; }

    [ObservableProperty]
    public partial string? ConfirmRemoveText { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPackage))]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    public partial EapManifest? Package { get; set; }

    [ObservableProperty]
    public partial string? PackagePath { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPackageError))]
    public partial string? PackageError { get; set; }

    [ObservableProperty]
    public partial bool AllowDowngrade { get; set; }

    [ObservableProperty]
    public partial bool StartAfterInstall { get; set; } = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand), nameof(PickPackageCommand), nameof(StartCommand), nameof(StopCommand), nameof(RemoveCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial double UploadProgress { get; set; }

    [ObservableProperty]
    public partial string? UploadStatus { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    public partial int CompatibleCount { get; set; }

    public bool HasSelection => SelectedApplication is not null;

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public bool IsEmpty => !IsLoading && !HasError && Applications.Count == 0;

    public bool HasPackage => Package is not null;

    public bool HasPackageError => !string.IsNullOrEmpty(PackageError);

    public string PackageTitle => Package is null ? string.Empty : $"{Package.DisplayName} {Package.Version}";

    public string PackageDetails => Package is null
        ? string.Empty
        : string.Join("  ·  ", new[]
        {
            $"Package {Package.AppName}",
            Package.Vendor is { Length: > 0 } v ? $"Vendor {v}" : null,
            $"Architecture {(AcapCompatibility.IsArchitectureIndependent(Package.Architecture) ? "any" : Package.Architecture)}",
            Package.CompatibleOsVersions.Count > 0 ? $"AXIS OS {string.Join(", ", Package.CompatibleOsVersions)}" : null,
            Package.SchemaVersion is { Length: > 0 } s ? $"Manifest {s}" : $"From {Package.Source}",
            Package.RunsAsRoot ? "Runs as root" : null,
        }.Where(p => p is not null));

    public string CompatibilitySummary => Package is null
        ? string.Empty
        : $"{CompatibleCount} of {Devices.Count} device(s) can install this package.";

    /// <summary>Loads the application list of the selected device. Called when the dialog opens.</summary>
    public Task InitializeAsync() => LoadApplicationsAsync(refresh: false);

    [RelayCommand]
    private Task RefreshAsync()
    {
        _cache.Clear();
        return LoadApplicationsAsync(refresh: true);
    }

    partial void OnSelectedDeviceChanged(DeviceChoice value)
    {
        if (_initialized)
        {
            _ = LoadApplicationsAsync(refresh: false);
        }
    }

    private async Task LoadApplicationsAsync(bool refresh)
    {
        var device = SelectedDevice;
        IsLoading = true;
        ErrorMessage = null;
        ConfirmRemoveVisible = false;
        Applications.Clear();
        SelectedApplication = null;
        try
        {
            var result = await QueryAsync(device.Device).ConfigureAwait(true);
            if (!ReferenceEquals(device, SelectedDevice))
            {
                return;
            }

            foreach (var app in result.Applications)
            {
                Applications.Add(new ApplicationRow(app));
            }
        }
#pragma warning disable CA1031 // Any query failure is shown in the dialog.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            if (ReferenceEquals(device, SelectedDevice))
            {
                ErrorMessage = "Could not read the applications: " + ex.Message;
            }
        }
        finally
        {
            if (ReferenceEquals(device, SelectedDevice))
            {
                IsLoading = false;
            }

            OnPropertyChanged(nameof(IsEmpty));
        }

        if (refresh && Package is not null)
        {
            await CheckCompatibilityAsync().ConfigureAwait(true);
        }
    }

    private Task<ListApplicationsResult> QueryAsync(IDeviceInfo device)
    {
        if (!_cache.TryGetValue(device.Id, out var task) || task.IsFaulted || task.IsCanceled)
        {
            task = QueryCoreAsync(device.Id);
            _cache[device.Id] = task;
        }

        return task;
    }

    private async Task<ListApplicationsResult> QueryCoreAsync(Guid deviceId)
    {
        var json = await _ctx.QueryAsync(deviceId, AcapPlugin.ListApplicationsMethod, null, CancellationToken.None).ConfigureAwait(true);
        return string.IsNullOrWhiteSpace(json)
            ? throw new InvalidOperationException("The server returned no data.")
            : ListApplicationsResult.FromJson(json);
    }

    private bool CanActOnSelection() => SelectedApplication is not null && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanActOnSelection))]
    private void Start() => Complete(new AcapPayload { Action = AcapAction.Start, Application = SelectedApplication!.PackageName });

    [RelayCommand(CanExecute = nameof(CanActOnSelection))]
    private void Stop() => Complete(new AcapPayload { Action = AcapAction.Stop, Application = SelectedApplication!.PackageName });

    /// <summary>Asks for confirmation first; <see cref="ConfirmRemoveCommand"/> completes the dialog.</summary>
    [RelayCommand(CanExecute = nameof(CanActOnSelection))]
    private void Remove()
    {
        var app = SelectedApplication!;
        var where = Devices.Count == 1 ? Devices[0].Label : $"all {Devices.Count} selected devices";
        ConfirmRemoveText = app.App.Bundled
            ? $"{app.Name} is bundled with AXIS OS and cannot be removed."
            : $"Remove {app.Name} from {where}? The application and its settings are deleted from the device.";
        ConfirmRemoveVisible = true;
    }

    [RelayCommand]
    private void ConfirmRemove()
    {
        if (SelectedApplication is { } app && !app.App.Bundled)
        {
            Complete(new AcapPayload { Action = AcapAction.Remove, Application = app.PackageName });
        }
        else
        {
            ConfirmRemoveVisible = false;
        }
    }

    [RelayCommand]
    private void CancelRemove() => ConfirmRemoveVisible = false;

    private bool CanPick() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanPick))]
    private async Task PickPackageAsync()
    {
        var path = await _picker.PickEapAsync().ConfigureAwait(true);
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        await LoadPackageAsync(path).ConfigureAwait(true);
    }

    /// <summary>Reads the manifest of a local .eap and checks it against every selected device.</summary>
    public async Task LoadPackageAsync(string path)
    {
        PackagePath = path;
        PackageError = null;
        Package = null;
        Compatibility.Clear();
        try
        {
            Package = await Task.Run(() => EapReader.ReadAsync(path, CancellationToken.None)).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is InvalidEapException or IOException or UnauthorizedAccessException)
        {
            PackageError = ex.Message;
        }

        OnPropertyChanged(nameof(PackageTitle));
        OnPropertyChanged(nameof(PackageDetails));
        await CheckCompatibilityAsync().ConfigureAwait(true);
    }

    partial void OnAllowDowngradeChanged(bool value) => _ = CheckCompatibilityAsync();

    private async Task CheckCompatibilityAsync()
    {
        var package = Package;
        Compatibility.Clear();
        CompatibleCount = 0;
        OnPropertyChanged(nameof(CompatibilitySummary));
        if (package is null)
        {
            return;
        }

        var rows = Devices.Select(d => new DeviceCompatibilityRow(d)).ToList();
        foreach (var row in rows)
        {
            Compatibility.Add(row);
        }

        foreach (var row in rows)
        {
            try
            {
                var state = await QueryAsync(row.Device.Device).ConfigureAwait(true);
                var installed = state.Applications.FirstOrDefault(a => string.Equals(a.Name, package.AppName, StringComparison.Ordinal));
                var report = AcapCompatibility.Check(package, state.Device, installed, AllowDowngrade);
                row.IsOk = report.IsCompatible;
                row.IsError = !report.IsCompatible;
                row.Result = report.Summary;
            }
#pragma warning disable CA1031 // Shown per device.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                row.IsError = true;
                row.Result = "Could not read the device: " + ex.Message;
            }
        }

        if (!ReferenceEquals(package, Package))
        {
            return;
        }

        CompatibleCount = rows.Count(r => r.IsOk);
        OnPropertyChanged(nameof(CompatibilitySummary));
    }

    private bool CanInstall() => Package is not null && CompatibleCount > 0 && !IsBusy;

    /// <summary>Uploads the package to the server, then completes with the install payload.</summary>
    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAsync()
    {
        var package = Package!;
        IsBusy = true;
        UploadProgress = 0;
        UploadStatus = "Uploading the package to the server...";
        using var cts = new CancellationTokenSource();
        _cancelUpload = cts.Cancel;
        try
        {
            var progress = new Progress<double>(v => UploadProgress = Math.Clamp(v > 1.0 ? v : v * 100.0, 0, 100));
            var file = await _ctx.UploadAsync(PackagePath!, progress, cts.Token).ConfigureAwait(true);
            UploadProgress = 100;
            Complete(new AcapPayload
            {
                Action = AcapAction.Install,
                FileId = file.Id,
                Sha256 = file.Sha256,
                Application = package.AppName,
                Version = package.Version,
                AllowDowngrade = AllowDowngrade,
                StartAfterInstall = StartAfterInstall,
            });
        }
        catch (OperationCanceledException)
        {
            UploadStatus = "Upload cancelled.";
        }
#pragma warning disable CA1031 // Shown in the dialog.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            UploadStatus = "Upload failed: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
            _cancelUpload = null;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        if (_cancelUpload is { } cancel)
        {
            cancel();
            return;
        }

        Result = null;
        CloseRequested?.Invoke(this, null);
    }

    private void Complete(AcapPayload payload)
    {
        Result = payload.ToJson();
        CloseRequested?.Invoke(this, Result);
    }
}
