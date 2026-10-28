using System.Collections.ObjectModel;
using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Sdk.Client;
using Oadm.Sdk.Devices;

namespace Oadm.Plugins.Acap.Client;

/// <summary>Picks a local .eap file; implemented by the window with the platform storage provider.</summary>
public interface IEapFilePicker
{
    Task<string?> PickEapAsync();

    /// <summary>Several .eap files of one application, one per architecture; by default one file.</summary>
    async Task<IReadOnlyList<string>> PickEapFilesAsync() => await PickEapAsync().ConfigureAwait(true) is { Length: > 0 } path ? [path] : [];
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

    /// <summary>"Valid until 2027-09-18", "Not required" (the device says None), "Missing", ...</summary>
    public string License => App.LicenseText;

    public string Signature => App.SignatureStatus ?? string.Empty;
}

/// <summary>Per-device compatibility of the picked package.</summary>
public sealed partial class DeviceCompatibilityRow(DeviceChoice device) : ObservableObject
{
    public DeviceChoice Device { get; } = device;

    public string DeviceLabel => Device.Label;

    /// <summary>Full report text (kind of install and warnings, or the problems).</summary>
    [ObservableProperty]
    public partial string Result { get; set; } = "Checking...";

    /// <summary>Short verdict for the status chip, e.g. "Upgrade from 3.8.0" or "Not compatible".</summary>
    [ObservableProperty]
    public partial string Verdict { get; set; } = "Checking";

    /// <summary>Warnings of a compatible device, problems of an incompatible one.</summary>
    [ObservableProperty]
    public partial string? Details { get; set; }

    [ObservableProperty]
    public partial bool IsOk { get; set; }

    /// <summary>Install, upgrade, reinstall or downgrade on a compatible device; null otherwise.</summary>
    public InstallKind? Kind { get; set; }

    /// <summary>With several packages: the architecture of the one this device gets ("aarch64"); null otherwise.</summary>
    public string? PackageArchitecture { get; set; }

    /// <summary>Installable, but with warnings or as a downgrade.</summary>
    [ObservableProperty]
    public partial bool IsWarning { get; set; }

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
    /// <summary>Devices whose applications the dialog reads to preview compatibility (one per model and firmware first).</summary>
    public const int CompatibilitySampleSize = 25;

    /// <summary>Application list queries in flight at most while checking compatibility.</summary>
    public const int MaxParallelQueries = 4;

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

        // Only what the user's role allows (the server refuses the rest): each action is a right of its own.
        MayInstall = ctx.Has(Key(AcapPermissions.Install));
        MayDowngrade = ctx.Has(Key(AcapPermissions.Downgrade));
        MayStartStop = ctx.Has(Key(AcapPermissions.StartStop));
        MayRemove = ctx.Has(Key(AcapPermissions.Remove));
        Devices = devices.Select(d => new DeviceChoice(d)).ToList();
        SelectedDevice = Devices[0];
        _startView = StartView.Parse(ctx.StartView);
        _initialized = true;
    }

    /// <summary>The role may install and upgrade applications.</summary>
    public bool MayInstall { get; }

    /// <summary>The role may install an older version ("Allow downgrade").</summary>
    public bool MayDowngrade { get; }

    /// <summary>The role may start and stop applications.</summary>
    public bool MayStartStop { get; }

    /// <summary>The role may remove applications.</summary>
    public bool MayRemove { get; }

    private static string Key(string permission) => Oadm.Sdk.Plugins.PermissionKeys.Task(AcapPlugin.Id, permission);

    /// <summary>Raised with the payload JSON, or null when the dialog was cancelled.</summary>
    public event EventHandler<string?>? CloseRequested;

    public IReadOnlyList<DeviceChoice> Devices { get; }

    public bool ShowDevicePicker => Devices.Count > 1;

    public string Title { get; } = "Applications (ACAP)";

    public string ScopeText => Devices.Count == 1
        ? $"Actions apply to {Devices[0].Label}."
        : string.Create(CultureInfo.InvariantCulture, $"Actions apply to all {Devices.Count:N0} selected devices.");

    public ObservableCollection<ApplicationRow> Applications { get; } = [];

    /// <summary>
    /// One row per selected device, replaced as a whole (one change notification, virtualized grid). Only
    /// <see cref="CompatibilitySampleSize"/> devices are read and checked in the dialog; the task checks every device
    /// again before installing.
    /// </summary>
    [ObservableProperty]
    public partial IReadOnlyList<DeviceCompatibilityRow> Compatibility { get; private set; } = [];

    /// <summary>Devices of <see cref="Compatibility"/> that were checked in the dialog.</summary>
    [ObservableProperty]
    public partial int CheckedCount { get; private set; }

    /// <summary>The payload when the dialog completed, null otherwise.</summary>
    public string? Result { get; private set; }

    [ObservableProperty]
    public partial DeviceChoice SelectedDevice { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand), nameof(RemoveCommand), nameof(LicenseSelectionCommand))]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(LicenseSelectionBlockedReason))]
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
    [NotifyPropertyChangedFor(nameof(HasPackage), nameof(InstallBlockedReason), nameof(ShowInstallButton))]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    public partial EapManifest? Package { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPackageFile), nameof(PackageFileName), nameof(ShowPackageCard))]
    public partial string? PackagePath { get; set; }

    /// <summary>Size of the picked file ("1.2 MB"), shown in the file row.</summary>
    [ObservableProperty]
    public partial string? PackageFileDetails { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPackageError), nameof(InstallBlockedReason))]
    public partial string? PackageError { get; set; }

    [ObservableProperty]
    public partial bool AllowDowngrade { get; set; }

    [ObservableProperty]
    public partial bool StartAfterInstall { get; set; } = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand), nameof(PickPackageCommand), nameof(StartCommand), nameof(StopCommand), nameof(RemoveCommand), nameof(LicenseSelectionCommand), nameof(InstallLicenseCommand))]
    [NotifyPropertyChangedFor(nameof(ShowUploadStatus), nameof(InstallBlockedReason))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial double UploadProgress { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowUploadStatus))]
    public partial string? UploadStatus { get; set; }

    /// <summary>The last upload outcome (cancelled, failed) while no upload is running.</summary>
    public bool ShowUploadStatus => !IsBusy && !string.IsNullOrEmpty(UploadStatus);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    [NotifyPropertyChangedFor(nameof(InstallBlockedReason))]
    public partial int CompatibleCount { get; set; }

    public bool HasSelection => SelectedApplication is not null;

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public bool IsEmpty => !IsLoading && !HasError && Applications.Count == 0;

    public bool HasPackage => Package is not null;

    /// <summary>The footer's Install: a package was read and no license is being installed on its own.</summary>
    public bool ShowInstallButton => HasPackage && !IsLicenseMode;

    public bool HasPackageError => !string.IsNullOrEmpty(PackageError);

    /// <summary>A package file was picked (valid or not): the install card is shown.</summary>
    public bool HasPackageFile => !string.IsNullOrEmpty(PackagePath);

    public string? PackageFileName => PackagePath is null ? null
        : _paths.Count > 1 ? string.Create(CultureInfo.InvariantCulture, $"{_paths.Count} files")
        : Path.GetFileName(PackagePath);

    /// <summary>The chosen files (one per architecture when several).</summary>
    private IReadOnlyList<string> _paths = [];

    /// <summary>The packages read from <see cref="_paths"/>, in the same order; empty while a file is refused.</summary>
    private IReadOnlyList<EapManifest> _packages = [];

    /// <summary>The packages of the install (one, or one per architecture). <see cref="Package"/> is the first.</summary>
    public IReadOnlyList<EapManifest> Packages => _packages;

    /// <summary>Several packages, one per architecture.</summary>
    public bool HasSeveralPackages => _packages.Count > 1;

    public string PackageTitle => Package is null ? "Install or upgrade" : $"{Package.DisplayName} {Package.Version}";

    public string PackageDetails => Package is null
        ? string.Empty
        : string.Join("  ·  ", new[]
        {
            $"Package {Package.AppName}",
            Package.Vendor is { Length: > 0 } v ? $"Vendor {v}" : null,
            HasSeveralPackages
                ? $"Architecture {string.Join(", ", _packages.Select(p => p.Architecture))}"
                : $"Architecture {(AcapCompatibility.IsArchitectureIndependent(Package.Architecture) ? "any" : Package.Architecture)}",
            Package.CompatibleOsVersions.Count > 0 ? $"AXIS OS {string.Join(", ", Package.CompatibleOsVersions)}" : null,
            ManifestOf(Package) is { } manifest ? $"Manifest {manifest}" : null,
            Package.RunsAsRoot ? "Runs as root" : null,
        }.Where(p => p is not null));

    /// <summary>
    /// The Manifest value of the package facts: the manifest schema version ("1.3", "2.1"), "package.conf" for a package
    /// without manifest.json, null for a manifest.json without a schema version.
    /// </summary>
    public static string? ManifestOf(EapManifest package)
    {
        ArgumentNullException.ThrowIfNull(package);
        return package.SchemaVersion is { Length: > 0 } schema ? schema
            : string.Equals(package.Source, "package.conf", StringComparison.Ordinal) ? "package.conf"
            : null;
    }

    public string CompatibilitySummary => Package is null
        ? string.Empty
        : CheckedCount >= Devices.Count
            ? string.Create(CultureInfo.InvariantCulture, $"{CompatibleCount:N0} of {Devices.Count:N0} {(Devices.Count == 1 ? "device" : "devices")} can install this package.")
            : string.Create(CultureInfo.InvariantCulture, $"{CompatibleCount:N0} of {CheckedCount:N0} checked devices can install this package; the other {Devices.Count - CheckedCount:N0} are checked by the task before installing.");

    /// <summary>Loads the application list of the selected device. Called when the dialog opens.</summary>
    public Task InitializeAsync() => LoadApplicationsAsync(refresh: false);

    [RelayCommand]
    private Task RefreshAsync()
    {
        lock (_cache)
        {
            _cache.Clear();
        }

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

            OpenStartView();
        }
#pragma warning disable CA1031 // Any query failure is shown in the dialog.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            if (ReferenceEquals(device, SelectedDevice))
            {
                ErrorMessage = "Could not read the applications: " + ServerErrors.Message(ex);
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
        // Locked: the compatibility check runs several queries at once.
        lock (_cache)
        {
            if (!_cache.TryGetValue(device.Id, out var task) || task.IsFaulted || task.IsCanceled)
            {
                task = QueryCoreAsync(device.Id);
                _cache[device.Id] = task;
            }

            return task;
        }
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
    private void Start() => Complete(new AcapPayload { Action = AcapAction.Start, Application = SelectedApplication!.PackageName, AppDisplayName = SelectedApplication.Name });

    [RelayCommand(CanExecute = nameof(CanActOnSelection))]
    private void Stop() => Complete(new AcapPayload { Action = AcapAction.Stop, Application = SelectedApplication!.PackageName, AppDisplayName = SelectedApplication.Name });

    /// <summary>Asks for confirmation first; <see cref="ConfirmRemoveCommand"/> completes the dialog.</summary>
    [RelayCommand(CanExecute = nameof(CanActOnSelection))]
    private void Remove()
    {
        var app = SelectedApplication!;
        var where = Devices.Count == 1 ? Devices[0].Label : string.Create(CultureInfo.InvariantCulture, $"all {Devices.Count:N0} selected devices");
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
            Complete(new AcapPayload { Action = AcapAction.Remove, Application = app.PackageName, AppDisplayName = app.Name });
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
        var paths = await _picker.PickEapFilesAsync().ConfigureAwait(true);
        if (paths.Count == 0)
        {
            return;
        }

        await LoadPackagesAsync(paths).ConfigureAwait(true);
    }

    /// <summary>Reads the manifest of a local .eap and checks it against every selected device.</summary>
    public Task LoadPackageAsync(string path) => LoadPackagesAsync([path]);

    /// <summary>
    /// Reads the manifests of local .eap files (one, or one per architecture of the same application and version) and
    /// checks them against the selected devices; a set that does not fit together is refused at the file row before any upload.
    /// </summary>
    public async Task LoadPackagesAsync(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count == 0)
        {
            return;
        }

        if (IsLicenseMode)
        {
            CloseLicense();
        }

        _paths = [.. paths];
        _packages = [];
        PackagePath = paths[0];
        OnPropertyChanged(nameof(PackageFileName));
        PackageError = null;
        PackageFileDetails = null;
        Package = null;
        Compatibility = [];
        var read = new List<(string FileName, EapManifest Package)>(paths.Count);
        var several = paths.Count > 1;
        try
        {
            PackageFileDetails = FileSizeText.Format(paths.Sum(p => new FileInfo(p).Length));
            foreach (var path in paths)
            {
                try
                {
                    read.Add((Path.GetFileName(path), await Task.Run(() => EapReader.ReadAsync(path, CancellationToken.None)).ConfigureAwait(true)));
                }
                catch (Exception ex) when (several && ex is InvalidEapException or IOException or UnauthorizedAccessException)
                {
                    throw new InvalidEapException(Path.GetFileName(path) + ": " + ex.Message, ex);
                }
            }

            if (AcapCompatibility.CheckPackageSet(read) is { } refused)
            {
                PackageError = refused;
            }
            else
            {
                _packages = [.. read.Select(r => r.Package)];
                Package = _packages[0];
            }
        }
        catch (Exception ex) when (ex is InvalidEapException or IOException or UnauthorizedAccessException)
        {
            PackageError = ex.Message;
        }

        OnPropertyChanged(nameof(HasSeveralPackages));
        OnPropertyChanged(nameof(PackageTitle));
        OnPropertyChanged(nameof(PackageDetails));
        await CheckCompatibilityAsync().ConfigureAwait(true);
    }

    partial void OnAllowDowngradeChanged(bool value) => _ = CheckCompatibilityAsync();

    /// <summary>
    /// Builds one row per device in one pass and checks a bounded sample (<see cref="SampleIndexes"/>) with at most
    /// <see cref="MaxParallelQueries"/> queries in flight: never one server call per selected device.
    /// </summary>
    private async Task CheckCompatibilityAsync()
    {
        var package = Package;
        var packages = _packages;
        var allowDowngrade = AllowDowngrade;
        CompatibleCount = 0;
        CheckedCount = 0;
        if (package is null)
        {
            Compatibility = [];
            OnPropertyChanged(nameof(CompatibilitySummary));
            return;
        }

        var rows = new DeviceCompatibilityRow[Devices.Count];
        for (var i = 0; i < rows.Length; i++)
        {
            rows[i] = new DeviceCompatibilityRow(Devices[i]);
        }

        var sample = SampleIndexes(Devices, CompatibilitySampleSize);
        var sampled = new bool[rows.Length];
        foreach (var i in sample)
        {
            sampled[i] = true;
        }

        for (var i = 0; i < rows.Length; i++)
        {
            if (!sampled[i])
            {
                rows[i].Verdict = "Checked at install";
                rows[i].Result = "Checked by the task on the device before anything is installed.";
            }
        }

        Compatibility = rows;
        OnPropertyChanged(nameof(CompatibilitySummary));

        using var slots = new SemaphoreSlim(MaxParallelQueries);
        await Task.WhenAll(sample.Select(i => CheckRowAsync(rows[i], packages, allowDowngrade, slots))).ConfigureAwait(true);

        if (!ReferenceEquals(package, Package) || !ReferenceEquals(rows, Compatibility))
        {
            return;
        }

        var compatible = 0;
        foreach (var i in sample)
        {
            compatible += rows[i].IsOk ? 1 : 0;
        }

        CheckedCount = sample.Count;
        CompatibleCount = compatible;
        OnPropertyChanged(nameof(CompatibilitySummary));
    }

    private async Task CheckRowAsync(DeviceCompatibilityRow row, IReadOnlyList<EapManifest> packages, bool allowDowngrade, SemaphoreSlim slots)
    {
        await slots.WaitAsync().ConfigureAwait(true);
        try
        {
            var state = await QueryAsync(row.Device.Device).ConfigureAwait(true);

            // Several packages: the one of the device's architecture, the same choice the task makes again before uploading
            // (the shared preview, also the Applications page's).
            var preview = AcapCompatibility.Preview(packages, state.Device, state.Applications, allowDowngrade);
            row.PackageArchitecture = preview.PackageArchitecture;
            row.IsOk = preview.IsOk;
            row.Kind = preview.Kind;
            row.IsWarning = preview.IsWarning;
            row.IsError = preview.IsError;
            row.Result = preview.Result;
            row.Verdict = preview.Verdict;
            row.Details = preview.Details;
        }
#pragma warning disable CA1031 // Shown per device.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            row.IsError = true;
            row.Result = "Could not read the device: " + ServerErrors.Message(ex);
            row.Verdict = "Not readable";
            row.Details = row.Result;
        }
        finally
        {
            slots.Release();
        }
    }

    /// <summary>
    /// Indexes (ascending) of the devices the dialog checks: the first device of every model and firmware version, then
    /// the first devices in order, at most <paramref name="max"/>. O(n).
    /// </summary>
    public static IReadOnlyList<int> SampleIndexes(IReadOnlyList<DeviceChoice> devices, int max)
    {
        ArgumentNullException.ThrowIfNull(devices);
        var picked = new SortedSet<int>();
        var seen = new HashSet<(string?, string?)>();
        for (var i = 0; i < devices.Count && picked.Count < max; i++)
        {
            if (seen.Add((devices[i].Device.Model, devices[i].Device.FirmwareVersion)))
            {
                picked.Add(i);
            }
        }

        for (var i = 0; i < devices.Count && picked.Count < max; i++)
        {
            picked.Add(i);
        }

        return [.. picked];
    }

    /// <summary>The install kind shared by every compatible device, null when they differ (names the task only).</summary>
    public InstallKind? InstallKindOfAll()
    {
        var kinds = Compatibility.Where(r => r.IsOk && r.Kind is not null).Select(r => r.Kind!.Value).Distinct().ToList();
        return kinds.Count == 1 ? kinds[0] : null;
    }

    private bool CanInstall() => Package is not null && CompatibleCount > 0 && !IsBusy;

    /// <summary>Why Install is disabled (its tooltip); the package's own error stays at the file row.</summary>
    public string? InstallBlockedReason =>
        IsBusy ? null
        : Package is null ? PackageError ?? "Choose an ACAP package (.eap)."
        : CompatibleCount == 0 ? "None of the selected devices can install this package (see Result)."
        : null;

    /// <summary>Uploads the package to the server, then completes with the install payload.</summary>
    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAsync()
    {
        var package = Package!;
        IsBusy = true;
        PackageError = null;
        UploadProgress = 0;
        UploadStatus = _packages.Count > 1 ? "Uploading the packages to the server..." : "Uploading the package to the server...";
        using var cts = new CancellationTokenSource();
        _cancelUpload = cts.Cancel;
        try
        {
            // Every package is uploaded: the devices the preview did not read pick theirs in the task. Several files: one bar.
            var packages = _packages;
            var paths = _paths;
            var finished = false;
            var current = 0;
            var progress = new Progress<double>(v =>
            {
                if (!finished)
                {
                    var percent = Math.Clamp(v > 1.0 ? v : v * 100.0, 0, 100);
                    UploadProgress = Math.Clamp(((current * 100) + percent) / packages.Count, 0, 100);
                }
            });
            var files = new List<AcapPackageFile>(packages.Count);
            for (current = 0; current < packages.Count; current++)
            {
                var uploaded = await _ctx.UploadAsync(paths[current], progress, cts.Token).ConfigureAwait(true);
                files.Add(new AcapPackageFile(uploaded.Id, uploaded.Sha256, packages[current].Architecture));
            }

            finished = true;
            UploadProgress = 100;
            Complete(new AcapPayload
            {
                Action = AcapAction.Install,
                FileId = files[0].FileId,
                Sha256 = files[0].Sha256,
                Files = files.Count > 1 ? files : null,
                Application = package.AppName,
                Version = package.Version,
                AllowDowngrade = AllowDowngrade,
                StartAfterInstall = StartAfterInstall,
                AppDisplayName = package.DisplayName,
                Kind = InstallKindOfAll(),
                LicenseKeys = PayloadKeys(),
                LicenseApplicationId = PayloadApplicationId(),
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
            UploadStatus = null;
            PackageError = "Upload failed: " + ServerErrors.Message(ex); // at the file row, like a rejected package
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
