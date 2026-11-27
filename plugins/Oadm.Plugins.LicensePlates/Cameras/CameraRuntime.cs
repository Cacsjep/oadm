using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Plugins.LicensePlates.Alpv;
using Oadm.Plugins.LicensePlates.Reads;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.LicensePlates.Cameras;

/// <summary>Timing of the camera connections; tests shorten them.</summary>
public sealed class CameraOptions
{
    /// <summary>One request to the camera (ALPV's calls, the session token, the filter's answer).</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>A camera OADM may not contact (status) is looked at again this often.</summary>
    public TimeSpan StatusRetry { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Waits between reconnects: 1, 2, 4, 8, then 10 s.</summary>
    public IReadOnlyList<TimeSpan> Backoff { get; init; } = EventStreamSocket.Backoff;

    /// <summary>WebSocket ping (10 s).</summary>
    public TimeSpan KeepAlive { get; init; } = EventStreamSocket.KeepAlive;

    /// <summary>Cameras with a problem are checked again this often (5 min).</summary>
    public TimeSpan ProblemRecheck { get; init; } = LicensePlateLimits.ProblemRecheck;

    /// <summary>Cameras that connect at the same time (16).</summary>
    public int ConnectParallelism { get; init; } = LicensePlateLimits.ConnectParallelism;
}

/// <summary>
/// The cameras of the plugin at work (docs/spec/license-plate-center.md "Server"): one permanent event connection per camera
/// (the VAPIX event WebSocket with a filter on ALPV's New, Update and Lost, through the host on the device's route), 16
/// connecting at once, reconnects after 1, 2, 4, 8, then 10 s, a ping every 10 s; devices that are Unreachable, Credentials
/// required or Certificate changed are not contacted. ALPV (the application list, the configuration, the event switches)
/// is read when a camera is added, on Refresh, after a task and every 5 minutes for a camera with a problem; never for a
/// healthy camera in between. Reads during a lost connection are lost; the loss becomes a gap row of the log.
/// </summary>
internal sealed partial class CameraRuntime : IAsyncDisposable
{
    private readonly ICorePluginContext _ctx;
    private readonly CameraOptions _options;
    private readonly TimeProvider _time;
    private readonly Action<Guid, AlpvEvent> _onEvent;
    private readonly ConcurrentDictionary<Guid, Connection> _connections = new();
    private readonly ConcurrentDictionary<Guid, CameraState> _states = new();

    public CameraRuntime(ICorePluginContext ctx, CameraOptions options, TimeProvider time, Action<Guid, AlpvEvent> onEvent)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _onEvent = onEvent ?? throw new ArgumentNullException(nameof(onEvent));
        ConnectGate = new SemaphoreSlim(Math.Max(1, options.ConnectParallelism));
    }

    /// <summary>At most <see cref="CameraOptions.ConnectParallelism"/> cameras check and connect at once.</summary>
    internal SemaphoreSlim ConnectGate { get; }

    /// <summary>The cameras whose state changed (status, image settings, last read).</summary>
    public event Action<IReadOnlyCollection<Guid>>? Changed;

    /// <summary>A camera is connected again after it had lost its connection for 30 s: the gap (from, until).</summary>
    public event Action<Guid, DateTimeOffset, DateTimeOffset>? Reconnected;

    private ILogger Logger => _ctx.Logger ?? NullLogger.Instance;

    public CameraState StateOf(Guid camera) => _states.GetValueOrDefault(camera) ?? new CameraState();

    /// <summary>Starts connections for new cameras and stops those of cameras that left.</summary>
    public async Task SyncAsync(IReadOnlyCollection<Guid> cameras)
    {
        ArgumentNullException.ThrowIfNull(cameras);
        var wanted = cameras as IReadOnlySet<Guid> ?? cameras.ToHashSet();
        foreach (var id in wanted)
        {
            if (!_connections.ContainsKey(id))
            {
                var connection = new Connection(id, this);
                if (_connections.TryAdd(id, connection))
                {
                    _states.TryAdd(id, new CameraState());
                    connection.Start();
                }
            }
        }

        foreach (var (id, connection) in _connections.ToArray())
        {
            if (!wanted.Contains(id) && _connections.TryRemove(id, out _))
            {
                _states.TryRemove(id, out _);
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>ALPV of these cameras is read again (Refresh, after a task).</summary>
    public void Recheck(IEnumerable<Guid> cameras)
    {
        ArgumentNullException.ThrowIfNull(cameras);
        foreach (var id in cameras)
        {
            if (_connections.TryGetValue(id, out var connection))
            {
                connection.RequestCheck();
            }
        }
    }

    /// <summary>A read of the camera was decided (the Set up dialog's last read).</summary>
    public void MarkRead(Guid camera, DateTimeOffset time)
    {
        // Compare and swap like Update: a connection changing the state at the same time is never overwritten.
        while (_states.TryGetValue(camera, out var state) && !_states.TryUpdate(camera, state with { LastRead = time }, state))
        {
        }
    }

    /// <summary>Once a second: a camera without a connection for 30 s shows Connection lost.</summary>
    public void Tick()
    {
        var now = _time.GetUtcNow();
        var changed = new List<Guid>();
        foreach (var (id, state) in _states.ToArray())
        {
            if (state is { Status: CameraStatus.Connected or CameraStatus.EventsOff or CameraStatus.Connecting, LostSince: { } since } && now - since >= LicensePlateLimits.LostDelay
                && _states.TryUpdate(id, state with { Status = CameraStatus.ConnectionLost, Detail = null }, state))
            {
                changed.Add(id);
            }
        }

        if (changed.Count > 0)
        {
            Changed?.Invoke(changed);
        }
    }

    internal void Update(Guid camera, Func<CameraState, CameraState> change)
    {
        if (!_connections.ContainsKey(camera))
        {
            return;
        }

        CameraState before, after;
        do
        {
            before = _states.GetValueOrDefault(camera) ?? new CameraState();
            after = change(before);
            if (after == before)
            {
                return;
            }
        }
        while (!_states.TryUpdate(camera, after, before) && _states.ContainsKey(camera));

        Changed?.Invoke([camera]);
    }

    /// <summary>A camera that cannot be reached since now (keeps the first time of a loss).</summary>
    private void Lost(Guid camera, CameraStatus status, string? detail) =>
        Update(camera, s =>
        {
            var since = s.LostSince ?? _time.GetUtcNow();
            var lostLong = _time.GetUtcNow() - since >= LicensePlateLimits.LostDelay;
            var shown = status == CameraStatus.ConnectionLost && !lostLong ? (s.WasConnected ? s.Status : CameraStatus.Connecting) : status;
            return s with { Status = shown, Detail = detail, LostSince = since };
        });

    public async ValueTask DisposeAsync()
    {
        foreach (var id in _connections.Keys.ToArray())
        {
            if (_connections.TryRemove(id, out var connection))
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }

        _states.Clear();
        ConnectGate.Dispose();
    }

    /// <summary>The loop of one camera.</summary>
    private sealed class Connection(Guid id, CameraRuntime runtime) : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private TaskCompletionSource _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task _loop = Task.CompletedTask;
        private volatile bool _checkWanted = true;

        /// <summary>Failed connections in a row; back to 0 once the camera answered the event filter.</summary>
        private int _failures;

        /// <summary>Starts the loop without the user's call that added the camera: it is background work of the server.</summary>
        public void Start()
        {
            using (ExecutionContext.SuppressFlow())
            {
                _loop = Task.Run(LoopAsync, CancellationToken.None);
            }
        }

        public void RequestCheck()
        {
            _checkWanted = true;
            Volatile.Read(ref _signal).TrySetResult();
        }

        private TaskCompletionSource ResetSignal()
        {
            var current = Volatile.Read(ref _signal);
            if (current.Task.IsCompleted)
            {
                Interlocked.CompareExchange(ref _signal, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), current);
            }

            return Volatile.Read(ref _signal);
        }

        private async Task WaitAsync(Task signal, TimeSpan delay, CancellationToken ct)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var timer = Task.Delay(delay, runtime._time, cts.Token);
            await Task.WhenAny(signal, timer).ConfigureAwait(false);
            await cts.CancelAsync().ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }

        private async Task LoopAsync()
        {
            var ct = _stop.Token;
            while (!ct.IsCancellationRequested)
            {
                var signal = ResetSignal();
                TimeSpan wait;
                try
                {
                    var device = await runtime._ctx.System.Devices.FindAsync(id, ct).ConfigureAwait(false);
                    if (device is null)
                    {
                        runtime.Lost(id, CameraStatus.DeviceProblem, DeviceMessages.Removed);
                        await WaitAsync(signal.Task, runtime._options.StatusRetry, ct).ConfigureAwait(false);
                        continue;
                    }

                    if (EventStreamSocket.RefusalFor(device) is { } refusal)
                    {
                        if (device.Status == DeviceStatus.SiteOffline)
                        {
                            runtime.Lost(id, CameraStatus.SiteOffline, device.SiteName);
                        }
                        else
                        {
                            runtime.Lost(id, CameraStatus.DeviceProblem, refusal);
                        }

                        await WaitAsync(signal.Task, runtime._options.StatusRetry, ct).ConfigureAwait(false);
                        continue;
                    }

                    if (device.Apis.Count > 0 && !device.Apis.Supports(EventStreamSocket.ApiId, EventStreamSocket.ApiVersion))
                    {
                        runtime.Lost(id, CameraStatus.DeviceProblem, "Cannot send events to OADM (update AXIS OS)");
                        await WaitAsync(signal.Task, runtime._options.ProblemRecheck, ct).ConfigureAwait(false);
                        continue;
                    }

                    var state = runtime.StateOf(id);
                    var stale = state.Checked is not { } checkedAt || runtime._time.GetUtcNow() - checkedAt >= runtime._options.ProblemRecheck;
                    // Never a connection before ALPV was checked: a check that failed is wanted again (it stays wanted until it succeeds).
                    if (_checkWanted || state.Checked is null || (stale && state.Status is not (CameraStatus.Connected or CameraStatus.Connecting)))
                    {
                        _checkWanted = true;
                        await CheckAsync(ct).ConfigureAwait(false);
                        _checkWanted = false;
                    }

                    if (runtime.StateOf(id).Status is CameraStatus.AlpvNotInstalled or CameraStatus.AlpvNeedsVersion3 or CameraStatus.AlpvNoLicense or CameraStatus.AlpvStopped or CameraStatus.OtherSensor)
                    {
                        await WaitAsync(signal.Task, runtime._options.ProblemRecheck, ct).ConfigureAwait(false);
                        _checkWanted = true;
                        continue;
                    }

                    await RunSessionAsync(signal, ct).ConfigureAwait(false);
                    wait = TimeSpan.Zero;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
#pragma warning disable CA1031 // A broken session is the camera's problem; the loop reconnects.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    var text = ex is AlpvException ? ex.Message : EventStreamSocket.Describe(ex, runtime._options.RequestTimeout);
                    runtime.Lost(id, CameraStatus.ConnectionLost, text);
                    LogSessionFailed(runtime.Logger, id, text);
                    wait = EventStreamSocket.WaitAfter(_failures, runtime._options.Backoff);
                    _failures++;
                }

                if (wait > TimeSpan.Zero)
                {
                    await WaitQuietlyAsync(wait, ct).ConfigureAwait(false);
                }
            }
        }

        private async Task WaitQuietlyAsync(TimeSpan wait, CancellationToken ct)
        {
            try
            {
                await Task.Delay(wait, runtime._time, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        /// <summary>Reads ALPV of the camera (read only): installed, version, license, running, the sensor, the image settings, the switches.</summary>
        private async Task CheckAsync(CancellationToken ct)
        {
            await runtime.ConnectGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(runtime._options.RequestTimeout * 3);
                var vapix = await runtime._ctx.System.Vapix.CreateAsync(id, timeout.Token).ConfigureAwait(false);
                var alpv = new AlpvClient(vapix);
                var app = await alpv.AppAsync(timeout.Token).ConfigureAwait(false);
                var now = runtime._time.GetUtcNow();
                if (AlpvClient.Problem(app) is { } problem)
                {
                    runtime.Update(id, s => s with { Status = problem, Detail = null, AlpvVersion = app?.Version, Checked = now });
                    return;
                }

                var config = await alpv.ReadConfigAsync(timeout.Token).ConfigureAwait(false);
                if (!config.ReadsFirstSensor)
                {
                    runtime.Update(id, s => s with { Status = CameraStatus.OtherSensor, Detail = null, AlpvVersion = app!.Version, Image = config.Image, Checked = now });
                    return;
                }

                var events = AlpvClient.SwitchesOf(await alpv.ReadEventsAsync(timeout.Token).ConfigureAwait(false));
                runtime.Update(id, s => s with
                {
                    AlpvVersion = app!.Version,
                    Image = config.Image,
                    Events = events,
                    Checked = now,
                    Status = s.Status is CameraStatus.Connected or CameraStatus.EventsOff
                        ? events.AllOn ? CameraStatus.Connected : CameraStatus.EventsOff
                        : s.WasConnected && s.Status == CameraStatus.ConnectionLost ? s.Status : CameraStatus.Connecting,
                    Detail = null,
                });
            }
            finally
            {
                runtime.ConnectGate.Release();
            }
        }

        /// <summary>One connection; returns when a check is wanted (true: it was connected), throws when it breaks.</summary>
        private async Task<bool> RunSessionAsync(TaskCompletionSource signal, CancellationToken ct)
        {
            WebSocket connected;
            await runtime.ConnectGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var vapix = await runtime._ctx.System.Vapix.CreateAsync(id, ct).ConfigureAwait(false);
                connected = await vapix.ConnectWebSocketAsync(EventStreamSocket.StreamPath, runtime._options.KeepAlive, ct).ConfigureAwait(false);
            }
            finally
            {
                runtime.ConnectGate.Release();
            }

            using var socket = connected;
            Task<string?>? receive = null;
            try
            {
                var context = "oadm-lpc";
                var filter = EventStreamJson.ConfigureTopics(AlpvEvent.Topics.Select(t => new KeyValuePair<string, string?>(t, null)), context);
                await socket.SendAsync(Encoding.UTF8.GetBytes(filter), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
                var answered = false;
                var connectedAt = runtime._time.GetUtcNow();
                var replyDue = runtime._time.GetUtcNow() + runtime._options.RequestTimeout;
                while (true)
                {
                    receive ??= EventStreamSocket.ReceiveTextAsync(socket, ct);
                    using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    // Before the filter's answer: its timeout. Connected with ALPV's events off: ALPV is read again in 5 minutes.
                    DateTimeOffset? due = !answered ? replyDue : runtime.StateOf(id).Status == CameraStatus.EventsOff ? connectedAt + runtime._options.ProblemRecheck : null;
                    var waitFor = due is { } at ? (at - runtime._time.GetUtcNow()) is var left && left > TimeSpan.Zero ? left : TimeSpan.Zero : Timeout.InfiniteTimeSpan;
                    var delay = Task.Delay(waitFor, runtime._time, delayCts.Token);
                    var done = await Task.WhenAny(receive, delay, signal.Task).ConfigureAwait(false);
                    await delayCts.CancelAsync().ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    if (done == signal.Task)
                    {
                        return answered; // a check is wanted: the loop reads ALPV and connects again
                    }

                    if (done == delay)
                    {
                        if (answered)
                        {
                            _checkWanted = true;
                            return true;
                        }

                        throw new TimeoutException("The camera did not answer the event filter.");
                    }

                    var text = await receive.ConfigureAwait(false) ?? throw new WebSocketException("The camera closed the connection.");
                    receive = null;
                    var message = EventStreamJson.Parse(text);
                    if (message.IsConfigureReply)
                    {
                        if (message.Context is { } c && c != context)
                        {
                            continue;
                        }

                        if (message.Error is { } error)
                        {
                            throw new InvalidOperationException("The camera refused the event filter: " + error.Trim().TrimEnd('.'));
                        }

                        answered = true;
                        connectedAt = runtime._time.GetUtcNow();
                        _failures = 0; // a session that breaks later starts again at the first wait
                        Connected();
                        continue;
                    }

                    if (message.Notification is { } notification && AlpvEvent.Of(notification) is { } alpv)
                    {
                        runtime._onEvent(id, alpv);
                    }
                }
            }
            finally
            {
                if (receive is not null)
                {
                    _ = receive.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                }

                if (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    try
                    {
                        using var closing = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, closing.Token).ConfigureAwait(false);
                    }
#pragma warning disable CA1031 // Closing is best effort.
                    catch (Exception)
#pragma warning restore CA1031
                    {
                    }
                }
            }
        }

        /// <summary>The filter was accepted: connected (events off when ALPV's switches are off); a long loss becomes a gap.</summary>
        private void Connected()
        {
            var now = runtime._time.GetUtcNow();
            var before = runtime.StateOf(id);
            runtime.Update(id, s => s with
            {
                Status = s.Events is { AllOn: false } ? CameraStatus.EventsOff : CameraStatus.Connected,
                Detail = null,
                LostSince = null,
                WasConnected = true,
            });
            if (before is { WasConnected: true, LostSince: { } since } && now - since >= LicensePlateLimits.LostDelay)
            {
                runtime.Reconnected?.Invoke(id, since, now);
            }

            LogConnected(runtime.Logger, id);
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync().ConfigureAwait(false);
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            _stop.Dispose();
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "License Plate Center: camera {DeviceId} connected")]
    private static partial void LogConnected(ILogger logger, Guid deviceId);

    [LoggerMessage(Level = LogLevel.Information, Message = "License Plate Center: camera {DeviceId} lost its connection: {Problem}")]
    private static partial void LogSessionFailed(ILogger logger, Guid deviceId, string problem);
}
