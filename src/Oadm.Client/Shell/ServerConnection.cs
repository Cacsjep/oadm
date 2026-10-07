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
    private CancellationTokenSource? _cts;

    public ServerConnection(IOadmApi api, DeviceStore devices, TaskStore tasks, IUiDispatcher ui, ILogger<ServerConnection> logger)
    {
        _api = api;
        _devices = devices;
        _tasks = tasks;
        _ui = ui;
        _logger = logger;
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
    /// One Watch stream: the snapshot (one ADDED per device, then SNAPSHOT_END) is collected off the UI
    /// thread and applied as one <see cref="DeviceStore.Reset"/>; live changes go through a
    /// <see cref="ChangeBatcher{T}"/> so a burst of 5,000 changes becomes a few UI batches. Snapshot and
    /// changes share the batcher, so they are applied in stream order.
    /// </summary>
    private async Task DeviceLoopAsync(CancellationToken ct)
    {
        TimeSpan delay = MinDelay;
        var batcher = new ChangeBatcher<StreamItem<Device, DeviceChanged>>(_ui, ApplyDevices);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                List<Device>? snapshot = [];
                await foreach (DeviceChanged change in _api.WatchDevicesAsync(ct).ConfigureAwait(false))
                {
                    if (snapshot is null)
                    {
                        batcher.Add(StreamItem<Device, DeviceChanged>.Of(change));
                    }
                    else if (change.Kind == DeviceChanged.Types.Kind.SnapshotEnd)
                    {
                        batcher.Add(StreamItem<Device, DeviceChanged>.Of(snapshot));
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
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            delay = TimeSpan.FromTicks(Math.Min(MaxDelay.Ticks, delay.Ticks * 2));
            _ui.Post(() => SetState(ConnectionState.Connecting, "Connecting to " + _api.ServerAddress));
        }
    }

    /// <summary>Like <see cref="DeviceLoopAsync"/>: snapshot (newest <see cref="TaskStore.MaxTasks"/> plus active) as one reset, then batched changes.</summary>
    private async Task TaskLoopAsync(CancellationToken ct)
    {
        TimeSpan delay = MinDelay;
        var batcher = new ChangeBatcher<StreamItem<TaskInfo, TaskChanged>>(_ui, ApplyTasks);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                List<TaskInfo>? snapshot = [];
                await foreach (TaskChanged change in _api.WatchTasksAsync(ct).ConfigureAwait(false))
                {
                    if (snapshot is null)
                    {
                        batcher.Add(StreamItem<TaskInfo, TaskChanged>.Of(change));
                    }
                    else if (change.Kind == TaskChanged.Types.Kind.SnapshotEnd)
                    {
                        batcher.Add(StreamItem<TaskInfo, TaskChanged>.Of(snapshot));
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
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            delay = TimeSpan.FromTicks(Math.Min(MaxDelay.Ticks, delay.Ticks * 2));
        }
    }

    /// <summary>UI thread: applies a batch in order; consecutive changes go to the store as one batch.</summary>
    private void ApplyDevices(IReadOnlyList<StreamItem<Device, DeviceChanged>> items)
    {
        var changes = new List<DeviceChanged>();
        foreach (StreamItem<Device, DeviceChanged> item in items)
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

    private void ApplyTasks(IReadOnlyList<StreamItem<TaskInfo, TaskChanged>> items)
    {
        var changes = new List<TaskChanged>();
        foreach (StreamItem<TaskInfo, TaskChanged> item in items)
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
    private sealed record StreamItem<TItem, TChange>(IReadOnlyList<TItem>? Snapshot, TChange? Change)
        where TChange : class
    {
        public static StreamItem<TItem, TChange> Of(IReadOnlyList<TItem> snapshot) => new(snapshot, null);

        public static StreamItem<TItem, TChange> Of(TChange change) => new(null, change);
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
