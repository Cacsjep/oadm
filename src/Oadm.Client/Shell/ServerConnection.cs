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

    private readonly Updates.ServerUpdateTracker? _updates;
    private string _rawText = string.Empty;

    public ServerConnection(IOadmApi api, DeviceStore devices, TaskStore tasks, IUiDispatcher ui, ILogger<ServerConnection> logger, UserSession? session = null,
        TimeProvider? time = null, Updates.ServerUpdateTracker? updates = null)
    {
        _updates = updates;
        if (updates is not null)
        {
            // "Updating the server to V4.9.0" instead of "unreachable" while the server installs its update.
            updates.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(Updates.ServerUpdateTracker.AwayText))
                {
                    SetState(State, _rawText);
                }
            };
        }

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
        if (State != ConnectionState.Connected)
        {
            SetState(ConnectionState.Connecting, "Connecting to " + _api.ServerAddress); // the server of this login
        }

        _cts = new CancellationTokenSource();
        CancellationToken ct = _cts.Token;
        _ = Task.Run(() => DeviceLoopAsync(ct), ct);
        _ = Task.Run(() => TaskLoopAsync(ct), ct);
        _ = Task.Run(() => TagLoopAsync(ct), ct);
        _ = Task.Run(() => WaitingLoopAsync(ct), ct);
        _ = Task.Run(() => SitesLoopAsync(ct), ct);
        _ = Task.Run(() => ComplianceChecksLoopAsync(ct), ct);
        _ = Task.Run(() => ComplianceResultsLoopAsync(ct), ct);
        _ = Task.Run(() => VulnerabilityLoopAsync(ct), ct);
        if (_updates is not null)
        {
            _ = Task.Run(() => UpdateLoopAsync(_updates, ct), ct);
        }
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

    /// <summary>
    /// Logout (UI thread): stops the streams and empties what they filled (devices, tasks, tags, the waiting list, compliance,
    /// sites), so the next user, maybe of another server, never sees the last one's. What a stopped stream still had on its
    /// way to the UI thread is dropped (<see cref="StillRuns"/>).
    /// </summary>
    public void SignOut()
    {
        Stop();
        _devices.Clear();
        _tasks.Reset([]);
        _devices.Tags.Reset([]);
        _devices.Waiting.Clear();
        _devices.Compliance.Clear();
        _devices.Sites.Clear();
        _devices.Sites.SetFromDevices(false);
        SetState(ConnectionState.Connecting, "Connecting to " + _api.ServerAddress);
    }

    /// <summary>
    /// A batcher whose batches are dropped once its stream was stopped: a batch posted before a logout must not bring the
    /// last session's rows back after <see cref="SignOut"/> emptied the stores.
    /// </summary>
    private ChangeBatcher<T> StillRuns<T>(Action<IReadOnlyList<T>> apply, CancellationToken ct) => new(_ui, items =>
    {
        if (!ct.IsCancellationRequested)
        {
            apply(items);
        }
    });

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
        var batcher = StillRuns<StreamItem<PreparedDeviceSnapshot, DeviceChanged>>(ApplyDevices, ct);
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
                _ui.Post(() =>
                {
                    if (ct.IsCancellationRequested)
                    {
                        return; // stopped meanwhile (logout): the state is the next login's
                    }

                    _updates?.Disconnected();
                    SetState(ConnectionState.Disconnected, $"Server {_api.ServerAddress} unreachable, retrying in {seconds} s");
                });
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
        var batcher = StillRuns<StreamItem<PreparedTaskSnapshot, TaskChanged>>(ApplyTasks, ct);
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
        var batcher = StillRuns<TagList>(lists => _devices.Tags.Reset(lists[^1].Tags), ct);
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
        var batcher = StillRuns<WaitingSummary>(summaries => _devices.Waiting.Apply(summaries[^1]), ct);
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
        var batcher = StillRuns<SiteList>(lists => _devices.Sites.Apply(lists[^1]), ct);
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

    /// <summary>Hourly check of the vulnerability list; a client fetches online only with the server's setting "By the clients".</summary>
    public static readonly TimeSpan VulnerabilityPeriod = TimeSpan.FromHours(1);

    /// <summary>
    /// The server's list of Axis CVEs (the Vulnerabilities column): at connect, then every hour; with the setting "By the
    /// clients" this client fetches a newer one online when the server's is older than a day (docs/spec/vulnerabilities.md).
    /// </summary>
    private async Task VulnerabilityLoopAsync(CancellationToken ct)
    {
        var updates = new Vulnerabilities.VulnerabilityUpdates(_api, _devices.Vulnerabilities, _ui, time: _time);
        TimeSpan delay = MinDelay;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await updates.SyncAsync(force: false, ct).ConfigureAwait(false);
                delay = VulnerabilityPeriod;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LogVulnerabilitiesFailed(ex);
                delay = delay >= VulnerabilityPeriod ? VulnerabilityPeriod : TimeSpan.FromTicks(Math.Min(MaxDelay.Ticks * 4, delay.Ticks * 2));
            }

            try
            {
                await Task.Delay(delay, _time, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Vulnerability list not updated")]
    private partial void LogVulnerabilitiesFailed(Exception ex);

    /// <summary>Whether the role may see compliance (a server without roles: everyone).</summary>
    private bool MaySeeCompliance => _session is null || _session.Has(Oadm.Sdk.Plugins.HostPermissionKeys.ComplianceView);

    /// <summary>The compliance checks with their counts (ComplianceService.WatchChecks), while the role may see compliance.</summary>
    private async Task ComplianceChecksLoopAsync(CancellationToken ct)
    {
        TimeSpan delay = MinDelay;
        var batcher = StillRuns<ComplianceCheckList>(lists => _devices.Compliance.ApplyChecks(lists[^1]), ct);
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
        var batcher = StillRuns<(IReadOnlyList<ComplianceResultEntry> Results, bool Reset)>(batches =>
        {
            foreach (var (results, reset) in batches)
            {
                _devices.Compliance.ApplyResults(results, reset);
            }
        }, ct);
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
        var batcher = StillRuns<UserInfo>(users => session.Update(users[^1]), ct);
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
            _updates?.Connected();
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
        _rawText = text;
        State = state;
        StatusText = state != ConnectionState.Connected && _updates?.AwayText is { } away ? away : text;
    }

    /// <summary>
    /// The steps of a server update (every signed-in user): "Updating the server to V4.9.0" while it is away, the result
    /// after the reconnect. A server without self update (UNIMPLEMENTED) ends the loop.
    /// </summary>
    private async Task UpdateLoopAsync(Updates.ServerUpdateTracker updates, CancellationToken ct)
    {
        TimeSpan delay = MinDelay;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await foreach (var progress in _api.WatchUpdateAsync(ct).ConfigureAwait(false))
                {
                    _ui.Post(() => updates.Apply(progress));
                    delay = MinDelay;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Grpc.Core.RpcException ex) when (ex.StatusCode == Grpc.Core.StatusCode.Unimplemented)
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Connected to server {Address}")]
    private static partial void LogConnected(ILogger logger, string address);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Server {Address} unreachable: {Reason}")]
    private static partial void LogDisconnected(ILogger logger, string address, string reason);
}
