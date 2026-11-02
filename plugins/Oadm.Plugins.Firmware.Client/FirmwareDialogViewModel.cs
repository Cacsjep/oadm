using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Collections;
using Oadm.Sdk.Devices;

namespace Oadm.Plugins.Firmware.Client;

/// <summary>Local file access for the dialog, replaceable in tests.</summary>
public interface IFirmwareFileSource
{
    /// <summary>Lets the user choose .bin files (one per model); empty when cancelled.</summary>
    Task<IReadOnlyList<string>> PickAsync();

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

    /// <summary>The chosen file this device installs (index into the dialog's files); -1 when none fits.</summary>
    internal int FileIndex { get; set; } = -1;

    /// <summary>The version from axis.com this device installs (index into the dialog's online entries); -1 when none.</summary>
    internal int OnlineIndex { get; set; } = -1;

    /// <summary>The firmware row of the device's model and track.</summary>
    internal FirmwareModelRow? ModelRow { get; set; }

    [ObservableProperty]
    public partial string? CurrentVersion { get; set; } = device.FirmwareVersion;

    /// <summary>True while the status query of this row runs (the Firmware cell shows "Reading").</summary>
    [ObservableProperty]
    public partial bool IsReadingStatus { get; set; }

    /// <summary>False when the device does not offer fwmgr 1.x (from the status query).</summary>
    [ObservableProperty]
    public partial bool Supported { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VerdictText), nameof(Message), nameof(IsOk), nameof(IsWarn), nameof(IsError), nameof(WillInstall))]
    public partial FirmwareCheck? Check { get; set; }

    public string VerdictText => Check?.Verdict switch
    {
        null => "Choose a file",
        FirmwareVerdict.Upgrade => Check.PathAdvice is null ? "Upgrade" : "Not recommended",
        FirmwareVerdict.DeviceValidates => "Device checks",
        FirmwareVerdict.AlreadyUpToDate => "Up to date",
        FirmwareVerdict.Downgrade => "Downgrade",
        FirmwareVerdict.DowngradeBlocked => "Downgrade blocked",
        FirmwareVerdict.WrongProduct => "Wrong product",
        FirmwareVerdict.InvalidFile => "Invalid file",
        FirmwareVerdict.UnknownVersionRefused => "Refused",
        FirmwareVerdict.NoFileForModel => "No file",
        FirmwareVerdict.AppsBlock => "Apps block it",
        FirmwareVerdict.SignInNeeded => "Sign in",
        _ => "Not supported",
    };

    public string Message => Check?.Message ?? string.Empty;

    public bool IsOk => Check is { Verdict: FirmwareVerdict.Upgrade, PathAdvice: null };

    /// <summary>A downgrade, a file the device checks, or an upgrade off the recommended path (it still runs).</summary>
    public bool IsWarn => Check?.Verdict is FirmwareVerdict.Downgrade or FirmwareVerdict.DeviceValidates or FirmwareVerdict.SignInNeeded || Check?.PathAdvice is not null;

    public bool IsError => Check is { WillInstall: false, IsNoOp: false } && Check.Verdict != FirmwareVerdict.SignInNeeded;

    public bool WillInstall => Check?.WillInstall == true;

    /// <summary>The device's installed applications from the status query; null until read or when not readable.</summary>
    internal IReadOnlyList<InstalledApp>? Apps { get; set; }

    /// <summary>The status query was started for this row (preloaded or because the grid showed it).</summary>
    internal bool StatusRequested { get; set; }
}

/// <summary>
/// "Upgrade firmware" dialog: per model and track a version from axis.com or a chosen .bin (README "Firmware online"),
/// the preview per device, the options; uploads the chosen files to the server and returns the task payload.
/// </summary>
public sealed partial class FirmwareDialogViewModel : ObservableObject, IDisposable
{
    public const string NothingChosenText = "Choose a version or a file.";
    public const string LookupText = "Reading the versions on axis.com";

    private readonly ITaskDialogContext _ctx;
    private readonly IFirmwareFileSource _files;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<(string Key, int? Major), FirmwareModelRow> _rowsByKey = [];
    private readonly Dictionary<string, OnlineModel> _onlineByKey = new(StringComparer.Ordinal);

    // Never disposed: status reads still in flight release it after the dialog closed (it has no wait handle).
    private readonly SemaphoreSlim _statusSlots = new(MaxParallelStatusQueries);
    private CancellationTokenSource? _uploadCts;
    private string? _selectError;
    private bool _lookupDone;
    private int _bulk;
    private bool _dirty;

    private List<FileChoice> _fileChoices = [];
    private FirmwareImageInfo[] _images = [];
    private List<FirmwareOnline> _online = [];
    private FirmwareImageInfo[] _onlineImages = [];
    private Dictionary<(string, int?), int> _onlineIndex = [];

    public FirmwareDialogViewModel(ITaskDialogContext ctx, IReadOnlyList<IDeviceInfo> devices, IFirmwareFileSource files)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(files);
        _ctx = ctx;
        _files = files;
        Devices = new ObservableCollection<FirmwareDeviceRow>(devices.Select(d => new FirmwareDeviceRow(d)));

        // Only what the user's role allows: a factory default and a downgrade are rights of their own.
        MayFactoryDefault = ctx.Has(Oadm.Sdk.Plugins.PermissionKeys.Task(FirmwarePermissions.TaskPluginId, FirmwarePermissions.FactoryDefault));
        MayDowngrade = ctx.Has(Oadm.Sdk.Plugins.PermissionKeys.Task(FirmwarePermissions.TaskPluginId, FirmwarePermissions.Downgrade));
        Modes = MayFactoryDefault ? AllModes : [AllModes[0]];
        SelectedMode = Modes[0];

        CanSignIn = ctx.MyAxis is not null;
        IsSignedIn = ctx.MyAxis?.IsSignedIn == true;
        if (ctx.MyAxis is { } myAxis)
        {
            myAxis.Changed += OnMyAxisChanged;
        }

        BuildModelRows();
        Evaluate();
    }

    /// <summary>The role may reset devices with the upgrade ("task:oadm.firmware:factoryDefault").</summary>
    public bool MayFactoryDefault { get; }

    /// <summary>The role may install older firmware ("task:oadm.firmware:downgrade").</summary>
    public bool MayDowngrade { get; }

    /// <summary>Raised with the payload JSON (or null when cancelled) when the dialog should close.</summary>
    public event EventHandler<string?>? CloseRequested;

    public ObservableCollection<FirmwareDeviceRow> Devices { get; }

    /// <summary>One row per model and track of the selected devices.</summary>
    public RangeObservableCollection<FirmwareModelRow> ModelRows { get; } = [];

    private static readonly FactoryDefaultModeOption[] AllModes =
    [
        new(FactoryDefaultMode.None, "None, keep all settings (recommended)"),
        new(FactoryDefaultMode.Soft, "Soft, reset settings but keep network settings"),
        new(FactoryDefaultMode.Hard, "Hard, reset everything including the IP address"),
    ];

    /// <summary>The factory default choices the role may use (only "None" without the permission).</summary>
    public IReadOnlyList<FactoryDefaultModeOption> Modes { get; }

    public string Title { get; } = "Upgrade firmware";

    /// <summary>The host offers the MyAxis sign-in (older hosts: chosen files only).</summary>
    public bool CanSignIn { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSignIn))]
    public partial bool IsSignedIn { get; private set; }

    /// <summary>The line "Sign in to MyAxis to download from axis.com." with its button.</summary>
    public bool ShowSignIn => CanSignIn && !IsSignedIn;

    /// <summary>Opens the MyAxis sign-in window (set by the dialog: <c>ITaskDialogContext.MyAxis.SignInAsync(window)</c>).</summary>
    public Func<Task<bool>>? SignInHandler { get; set; }

    /// <summary>True while the versions are read from axis.com.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RecommendedCommand), nameof(LatestLtsCommand), nameof(LatestActiveCommand))]
    public partial bool IsLookingUp { get; private set; }

    /// <summary>0-100: models read of all.</summary>
    [ObservableProperty]
    public partial double LookupProgress { get; private set; }

    [ObservableProperty]
    public partial string? LookupStatus { get; private set; }

    /// <summary>axis.com did not answer: "axis.com could not be reached." above the table, the rows keep Choose file.</summary>
    [ObservableProperty]
    public partial bool OnlineUnreachable { get; private set; }

    /// <summary>Some model is listed on axis.com (the rule buttons have something to set).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RecommendedCommand), nameof(LatestLtsCommand), nameof(LatestActiveCommand))]
    public partial bool HasOnline { get; private set; }

    /// <summary>The first problem of a row's choice (a refused file, two files for one model); it blocks Upgrade.</summary>
    [ObservableProperty]
    public partial string? FileError { get; private set; }

    [ObservableProperty]
    public partial FactoryDefaultModeOption SelectedMode { get; set; }

    [ObservableProperty]
    public partial bool AllowDowngrade { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    public partial bool IsUploading { get; set; }

    /// <summary>0-100.</summary>
    [ObservableProperty]
    public partial double UploadProgress { get; set; }

    [ObservableProperty]
    public partial string? UploadText { get; set; }

    /// <summary>Why the upload of the chosen files failed (shown below the upload progress).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; set; }

    public bool HasError => Error is not null;

    /// <summary>Why Upgrade is disabled (its tooltip); null when it is enabled or an upload runs.</summary>
    public string? StartBlockedReason =>
        IsUploading ? null
        : IsLookingUp ? LookupText + "."
        : FileError is not null ? FileError
        : ModelRows.Any(r => r.IsChecking) ? "Checking the version on axis.com."
        : !AnyChoice ? NothingChosenText
        : !Devices.Any(d => d.WillInstall) ? "None of the selected devices can install the chosen firmware (see Result)."
        : null;

    [ObservableProperty]
    public partial string Summary { get; set; } = NothingChosenText;

    public string? ModeWarning => SelectedMode.Mode switch
    {
        FactoryDefaultMode.Soft => "Soft factory default: all settings, users and passwords are reset; the IP address and network settings are kept. OADM loses access until a new password is set.",
        FactoryDefaultMode.Hard => "Hard factory default: everything is reset, including the IP address. Devices with a static address fall back to DHCP or 192.168.0.90 and may become unreachable for OADM.",
        _ => null,
    };

    public bool HasModeWarning => ModeWarning is not null;

    /// <summary>Devices whose upgrade is off the recommended path (advice only; the Details column says what first).</summary>
    public string? PathWarning
    {
        get
        {
            var count = Devices.Count(d => d.Check?.PathAdvice is not null);
            return count == 0 ? null
                : (count == 1 ? "1 device is" : string.Create(CultureInfo.InvariantCulture, $"{count:N0} devices are"))
                  + " not on the upgrade path Axis recommends (Details). AXIS OS 12.6.85, 11.11.169 and later refuse such an upgrade; the upgrade still starts.";
        }
    }

    public bool HasPathWarning => PathWarning is not null;

    /// <summary>The Axis guides of major versions with breaking changes, by major version.</summary>
    public static IReadOnlyDictionary<int, Uri> BreakingChangesGuides { get; } = new Dictionary<int, Uri>
    {
        [13] = new("https://www.axis.com/for-developers/news/AXIS-OS-13-breaking-changes"),
    };

    /// <summary>
    /// "AXIS OS 13 has breaking changes." when a device that installs moves up to a major version with an Axis guide of
    /// its breaking changes (<see cref="BreakingChangesGuides"/>); null otherwise.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBreakingChanges))]
    public partial string? BreakingChangesNotice { get; private set; }

    public bool HasBreakingChanges => BreakingChangesNotice is not null;

    private Uri? _breakingChangesGuide;

    /// <summary>Raised with a web address the window opens in the browser.</summary>
    public event EventHandler<Uri>? OpenLinkRequested;

    [RelayCommand]
    private void OpenBreakingChanges()
    {
        if (_breakingChangesGuide is { } uri)
        {
            OpenLinkRequested?.Invoke(this, uri);
        }
    }

    /// <summary>Set after a successful start.</summary>
    public string? Result { get; private set; }

    /// <summary>The devices the task runs on, set with <see cref="Result"/>: only those that install a file.</summary>
    public IReadOnlyList<Guid> RunDevices { get; private set; } = [];

    /// <summary>Rows whose status is read when the dialog opens; the others are read when the grid shows them.</summary>
    public const int StatusPreloadCount = 50;

    /// <summary>Status queries in flight at most (each one reaches a device through the server).</summary>
    public const int MaxParallelStatusQueries = 4;

    /// <summary>Models per <c>online</c> query (the server's limit; 50 models take about 3 s).</summary>
    public const int OnlineModelsPerQuery = 50;

    private bool AnyChoice => ModelRows.Any(r => r.SelectedChoice is not null);

    // ---------------------------------------------------------------- device status

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
        RegroupIfMoved(rows);
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

        if (RegroupIfMoved([row]))
        {
            Evaluate();
            return;
        }

        EvaluateRow(row);
        row.ModelRow?.UpdateDevices();
        UpdateRowStates();
        UpdateSummary();
    }

    private async Task ReadStatusAsync(FirmwareDeviceRow row, CancellationToken ct)
    {
        row.IsReadingStatus = true;
        try
        {
            await ReadStatusInSlotAsync(row, ct).ConfigureAwait(true);
        }
        finally
        {
            row.IsReadingStatus = false;
        }
    }

    private async Task ReadStatusInSlotAsync(FirmwareDeviceRow row, CancellationToken ct)
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
            row.Apps = status.Applications;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // A failed status read only means the table shows the cached version.
        catch (Exception)
#pragma warning restore CA1031
        {
            // the cached version stays
        }
        finally
        {
            _statusSlots.Release();
        }
    }

    // ---------------------------------------------------------------- firmware rows

    private static (string Key, int? Major) KeyOf(FirmwareDeviceRow row) =>
        (OnlineFirmware.Normalize(row.Model), AxisOsVersion.TryParse(row.CurrentVersion)?.Major);

    /// <summary>
    /// One row per model and AXIS OS major of the selected devices, in one pass. Rows of earlier passes keep their object
    /// and so their choice; a new row starts on Recommended when signed in and the versions are known.
    /// </summary>
    private void BuildModelRows() => Bulk(BuildModelRowsCore);

    private void BuildModelRowsCore()
    {
        foreach (var row in _rowsByKey.Values)
        {
            row.DeviceRows.Clear();
        }

        var created = new List<FirmwareModelRow>();
        foreach (var device in Devices)
        {
            var key = KeyOf(device);
            if (!_rowsByKey.TryGetValue(key, out var row))
            {
                row = new FirmwareModelRow(device.Model.Length == 0 ? "Unknown model" : device.Model, key.Major, OnRowChanged, OnChooseFile);
                _rowsByKey[key] = row;
                if (_onlineByKey.TryGetValue(key.Key, out var online))
                {
                    row.SetOnline(online);
                }

                created.Add(row);
            }

            row.DeviceRows.Add(device);
            device.ModelRow = row;
        }

        foreach (var empty in _rowsByKey.Where(p => p.Value.DeviceRows.Count == 0).Select(p => p.Key).ToList())
        {
            _rowsByKey.Remove(empty);
        }

        var rows = _rowsByKey.Values
            .OrderBy(r => r.Model, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(r => r.Major ?? -1)
            .ToList();
        foreach (var row in rows)
        {
            row.UpdateDevices();
        }

        if (IsSignedIn && _lookupDone)
        {
            foreach (var row in created.Where(r => r.SelectedChoice is null && r.Online is { IsListed: true }))
            {
                row.SelectVersion(OnlineFirmware.Recommended(row.Online!, row.Major));
            }
        }

        ModelRows.ReplaceAll(rows);
    }

    /// <summary>A device whose status moved it to another major regroups the rows (rare); true when it did.</summary>
    private bool RegroupIfMoved(IEnumerable<FirmwareDeviceRow> rows)
    {
        if (!rows.Any(r => r.ModelRow is { } m && m.Major != KeyOf(r).Major))
        {
            return false;
        }

        BuildModelRows();
        return true;
    }

    /// <summary>
    /// Reads the versions of the selected models from axis.com through the server (query <c>online</c>,
    /// <see cref="OnlineModelsPerQuery"/> models a call); the rows fill as the parts answer. Signed in, the rows without a
    /// choice then get Recommended.
    /// </summary>
    public async Task LoadOnlineAsync(CancellationToken ct)
    {
        if (Devices.Count == 0)
        {
            _lookupDone = true;
            return;
        }

        var models = _rowsByKey.Values.Where(r => r.Key.Length > 0).Select(r => r.Model).DistinctBy(OnlineFirmware.Normalize, StringComparer.Ordinal).ToList();
        var queryDevice = (Devices.FirstOrDefault(d => d.Device.Status == DeviceStatus.Ok) ?? Devices[0]).Device.Id;
        IsLookingUp = true;
        LookupProgress = 0;
        LookupStatus = LookupText;
        OnPropertyChanged(nameof(StartBlockedReason));
        StartCommand.NotifyCanExecuteChanged();
        try
        {
            for (var done = 0; done < models.Count; done += OnlineModelsPerQuery)
            {
                var part = models.Skip(done).Take(OnlineModelsPerQuery).ToList();
                var request = JsonSerializer.Serialize(new OnlineLookupRequest { Models = part }, FirmwarePayload.JsonOptions);
                OnlineLookupResult? result;
                try
                {
                    result = OnlineLookupResult.FromJson(await _ctx.QueryAsync(queryDevice, FirmwareTaskPluginIds.OnlineQuery, request, ct).ConfigureAwait(true));
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
#pragma warning disable CA1031 // Any failure of the lookup: the rows keep Choose file and the line says so.
                catch (Exception)
#pragma warning restore CA1031
                {
                    result = null;
                }

                if (result is null || result.Unreachable)
                {
                    OnlineUnreachable = true;
                    break;
                }

                Bulk(() =>
                {
                    foreach (var model in result.Models)
                    {
                        var key = OnlineFirmware.Normalize(model.Model);
                        _onlineByKey[key] = model;
                        OnlineUnreachable |= model.Failed;
                        foreach (var row in _rowsByKey.Values.Where(r => r.Key == key))
                        {
                            row.SetOnline(model);
                        }
                    }
                });

                var read = Math.Min(models.Count, done + part.Count);
                LookupProgress = 100.0 * read / Math.Max(1, models.Count);
                LookupStatus = string.Create(CultureInfo.InvariantCulture, $"{LookupText}, {read:N0} of {models.Count:N0} models");
            }
        }
        finally
        {
            _lookupDone = true;
            IsLookingUp = false;
            LookupStatus = null;
            HasOnline = _rowsByKey.Values.Any(r => r.IsListed);
        }

        if (IsSignedIn)
        {
            ApplyRule(OnlineFirmware.Recommended, onlyUnchosen: true);
        }
        else
        {
            Evaluate();
        }
    }

    /// <summary>Every row: the newest version on its own track.</summary>
    [RelayCommand(CanExecute = nameof(CanApplyRule))]
    private void Recommended() => ApplyRule(OnlineFirmware.Recommended, onlyUnchosen: false);

    /// <summary>Every row: the newest LTS track of its model.</summary>
    [RelayCommand(CanExecute = nameof(CanApplyRule))]
    private void LatestLts() => ApplyRule((model, _) => OnlineFirmware.LatestLts(model), onlyUnchosen: false);

    /// <summary>Every row: the active track of its model, else its newest version.</summary>
    [RelayCommand(CanExecute = nameof(CanApplyRule))]
    private void LatestActive() => ApplyRule((model, _) => OnlineFirmware.LatestActive(model), onlyUnchosen: false);

    private bool CanApplyRule() => HasOnline && !IsLookingUp && !IsUploading;

    private void ApplyRule(Func<OnlineModel, int?, string?> rule, bool onlyUnchosen)
    {
        Bulk(() =>
        {
            foreach (var row in _rowsByKey.Values)
            {
                if (row.Online is { IsListed: true } online && (!onlyUnchosen || row.SelectedChoice is null))
                {
                    row.SelectVersion(rule(online, row.Major));
                }
            }
        });
    }

    [RelayCommand]
    private async Task SignInAsync()
    {
        if (SignInHandler is null)
        {
            return;
        }

        var signedIn = await SignInHandler().ConfigureAwait(true);
        SetSignedIn(_ctx.MyAxis?.IsSignedIn ?? signedIn);
    }

    private void OnMyAxisChanged(object? sender, EventArgs e) => SetSignedIn(_ctx.MyAxis?.IsSignedIn == true);

    private void SetSignedIn(bool signedIn)
    {
        if (signedIn == IsSignedIn)
        {
            return;
        }

        IsSignedIn = signedIn;
        if (signedIn && _lookupDone)
        {
            ApplyRule(OnlineFirmware.Recommended, onlyUnchosen: true);
        }
        else
        {
            Evaluate();
        }
    }

    private void OnRowChanged(FirmwareModelRow row)
    {
        row.CheckError = null;
        if (_bulk == 0 && row.OnlineVersion is { Version.Size: null } && IsSignedIn)
        {
            _ = CheckVersionAsync(row);
        }

        if (_bulk > 0)
        {
            _dirty = true;
            return;
        }

        Evaluate();
    }

    /// <summary>An older version of a track: its size and SHA-256 are read when it is chosen (query <c>onlineVersion</c>).</summary>
    private async Task CheckVersionAsync(FirmwareModelRow row)
    {
        var chosen = row.OnlineVersion;
        if (chosen is null || row.Online is not { Product: { } product, Source: { } source })
        {
            return;
        }

        row.IsChecking = true;
        string? problem = null;
        OnlineFileInfo? info = null;
        try
        {
            var request = JsonSerializer.Serialize(new OnlineVersionRequest { Source = source, Product = product, Version = chosen.Version.Version }, FirmwarePayload.JsonOptions);
            var queryDevice = row.DeviceRows.FirstOrDefault(d => d.Device.Status == DeviceStatus.Ok)?.Device.Id ?? row.DeviceRows[0].Device.Id;
            info = OnlineFileInfo.FromJson(await _ctx.QueryAsync(queryDevice, FirmwareTaskPluginIds.OnlineVersionQuery, request, _lifetime.Token).ConfigureAwait(true));
            problem = info is null ? $"AXIS OS {chosen.Version.Version} is no longer on axis.com." : null;
        }
        catch (OperationCanceledException)
        {
            return; // dialog closed
        }
#pragma warning disable CA1031 // Shown below the row's field.
        catch (Exception)
#pragma warning restore CA1031
        {
            problem = OnlineFirmware.UnreachableText;
        }
        finally
        {
            row.IsChecking = false;
        }

        if (!ReferenceEquals(row.OnlineVersion, chosen) && row.OnlineVersion?.Version.Version != chosen.Version.Version)
        {
            return; // the row changed meanwhile
        }

        row.CheckedVersion = info;
        row.CheckError = problem;
        Evaluate();
    }

    private void OnChooseFile(FirmwareModelRow row) => _ = ChooseFileForAsync(row);

    /// <summary>"Choose file" of a row: one local .bin for that row, checked as today.</summary>
    public async Task ChooseFileForAsync(FirmwareModelRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (IsUploading)
        {
            return;
        }

        var paths = await _files.PickAsync().ConfigureAwait(true);
        if (paths.Count == 0)
        {
            return;
        }

        var path = paths[0];
        try
        {
            var size = await _files.GetSizeAsync(path, _lifetime.Token).ConfigureAwait(true);
            row.SelectFile(new FileChoice(path, FirmwareImageInspector.Inspect(path, size)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            row.CheckError = "The file could not be read: " + ex.Message;
            Evaluate();
        }
    }

    /// <summary>Uses a file for every model without the picker (tests, drag and drop).</summary>
    public Task SelectFileAsync(string path, CancellationToken ct) => SelectFilesAsync([path], ct);

    /// <summary>
    /// Uses these files (one per model): each row takes the file for its model, else a file whose model the name does not
    /// tell; two files for one model are refused at that row.
    /// </summary>
    public async Task SelectFilesAsync(IReadOnlyList<string> paths, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count == 0)
        {
            return;
        }

        Error = null;
        _selectError = null;
        var chosen = new List<FileChoice>(paths.Count);
        foreach (var path in paths)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            try
            {
                var size = await _files.GetSizeAsync(path, ct).ConfigureAwait(true);
                chosen.Add(new FileChoice(path, FirmwareImageInspector.Inspect(path, size)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _selectError = (paths.Count == 1 ? string.Empty : FirmwareImageInspector.FileNameOf(path) + ": ") + "The file could not be read: " + ex.Message;
                Evaluate();
                return;
            }
        }

        Bulk(() =>
        {
            foreach (var row in _rowsByKey.Values)
            {
                var matching = chosen.Where(c => c.Image.Product is { } p && FirmwareCompatibility.ProductMatches(p, row.Model)).ToList();
                if ((matching.FirstOrDefault() ?? chosen.FirstOrDefault(c => c.Image.Product is null)) is { } file)
                {
                    row.SelectFile(file);
                    row.DuplicateFile = matching.Count > 1;
                }
                else
                {
                    row.ClearFile(); // these files replace the files chosen before
                }
            }
        });
    }

    // ---------------------------------------------------------------- start

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        var installing = Devices.Where(d => d.WillInstall).ToList();
        if (installing.Count == 0)
        {
            return;
        }

        // Only the files a device installs, in the order chosen; only the versions a device installs.
        var used = installing.Where(d => d.OnlineIndex < 0 && d.FileIndex >= 0).Select(d => d.FileIndex).Distinct().Order().ToList();
        var online = installing.Where(d => d.OnlineIndex >= 0).Select(d => d.OnlineIndex).Distinct().Order().Select(i => _online[i]).ToList();

        Error = null;
        IsUploading = true;
        UploadProgress = 0;
        UploadText = used.Count > 0 ? "Uploading firmware to the server" : null;
        var uploadCts = new CancellationTokenSource();
        _uploadCts = uploadCts;
        try
        {
            var finished = false;
            var current = 0;
            var progress = new Progress<double>(p =>
            {
                if (finished)
                {
                    return;
                }

                // The SDK does not specify the unit: accept a fraction (0..1) or a percentage. Several files: one bar.
                var percent = Math.Clamp(p <= 1.0 ? p * 100 : p, 0, 100);
                UploadProgress = Math.Clamp(((current * 100) + percent) / Math.Max(1, used.Count), 0, 100);
                UploadText = string.Create(CultureInfo.InvariantCulture, $"Uploading firmware to the server ({UploadProgress:0} %)");
            });
            var uploaded = new List<FirmwareFile>(used.Count);
            for (current = 0; current < used.Count; current++)
            {
                var choice = _fileChoices[used[current]];
                var file = await _ctx.UploadAsync(choice.Path, progress, uploadCts.Token).ConfigureAwait(true);
                uploaded.Add(new FirmwareFile(file.Id, choice.Image.FileName));
            }

            RunDevices = [.. installing.Select(d => d.Device.Id)];
            Result = new FirmwarePayload
            {
                FileId = uploaded.Count > 0 ? uploaded[0].FileId : null,
                FileName = uploaded.Count > 0 ? uploaded[0].FileName : null,
                Files = uploaded.Count > 1 ? uploaded : null,
                Online = online.Count > 0 ? online : null,
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

    public bool CanStart() => StartBlockedReason is null && !IsUploading;

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
        if (_ctx.MyAxis is { } myAxis)
        {
            myAxis.Changed -= OnMyAxisChanged;
        }

        _uploadCts?.Cancel();
        _uploadCts?.Dispose();
        _uploadCts = null;
        if (!_lifetime.IsCancellationRequested)
        {
            _lifetime.Cancel();
        }

        _lifetime.Dispose();
    }

    // ---------------------------------------------------------------- evaluation

    /// <summary>Changes many rows with one evaluation at the end.</summary>
    private void Bulk(Action change)
    {
        _bulk++;
        try
        {
            change();
        }
        finally
        {
            _bulk--;
        }

        if (_bulk == 0 && _dirty)
        {
            _dirty = false;
            Evaluate();
        }
    }

    /// <summary>One pass over all rows (thousands of devices: no nested loops, one summary).</summary>
    private void Evaluate()
    {
        if (_bulk > 0)
        {
            _dirty = true;
            return;
        }

        // The chosen files (each once) and the chosen versions of the rows (only with a sign-in, only usable ones).
        _fileChoices = [.. ModelRows.Select(r => r.ChosenFile).OfType<FileChoice>().DistinctBy(f => f.Path, StringComparer.Ordinal)];
        _images = [.. _fileChoices.Select(f => f.Image)];
        _online = [];
        var onlineImages = new List<FirmwareImageInfo>();
        foreach (var row in ModelRows)
        {
            if (IsSignedIn && row.OnlineVersion is { } v && row.Online is { Product: { } product, Source: { } source } && row.CheckError is null)
            {
                var entry = new FirmwareOnline(source, product, v.Version.Version, row.Major);
                _online.Add(entry);
                onlineImages.Add(OnlineFirmware.ImageOf(entry, v.Version.Size ?? row.CheckedVersion?.Size ?? 0));
            }
        }

        _onlineImages = [.. onlineImages];

        // FindOnline by an index: the entry for (model, major), else the one for (model, unknown version); first wins.
        _onlineIndex = new Dictionary<(string, int?), int>();
        for (var i = 0; i < _online.Count; i++)
        {
            _onlineIndex.TryAdd((OnlineFirmware.Normalize(_online[i].Product), _online[i].FromMajor), i);
        }

        foreach (var row in Devices)
        {
            EvaluateRow(row);
        }

        UpdateRowStates();
        UpdateSummary();
    }

    private void EvaluateRow(FirmwareDeviceRow row)
    {
        row.OnlineIndex = -1;
        row.FileIndex = -1;
        if (row.ModelRow?.OnlineVersion is not null && !IsSignedIn)
        {
            row.Check = new FirmwareCheck(FirmwareVerdict.SignInNeeded, OnlineFirmware.SignInText);
        }
        else if (_images.Length == 0 && _online.Count == 0)
        {
            row.Check = null;
        }
        else if (!row.Supported)
        {
            row.Check = new FirmwareCheck(FirmwareVerdict.InvalidFile, "This device's firmware cannot be updated from OADM.");
        }
        else
        {
            var key = row.ModelRow?.Key ?? OnlineFirmware.Normalize(row.Model);
            var index = _onlineIndex.Count == 0 ? -1
                : _onlineIndex.TryGetValue((key, AxisOsVersion.TryParse(row.CurrentVersion)?.Major), out var exact) ? exact
                : _onlineIndex.TryGetValue((key, null), out var unknown) ? unknown
                : -1;
            var (online, file, check) = FirmwareCompatibility.ChooseWithOnline(row.Model, row.CurrentVersion, _online, _onlineImages, _images, SelectedMode.Mode, AllowDowngrade, row.Apps,
                matchByProduct: row.ModelRow?.SelectedChoice is null, onlineIndex: index);
            row.OnlineIndex = online;
            row.FileIndex = file;
            row.Check = check;
        }
    }

    /// <summary>Per row: the problem of its choice, the sign-in line, "Up to date"; then the first problem for Upgrade.</summary>
    private void UpdateRowStates()
    {
        var byProduct = _fileChoices.Where(f => f.Image.Product is not null && !f.Image.IsRejected)
            .GroupBy(f => FirmwareCompatibility.NormalizeProduct(f.Image.Product!))
            .Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        string? first = _selectError;
        foreach (var row in ModelRows)
        {
            var file = row.ChosenFile;
            var product = file?.Image.Product is { } p ? FirmwareCompatibility.NormalizeProduct(p) : null;
            row.Error = file?.Image.RejectReason
                ?? (row.DuplicateFile || (product is not null && byProduct.Contains(product))
                    ? $"Two files are for {(product ?? FirmwareCompatibility.NormalizeProduct(row.Model))}. Choose one file per model."
                    : null)
                ?? row.CheckError;
            row.Notice = row.OnlineVersion is not null && !IsSignedIn ? OnlineFirmware.SignInText : null;
            var upToDate = row.SelectedChoice is not null && row.DeviceRows.Count > 0 && row.DeviceRows.All(d => d.Check?.IsNoOp == true);
            row.Status = upToDate ? "Up to date" : null;
            first ??= row.Error;
        }

        FileError = first;
    }

    private void UpdateSummary()
    {
        OnPropertyChanged(nameof(PathWarning));
        OnPropertyChanged(nameof(HasPathWarning));
        if (!AnyChoice)
        {
            Summary = NothingChosenText;
            _breakingChangesGuide = null;
            BreakingChangesNotice = null;
        }
        else
        {
            int install = 0, upToDate = 0;
            var breakingMajor = 0;
            foreach (var row in Devices)
            {
                if (row.WillInstall || row.Check?.Verdict == FirmwareVerdict.AppsBlock)
                {
                    install += row.WillInstall ? 1 : 0;
                    breakingMajor = Math.Max(breakingMajor, BreakingMajor(row));
                }
                else if (row.Check?.IsNoOp == true)
                {
                    upToDate++;
                }
            }

            var skipped = Devices.Count - install - upToDate;
            Summary = string.Create(CultureInfo.InvariantCulture, $"{install:N0} of {Devices.Count:N0} {(Devices.Count == 1 ? "device" : "devices")} will be updated, {upToDate:N0} already up to date, {skipped:N0} not possible.");
            _breakingChangesGuide = breakingMajor > 0 ? BreakingChangesGuides[breakingMajor] : null;
            BreakingChangesNotice = breakingMajor > 0 ? string.Create(CultureInfo.InvariantCulture, $"AXIS OS {breakingMajor} has breaking changes.") : null;
        }

        StartCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(StartBlockedReason));
    }

    /// <summary>The newest major version with a breaking changes guide this row moves up to, else 0.</summary>
    private int BreakingMajor(FirmwareDeviceRow row)
    {
        var image = row.OnlineIndex >= 0 && row.OnlineIndex < _onlineImages.Length ? _onlineImages[row.OnlineIndex]
            : row.FileIndex >= 0 && row.FileIndex < _images.Length ? _images[row.FileIndex]
            : null;
        if (image?.Version is not { } target || AxisOsVersion.TryParse(row.CurrentVersion) is not { } current || target.Major <= current.Major)
        {
            return 0;
        }

        var major = 0;
        foreach (var m in BreakingChangesGuides.Keys)
        {
            if (m > current.Major && m <= target.Major && m > major)
            {
                major = m;
            }
        }

        return major;
    }

    partial void OnIsUploadingChanged(bool value)
    {
        OnPropertyChanged(nameof(StartBlockedReason));
        RecommendedCommand.NotifyCanExecuteChanged();
        LatestLtsCommand.NotifyCanExecuteChanged();
        LatestActiveCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsLookingUpChanged(bool value)
    {
        OnPropertyChanged(nameof(StartBlockedReason));
        StartCommand.NotifyCanExecuteChanged();
    }
}

/// <summary>Ids shared with the server part (kept here so the client does not reference the server assembly).</summary>
public static class FirmwareTaskPluginIds
{
    public const string PluginId = "oadm.firmware";
    public const string StatusQuery = "status";
    public const string OnlineQuery = "online";
    public const string OnlineVersionQuery = "onlineVersion";
}
