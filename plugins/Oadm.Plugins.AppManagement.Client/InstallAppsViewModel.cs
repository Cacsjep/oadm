using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Plugins.Acap;
using Oadm.Plugins.Shared;
using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Collections;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.AppManagement.Client;

/// <summary>Picks local files for the page's dialogs (the windows implement it with the platform storage provider).</summary>
public interface IAppFilePicker
{
    /// <summary>One .eap, or one per architecture of the same application.</summary>
    Task<IReadOnlyList<string>> PickEapFilesAsync();

    /// <summary>License key files (.key, .xml) or ZIPs of them.</summary>
    Task<IReadOnlyList<string>> PickLicenseKeysAsync();
}

/// <summary>One target device of Install applications and its check against the chosen packages.</summary>
public sealed class InstallDeviceRow
{
    public InstallDeviceRow(DeviceApps device, AcapInstallPreview? preview)
    {
        ArgumentNullException.ThrowIfNull(device);
        Device = device;
        Preview = preview;
        Address = device.Address;
        AddressSortKey = AddressSortKeys.Of(device.Address);
        Model = device.Model ?? string.Empty;
        Firmware = device.Firmware ?? string.Empty;
        Soc = DeviceTexts.Soc(device.Soc) ?? string.Empty;
        Architecture = device.Architecture ?? string.Empty;
        if (device.Error is { } error)
        {
            // Not read (not Ok, no Application API) or the read failed: skipped with the page's own text.
            Verdict = error;
            IsWarning = device.NotChecked;
            IsError = !device.NotChecked;
        }
        else if (preview is { PackageIndex: < 0 })
        {
            // "No package for mips." says it all.
            Verdict = preview.Details;
            IsWarning = true;
        }
        else if (preview is not null)
        {
            Verdict = preview.Verdict;
            Details = preview.Details;
            IsOk = preview.IsOk;
            IsWarning = preview.IsWarning;
            IsError = preview.IsError;
        }
    }

    public DeviceApps Device { get; }

    /// <summary>The shared preview (null before files are chosen or for a device that was not read).</summary>
    public AcapInstallPreview? Preview { get; }

    public Guid DeviceId => Device.DeviceId;

    public string Address { get; }

    public string AddressSortKey { get; }

    public string Model { get; }

    public string Firmware { get; }

    public string Soc { get; }

    public string Architecture { get; }

    /// <summary>"Upgrade from 2.9.1", "Not compatible", "No package", the read error; empty before files are chosen.</summary>
    public string Verdict { get; } = string.Empty;

    /// <summary>The warnings or problems ("aarch64 build. The device accepts only signed applications; ...").</summary>
    public string? Details { get; }

    public bool IsOk { get; }

    public bool IsWarning { get; }

    public bool IsError { get; }

    /// <summary>A result to show (none before files are chosen).</summary>
    public bool HasVerdict => Verdict.Length > 0;

    /// <summary>The device gets a task (compatible, warnings allowed).</summary>
    public bool CanInstall => Device.Error is null && Preview is { IsOk: true };

    /// <summary>Problems first, then warnings, then the devices that fit.</summary>
    public string ResultSortKey => (IsError ? "0" : IsWarning ? "1" : IsOk ? "2" : "3") + Verdict;
}

/// <summary>
/// The page's Install applications dialog (docs/spec/app-management.md): the target devices, one or several .eap files (one
/// per architecture), the check of every device with the Applications plugin's own rules (<see cref="AcapCompatibility.Preview"/>,
/// linked source) against what the page read, then the upload and the Install tasks through the page's server part
/// (<see cref="AppManagementMethods.Run"/>). Devices that do not fit are skipped; the task checks every device again.
/// </summary>
public sealed partial class InstallAppsViewModel : ObservableObject
{
    public const string Title = "Install applications";
    public const string ChooseFilesFirst = "Choose the files.";
    public const string NothingFits = "None of the devices can install this package.";
    public const string NoUpload = "This version of OADM cannot install from this page.";

    private readonly ICorePluginClientContext _ctx;
    private IReadOnlyList<string> _paths = [];
    private IReadOnlyList<EapManifest> _packages = [];
    private CancellationTokenSource? _upload;
    private int _version;

    public InstallAppsViewModel(ICorePluginClientContext ctx, IReadOnlyList<DeviceApps> devices)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        ArgumentNullException.ThrowIfNull(devices);
        Devices = devices;
        MayDowngrade = ctx.Has(PermissionKeys.Task(AcapPlugin.Id, AcapPermissions.Downgrade));
        Rows.ReplaceAll(devices.Select(d => new InstallDeviceRow(d, null)));
    }

    /// <summary>Raised when the dialog closes: true when the tasks were started.</summary>
    public event EventHandler<bool>? CloseRequested;

    /// <summary>Opens the file picker (set by the window).</summary>
    public IAppFilePicker? Picker { get; set; }

    public IReadOnlyList<DeviceApps> Devices { get; }

    /// <summary>One row per target device, replaced in one step after each check.</summary>
    public RangeObservableCollection<InstallDeviceRow> Rows { get; } = [];

    /// <summary>The packages read from the chosen files (one, or one per architecture).</summary>
    public IReadOnlyList<EapManifest> Packages => _packages;

    public bool MayDowngrade { get; }

    [ObservableProperty]
    public partial string? FileName { get; private set; }

    [ObservableProperty]
    public partial string? FileDetails { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstallBlockedReason))]
    public partial string? FileError { get; private set; }

    [ObservableProperty]
    public partial bool StartAfterInstall { get; set; } = true;

    [ObservableProperty]
    public partial bool AllowDowngrade { get; set; }

    partial void OnAllowDowngradeChanged(bool value) => _ = CheckAsync();

    /// <summary>Some device has a newer version than the package.</summary>
    [ObservableProperty]
    public partial bool HasDowngrade { get; private set; }

    /// <summary>Allow downgrade: always shown to roles that may downgrade (like the Applications dialog), never to others.</summary>
    public bool ShowDowngrade => MayDowngrade;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstallBlockedReason))]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand), nameof(ChooseFilesCommand))]
    public partial bool IsChecking { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstallBlockedReason))]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand), nameof(ChooseFilesCommand))]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    public partial double UploadProgress { get; private set; }

    [ObservableProperty]
    public partial string? BusyText { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstallBlockedReason))]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    public partial int FitCount { get; private set; }

    /// <summary>"4 of 5 devices can install." once files are chosen.</summary>
    [ObservableProperty]
    public partial string? Summary { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorText { get; private set; }

    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    /// <summary>Why Install is disabled (its tooltip), or null.</summary>
    public string? InstallBlockedReason =>
        IsBusy || IsChecking ? null
        : _packages.Count == 0 ? FileError ?? ChooseFilesFirst
        : FitCount == 0 ? NothingFits
        : null;

    private bool CanChoose => !IsBusy && !IsChecking;

    [RelayCommand(CanExecute = nameof(CanChoose))]
    private async Task ChooseFilesAsync()
    {
        if (Picker is { } picker && await picker.PickEapFilesAsync().ConfigureAwait(true) is { Count: > 0 } paths)
        {
            await LoadFilesAsync(paths).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Reads the manifests off the UI thread (one file, or one per architecture of one application and version; a set that
    /// does not fit together is refused at the file row before any upload), then checks every device.
    /// </summary>
    public async Task LoadFilesAsync(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count == 0)
        {
            return;
        }

        _paths = [.. paths];
        _packages = [];
        FileError = null;
        FileName = paths.Count == 1 ? Path.GetFileName(paths[0]) : string.Create(CultureInfo.CurrentCulture, $"{paths.Count:N0} files");
        FileDetails = null;
        IsChecking = true;
        try
        {
            var several = paths.Count > 1;
            var read = await Task.Run(async () =>
            {
                var list = new List<(string FileName, EapManifest Package)>(paths.Count);
                foreach (var path in paths)
                {
                    try
                    {
                        list.Add((Path.GetFileName(path), await EapReader.ReadAsync(path, CancellationToken.None).ConfigureAwait(false)));
                    }
                    catch (Exception ex) when (several && ex is InvalidEapException or IOException or UnauthorizedAccessException)
                    {
                        throw new InvalidEapException(Path.GetFileName(path) + ": " + ex.Message, ex);
                    }
                }

                return list;
            }).ConfigureAwait(true);

            if (AcapCompatibility.CheckPackageSet(read) is { } refused)
            {
                FileError = refused;
            }
            else
            {
                _packages = [.. read.Select(r => r.Package)];
                FileDetails = Describe(_packages, paths);
            }
        }
        catch (Exception ex) when (ex is InvalidEapException or IOException or UnauthorizedAccessException)
        {
            FileError = ex.Message;
        }
        finally
        {
            IsChecking = false;
        }

        await CheckAsync().ConfigureAwait(true);
    }

    /// <summary>"Parking Plate Reader 2.10.3 · aarch64, armv7hf · 12.4 MB".</summary>
    private static string Describe(IReadOnlyList<EapManifest> packages, IReadOnlyList<string> paths)
    {
        var first = packages[0];
        var arch = packages.Count > 1
            ? string.Join(", ", packages.Select(p => p.Architecture))
            : AcapCompatibility.IsArchitectureIndependent(first.Architecture) ? "Any architecture" : first.Architecture;
        long size = 0;
        foreach (var path in paths)
        {
            try
            {
                size += new FileInfo(path).Length;
            }
            catch (IOException)
            {
            }
        }

        return string.Join("  ·  ", $"{first.DisplayName} {first.Version}", arch, FileSizeText.Format(size));
    }

    /// <summary>Checks every device off the UI thread (one pass, the shared rules), then replaces the rows once.</summary>
    private async Task CheckAsync()
    {
        var version = ++_version;
        var packages = _packages;
        var allowDowngrade = AllowDowngrade;
        if (packages.Count == 0)
        {
            Rows.ReplaceAll(Devices.Select(d => new InstallDeviceRow(d, null)));
            FitCount = 0;
            HasDowngrade = false;
            Summary = null;
            return;
        }

        IsChecking = true;
        try
        {
            var rows = await Task.Run(() => Check(Devices, packages, allowDowngrade)).ConfigureAwait(true);
            if (version != _version)
            {
                return;
            }

            Rows.ReplaceAll(rows);
            var fit = 0;
            var downgrade = false;
            foreach (var row in rows)
            {
                fit += row.CanInstall ? 1 : 0;
                downgrade |= row.Preview?.IsDowngrade == true;
            }

            FitCount = fit;
            HasDowngrade = downgrade;
            Summary = string.Create(CultureInfo.CurrentCulture, $"{fit:N0} of {rows.Length:N0} {(rows.Length == 1 ? "device" : "devices")} can install.");
        }
        finally
        {
            if (version == _version)
            {
                IsChecking = false;
            }
        }
    }

    /// <summary>The rows of <paramref name="devices"/> against the packages (tests call it directly).</summary>
    internal static InstallDeviceRow[] Check(IReadOnlyList<DeviceApps> devices, IReadOnlyList<EapManifest> packages, bool allowDowngrade)
    {
        var rows = new InstallDeviceRow[devices.Count];
        for (var i = 0; i < rows.Length; i++)
        {
            var device = devices[i];
            var preview = device.Error is null
                ? AcapCompatibility.Preview(packages, new AcapDeviceFacts
                {
                    Architecture = device.Architecture,
                    FirmwareVersion = device.Firmware,
                    AllowUnsigned = device.AllowUnsigned,
                    EmbeddedDevelopmentVersion = device.EmbeddedDevelopmentVersion,
                }, device.Applications, allowDowngrade)
                : null;
            rows[i] = new InstallDeviceRow(device, preview);
        }

        return rows;
    }

    /// <summary>The install kind shared by every device that fits, null when they differ (names the task only).</summary>
    private InstallKind? KindOfAll()
    {
        var kinds = Rows.Where(r => r.CanInstall).Select(r => r.Preview!.Kind).Distinct().ToList();
        return kinds.Count == 1 ? kinds[0] : null;
    }

    private bool CanInstall => !IsBusy && !IsChecking && _packages.Count > 0 && FitCount > 0;

    /// <summary>Uploads every file (one bar), then starts the Install tasks for the devices that fit through the server part.</summary>
    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAsync()
    {
        var packages = _packages;
        var paths = _paths;
        var devices = Rows.Where(r => r.CanInstall).Select(r => r.DeviceId).ToList();
        IsBusy = true;
        ErrorText = null;
        UploadProgress = 0;
        BusyText = "Uploading the files";
        using var cts = new CancellationTokenSource();
        _upload = cts;
        try
        {
            var files = new List<AcapPackageFile>(packages.Count);
            var current = 0;
            var progress = new Progress<double>(v => UploadProgress = Math.Clamp((current + Math.Clamp(v, 0, 1)) * 100.0 / packages.Count, 0, 100));
            for (current = 0; current < packages.Count; current++)
            {
                var uploaded = await _ctx.UploadAsync(paths[current], progress, cts.Token).ConfigureAwait(true);
                files.Add(new AcapPackageFile(uploaded.Id, uploaded.Sha256, packages[current].Architecture));
            }

            UploadProgress = 100;
            BusyText = "Starting the tasks";
            var package = packages[0];
            var request = new RunRequest
            {
                Action = AcapAction.Install,
                Items = [new RunItem { Application = package.AppName, DisplayName = package.DisplayName, DeviceIds = devices }],
                Install = new AppInstall
                {
                    Files = files,
                    Version = package.Version,
                    AllowDowngrade = AllowDowngrade && MayDowngrade,
                    StartAfterInstall = StartAfterInstall,
                    Kind = KindOfAll(),
                },
                Owner = _ctx.OwnerName,
            };
            await _ctx.InvokeAsync(AppManagementMethods.Run, AppManagementJson.Serialize(request), cts.Token).ConfigureAwait(true);
            _upload = null;
            CloseRequested?.Invoke(this, true);
        }
        catch (OperationCanceledException)
        {
            ErrorText = "Upload cancelled.";
        }
        catch (NotSupportedException)
        {
            ErrorText = NoUpload;
        }
#pragma warning disable CA1031 // Shown in the dialog.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ErrorText = ServerErrors.Message(ex);
        }
        finally
        {
            _upload = null;
            IsBusy = false;
            BusyText = null;
        }
    }

    /// <summary>Cancel: stops a running upload, else closes the dialog.</summary>
    [RelayCommand]
    private void Cancel()
    {
        if (_upload is { } upload)
        {
            upload.Cancel();
            return;
        }

        CloseRequested?.Invoke(this, false);
    }
}
