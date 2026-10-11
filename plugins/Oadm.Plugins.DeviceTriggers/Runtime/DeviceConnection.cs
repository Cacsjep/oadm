using System.Net.Http;
using System.Net.WebSockets;
using System.Text;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Plugins.DeviceTriggers.Device;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.DeviceTriggers.Runtime;

/// <summary>
/// The permanent event stream of one device for all its turned-on triggers. Each session: read the device's event list
/// (events that are gone are a problem of their triggers), open the WebSocket (session token, pinned certificate, ping
/// every 10 s), set one filter per topic, take what arrives within the settle time as the device's report, then pass
/// every event on. Trigger changes set a new filter on the open socket; the event list is read again every 5 minutes.
/// A broken session is a problem of every trigger; reconnects wait 1, 2, 4, 8, 10 s.
/// </summary>
internal sealed partial class DeviceConnection : IAsyncDisposable
{
    private const int MaxMessageBytes = 1024 * 1024;

    private readonly Guid _deviceId;
    private readonly TriggerRuntime _runtime;
    private readonly ICorePluginContext _ctx;
    private readonly DeviceTriggersOptions _options;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _stop = new();
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task _loop = Task.CompletedTask;
    private int _disposed;
    private bool _reportedThisSession;

    public DeviceConnection(Guid deviceId, TriggerRuntime runtime, ICorePluginContext ctx, DeviceTriggersOptions options, TimeProvider time)
    {
        _deviceId = deviceId;
        _runtime = runtime;
        _ctx = ctx;
        _options = options;
        _time = time;
    }

    private ILogger Logger => _ctx.Logger ?? NullLogger.Instance;

    public void Start() => _loop = Task.Run(LoopAsync, CancellationToken.None);

    /// <summary>The device's triggers changed: set a new filter (or stop when none is left).</summary>
    public void Signal() => Volatile.Read(ref _changed).TrySetResult();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

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

    /// <summary>The refusal text for a device status, or null when OADM may connect.</summary>
    internal static string? RefusalFor(DeviceStatus status) => status switch
    {
        DeviceStatus.CertificateChanged => DeviceMessages.CertificateChanged,
        DeviceStatus.CredentialsRequired => DeviceMessages.CredentialsRequired,
        DeviceStatus.PasswordNotSet => DeviceMessages.PasswordNotSet,
        _ => null,
    };

    /// <summary>A short user text for a failed session.</summary>
    internal static string Describe(Exception ex, TimeSpan timeout) => ex switch
    {
        UnauthorizedAccessException => DeviceMessages.Unauthorized,
        TaskCanceledException or TimeoutException => DeviceMessages.Timeout(timeout),
        HttpRequestException http => DeviceMessages.Unreachable(http.InnerException?.Message ?? http.Message),
        WebSocketException => "The connection to the device broke",
        _ => ex.Message,
    };

    private TaskCompletionSource ResetSignal()
    {
        var current = Volatile.Read(ref _changed);
        if (current.Task.IsCompleted)
        {
            Interlocked.CompareExchange(ref _changed, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), current);
        }

        return Volatile.Read(ref _changed);
    }

    private async Task LoopAsync()
    {
        var ct = _stop.Token;
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            var signal = ResetSignal();
            if (_runtime.TriggersFor(_deviceId).Count == 0)
            {
                await WaitAsync(signal.Task, Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
                continue;
            }

            TimeSpan wait;
            try
            {
                var device = await _ctx.Devices.FindAsync(_deviceId, ct).ConfigureAwait(false);
                if (device is null)
                {
                    _runtime.Problem(_deviceId, null, DeviceMessages.Removed);
                    _runtime.DeviceGone(_deviceId);
                    await WaitAsync(signal.Task, _options.StatusRetry, ct).ConfigureAwait(false);
                    continue;
                }

                var refusal = RefusalFor(device.Status)
                    ?? (device.Apis.Count > 0 && !device.Apis.Supports(DeviceTriggersPluginInfo.ApiId, DeviceTriggersPluginInfo.ApiVersion) ? DeviceTriggersPluginInfo.NotSupportedReason : null);
                if (refusal is not null)
                {
                    _runtime.Problem(_deviceId, null, refusal);
                    await WaitAsync(signal.Task, _options.StatusRetry, ct).ConfigureAwait(false);
                    continue;
                }

                _runtime.Connecting(_deviceId);
                _reportedThisSession = false;
                await RunSessionAsync(signal, ct).ConfigureAwait(false);
                failures = 0;
                wait = TimeSpan.Zero;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // A broken session is a problem of the triggers; the loop reconnects.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                failures = _reportedThisSession ? 0 : failures; // a stream that worked starts the waits again at 1 s
                var text = Describe(ex, _options.RequestTimeout);
                _runtime.Problem(_deviceId, null, text);
                LogSessionFailed(Logger, _deviceId, text);
                wait = _options.Backoff[Math.Min(failures, _options.Backoff.Count - 1)];
                failures++;
            }

            if (wait > TimeSpan.Zero)
            {
                await WaitAsync(Task.Delay(Timeout.Infinite, ct), wait, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Waits for <paramref name="signal"/> or <paramref name="delay"/> (infinite allowed).</summary>
    private async Task WaitAsync(Task signal, TimeSpan delay, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var timer = Task.Delay(delay, _time, cts.Token);
        await Task.WhenAny(signal, timer).ConfigureAwait(false);
        await cts.CancelAsync().ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
    }

    /// <summary>One connection. Returns when no trigger is left; throws when it breaks.</summary>
    private async Task RunSessionAsync(TaskCompletionSource signal, CancellationToken ct)
    {
        var vapix = await _ctx.Vapix.CreateAsync(_deviceId, ct).ConfigureAwait(false); // cached by the host, never disposed here
        var offered = await ReadOfferedAsync(vapix, ct).ConfigureAwait(false);
        var nextRecheck = _time.GetUtcNow() + _options.RecheckInterval;
        using var socket = await vapix.ConnectWebSocketAsync(DeviceTriggersPluginInfo.StreamPath, _options.KeepAlive, ct).ConfigureAwait(false);
        LogConnected(Logger, _deviceId);

        Task<string?>? receive = null;
        IReadOnlyList<TriggerDefinition> filtered = [];
        List<StreamNotification>? report = null;
        DateTimeOffset? reportDue = null;
        var awaitingReply = false;
        try
        {
            async Task ConfigureAsync()
            {
                var triggers = _runtime.TriggersFor(_deviceId);
                if (triggers.Any(t => !offered.Contains(t.Topic)))
                {
                    offered = await ReadOfferedAsync(vapix, ct).ConfigureAwait(false); // a trigger for an event read before this session
                }

                var missing = triggers.Where(t => !offered.Contains(t.Topic) && !t.Implicit.Contains(_deviceId)).Select(t => t.Id).ToList();
                if (missing.Count > 0)
                {
                    _runtime.Problem(_deviceId, missing, "The event is no longer on the device");
                }

                var notOffered = triggers.Where(t => !offered.Contains(t.Topic) && t.Implicit.Contains(_deviceId)).Select(t => t.Id).ToList();
                if (notOffered.Count > 0)
                {
                    _runtime.NotOffered(_deviceId, notOffered); // a device of a tag or of all devices without this event
                }

                filtered = [.. triggers.Where(t => offered.Contains(t.Topic))];
                if (filtered.Count == 0)
                {
                    report = null; // nothing to ask for until the event is back (next check of the event list)
                    reportDue = null;
                    awaitingReply = false;
                    return;
                }

                report = [];
                awaitingReply = true;
                reportDue = _time.GetUtcNow() + _options.RequestTimeout;
                await socket.SendAsync(Encoding.UTF8.GetBytes(EventStreamJson.Configure(filtered)), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
            }

            await ConfigureAsync().ConfigureAwait(false);
            while (true)
            {
                if (_runtime.TriggersFor(_deviceId).Count == 0)
                {
                    return;
                }

                receive ??= ReceiveTextAsync(socket, ct);
                var now = _time.GetUtcNow();
                var due = reportDue is { } r && r < nextRecheck ? r : nextRecheck;
                using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var delay = Task.Delay(due > now ? due - now : TimeSpan.Zero, _time, delayCts.Token);
                var done = await Task.WhenAny(receive, delay, signal.Task).ConfigureAwait(false);
                await delayCts.CancelAsync().ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();

                if (done == receive)
                {
                    var text = await receive.ConfigureAwait(false) ?? throw new WebSocketException("The device closed the connection.");
                    receive = null;
                    var message = EventStreamJson.Parse(text);
                    if (message.IsConfigureReply)
                    {
                        if (message.Error is { } error)
                        {
                            throw new InvalidOperationException("The device refused the event filter: " + error);
                        }

                        awaitingReply = false;
                        reportDue = _time.GetUtcNow() + _options.SettleTime;
                    }
                    else if (message.Notification is { } notification)
                    {
                        if (report is not null)
                        {
                            report.Add(notification);
                        }
                        else
                        {
                            _runtime.Notify(_deviceId, notification);
                        }
                    }
                }
                else if (done == signal.Task)
                {
                    signal = ResetSignal();
                    await ConfigureAsync().ConfigureAwait(false);
                }
                else if (reportDue is { } reportAt && _time.GetUtcNow() >= reportAt)
                {
                    if (awaitingReply)
                    {
                        throw new TimeoutException("The device did not answer the event filter.");
                    }

                    _runtime.Report(_deviceId, filtered, report ?? []);
                    report = null;
                    reportDue = null;
                    _reportedThisSession = true;
                }
                else if (_time.GetUtcNow() >= nextRecheck)
                {
                    nextRecheck = _time.GetUtcNow() + _options.RecheckInterval;
                    var before = offered;
                    offered = await ReadOfferedAsync(vapix, ct).ConfigureAwait(false);
                    var triggers = _runtime.TriggersFor(_deviceId);
                    if (triggers.Any(t => before.Contains(t.Topic) != offered.Contains(t.Topic)))
                    {
                        await ConfigureAsync().ConfigureAwait(false);
                    }
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

    private async Task<HashSet<string>> ReadOfferedAsync(IVapixClient vapix, CancellationToken ct) =>
        [.. (await EventDeclarations.ReadAsync(vapix, _options.RequestTimeout, ct).ConfigureAwait(false)).Select(e => e.Topic)];

    /// <summary>One whole text message, null when the device closed the connection.</summary>
    internal static async Task<string?> ReceiveTextAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            message.Write(buffer, 0, result.Count);
            if (message.Length > MaxMessageBytes)
            {
                throw new InvalidOperationException("The device sent a message larger than 1 MB.");
            }

            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Device triggers: event stream of device {DeviceId} connected")]
    private static partial void LogConnected(ILogger logger, Guid deviceId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Device triggers: event stream of device {DeviceId} failed: {Problem}")]
    private static partial void LogSessionFailed(ILogger logger, Guid deviceId, string problem);
}
