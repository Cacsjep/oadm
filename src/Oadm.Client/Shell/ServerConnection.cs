using CommunityToolkit.Mvvm.ComponentModel;

using Microsoft.Extensions.Logging;

using Oadm.Client.Api;
using Oadm.Client.Devices;
using Oadm.Client.Infrastructure;
using Oadm.Client.Tasks;
using Oadm.Contracts.V1;

namespace Oadm.Client.Shell;

public enum ConnectionState
{
    Connecting,
    Connected,
    Disconnected,
}

/// <summary>
/// Keeps the device and task mirrors in sync with the server: List snapshot, then Watch stream,
/// with exponential back-off reconnect. Never throws to the UI.
/// </summary>
public sealed partial class ServerConnection : ObservableObject, IDisposable
{
    private static readonly TimeSpan MinDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(15);

    private readonly IOadmApi _api;
    private readonly DeviceStore _devices;
    private readonly TaskStore _tasks;
    private readonly IUiDispatcher _ui;
    private readonly ILogger<ServerConnection> _logger;
    private readonly UserSession? _session;
    private readonly TimeProvider _time;
    private CancellationTokenSource? _cts;

    public ServerConnection(IOadmApi api, DeviceStore devices, TaskStore tasks, IUiDispatcher ui, ILogger<ServerConnection> logger, UserSession? session = null,
        TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System; // the reconnect back-off; tests pass a fake clock
        _api = api;
        _devices = devices;
        _tasks = tasks;
        _ui = ui;
        _logger = logger;
        _session = session;
        StatusText = "Connecting to " + api.ServerAddress;
    }

    [ObservableProperty]
    public partial ConnectionState State { get; private set; } = ConnectionState.Connecting;

    [ObservableProperty]
    public partial string StatusText { get; private set; }

    public bool IsConnected => State == ConnectionState.Connected;
    public bool IsDisconnected => State == ConnectionState.Disconnected;
    public bool IsConnecting => State == ConnectionState.Connecting;

    public string ServerAddress => _api.ServerAddress;

    /// <summary>Raised on the UI thread every time the device stream (re)connects.</summary>
    public event EventHandler? Connected;

    public void Start()
    {
        Stop();
        _cts = new CancellationTokenSource();
        CancellationToken ct = _cts.Token;
        _ = Task.Run(() => DeviceLoopAsync(ct), ct);
        _ = Task.Run(() => TaskLoopAsync(ct), ct);
        _ = Task.Run(() => TagLoopAsync(ct), ct);
        _ = Task.Run(() => WaitingLoopAsync(ct), ct);
        _ = Task.Run(() => SitesLoopAsync(ct), ct);
        _ = Task.Run(() => ComplianceChecksLoopAsync(ct), ct);
        _ = Task.Run(() => ComplianceResultsLoopAsync(ct), ct);
        if (_session is not null)
        {
            _ = Task.Run(() => SessionLoopAsync(_session, ct), ct);
        }
    }

    /// <summary>Re-points the API and restarts both streams.</summary>
    public void Reconnect(string serverAddress)
    {
        _api.SetServerAddress(serverAddress);
        OnPropertyChanged(nameof(ServerAddress));
        SetState(ConnectionState.Connecting, "Connecting to " + _api.ServerAddress);
        Start();
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    public void Dispose() => Stop();

    partial void OnStateChanged(ConnectionState value)
    {
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(IsDisconnected));
        OnPropertyChanged(nameof(IsConnecting));
    }

    /// <summary>
    /// One Watch stream: the snapshot (one ADDED per device, then SNAPSHOT_END) is collected and its rows are built
    /// (<see cref="DeviceStore.Prepare"/>) off the UI thread, then applied as one <see cref="DeviceStore.Reset(PreparedDeviceSnapshot)"/>; live changes go through a
    /// <see cref="ChangeBatcher{T}"/> so a burst of 5,000 changes becomes a few UI batches. Snapshot and
    /// changes share the batcher, so they are applied in stream order.
    /// </summary>
    private async Task DeviceLoopAsync(CancellationToken ct)
    {
        TimeSpan delay = MinDelay;
        var batcher = new ChangeBatcher<StreamItem<PreparedDeviceSnapshot, DeviceChanged>>(_ui, ApplyDevices);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                List<Device>? snapshot = [];
                await foreach (DeviceChanged change in _api.WatchDevicesAsync(ct).ConfigureAwait(false))
                {
                    if (snapshot is null)
                    {
                        batcher.Add(StreamItem<PreparedDeviceSnapshot, DeviceChanged>.Of(change));
                    }
                    else if (change.Kind == DeviceChanged.Types.Kind.SnapshotEnd)
                    {
                        // The rows are built here, off the UI thread; the UI thread only swaps them in.
                        batcher.Add(StreamItem<PreparedDeviceSnapshot, DeviceChanged>.Of(DeviceStore.Prepare(snapshot)));
                        snapshot = null;
                        LogConnected(_logger, _api.ServerAddress);
                        delay = MinDelay;
                    }
                    else if (change.Device is not null)
                    {
                        snapshot.Add(change.Device);
                    }
                }

                throw new InvalidOperationException("Device stream ended");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LogDisconnected(_logger, _api.ServerAddress, ex.Message);
                int seconds = (int)delay.TotalSeconds;
                _ui.Post(() => SetState(ConnectionState.Disconnected,
                    $"Server {_api.ServerAddress} unreachable, retrying in {seconds} s"));
            }

            try
            {
                await Task.Delay(delay, _time, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            delay = TimeSpan.FromTicks(Math.Min(MaxDelay.Ticks, delay.Ticks * 2));
            _ui.Post(() => SetState(ConnectionState.Connecting, "Connecting to " + _api.ServerAddress));
        }
    }

    /// <summary>Like <see cref="DeviceLoopAsync"/>: snapshot (newest <see cref="TaskStore.MaxTasks"/> plus active, rows built off the UI thread) as one reset, then batched changes.</summary>
    private async Task TaskLoopAsync(CancellationToken ct)
    {
        TimeSpan delay = MinDelay;
        var batcher = new ChangeBatcher<StreamItem<PreparedTaskSnapshot, TaskChanged>>(_ui, ApplyTasks);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                List<TaskInfo>? snapshot = [];
                await foreach (TaskChanged change in _api.WatchTasksAsync(ct).ConfigureAwait(false))
                {
                    if (snapshot is null)
                    {
                        batcher.Add(StreamItem<PreparedTaskSnapshot, TaskChanged>.Of(change));
                    }
                    else if (change.Kind == TaskChanged.Types.Kind.SnapshotEnd)
                    {
                        batcher.Add(StreamItem<PreparedTaskSnapshot, TaskChanged>.Of(TaskStore.Prepare(snapshot)));
                        snapshot = null;
                        delay = MinDelay;
                    }
                    else if (change.Task is not null)
                    {
                        snapshot.Add(change.Task);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                // the device loop reports the connection state; just retry
            }

            try
            {
                await Task.Delay(delay, _time, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            delay = TimeSpan.FromTicks(Math.Min(MaxDelay.Ticks, delay.Ticks * 2));
        }
    }

    /// <summary>
    /// The tag definitions (TagService.Watch): every message is the whole list (small), applied on the UI thread; the
    /// newest wins when several arrive at once. A server without tags (older version) is retried like the other streams.
    /// </summary>
    private async Task TagLoopAsync(CancellationToken ct)
    {
        TimeSpan delay = MinDelay;
        var batcher = new ChangeBatcher<TagList>(_ui, lists => _devices.Tags.Reset(lists[^1].Tags));
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await foreach (TagList list in _api.WatchTagsAsync(ct).ConfigureAwait(false))
                {
                    batcher.Add(list);
                    delay = MinDelay;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                // the device loop reports the connection state; just retry
            }

            try
            {
                await Task.Delay(delay, _time, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            delay = TimeSpan.FromTicks(Math.Min(MaxDelay.Ticks, delay.Ticks * 2));
        }
    }

    /// <summary>
    /// The waiting list summary (WaitingDeviceService.Watch) for the Devices page line. Only for a role with "Add and remove
    /// devices" (others are refused by the server, so they do not ask); an older server without the list is retried like
    /// the other streams.
    /// </summary>
    private async Task WaitingLoopAsync(CancellationToken ct)
    {
        TimeSpan delay = MinDelay;
        var batcher = new ChangeBatcher<WaitingSummary>(_ui, summaries => _devices.Waiting.Apply(summaries[^1]));
        while (!ct.IsCancellationRequested)
        {
            if (_session is null || _session.Has(Oadm.Sdk.Plugins.HostPermissionKeys.DevicesAddRemove))
            {
                try
                {
                    await foreach (WaitingSummary summary in _api.WatchWaitingAsync(ct).ConfigureAwait(false))
                    {
                        batcher.Add(summary);
                        delay = MinDelay;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception)
                {
                    // the device loop reports the connection state; just retry
                }
            }

            _ui.Post(_devices.Waiting.Clear);
            try
            {
                await Task.Delay(delay, _time, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            delay = TimeSpan.FromTicks(Math.Min(MaxDelay.Ticks, delay.Ticks * 2));
        }
    }

    /// <summary>
    /// Whether the server has sites (the Site columns): the site list (SiteService.WatchSites, small, at most once a second)
    /// for a role with "See sites"; without it the devices decide (one of them at a site). An older server without sites is
    /// retried like the other streams.
    /// </summary>
    private async Task SitesLoopAsync(CancellationToken ct)
    {
        TimeSpan delay = MinDelay;
        var batcher = new ChangeBatcher<SiteList>(_ui, lists => _devices.Sites.Apply(lists[^1]));
        // O(1) per batch: the store counts the devices at a site; nothing to do while the server's list decides.
        void FromDevices() => _devices.Sites.SetFromDevices(_devices.DevicesAtSite > 0);
        void OnDevices(object? sender, DeviceStoreChangedEventArgs e)
        {
            if (!_devices.Sites.DecidedByServer)
            {
                FromDevices();
            }
        }

        _ui.Post(() =>
        {
            _devices.Changed += OnDevices;
            FromDevices();
        });
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (_session is null || _session.Has(Oadm.Sdk.Plugins.HostPermissionKeys.SitesView))
                {
                    try
                    {
                        await foreach (SiteList list in _api.WatchSitesAsync(ct).ConfigureAwait(false))
                        {
                            batcher.Add(list);
                            delay = MinDelay;
                        }
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception)
                    {
                        // the device loop reports the connection state; just retry
                    }
                }

                _ui.Post(() =>
                {
                    _devices.Sites.Clear();
                    FromDevices(); // the count kept while the server's list decided
                });
                try
                {
                    await Task.Delay(delay, _time, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                delay = TimeSpan.FromTicks(Math.Min(MaxDelay.Ticks, delay.Ticks * 2));
            }
        }
        finally
        {
            _ui.Post(() => _devices.Changed -= OnDevices);
        }
    }

    /// <summary>Whether the role may see compliance (a server without roles: everyone).</summary>
    private bool MaySeeCompliance => _session is null || _session.Has(Oadm.Sdk.Plugins.HostPermissionKeys.ComplianceView);

    /// <summary>The compliance checks with their counts (ComplianceService.WatchChecks), while the role may see compliance.</summary>
    private async Task ComplianceChecksLoopAsync(CancellationToken ct)
    {
        TimeSpan delay = MinDelay;
        var batcher = new ChangeBatcher<ComplianceCheckList>(_ui, lists => _devices.Compliance.ApplyChecks(lists[^1]));
        while (!ct.IsCancellationRequested)
        {
            if (MaySeeCompliance)
            {
                try
                {
                    await foreach (ComplianceCheckList list in _api.WatchComplianceChecksAsync(ct).ConfigureAwait(false))
                    {
                        batcher.Add(list);
                        delay = MinDelay;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception)
                {
                    // the device loop reports the connection state; just retry
                }
            }

            _ui.Post(_devices.Compliance.Clear);
            try
            {
                await Task.Delay(delay, _time, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            delay = TimeSpan.FromTicks(Math.Min(MaxDelay.Ticks, delay.Ticks * 2));
        }
    }

    /// <summary>
    /// Every compliance result (ComplianceService.WatchResults): the snapshot is collected off the UI thread and applied as
    /// one reset, then changes in batches, in stream order.
    /// </summary>
    private async Task ComplianceResultsLoopAsync(CancellationToken ct)
    {
        TimeSpan delay = MinDelay;
        var batcher = new ChangeBatcher<(IReadOnlyList<ComplianceResultEntry> Results, bool Reset)>(_ui, batches =>
        {
            foreach (var (results, reset) in batches)
            {
                _devices.Compliance.ApplyResults(results, reset);
            }
        });
        while (!ct.IsCancellationRequested)
        {
            if (MaySeeCompliance)
            {
                try
                {
                    List<ComplianceResultEntry>? snapshot = [];
                    await foreach (ComplianceResultBatch batch in _api.WatchComplianceResultsAsync(ct).ConfigureAwait(false))
                    {
                        if (snapshot is not null)
                        {
                            snapshot.AddRange(batch.Results);
                            if (batch.SnapshotEnd)
                            {
                                batcher.Add((snapshot, true));
                                snapshot = null;
                            }
                        }
                        else
                        {
                            batcher.Add(([.. batch.Results], false));
                        }

                        delay = MinDelay;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception)
                {
                    // the device loop reports the connection state; just retry
                }
            }

            try
            {
                await Task.Delay(delay, _time, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            delay = TimeSpan.FromTicks(Math.Min(MaxDelay.Ticks, delay.Ticks * 2));
        }
    }

    /// <summary>
    /// The session stream (AuthService.WatchSession): the user's role and permissions now and after every change, applied
    /// on the UI thread; the pages then show or hide what the role allows. A server older than roles is retried like the
    /// other streams (its session never changes).
    /// </summary>
    private async Task SessionLoopAsync(UserSession session, CancellationToken ct)
    {
        TimeSpan delay = MinDelay;
        var batcher = new ChangeBatcher<UserInfo>(_ui, users => session.Update(users[^1]));
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await foreach (UserInfo user in _api.WatchSessionAsync(ct).ConfigureAwait(false))
                {
                    batcher.Add(user);
                    delay = MinDelay;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                // the device loop reports the connection state; just retry
            }

            try
            {
                await Task.Delay(delay, _time, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            delay = TimeSpan.FromTicks(Math.Min(MaxDelay.Ticks, delay.Ticks * 2));
        }
    }

    /// <summary>UI thread: applies a batch in order; consecutive changes go to the store as one batch.</summary>
    private void ApplyDevices(IReadOnlyList<StreamItem<PreparedDeviceSnapshot, DeviceChanged>> items)
    {
        var changes = new List<DeviceChanged>();
        foreach (StreamItem<PreparedDeviceSnapshot, DeviceChanged> item in items)
        {
            if (item.Snapshot is null)
            {
                changes.Add(item.Change!);
                continue;
            }

            _devices.ApplyBatch(changes);
            changes.Clear();
            _devices.Reset(item.Snapshot);
            SetState(ConnectionState.Connected, "Connected to " + _api.ServerAddress);
            Connected?.Invoke(this, EventArgs.Empty);
        }

        _devices.ApplyBatch(changes);
    }

    private void ApplyTasks(IReadOnlyList<StreamItem<PreparedTaskSnapshot, TaskChanged>> items)
    {
        var changes = new List<TaskChanged>();
        foreach (StreamItem<PreparedTaskSnapshot, TaskChanged> item in items)
        {
            if (item.Snapshot is null)
            {
                changes.Add(item.Change!);
                continue;
            }

            _tasks.ApplyBatch(changes);
            changes.Clear();
            _tasks.Reset(item.Snapshot);
        }

        _tasks.ApplyBatch(changes);
    }

    /// <summary>Either a whole snapshot or one live change of a Watch stream.</summary>
    private sealed record StreamItem<TSnapshot, TChange>(TSnapshot? Snapshot, TChange? Change)
        where TSnapshot : class
        where TChange : class
    {
        public static StreamItem<TSnapshot, TChange> Of(TSnapshot snapshot) => new(snapshot, null);

        public static StreamItem<TSnapshot, TChange> Of(TChange change) => new(null, change);
    }

    private void SetState(ConnectionState state, string text)
    {
        State = state;
        StatusText = text;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Connected to server {Address}")]
    private static partial void LogConnected(ILogger logger, string address);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Server {Address} unreachable: {Reason}")]
    private static partial void LogDisconnected(ILogger logger, string address, string reason);
}
