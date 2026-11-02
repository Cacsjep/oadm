using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Controls;
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

/// <summary>
/// "Upgrade firmware" dialog: one table of the selected devices, each with the version from axis.com or the chosen file it
/// installs (README "Dialog", "Firmware online"), the preview per device and the options; uploads the chosen files to the
/// server and returns the task payload.
/// </summary>
public sealed partial class FirmwareDialogViewModel : ObservableObject, IDisposable
{
    public const string NothingChosenText = "Choose a version or a file.";
    public const string LookupText = "Reading the versions on axis.com";

    private readonly ITaskDialogContext _ctx;
    private readonly IFirmwareFileSource _files;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<string, FirmwareModelChoices> _models = new(StringComparer.Ordinal);

    /// <summary>Older versions read with <c>onlineVersion</c> ("MPQT/P3265-V/11.11.205"): the file, or null when gone.</summary>
    private readonly Dictionary<string, OnlineFileInfo?> _checked = new(StringComparer.Ordinal);
    private readonly HashSet<string> _checking = new(StringComparer.Ordinal);

    // Never disposed: status reads still in flight release it after the dialog closed (it has no wait handle).
    private readonly SemaphoreSlim _statusSlots = new(MaxParallelStatusQueries);
    private CancellationTokenSource? _uploadCts;
    private bool _lookupDone;
    private int _chosenCount;

    public FirmwareDialogViewModel(ITaskDialogContext ctx, IReadOnlyList<IDeviceInfo> devices, IFirmwareFileSource files)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(files);
        _ctx = ctx;
        _files = files;
        Devices = new ObservableCollection<FirmwareDeviceRow>(devices.Select(d => new FirmwareDeviceRow(d)));
        foreach (var row in Devices)
        {
            var key = OnlineFirmware.Normalize(row.Model);
            if (!_models.TryGetValue(key, out var model))
            {
                model = new FirmwareModelChoices(key);
                _models[key] = model;
            }

            row.ModelChoices = model;
            row.Chosen = OnChosen;
        }

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

        Evaluate();
    }

    /// <summary>The role may reset devices with the upgrade ("task:oadm.firmware:factoryDefault").</summary>
    public bool MayFactoryDefault { get; }

    /// <summary>The role may install older firmware ("task:oadm.firmware:downgrade").</summary>
    public bool MayDowngrade { get; }

    /// <summary>Raised with the payload JSON (or null when cancelled) when the dialog should close.</summary>
    public event EventHandler<string?>? CloseRequested;

    public ObservableCollection<FirmwareDeviceRow> Devices { get; }

    /// <summary>The choices of a model ("P3265-V"), shared by its devices.</summary>
    public FirmwareModelChoices? ModelChoices(string model) => _models.GetValueOrDefault(OnlineFirmware.Normalize(model));

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

    /// <summary>axis.com did not answer: "axis.com could not be reached." above the table, the devices keep Choose file.</summary>
    [ObservableProperty]
    public partial bool OnlineUnreachable { get; private set; }

    /// <summary>Some model is listed on axis.com (the buttons have something to set).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RecommendedCommand), nameof(LatestLtsCommand), nameof(LatestActiveCommand))]
    public partial bool HasOnline { get; private set; }

    /// <summary>
    /// A problem of the files given to <see cref="SelectFilesAsync"/> (unreadable, not an AXIS OS image, two for one model),
    /// shown below the table; it blocks Upgrade. A file chosen for one device is judged in its Result instead.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFileError))]
    public partial string? FileError { get; private set; }

    public bool HasFileError => FileError is not null;

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
        : IsBusy ? BusyText + "."
        : IsLookingUp ? LookupText + "."
        : FileError is not null ? FileError
        : _checking.Count > 0 ? "Checking the version on axis.com."
        : _chosenCount == 0 ? NothingChosenText
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
        await RefreshAsync().ConfigureAwait(true);
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

    // ---------------------------------------------------------------- versions from axis.com

    /// <summary>
    /// Reads the versions of the selected models from axis.com through the server (query <c>online</c>,
    /// <see cref="OnlineModelsPerQuery"/> models a call); the select fields fill as the parts answer. Signed in, the devices
    /// without a choice then get Recommended. Any failure shows "axis.com could not be reached.", never nothing.
    /// </summary>
    public async Task LoadOnlineAsync(CancellationToken ct)
    {
        if (Devices.Count == 0)
        {
            _lookupDone = true;
            return;
        }

        // One device of the selection for the host (the query reads only axis.com, never that device).
        var queryDevice = (Devices.FirstOrDefault(d => d.Device.Status == DeviceStatus.Ok) ?? Devices[0]).Device.Id;
        var models = Devices.Select(d => d.Model).Where(m => OnlineFirmware.Normalize(m).Length > 0)
            .DistinctBy(OnlineFirmware.Normalize, StringComparer.Ordinal).ToList();
        IsLookingUp = true;
        LookupProgress = 0;
        LookupStatus = LookupText;
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
#pragma warning disable CA1031 // Any failure of the lookup: the devices keep Choose file and the line says so.
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

                foreach (var model in result.Models)
                {
                    OnlineUnreachable |= model.Failed;
                    if (model.IsListed && _models.TryGetValue(OnlineFirmware.Normalize(model.Model), out var choices))
                    {
                        choices.SetOnline(model);
                    }
                }

                var read = Math.Min(models.Count, done + part.Count);
                LookupProgress = 100.0 * read / Math.Max(1, models.Count);
                LookupStatus = string.Create(CultureInfo.InvariantCulture, $"{LookupText}, {read:N0} of {models.Count:N0} models");
            }
        }
#pragma warning disable CA1031 // Never a silent dialog: any failure of the lookup shows "axis.com could not be reached."
        catch (Exception) when (!ct.IsCancellationRequested)
#pragma warning restore CA1031
        {
            OnlineUnreachable = true;
        }
        finally
        {
            _lookupDone = true;
            IsLookingUp = false;
            LookupStatus = null;
            HasOnline = _models.Values.Any(m => m.IsListed);
        }

        await (IsSignedIn ? ApplyRuleAsync(OnlineFirmware.Recommended, onlyUnchosen: true) : RefreshAsync()).ConfigureAwait(true);
    }

    /// <summary>Every device: the newest version on its own track.</summary>
    [RelayCommand(CanExecute = nameof(CanApplyRule))]
    private Task RecommendedAsync() => ApplyRuleAsync(OnlineFirmware.Recommended, onlyUnchosen: false);

    /// <summary>Every device: the newest LTS track of its model.</summary>
    [RelayCommand(CanExecute = nameof(CanApplyRule))]
    private Task LatestLtsAsync() => ApplyRuleAsync((model, _) => OnlineFirmware.LatestLts(model), onlyUnchosen: false);

    /// <summary>Every device: the active track of its model, else its newest version.</summary>
    [RelayCommand(CanExecute = nameof(CanApplyRule))]
    private Task LatestActiveAsync() => ApplyRuleAsync((model, _) => OnlineFirmware.LatestActive(model), onlyUnchosen: false);

    private bool CanApplyRule() => HasOnline && !IsLookingUp && !IsUploading && !IsBusy;

    /// <summary>
    /// Sets the rule's version on every device: one pass, the rule computed once per model and AXIS OS major. From
    /// <see cref="BackgroundFrom"/> devices it runs off the UI thread behind the busy ring (HARD RULE loading feedback: the
    /// three buttons take about 0.6 s each at 100,000 devices); only setting the results touches the rows on the UI thread.
    /// </summary>
    private Task ApplyRuleAsync(Func<OnlineModel, int?, string?> rule, bool onlyUnchosen)
    {
        if (Devices.Count < BackgroundFrom)
        {
            SetPicks(Picks([.. Devices], rule, onlyUnchosen));
            Evaluate();
            return Task.CompletedTask;
        }

        return RunBusyAsync(SettingText, async () =>
        {
            FirmwareDeviceRow[] rows = [.. Devices];
            SetPicks(await Task.Run(() => Picks(rows, rule, onlyUnchosen)).ConfigureAwait(true));
            await JudgeAllAsync().ConfigureAwait(true);
        });

        void SetPicks(GroupedChoice?[] picks)
        {
            for (var i = 0; i < picks.Length && i < Devices.Count; i++)
            {
                if (picks[i] is { } choice)
                {
                    Devices[i].SetChoice(choice);
                }
            }
        }
    }

    /// <summary>The rule's choice per device (null: unchanged); reads the rows only.</summary>
    private static GroupedChoice?[] Picks(FirmwareDeviceRow[] rows, Func<OnlineModel, int?, string?> rule, bool onlyUnchosen)
    {
        var picks = new GroupedChoice?[rows.Length];
        var perTrack = new Dictionary<(string, int?), GroupedChoice?>();
        for (var i = 0; i < rows.Length; i++)
        {
            var row = rows[i];
            if (row.ModelChoices is not { Online: { IsListed: true } online } model || (onlyUnchosen && row.SelectedChoice is not null))
            {
                continue;
            }

            var key = (model.Key, AxisOsVersion.TryParse(row.CurrentVersion)?.Major);
            if (!perTrack.TryGetValue(key, out var choice))
            {
                choice = model.Version(rule(online, key.Major));
                perTrack[key] = choice;
            }

            picks[i] = choice;
        }

        return picks;
    }

    public const string SettingText = "Setting the versions";
    public const string CheckingText = "Checking the devices";

    /// <summary>From this many devices, judging all of them runs off the UI thread behind the busy ring.</summary>
    public int BackgroundFrom { get; set; } = 2_000;

    /// <summary>The time the ring gets to paint before the work starts (tests: zero).</summary>
    public TimeSpan BusyPaintDelay { get; set; } = TimeSpan.FromMilliseconds(30);

    /// <summary>True while all devices are judged or set: the ring over the table, the buttons and Upgrade wait.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RecommendedCommand), nameof(LatestLtsCommand), nameof(LatestActiveCommand), nameof(StartCommand))]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    public partial string? BusyText { get; private set; }

    private readonly SemaphoreSlim _busyGate = new(1, 1);

    /// <summary>Shows the ring (one frame to paint), then runs <paramref name="work"/>; one run at a time.</summary>
    private async Task RunBusyAsync(string text, Func<Task> work)
    {
        await _busyGate.WaitAsync().ConfigureAwait(true);
        BusyText = text;
        IsBusy = true;
        OnPropertyChanged(nameof(StartBlockedReason));
        try
        {
            if (BusyPaintDelay > TimeSpan.Zero)
            {
                await Task.Delay(BusyPaintDelay).ConfigureAwait(true);
            }
            else
            {
                await Task.Yield();
            }

            await work().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
            BusyText = null;
            _busyGate.Release();
            OnPropertyChanged(nameof(StartBlockedReason));
        }
    }

    /// <summary>Judges every device again: at once for a small selection, else off the UI thread behind the ring.</summary>
    private Task RefreshAsync()
    {
        if (Devices.Count < BackgroundFrom)
        {
            Evaluate();
            return Task.CompletedTask;
        }

        return RunBusyAsync(CheckingText, JudgeAllAsync);
    }

    /// <summary>The decision table for every device on the thread pool (pure: reads the rows), then the results on the UI thread.</summary>
    private async Task JudgeAllAsync()
    {
        FirmwareDeviceRow[] rows = [.. Devices];
        var chosen = 0;
        foreach (var row in rows)
        {
            chosen += row.SelectedChoice is null ? 0 : 1;
        }

        var state = new JudgeState(chosen, IsSignedIn, SelectedMode.Mode, AllowDowngrade, new Dictionary<string, OnlineFileInfo?>(_checked, StringComparer.Ordinal));
        var results = await Task.Run(() => rows.Select(r => Judge(r, state)).ToArray()).ConfigureAwait(true);
        _chosenCount = chosen;
        for (var i = 0; i < rows.Length; i++)
        {
            rows[i].Image = results[i].Image;
            rows[i].Check = results[i].Check;
        }

        FileError = _selectError;
        UpdateSummary();
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
        _ = signedIn && _lookupDone ? ApplyRuleAsync(OnlineFirmware.Recommended, onlyUnchosen: true) : RefreshAsync();
    }

    /// <summary>The user chose in one device's field: only that device changes.</summary>
    private void OnChosen(FirmwareDeviceRow row, GroupedChoice? choice)
    {
        if (choice?.Value is ChooseFileAction)
        {
            _ = ChooseFileForAsync(row);
            return;
        }

        if (IsSignedIn && row.OnlineVersion is { Version.Size: null } older && row.ModelChoices?.Online is { } online)
        {
            _ = CheckVersionAsync(row, online, older);
        }

        EvaluateOne(row);
    }

    /// <summary>
    /// One device changed: only it is judged and the summary counted (100,000 devices: no pass of the decision table over
    /// all of them), except when it is the first or the last choice ("No file" of the others changes).
    /// </summary>
    private void EvaluateOne(FirmwareDeviceRow row)
    {
        var chosen = 0;
        foreach (var d in Devices)
        {
            chosen += d.SelectedChoice is null ? 0 : 1;
        }

        if ((chosen == 0) != (_chosenCount == 0))
        {
            _ = RefreshAsync();
            return;
        }

        _chosenCount = chosen;
        EvaluateRow(row);
        UpdateSummary();
    }

    private static string CheckKey(OnlineModel online, string version) => online.Source + "/" + online.Product + "/" + version;

    /// <summary>An older version of a track: its size and SHA-256 are read once when chosen (query <c>onlineVersion</c>).</summary>
    private async Task CheckVersionAsync(FirmwareDeviceRow row, OnlineModel online, OnlineChoice chosen)
    {
        var key = CheckKey(online, chosen.Version.Version);
        if (_checked.ContainsKey(key) || !_checking.Add(key) || online.Product is null || online.Source is null)
        {
            return;
        }

        OnlineFileInfo? info = null;
        var gone = false;
        try
        {
            var request = JsonSerializer.Serialize(new OnlineVersionRequest { Source = online.Source, Product = online.Product, Version = chosen.Version.Version }, FirmwarePayload.JsonOptions);
            info = OnlineFileInfo.FromJson(await _ctx.QueryAsync(row.Device.Id, FirmwareTaskPluginIds.OnlineVersionQuery, request, _lifetime.Token).ConfigureAwait(true));
            gone = info is null;
        }
        catch (OperationCanceledException)
        {
            return; // dialog closed
        }
#pragma warning disable CA1031 // Judged in the Result of the devices with that version.
        catch (Exception)
#pragma warning restore CA1031
        {
            OnlineUnreachable = true;
        }
        finally
        {
            _checking.Remove(key);
        }

        if (info is not null || gone)
        {
            _checked[key] = info;
        }

        await RefreshAsync().ConfigureAwait(true);
    }

    /// <summary>"Choose file" of a device: one local .bin for that device; the same file can then be chosen for others.</summary>
    public async Task ChooseFileForAsync(FirmwareDeviceRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (IsUploading || row.ModelChoices is not { } model)
        {
            return;
        }

        var paths = await _files.PickAsync().ConfigureAwait(true);
        if (paths.Count == 0)
        {
            return;
        }

        try
        {
            var size = await _files.GetSizeAsync(paths[0], _lifetime.Token).ConfigureAwait(true);
            row.SetChoice(model.AddFile(new FileChoice(paths[0], FirmwareImageInspector.Inspect(paths[0], size))));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            row.SetChoice(null);
            row.Check = new FirmwareCheck(FirmwareVerdict.InvalidFile, "The file could not be read: " + ex.Message);
            return;
        }

        EvaluateOne(row);
    }

    /// <summary>Uses a file for every device of its model without the picker (tests, drag and drop).</summary>
    public Task SelectFileAsync(string path, CancellationToken ct) => SelectFilesAsync([path], ct);

    /// <summary>
    /// Uses these files (one per model): each device takes the file for its model, else a file whose model the name does
    /// not tell; the files chosen before are replaced. Two files for one model or a refused file block Upgrade.
    /// </summary>
    public async Task SelectFilesAsync(IReadOnlyList<string> paths, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count == 0)
        {
            return;
        }

        Error = null;
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

        var rejected = chosen.FirstOrDefault(c => c.Image.IsRejected);
        var duplicate = chosen.Where(c => c.Image.Product is not null).GroupBy(c => FirmwareCompatibility.NormalizeProduct(c.Image.Product!))
            .FirstOrDefault(g => g.Count() > 1)?.Key;
        _selectError = rejected is not null ? (paths.Count == 1 ? string.Empty : rejected.Image.FileName + ": ") + rejected.Image.RejectReason
            : duplicate is not null ? $"Two files are for {duplicate}. Choose one file per model."
            : null;

        // Per model once, then one pass over the devices.
        var forModel = new Dictionary<string, GroupedChoice?>(StringComparer.Ordinal);
        var unnamed = chosen.FirstOrDefault(c => c.Image.Product is null);
        foreach (var row in Devices)
        {
            if (row.ModelChoices is not { } model)
            {
                continue;
            }

            if (!forModel.TryGetValue(model.Key, out var choice))
            {
                var file = chosen.FirstOrDefault(c => c.Image.Product is { } p && FirmwareCompatibility.ProductMatches(p, row.Model)) ?? unnamed;
                choice = file is null ? null : model.AddFile(file);
                forModel[model.Key] = choice;
            }

            if (choice is not null)
            {
                row.SetChoice(choice);
            }
            else if (row.ChosenFile is not null)
            {
                row.SetChoice(null); // these files replace the files chosen before
            }
        }

        await RefreshAsync().ConfigureAwait(true);
    }

    private string? _selectError;

    // ---------------------------------------------------------------- start

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        var installing = Devices.Where(d => d.WillInstall).ToList();
        if (installing.Count == 0)
        {
            return;
        }

        var (files, online) = PayloadParts(installing);
        Error = null;
        IsUploading = true;
        UploadProgress = 0;
        UploadText = files.Count > 0 ? "Uploading firmware to the server" : null;
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
                UploadProgress = Math.Clamp(((current * 100) + percent) / Math.Max(1, files.Count), 0, 100);
                UploadText = string.Create(CultureInfo.InvariantCulture, $"Uploading firmware to the server ({UploadProgress:0} %)");
            });

            // Each local file once, also when several devices install it.
            var uploaded = new List<FirmwareFile>(files.Count);
            for (current = 0; current < files.Count; current++)
            {
                var (file, devices) = files[current];
                var info = await _ctx.UploadAsync(file.Path, progress, uploadCts.Token).ConfigureAwait(true);
                uploaded.Add(new FirmwareFile(info.Id, file.Image.FileName, devices));
            }

            RunDevices = [.. installing.Select(d => d.Device.Id)];
            Result = new FirmwarePayload
            {
                FileId = uploaded.Count > 0 ? uploaded[0].FileId : null,
                FileName = uploaded.Count > 0 ? uploaded[0].FileName : null,
                Files = uploaded.Count > 1 || uploaded.Any(u => u.Devices is not null) ? uploaded : null,
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

    /// <summary>
    /// The payload of the devices that install (README "Payload"), small in the usual case: per product and AXIS OS major
    /// the version most of its devices install names no devices (the server takes it by product and major); other versions
    /// list their devices. A file names no devices when it is for one product and every installing device of that product
    /// installs it (as before); else it lists its devices. Listed entries win on the server, so the result is per device.
    /// </summary>
    internal static (List<(FileChoice File, IReadOnlyList<Guid>? Devices)> Files, List<FirmwareOnline> Online) PayloadParts(IReadOnlyList<FirmwareDeviceRow> installing)
    {
        var online = new List<FirmwareOnline>();
        foreach (var group in installing.Where(d => d.OnlineVersion is not null && d.ModelChoices?.Online is { Product: not null, Source: not null })
            .GroupBy(d => (Model: d.ModelChoices!, Major: AxisOsVersion.TryParse(d.CurrentVersion)?.Major)))
        {
            var source = group.Key.Model.Online!.Source!;
            var product = group.Key.Model.Online!.Product!;
            var byVersion = group.GroupBy(d => d.OnlineVersion!.Version.Version).OrderByDescending(g => g.Count()).ToList();
            online.Add(new FirmwareOnline(source, product, byVersion[0].Key, group.Key.Major));
            online.AddRange(byVersion.Skip(1).Select(g => new FirmwareOnline(source, product, g.Key, null, [.. g.Select(d => d.Device.Id)])));
        }

        var installingByProduct = installing.GroupBy(d => FirmwareCompatibility.NormalizeProduct(d.Model)).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var files = new List<(FileChoice, IReadOnlyList<Guid>?)>();
        foreach (var group in installing.Where(d => d.ChosenFile is not null).GroupBy(d => d.ChosenFile!.Path, StringComparer.Ordinal))
        {
            var file = group.First().ChosenFile!;
            var product = file.Image.Product is { } p ? FirmwareCompatibility.NormalizeProduct(p) : null;
            var byProduct = product is not null
                && group.All(d => FirmwareCompatibility.NormalizeProduct(d.Model) == product)
                && installingByProduct.GetValueOrDefault(product) == group.Count();
            files.Add((file, byProduct ? null : [.. group.Select(d => d.Device.Id)]));
        }

        return (files, online);
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
        _ = RefreshAsync();
    }

    partial void OnAllowDowngradeChanged(bool value) => _ = RefreshAsync();

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

    /// <summary>One pass over all devices (100,000 devices: no nested loops, one summary).</summary>
    private void Evaluate()
    {
        _chosenCount = 0;
        foreach (var row in Devices)
        {
            _chosenCount += row.SelectedChoice is null ? 0 : 1;
        }

        foreach (var row in Devices)
        {
            EvaluateRow(row);
        }

        FileError = _selectError;
        UpdateSummary();
    }

    private void EvaluateRow(FirmwareDeviceRow row)
    {
        var (check, image) = Judge(row, new JudgeState(_chosenCount, IsSignedIn, SelectedMode.Mode, AllowDowngrade, _checked));
        row.Image = image;
        row.Check = check;
    }

    /// <summary>What <see cref="Judge"/> needs besides the row; a copy, so it can run off the UI thread.</summary>
    private sealed record JudgeState(int Chosen, bool SignedIn, FactoryDefaultMode Mode, bool AllowDowngrade, IReadOnlyDictionary<string, OnlineFileInfo?> Checked);

    /// <summary>
    /// The device's own choice through the decision table; a device without one says "No file" once others have one. Pure:
    /// reads the row and <paramref name="state"/>, changes nothing.
    /// </summary>
    private static (FirmwareCheck? Check, FirmwareImageInfo? Image) Judge(FirmwareDeviceRow row, JudgeState state)
    {
        var choice = row.SelectedChoice?.Value;
        if (choice is null)
        {
            return (state.Chosen == 0 ? null : new FirmwareCheck(FirmwareVerdict.NoFileForModel, FirmwareCompatibility.NoFileForModel), null);
        }

        if (!row.Supported)
        {
            return (new FirmwareCheck(FirmwareVerdict.InvalidFile, "This device's firmware cannot be updated from OADM."), null);
        }

        if (choice is FileChoice file)
        {
            return (FirmwareCompatibility.Evaluate(row.Model, row.CurrentVersion, file.Image, state.Mode, state.AllowDowngrade, row.Apps), file.Image);
        }

        var version = (OnlineChoice)choice;
        if (!state.SignedIn)
        {
            return (new FirmwareCheck(FirmwareVerdict.SignInNeeded, OnlineFirmware.SignInText), null);
        }

        var online = row.ModelChoices!.Online!;
        long? size = version.Version.Size;
        if (size is null && state.Checked.TryGetValue(CheckKey(online, version.Version.Version), out var info))
        {
            if (info is null)
            {
                return (new FirmwareCheck(FirmwareVerdict.NotListed, $"AXIS OS {version.Version.Version} is no longer on axis.com."), null);
            }

            size = info.Size;
        }

        var entry = new FirmwareOnline(online.Source ?? OnlineFirmware.Mpqt, online.Product ?? row.Model, version.Version.Version, null);
        var image = OnlineFirmware.ImageOf(entry, size ?? 0);
        return (FirmwareCompatibility.EvaluateOnline(entry, image, row.Model, row.CurrentVersion, state.Mode, state.AllowDowngrade, row.Apps), image);
    }

    private void UpdateSummary()
    {
        OnPropertyChanged(nameof(PathWarning));
        OnPropertyChanged(nameof(HasPathWarning));
        if (_chosenCount == 0)
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

    /// <summary>The newest major version with a breaking changes guide this device moves up to, else 0.</summary>
    private static int BreakingMajor(FirmwareDeviceRow row)
    {
        if (row.Image?.Version is not { } target || AxisOsVersion.TryParse(row.CurrentVersion) is not { } current || target.Major <= current.Major)
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
