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

    private async Task DeviceLoopAsync(CancellationToken ct)
    {
        TimeSpan delay = MinDelay;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                IReadOnlyList<Device> snapshot = await _api.ListDevicesAsync(ct).ConfigureAwait(false);
                _ui.Post(() =>
                {
                    _devices.Reset(snapshot);
                    SetState(ConnectionState.Connected, "Connected to " + _api.ServerAddress);
                    Connected?.Invoke(this, EventArgs.Empty);
                });
                LogConnected(_logger, _api.ServerAddress);
                delay = MinDelay;

                await foreach (DeviceChanged change in _api.WatchDevicesAsync(ct).ConfigureAwait(false))
                {
                    _ui.Post(() => _devices.Apply(change));
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

    private async Task TaskLoopAsync(CancellationToken ct)
    {
        TimeSpan delay = MinDelay;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                IReadOnlyList<TaskInfo> snapshot = await _api.ListTasksAsync(ct).ConfigureAwait(false);
                _ui.Post(() => _tasks.Reset(snapshot));
                delay = MinDelay;
                await foreach (TaskChanged change in _api.WatchTasksAsync(ct).ConfigureAwait(false))
                {
                    _ui.Post(() => _tasks.Apply(change));
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
