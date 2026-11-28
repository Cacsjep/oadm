using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Collections;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Devices;

namespace Oadm.Plugins.LicensePlates.Client;

/// <summary>One of ALPV's image settings in the Image settings tooltip: the camera's value and the recommended one.</summary>
public sealed record ImageValueRow(string Name, string Current, string Recommended, bool Matches);

/// <summary>A camera of the plugin in the Cameras grid, updated in place.</summary>
public sealed partial class CameraRowViewModel : ObservableObject
{
    internal CameraRowViewModel(Guid id) => DeviceId = id;

    public Guid DeviceId { get; }

    public CameraRow Row { get; private set; } = new();

    [ObservableProperty] public partial string Camera { get; private set; } = string.Empty;

    [ObservableProperty] public partial string Address { get; private set; } = string.Empty;

    [ObservableProperty] public partial string Site { get; private set; } = string.Empty;

    [ObservableProperty] public partial string AlpvVersion { get; private set; } = string.Empty;

    [ObservableProperty] public partial string StatusText { get; private set; } = string.Empty;

    [ObservableProperty] public partial bool IsOk { get; private set; }

    [ObservableProperty] public partial bool IsWarning { get; private set; }

    [ObservableProperty] public partial bool IsError { get; private set; }

    [ObservableProperty] public partial bool IsConnecting { get; private set; }

    [ObservableProperty] public partial IReadOnlyList<TagChip> AllowedTags { get; private set; } = [];

    /// <summary>The configuration was read: the Image settings column shows its check or triangle.</summary>
    [ObservableProperty] public partial bool HasImageSettings { get; private set; }

    [ObservableProperty] public partial bool ImageSettingsOk { get; private set; }

    [ObservableProperty] public partial bool ImageSettingsDiffer { get; private set; }

    /// <summary>The first line of the rich tooltip: "As ALPV recommends" or "Not as ALPV recommends".</summary>
    [ObservableProperty] public partial string ImageSettingsTitle { get; private set; } = string.Empty;

    /// <summary>Each value with the recommended one (WDR, Contrast, Local contrast, Tone mapping, Max gain, Shutter speed).</summary>
    [ObservableProperty] public partial IReadOnlyList<ImageValueRow> ImageValues { get; private set; } = [];

    /// <summary>Sort key of the Image settings column: differs first.</summary>
    public int ImageSortKey => !HasImageSettings ? 2 : ImageSettingsOk ? 1 : 0;

    /// <summary>Sort key of the Status column.</summary>
    public string StatusSortKey => StatusText;

    /// <summary>Sort key of the Allowed tags column.</summary>
    public string TagsSortKey => string.Join(", ", AllowedTags.Select(t => t.Name));

    internal void Apply(CameraRow row, PlateTagStore tags, TimeProvider time)
    {
        Row = row;
        Camera = !string.IsNullOrWhiteSpace(row.Name) ? row.Name : string.IsNullOrWhiteSpace(row.HostName) ? row.Address : row.HostName!;
        Address = row.Address;
        Site = row.Site ?? string.Empty;
        AlpvVersion = row.AlpvVersion ?? string.Empty;
        StatusText = row.Status == CameraStatus.ConnectionLost && row.Since is { } since
            ? LicensePlateTexts.ConnectionLost + " since " + Clock(since, time)
            : string.IsNullOrEmpty(row.StatusText) ? LicensePlateTexts.Status(row.Status) : row.StatusText;
        (IsOk, IsWarning, IsError, IsConnecting) = row.Status switch
        {
            CameraStatus.Connected => (true, false, false, false),
            CameraStatus.Connecting => (false, false, false, true),
            CameraStatus.ConnectionLost or CameraStatus.EventsOff or CameraStatus.SiteOffline => (false, true, false, false),
            _ => (false, false, true, false),
        };
        ApplyTags(tags);
        if (row.ImageSettings is { } image)
        {
            HasImageSettings = true;
            ImageSettingsOk = image.Recommended;
            ImageSettingsDiffer = !image.Recommended;
            ImageSettingsTitle = image.Recommended ? PageTexts.ImageSettingsMatch : PageTexts.ImageSettingsDiffer;
            ImageValues = [.. image.Values.Select(v => new ImageValueRow(v.Name, v.Current ?? "–", v.Recommended ?? "–", v.Matches))];
        }
        else
        {
            HasImageSettings = false;
            ImageSettingsOk = false;
            ImageSettingsDiffer = false;
            ImageSettingsTitle = PageTexts.NotReadYet;
            ImageValues = [];
        }

        OnPropertyChanged(nameof(ImageSortKey));
        OnPropertyChanged(nameof(StatusSortKey));
    }

    internal void ApplyTags(PlateTagStore tags)
    {
        AllowedTags = tags.Chips(Row.AllowedTags);
        OnPropertyChanged(nameof(TagsSortKey));
    }

    private static string Clock(DateTimeOffset at, TimeProvider time)
    {
        var local = TimeZoneInfo.ConvertTime(at, time.LocalTimeZone);
        var now = TimeZoneInfo.ConvertTime(time.GetUtcNow(), time.LocalTimeZone);
        return local.Date == now.Date
            ? local.ToString("HH:mm", CultureInfo.CurrentCulture)
            : local.ToString("d MMM HH:mm", CultureInfo.CurrentCulture);
    }
}

/// <summary>
/// The Cameras tab (docs/spec/license-plate-center.md "Cameras"): the cameras of the plugin with ALPV, status, allowed tags
/// and image settings; Add cameras, Set up, Allowed tags, Copy settings, Use recommended image settings, Refresh, Settings
/// and Remove. One read at a time, once more when a change came meanwhile; rows are updated in place.
/// </summary>
public sealed partial class CamerasViewModel : ObservableObject
{
    private readonly LicensePlatesViewModel _page;
    private readonly Dictionary<Guid, CameraRowViewModel> _rows = [];
    private List<CameraRowViewModel> _all = [];
    private bool _reloading;
    private bool _reloadAgain;

    internal CamerasViewModel(LicensePlatesViewModel page)
    {
        _page = page;
        page.Tags.Changed += (_, _) =>
        {
            foreach (var row in _all)
            {
                row.ApplyTags(page.Tags);
            }
        };
    }

    public RangeObservableCollection<CameraRowViewModel> Rows { get; } = [];

    /// <summary>Every camera of the plugin, also those the search hides.</summary>
    public IReadOnlyList<CameraRowViewModel> AllRows => _all;

    /// <summary>The rows chosen in the grid (several with Ctrl or Shift); the view hands them over.</summary>
    public IReadOnlyList<CameraRowViewModel> Selected { get; private set; } = [];

    [ObservableProperty] public partial string SearchText { get; set; } = string.Empty;

    /// <summary>The first read (the overlay).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty), nameof(ShowNoMatch))]
    public partial bool IsLoading { get; private set; } = true;

    /// <summary>A change runs: the buttons wait.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCamerasCommand), nameof(SetUpCommand), nameof(AllowedTagsCommand), nameof(CopySettingsCommand),
        nameof(UseRecommendedCommand), nameof(RefreshCommand), nameof(RemoveCommand), nameof(SettingsCommand))]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorText { get; private set; }

    /// <summary>A problem of the log store (no folder, a full disk) from the server.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLogProblem))]
    public partial string? LogProblem { get; private set; }

    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    public bool HasLogProblem => !string.IsNullOrEmpty(LogProblem);

    public bool ShowEmpty => !IsLoading && _all.Count == 0;

    public bool ShowNoMatch => !IsLoading && _all.Count > 0 && Rows.Count == 0;

    public bool MayChange => _page.MaySetUpCameras;

    public bool IsAdministrator => _page.IsAdministrator;

    public int Connected { get; private set; }

    public int Total { get; private set; }

    /// <summary>The cameras were read again.</summary>
    public event EventHandler? Changed;

    /// <summary>The read that runs now (tests wait for it).</summary>
    public Task ReloadTask { get; private set; } = Task.CompletedTask;

    /// <summary>The camera is in the plugin (the camera choice of Log and Stats offers only these).</summary>
    public bool Contains(Guid deviceId) => _rows.ContainsKey(deviceId);

    /// <summary>The ids of every camera of the plugin.</summary>
    public IReadOnlyCollection<Guid> Ids => _rows.Keys;

    public CameraRowViewModel? Find(Guid deviceId) => _rows.GetValueOrDefault(deviceId);

    public void SetSelection(IEnumerable<CameraRowViewModel> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        Selected = [.. rows];
        RaiseCommands();
    }

    public void RequestReload()
    {
        if (_reloading)
        {
            _reloadAgain = true;
            return;
        }

        ReloadTask = ReloadLoopAsync();
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    private bool CanChange() => MayChange && !IsBusy;

    private bool CanChangeSelected() => CanChange() && Selected.Count > 0;

    private bool CanChangeOne() => CanChange() && Selected.Count == 1;

    [RelayCommand(CanExecute = nameof(CanChange))]
    private async Task AddCamerasAsync()
    {
        if (_page.Dialogs is not { } dialogs)
        {
            return;
        }

        // Every camera that is not in the plugin yet (the picker cannot know where ALPV is installed: a camera without it shows
        // "ALPV is not installed" after it was added).
        var candidates = _page.Client.Context.Devices.Where(d => d.Category == DeviceCategory.Camera && d.HasVideo && !_rows.ContainsKey(d.Id)).ToList();
        if (await dialogs.PickCamerasAsync(candidates, [], PageTexts.AddCameras).ConfigureAwait(true) is not { Count: > 0 } ids)
        {
            return;
        }

        await ChangeAsync(async () => Apply(await _page.Client.CallAsync<CamerasReply>(LicensePlatesMethods.AddCameras, new DevicesRequest(ids)).ConfigureAwait(true)))
            .ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanChangeOne))]
    private async Task SetUpAsync()
    {
        if (_page.Dialogs is not { } dialogs || Selected is not [var row])
        {
            return;
        }

        var setup = new SetupViewModel(_page, row);
        _ = setup.LoadAsync();
        // Saved directly while the dialog waited: the list shows the camera's new state.
        if (await dialogs.SetupAsync(setup).ConfigureAwait(true))
        {
            RequestReload();
        }
    }

    [RelayCommand(CanExecute = nameof(CanChangeSelected))]
    private async Task AllowedTagsAsync()
    {
        if (_page.Dialogs is not { } dialogs)
        {
            return;
        }

        var ids = Selected.Select(r => r.DeviceId).ToList();
        var dialog = TagsDialogViewModel.ForCameras(_page, [.. Selected.Select(r => r.Row)]);
        if (!await dialogs.TagsAsync(dialog).ConfigureAwait(true) || dialog.Change is not { } change)
        {
            return;
        }

        await ChangeAsync(async () => Apply(await _page.Client.CallAsync<CamerasReply>(
            LicensePlatesMethods.SetAllowedTags, new AllowedTagsRequest(ids, Add: change.Add, Remove: change.Remove)).ConfigureAwait(true))).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanChangeOne))]
    private async Task CopySettingsAsync()
    {
        if (_page.Dialogs is not { } dialogs || Selected is not [var source])
        {
            return;
        }

        var copy = new CopySettingsViewModel(_page, source);
        _ = await dialogs.CopySettingsAsync(copy).ConfigureAwait(true); // the tasks show in the tasks pane
    }

    [RelayCommand(CanExecute = nameof(CanChangeSelected))]
    private async Task UseRecommendedAsync()
    {
        var ids = Selected.Select(r => r.DeviceId).ToList();
        if (!await _page.Client.Context.ConfirmAsync(PageTexts.UseRecommendedTitle, PageTexts.UseRecommendedMessage(ids.Count), PageTexts.UseRecommendedConfirm).ConfigureAwait(true))
        {
            return;
        }

        await ChangeAsync(async () =>
        {
            await _page.Client.CallAsync<TasksReply>(LicensePlatesMethods.UseRecommendedImageSettings, new DevicesRequest(ids)).ConfigureAwait(true); // the tasks show in the tasks pane
        }).ConfigureAwait(true);
    }

    private bool CanRefresh() => !IsBusy && _all.Count > 0;

    /// <summary>Reads ALPV of the selected cameras again (all without a selection).</summary>
    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task RefreshAsync()
    {
        var ids = (Selected.Count > 0 ? Selected : _all).Select(r => r.DeviceId).ToList();
        return MayChange
            ? ChangeAsync(async () => Apply(await _page.Client.CallAsync<CamerasReply>(LicensePlatesMethods.RefreshCameras, new DevicesRequest(ids)).ConfigureAwait(true)))
            : ChangeAsync(() =>
            {
                RequestReload();
                return ReloadTask;
            });
    }

    private bool CanOpenSettings() => IsAdministrator && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanOpenSettings))]
    private async Task SettingsAsync()
    {
        if (_page.Dialogs is not { } dialogs)
        {
            return;
        }

        var settings = new SettingsViewModel(_page);
        _ = settings.LoadAsync();
        if (await dialogs.SettingsAsync(settings).ConfigureAwait(true))
        {
            _page.Log.RequestStatus();
            RequestReload();
        }
    }

    [RelayCommand(CanExecute = nameof(CanChangeSelected))]
    private async Task RemoveAsync()
    {
        var ids = Selected.Select(r => r.DeviceId).ToList();
        if (!await _page.Client.Context.ConfirmAsync(PageTexts.RemoveCamerasTitle, PageTexts.RemoveCamerasMessage(ids.Count), PageTexts.Remove).ConfigureAwait(true))
        {
            return;
        }

        await ChangeAsync(async () => Apply(await _page.Client.CallAsync<CamerasReply>(LicensePlatesMethods.RemoveCameras, new DevicesRequest(ids)).ConfigureAwait(true)))
            .ConfigureAwait(true);
    }

    internal void OnPermissionsChanged()
    {
        OnPropertyChanged(nameof(MayChange));
        OnPropertyChanged(nameof(IsAdministrator));
        RaiseCommands();
    }

    /// <summary>The server's answer: the rows by camera, the counts of the header, the log's problem.</summary>
    internal void Apply(CamerasReply reply)
    {
        var all = new List<CameraRowViewModel>(reply.Cameras.Count);
        var seen = new HashSet<Guid>();
        foreach (var camera in reply.Cameras)
        {
            if (!seen.Add(camera.DeviceId))
            {
                continue;
            }

            if (!_rows.TryGetValue(camera.DeviceId, out var row))
            {
                row = new CameraRowViewModel(camera.DeviceId);
                _rows[camera.DeviceId] = row;
            }

            row.Apply(camera, _page.Tags, _page.Time);
            all.Add(row);
        }

        foreach (var gone in _rows.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _rows.Remove(gone);
        }

        var structural = all.Count != _all.Count || all.Where((r, i) => !ReferenceEquals(r, _all[i])).Any();
        _all = all;
        if (structural)
        {
            ApplyFilter();
        }

        Connected = reply.Connected;
        Total = reply.Total;
        LogProblem = reply.LogProblem;
        _page.RaiseConnected();
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(ShowNoMatch));
        RaiseCommands();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyFilter()
    {
        var text = SearchText.Trim();
        Rows.ReplaceAll(text.Length == 0
            ? _all
            : [.. _all.Where(r => r.Camera.Contains(text, StringComparison.OrdinalIgnoreCase)
                || r.Address.Contains(text, StringComparison.OrdinalIgnoreCase)
                || r.Site.Contains(text, StringComparison.OrdinalIgnoreCase))]);
        // The grid raised its own selection while the rows were replaced (it may move it to another row): keep that, only
        // without rows that are gone. Never the selection from before, which the grid no longer shows.
        var shown = Rows.ToHashSet();
        Selected = [.. Selected.Where(shown.Contains)];
        OnPropertyChanged(nameof(ShowNoMatch));
        RaiseCommands();
    }

    private async Task ReloadLoopAsync()
    {
        _reloading = true;
        try
        {
            do
            {
                _reloadAgain = false;
                var error = await GuardedRun.RunAsync(async () =>
                    Apply(await _page.Client.CallAsync<CamerasReply>(LicensePlatesMethods.Cameras, null).ConfigureAwait(true))).ConfigureAwait(true);
                ErrorText = error;
                IsLoading = false;
            }
            while (_reloadAgain);
        }
        finally
        {
            _reloading = false;
        }
    }

    private async Task ChangeAsync(Func<Task> change)
    {
        IsBusy = true;
        ErrorText = null;
        try
        {
            ErrorText = await GuardedRun.RunAsync(change).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RaiseCommands()
    {
        AddCamerasCommand.NotifyCanExecuteChanged();
        SetUpCommand.NotifyCanExecuteChanged();
        AllowedTagsCommand.NotifyCanExecuteChanged();
        CopySettingsCommand.NotifyCanExecuteChanged();
        UseRecommendedCommand.NotifyCanExecuteChanged();
        RefreshCommand.NotifyCanExecuteChanged();
        SettingsCommand.NotifyCanExecuteChanged();
        RemoveCommand.NotifyCanExecuteChanged();
    }
}
