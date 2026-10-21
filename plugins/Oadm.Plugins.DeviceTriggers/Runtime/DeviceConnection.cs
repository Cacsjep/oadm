using System.Globalization;
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
/// every event on. Trigger changes of this device set a new filter on the open socket; the event list is read again every
/// 5 minutes. At most <see cref="DeviceTriggersOptions.ConnectParallelism"/> devices connect at once. A filter entry the
/// device refuses is a problem of its triggers only; a broken session is a problem of every trigger; reconnects wait 1, 2,
/// 4, 8, 10 s.
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
    /// <summary>
    /// Devices not contacted: the refused statuses, and devices the status poll found not answering (in an outage no
    /// connection attempts; the device connects again once a poll sees it Ok).
    /// </summary>
    internal static string? RefusalFor(DeviceStatus status) => DeviceMessages.Refusal(status, refuseUnreachable: true);

    /// <summary>Why <paramref name="device"/> is not contacted ("Site offline: Building B" while its site is offline).</summary>
    internal static string? RefusalFor(IDeviceInfo device) => DeviceMessages.Refusal(device, refuseUnreachable: true);

    /// <summary>A short user text for a failed session.</summary>
    internal static string Describe(Exception ex, TimeSpan timeout) => ex switch
    {
        UnauthorizedAccessException { Message: DeviceMessages.Forbidden } => DeviceMessages.Forbidden,
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

                var refusal = RefusalFor(device)
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

    /// <summary>
    /// One connection. Returns when no trigger is left; throws when it breaks. A filter entry the device refuses (error
    /// 2104) is a problem of its triggers only: with several entries each is tried alone once, then the accepted ones are
    /// set; the refused ones stay out for the rest of the session.
    /// </summary>
    private async Task RunSessionAsync(TaskCompletionSource signal, CancellationToken ct)
    {
        IVapixClient vapix;
        HashSet<string> offered;
        WebSocket connected;
        await _runtime.ConnectGate.WaitAsync(ct).ConfigureAwait(false); // at most 16 devices connect at once
        try
        {
            vapix = await _ctx.Vapix.CreateAsync(_deviceId, ct).ConfigureAwait(false); // cached by the host, never disposed here
            offered = await ReadOfferedAsync(vapix, ct).ConfigureAwait(false);
            connected = await vapix.ConnectWebSocketAsync(DeviceTriggersPluginInfo.StreamPath, _options.KeepAlive, ct).ConfigureAwait(false);
        }
        finally
        {
            _runtime.ConnectGate.Release();
        }

        using var socket = connected;
        var nextRecheck = _time.GetUtcNow() + _options.RecheckInterval;
        LogConnected(Logger, _deviceId);

        // Topics of explicitly chosen triggers that the last read of the event list did not have: not read again on every
        // trigger change, only when a new one is missing (devices of a tag or all devices never cause a read).
        var confirmedMissing = _runtime.TriggersFor(_deviceId).Where(t => !offered.Contains(t.Topic)).Select(t => t.Topic).ToHashSet(StringComparer.Ordinal);
        var refused = new Dictionary<string, string>(StringComparer.Ordinal); // filter entry key -> the device's error
        Task<string?>? receive = null;
        IReadOnlyList<TriggerDefinition> filtered = [];
        IReadOnlyList<FilterEntry> sent = [];
        Queue<FilterEntry>? probe = null; // entries tried one at a time after a refused list
        FilterEntry? probing = null;
        var afterProbe = false;
        List<StreamNotification>? report = null;
        DateTimeOffset? reportDue = null;
        var awaitingReply = false;
        var sequence = 0;
        var context = string.Empty;
        try
        {
            async Task SendAsync(IReadOnlyList<FilterEntry> entries)
            {
                sent = entries;
                awaitingReply = true;
                reportDue = _time.GetUtcNow() + _options.RequestTimeout;
                context = "oadm-" + (++sequence).ToString(CultureInfo.InvariantCulture); // answers to older lists are ignored
                await socket.SendAsync(Encoding.UTF8.GetBytes(EventStreamJson.Configure(entries, context)), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
            }

            async Task ConfigureAsync(bool probed = false)
            {
                probe = null;
                probing = null;
                afterProbe = probed;
                var triggers = _runtime.TriggersFor(_deviceId);
                if (triggers.Any(t => !offered.Contains(t.Topic) && !t.Implicit.Contains(_deviceId) && !confirmedMissing.Contains(t.Topic)))
                {
                    offered = await ReadOfferedAsync(vapix, ct).ConfigureAwait(false); // a trigger for an event read before this session
                    confirmedMissing.Clear();
                }

                confirmedMissing.UnionWith(triggers.Where(t => !offered.Contains(t.Topic)).Select(t => t.Topic));
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

                var entries = EventStreamJson.Entries(triggers.Where(t => offered.Contains(t.Topic)));
                foreach (var entry in entries)
                {
                    if (refused.TryGetValue(entry.Key, out var error))
                    {
                        _runtime.Problem(_deviceId, [.. entry.Triggers.Select(t => t.Id)], RefusedText(error));
                    }
                }

                var usable = entries.Where(e => !refused.ContainsKey(e.Key)).ToList();
                filtered = [.. usable.SelectMany(e => e.Triggers)];
                if (usable.Count == 0)
                {
                    report = null; // nothing to ask for until the event is back (next check of the event list)
                    reportDue = null;
                    awaitingReply = false;
                    return;
                }

                report = [];
                await SendAsync(usable).ConfigureAwait(false);
            }

            async Task NextProbeAsync()
            {
                if (probe!.Count == 0)
                {
                    await ConfigureAsync(probed: true).ConfigureAwait(false); // the accepted entries together
                    return;
                }

                probing = probe.Dequeue();
                await SendAsync([probing]).ConfigureAwait(false);
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
                    if (message.IsConfigureReply && message.Context is { } answered && answered != context)
                    {
                        continue; // the answer to a list that was replaced since
                    }

                    if (message.IsConfigureReply)
                    {
                        awaitingReply = false;
                        if (probe is not null)
                        {
                            if (message.Error is { } probeError)
                            {
                                refused[probing!.Key] = probeError;
                            }

                            await NextProbeAsync().ConfigureAwait(false);
                        }
                        else if (message.Error is { } error)
                        {
                            if (afterProbe || sent.Count == 0)
                            {
                                throw new InvalidOperationException(RefusedText(error)); // every entry alone was accepted
                            }

                            if (sent.Count == 1)
                            {
                                refused[sent[0].Key] = error;
                                await ConfigureAsync().ConfigureAwait(false);
                            }
                            else
                            {
                                report = null; // states that arrive while probing are not the report
                                probe = new Queue<FilterEntry>(sent);
                                await NextProbeAsync().ConfigureAwait(false);
                            }
                        }
                        else
                        {
                            afterProbe = false;
                            reportDue = _time.GetUtcNow() + _options.SettleTime;
                        }
                    }
                    else if (message.Notification is { } notification && probe is null)
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
                    confirmedMissing.Clear();
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

    /// <summary>"The device refused the event filter: Could not use supplied event filter".</summary>
    internal static string RefusedText(string error) => "The device refused the event filter: " + error.Trim().TrimEnd('.');

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
