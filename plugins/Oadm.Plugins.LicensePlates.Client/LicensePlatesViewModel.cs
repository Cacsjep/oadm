using System.Collections.ObjectModel;
using System.Text.Json;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Controls;

namespace Oadm.Plugins.LicensePlates.Client;

/// <summary>One tab of the page: Log, Plates, Cameras or Stats.</summary>
public sealed partial class PageTabViewModel : ObservableObject
{
    public PageTabViewModel(string title, Action<PageTabViewModel> activate)
    {
        Title = title;
        ActivateCommand = new RelayCommand(() => activate(this));
    }

    public string Title { get; }

    [ObservableProperty]
    public partial bool IsActive { get; set; }

    public IRelayCommand ActivateCommand { get; }
}

/// <summary>
/// Page "License Plate Center" (docs/spec/license-plate-center.md "The page"): the tabs Log, Plates, Cameras and Stats, the
/// live status of the header ("41 of 42 cameras connected"), the plate tags all tabs share and the camera choice of Log and
/// Stats. Live through the plugin's events while shown: <c>reads</c> to the log, <c>cameras</c> to the cameras, <c>plates</c>
/// to the plates and tags, <c>status</c> to the log's footer. Without live events (older hosts) it reads again every 5 s.
/// </summary>
public sealed partial class LicensePlatesViewModel : ObservableObject, IDisposable
{
    private CancellationTokenSource? _active;

    public LicensePlatesViewModel(ICorePluginClientContext ctx, TimeProvider? time = null)
    {
        Client = new PageClient(ctx ?? throw new ArgumentNullException(nameof(ctx)));
        Time = time ?? TimeProvider.System;
        Cameras = new CamerasViewModel(this);
        CameraChoice = new PageDeviceChoice(ctx, PageTexts.Cameras, d => Cameras.Contains(d.Id));
        if (CameraChoice.IsEmpty)
        {
            CameraChoice.Set(DeviceChoice.AllDevices); // the default: every camera of the plugin
        }

        Log = new LogViewModel(this);
        Plates = new PlatesViewModel(this);
        Stats = new StatsViewModel(this);
        LogTab = new PageTabViewModel(PageTexts.LogTab, Activate);
        PlatesTab = new PageTabViewModel(PageTexts.PlatesTab, Activate);
        CamerasTab = new PageTabViewModel(PageTexts.CamerasTab, Activate);
        StatsTab = new PageTabViewModel(PageTexts.StatsTab, Activate);
        Cameras.Changed += (_, _) => OnPropertyChanged(nameof(ConnectedText));
        ctx.PermissionsChanged += (_, _) => OnPermissionsChanged();
        BuildTabs();
    }

    public PageClient Client { get; }

    public TimeProvider Time { get; }

    /// <summary>The plate tags of the server (chips, filters, dialogs).</summary>
    public PlateTagStore Tags { get; } = new();

    /// <summary>The cameras of Log and Stats (All: every camera of the plugin), kept on this computer.</summary>
    public PageDeviceChoice CameraChoice { get; }

    public LogViewModel Log { get; }

    public PlatesViewModel Plates { get; }

    public CamerasViewModel Cameras { get; }

    public StatsViewModel Stats { get; }

    /// <summary>The page's dialogs (the view; tests set fakes).</summary>
    public IPageDialogs? Dialogs { get; set; }

    /// <summary>How long the page waits before reading again when the host has no live events.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    public PageTabViewModel LogTab { get; }

    public PageTabViewModel PlatesTab { get; }

    public PageTabViewModel CamerasTab { get; }

    public PageTabViewModel StatsTab { get; }

    /// <summary>The tabs the role may see, in order (Log and Stats need "View plate log").</summary>
    public ObservableCollection<PageTabViewModel> Tabs { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLogTab), nameof(IsPlatesTab), nameof(IsCamerasTab), nameof(IsStatsTab))]
    public partial PageTabViewModel? ActiveTab { get; private set; }

    public bool IsLogTab => ActiveTab == LogTab;

    public bool IsPlatesTab => ActiveTab == PlatesTab;

    public bool IsCamerasTab => ActiveTab == CamerasTab;

    public bool IsStatsTab => ActiveTab == StatsTab;

    /// <summary>"View plate log": the Log and Stats tabs.</summary>
    public bool MayViewLog => Client.Has(LicensePlatePermissions.Log);

    /// <summary>"Export plate log".</summary>
    public bool MayExport => MayViewLog && Client.Has(LicensePlatePermissions.Export);

    /// <summary>"Manage plates": plates, plate tags, import.</summary>
    public bool MayManagePlates => Client.Has(LicensePlatePermissions.Plates);

    /// <summary>"Set up cameras".</summary>
    public bool MaySetUpCameras => Client.Has(LicensePlatePermissions.Cameras);

    /// <summary>The settings dialog.</summary>
    public bool IsAdministrator => Client.Context.IsAdministrator;

    /// <summary>The header's live status: "41 of 42 cameras connected"; empty without cameras.</summary>
    public string ConnectedText => Cameras.Total == 0 ? string.Empty : LicensePlateTexts.ConnectedCount(Cameras.Connected, Cameras.Total);

    public bool AllConnected => Cameras.Total > 0 && Cameras.Connected == Cameras.Total;

    public bool SomeNotConnected => Cameras.Total > 0 && Cameras.Connected < Cameras.Total;

    /// <summary>ALPV's lists (regions, countries, states, vehicle types and colours) from the server part; null until read.</summary>
    public AlpvLists? Lists { get; private set; }

    /// <summary>The lists were read.</summary>
    public event EventHandler? ListsChanged;

    /// <summary>The read of the lists (once per page; tests and the Set up dialog wait for it).</summary>
    public Task ListsTask { get; private set; } = Task.CompletedTask;

    /// <summary>The read of the tags that runs now (tests wait for it).</summary>
    public Task TagsTask { get; private set; } = Task.CompletedTask;

    /// <summary>The page is shown: read and follow the changes.</summary>
    public void Activate()
    {
        if (_active is not null)
        {
            return;
        }

        _active = new CancellationTokenSource();
        TagsTask = ReloadTagsAsync();
        if (Lists is null)
        {
            ListsTask = LoadListsAsync();
        }

        Cameras.RequestReload();
        Plates.RequestReload();
        if (MayViewLog)
        {
            Log.Start();
        }

        _ = WatchAsync(_active.Token);
    }

    public void Deactivate()
    {
        if (_active is { } active)
        {
            _active = null;
            active.Cancel();
            active.Dispose();
        }

        Log.Stop();
    }

    public void Dispose() => Deactivate();

    /// <summary>Shows a tab (tests, the view).</summary>
    public void Show(PageTabViewModel tab) => Activate(tab);

    /// <summary>Reads the plate tags again (after a change of the tag dialog or a <c>plates</c> event).</summary>
    public Task ReloadTagsAsync() => TagsTask = LoadTagsAsync();

    private async Task LoadListsAsync()
    {
        await GuardedRun.RunAsync(async () =>
        {
            Lists = await Client.CallAsync<AlpvLists>(LicensePlatesMethods.AlpvLists, null).ConfigureAwait(true);
            ListsChanged?.Invoke(this, EventArgs.Empty);
        }).ConfigureAwait(true);
    }

    private async Task LoadTagsAsync()
    {
        await GuardedRun.RunAsync(async () =>
        {
            var reply = await Client.CallAsync<TagsReply>(LicensePlatesMethods.Tags, null).ConfigureAwait(true);
            Tags.Set(reply.Tags);
        }).ConfigureAwait(true);
    }

    internal void RaiseConnected()
    {
        OnPropertyChanged(nameof(ConnectedText));
        OnPropertyChanged(nameof(AllConnected));
        OnPropertyChanged(nameof(SomeNotConnected));
    }

    private void Activate(PageTabViewModel tab)
    {
        foreach (var other in Tabs)
        {
            other.IsActive = ReferenceEquals(other, tab);
        }

        ActiveTab = tab;
        if (tab == StatsTab)
        {
            Stats.RequestReload();
        }
    }

    private void BuildTabs()
    {
        var shown = ActiveTab;
        Tabs.Clear();
        if (MayViewLog)
        {
            Tabs.Add(LogTab);
        }

        Tabs.Add(PlatesTab);
        Tabs.Add(CamerasTab);
        if (MayViewLog)
        {
            Tabs.Add(StatsTab);
        }

        Activate(shown is not null && Tabs.Contains(shown) ? shown : Tabs[0]);
    }

    private void OnPermissionsChanged()
    {
        BuildTabs();
        OnPropertyChanged(nameof(MayViewLog));
        OnPropertyChanged(nameof(MayExport));
        OnPropertyChanged(nameof(MayManagePlates));
        OnPropertyChanged(nameof(MaySetUpCameras));
        OnPropertyChanged(nameof(IsAdministrator));
        Log.OnPermissionsChanged();
        Plates.OnPermissionsChanged();
        Cameras.OnPermissionsChanged();
    }

    private async Task WatchAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await foreach (var item in Client.Context.WatchEventsAsync(ct).ConfigureAwait(true))
                {
                    OnEvent(item.Topic, item.PayloadJson);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // The connection dropped: read again and watch again.
            catch (Exception)
#pragma warning restore CA1031
            {
            }

            try
            {
                await Task.Delay(PollInterval, Time, ct).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            // No events (older host) or a dropped stream: read again.
            Cameras.RequestReload();
            Log.ReadNewer();
            Log.RequestStatus();
        }
    }

    /// <summary>One live event of the plugin (tests call it directly).</summary>
    internal void OnEvent(string topic, string? payloadJson)
    {
        switch (topic)
        {
            case LicensePlatesMethods.ReadsTopic:
                Log.OnReads(ParseIds(payloadJson));
                break;
            case LicensePlatesMethods.CamerasTopic:
                Cameras.RequestReload();
                break;
            case LicensePlatesMethods.PlatesTopic:
                TagsTask = ReloadTagsAsync();
                Plates.RequestReload();
                break;
            case LicensePlatesMethods.StatusTopic:
                Log.RequestStatus();
                break;
        }
    }

    private static List<Guid> ParseIds(string? json)
    {
        try
        {
            return string.IsNullOrEmpty(json) ? [] : JsonSerializer.Deserialize<List<Guid>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
