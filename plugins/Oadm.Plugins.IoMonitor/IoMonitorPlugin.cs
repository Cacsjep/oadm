using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.IoMonitor;

/// <summary>Timing and limits of the I/O Monitor; tests shorten them.</summary>
public sealed class IoMonitorOptions
{
    public TimeSpan PollInterval { get; init; } = IoMonitorPluginInfo.PollInterval;

    /// <summary>A stream without a keep-alive from its page for this long is stopped (closed client).</summary>
    public TimeSpan LeaseTimeout { get; init; } = TimeSpan.FromSeconds(20);

    public TimeSpan LeaseCheckInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>One read of the ports.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>After a failed read the next one starts this long after the failure (an unreachable device is not asked back to back).</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Devices read at the same time (each 5 requests per second); pages showing the same device share its reading.</summary>
    public int MaxStreams { get; init; } = 20;
}

/// <summary>
/// Core plugin "I/O Monitor" (off by default): the page picks one device with inputs or
/// outputs and shows their states live. While the page shows the device the server reads the ports every 200 ms
/// (<c>getPorts</c> of the I/O port management API, stored credentials) and pushes them to the page when something
/// changed. Pages showing the same device share one reading of it. Read-only for devices; no events, no actions.
/// Device groups (SDK 0.4.0): each page's stream is a job of its user (<see cref="ICorePluginContext.Jobs"/>: another user's
/// stream id answers like an unknown one), the device comes from the user's call, the reading of a device is shared only by
/// pages of the same device scope and reads through their call, so it ends when the device leaves their view.
/// </summary>
public sealed partial class IoMonitorPlugin : ICorePlugin, IAsyncDisposable
{
    private readonly IoMonitorOptions _options;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, PageStream> _streams = new(StringComparer.Ordinal);
    private readonly Dictionary<(Guid DeviceId, string Scope), DevicePoller> _pollers = [];
    private bool _accepting;
    private ICorePluginContext? _ctx;
    private CancellationTokenSource? _running;
    private Task _leaseLoop = Task.CompletedTask;

    public IoMonitorPlugin()
        : this(new IoMonitorOptions())
    {
    }

    public IoMonitorPlugin(IoMonitorOptions options, TimeProvider? time = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _time = time ?? TimeProvider.System;
    }

    public string Id => IoMonitorPluginInfo.PluginId;

    public string DisplayName => IoMonitorPluginInfo.DisplayName;

    public string? IconKey => IoMonitorPluginInfo.IconKey;

    public CorePluginGroup Group => CorePluginGroup.Monitoring;

    public IReadOnlyList<ITaskPlugin> TaskPlugins => [];

    /// <summary>Running streams, one per page (tests).</summary>
    public int StreamCount
    {
        get
        {
            lock (_gate)
            {
                return _streams.Count;
            }
        }
    }

    /// <summary>Devices being read (tests).</summary>
    public int DeviceCount
    {
        get
        {
            lock (_gate)
            {
                return _pollers.Count;
            }
        }
    }

    private ILogger Logger => _ctx?.Logger ?? NullLogger.Instance;

    public async Task StartAsync(ICorePluginContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        await StopAsync(ct).ConfigureAwait(false);
        _ctx = ctx;
        _running = new CancellationTokenSource();
        var token = _running.Token;
        _leaseLoop = Task.Run(() => ExpireLeasesAsync(token), CancellationToken.None);
        lock (_gate)
        {
            _accepting = true;
        }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        List<DevicePoller> pollers;
        List<PageStream> streams;
        lock (_gate)
        {
            _accepting = false; // a start still on its way is refused, so nothing keeps reading after the stop
            pollers = [.. _pollers.Values];
            streams = [.. _streams.Values];
            _pollers.Clear();
            _streams.Clear();
        }

        foreach (var stream in streams)
        {
            stream.Job.Cancel();
        }

        foreach (var poller in pollers)
        {
            await poller.DisposeAsync().ConfigureAwait(false);
        }

        if (_running is { } running)
        {
            _running = null;
            await running.CancelAsync().ConfigureAwait(false);
            try
            {
                await _leaseLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            running.Dispose();
        }
    }

    /// <summary>Only through the user's call (<see cref="InvokeAsync(ICallContext, string, string?, CancellationToken)"/>).</summary>
    public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) =>
        throw new NotSupportedException("Call the I/O Monitor plugin with the user's call.");

    public async Task<string?> InvokeAsync(ICallContext caller, string method, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        _ = _ctx ?? throw new InvalidOperationException("The I/O Monitor plugin is not running.");
        switch (method)
        {
            case IoMonitorMethods.Start:
                return IoMonitorJson.Serialize(await StartStreamAsync(caller, IoMonitorJson.Deserialize<StartRequest>(payloadJson).DeviceId, ct).ConfigureAwait(false));
            case IoMonitorMethods.Stop:
                var stopped = FindJob(caller, IoMonitorJson.Deserialize<StreamRequest>(payloadJson).StreamId);
                await StopStreamAsync(stopped.Id).ConfigureAwait(false);
                return null;
            case IoMonitorMethods.KeepAlive:
                return IoMonitorJson.Serialize(new KeepAliveReply(KeepAlive(FindJob(caller, IoMonitorJson.Deserialize<StreamRequest>(payloadJson).StreamId).Id)));
            default:
                throw new ArgumentException($"Unknown method '{method}'.", nameof(method));
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None).ConfigureAwait(false);

    /// <summary>The refusal text for a device status, or null when its ports may be read.</summary>
    internal static string? RefusalFor(DeviceStatus status) => status switch
    {
        DeviceStatus.CertificateChanged => DeviceMessages.CertificateChanged,
        DeviceStatus.CredentialsRequired => DeviceMessages.CredentialsRequired,
        DeviceStatus.PasswordNotSet => DeviceMessages.PasswordNotSet,
        DeviceStatus.SiteOffline => Oadm.Sdk.Sites.SiteTexts.SiteOffline(null),
        _ => null,
    };

    /// <summary>The refusal text for <paramref name="device"/>: "Site offline: Building B" while its site is offline (nothing is sent).</summary>
    internal static string? RefusalFor(IDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return device.Status == DeviceStatus.SiteOffline ? Oadm.Sdk.Sites.SiteTexts.SiteOffline(device.SiteName) : RefusalFor(device.Status);
    }


    /// <summary>The ports of a getPorts answer, in port order. Throws <see cref="InvalidOperationException"/> with the device's error.</summary>
    internal static IReadOnlyList<IoPort> ParsePorts(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("error", out var error))
        {
            throw new InvalidOperationException(error.TryGetProperty("message", out var message) ? message.GetString() : "The device refused the request.");
        }

        if (!root.TryGetProperty("data", out var data) || !data.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        static string Text(JsonElement item, string name) =>
            item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

        return
        [
            .. items.EnumerateArray()
                .Select(i => new IoPort(
                    Text(i, "port"),
                    Text(i, "name"),
                    string.Equals(Text(i, "direction"), "output", StringComparison.OrdinalIgnoreCase),
                    Text(i, "state"),
                    Text(i, "normalState"),
                    Text(i, "usage"),
                    i.TryGetProperty("configurable", out var c) && c.ValueKind == JsonValueKind.True))
                .OrderBy(p => int.TryParse(p.Port, out var n) ? n : int.MaxValue),
        ];
    }

    /// <summary>The user's stream job; another user's id and an unknown one answer alike.</summary>
    private IPluginJob FindJob(ICallContext caller, string? streamId) =>
        (streamId is null ? null : Jobs.Find(caller, streamId)) ?? throw new KeyNotFoundException(IoMonitorMethods.StreamEnded);

    private IPluginJobs Jobs => _ctx?.Jobs ?? throw new InvalidOperationException("This OADM server cannot run the I/O Monitor.");

    private async Task<StartReply> StartStreamAsync(ICallContext caller, Guid deviceId, CancellationToken ct)
    {
        var ctx = _ctx!;
        if (ctx.Events is not { } events)
        {
            return new StartReply(null, "This OADM server cannot push live updates");
        }

        var jobs = Jobs;
        var device = deviceId == Guid.Empty ? null : await caller.Devices.FindAsync(deviceId, ct).ConfigureAwait(false);
        if (device is null)
        {
            // A user with a device group: a device outside the group and an unknown one answer alike.
            return caller.HasAllDevices ? new StartReply(null, DeviceMessages.Removed) : throw new DevicesNotAvailableException();
        }

        if (RefusalFor(device) is { } refusal)
        {
            return new StartReply(null, refusal);
        }

        if (!device.Apis.Supports(IoMonitorPluginInfo.ApiId, "1.0"))
        {
            return new StartReply(null, "The device has no inputs or outputs to show");
        }

        var key = (deviceId, caller.DeviceScopeKey);
        if (TooMany(key) is { } tooMany)
        {
            return tooMany;
        }

        try
        {
            _ = await caller.Vapix.CreateAsync(deviceId, ct).ConfigureAwait(false); // reachable now; each read asks the factory again
        }
        catch (Exception ex) when (ex is not DevicesNotAvailableException && (ex is not OperationCanceledException || !ct.IsCancellationRequested))
        {
            return new StartReply(null, DeviceMessages.Unreachable(ex.Message));
        }

        // The stream is a job of the user: it ends on Stop, without keep-alives, when the plugin stops and when the user's
        // devices, role or session change (the host cancels it).
        var registered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var job = jobs.Start(caller, "stream", async (job, token) =>
        {
            if (!await registered.Task.ConfigureAwait(false))
            {
                return;
            }

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, _time, token).ConfigureAwait(false);
            }
            finally
            {
                await StopStreamAsync(job.Id).ConfigureAwait(false);
            }
        });
        var stream = new PageStream(job, deviceId, caller.DeviceScopeKey, _time);
        lock (_gate)
        {
            if (!_accepting)
            {
                registered.SetResult(false);
                return new StartReply(null, "The I/O Monitor plugin is turned off.");
            }

            // checked again with the insert: two starts must not pass the limit together
            if (!_pollers.TryGetValue(key, out var poller))
            {
                if (_pollers.Count >= _options.MaxStreams)
                {
                    registered.SetResult(false);
                    return TooManyReply();
                }

                poller = new DevicePoller(deviceId, events, _options, _time, Gone);
                _pollers[key] = poller;
                poller.Start();
            }

            _streams[stream.Id] = stream;
            poller.Add(stream.Id, job.Owner);
        }

        registered.SetResult(true);
        LogStarted(Logger, device.Address);
        return new StartReply(stream.Id, null);
    }

    /// <summary>The device left the view of the poller's pages: every stream of it ends (its job removes it).</summary>
    private void Gone(DevicePoller poller)
    {
        List<PageStream> ended;
        lock (_gate)
        {
            ended = [.. _streams.Values.Where(s => _pollers.TryGetValue((s.DeviceId, s.Scope), out var p) && ReferenceEquals(p, poller))];
        }

        foreach (var stream in ended)
        {
            stream.Job.Cancel();
        }
    }

    private StartReply? TooMany((Guid, string) key)
    {
        lock (_gate)
        {
            return _pollers.ContainsKey(key) || _pollers.Count < _options.MaxStreams ? null : TooManyReply();
        }
    }

    private StartReply TooManyReply() =>
        new(null, $"Too many devices are shown at the same time ({_options.MaxStreams}); close one first");

    /// <summary>False when the user's stream no longer runs (stopped, expired).</summary>
    private bool KeepAlive(string? streamId)
    {
        lock (_gate)
        {
            if (streamId is not null && _streams.TryGetValue(streamId, out var stream))
            {
                stream.KeepAlive();
                return true;
            }

            return false;
        }
    }

    private async Task StopStreamAsync(string? streamId)
    {
        DevicePoller? unused = null;
        PageStream? stream;
        lock (_gate)
        {
            if (streamId is null || !_streams.Remove(streamId, out stream))
            {
                return;
            }

            if (_pollers.TryGetValue((stream.DeviceId, stream.Scope), out var poller) && poller.Remove(streamId) == 0)
            {
                _pollers.Remove((stream.DeviceId, stream.Scope));
                unused = poller;
            }
        }

        stream.Job.Cancel(); // ends the user's job (outside the lock: its end runs this again, which finds nothing)
        if (unused is not null)
        {
            await unused.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task ExpireLeasesAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_options.LeaseCheckInterval, _time);
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            List<string> expired;
            lock (_gate)
            {
                expired = [.. _streams.Values.Where(s => s.SinceKeepAlive > _options.LeaseTimeout).Select(s => s.Id)];
            }

            foreach (var id in expired)
            {
                await StopStreamAsync(id).ConfigureAwait(false);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "I/O Monitor: reading the ports of {Address}")]
    private static partial void LogStarted(ILogger logger, string address);

    /// <summary>One page watching one device (a job of its user); its keep-alives hold it.</summary>
    private sealed class PageStream(IPluginJob job, Guid deviceId, string scope, TimeProvider time)
    {
        private long _keepAlive = time.GetTimestamp();

        public string Id => Job.Id;

        public IPluginJob Job { get; } = job;

        public Guid DeviceId { get; } = deviceId;

        /// <summary>The device scope of the page's user: pages share a reading only within one scope.</summary>
        public string Scope { get; } = scope;

        public TimeSpan SinceKeepAlive => time.GetElapsedTime(Interlocked.Read(ref _keepAlive));

        public void KeepAlive() => Interlocked.Exchange(ref _keepAlive, time.GetTimestamp());
    }

    /// <summary>
    /// The reading of one device for every stream of one device scope that shows it: reads every
    /// <see cref="IoMonitorOptions.PollInterval"/> (after a failure <see cref="IoMonitorOptions.RetryDelay"/> later) through
    /// the call of one of its streams' users and publishes changes to each stream's session. When the device left their view
    /// (<see cref="DevicesNotAvailableException"/>) it tells the pages and ends (<paramref name="gone"/>).
    /// </summary>
    private sealed class DevicePoller(Guid deviceId, IPluginEvents events, IoMonitorOptions options, TimeProvider time, Action<DevicePoller> gone) : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Lock _gate = new();
        private readonly Dictionary<string, ICallContext> _streamIds = new(StringComparer.Ordinal);
        private Task _loop = Task.CompletedTask;
        private string? _lastJson;
        private PortsEvent? _last;

        public void Start() => _loop = Task.Run(LoopAsync, CancellationToken.None);

        /// <summary>Adds a stream of <paramref name="owner"/>; it gets the last read at once (a second page of the same device).</summary>
        public void Add(string streamId, ICallContext owner)
        {
            lock (_gate)
            {
                _streamIds[streamId] = owner;
                if (_last is { } last)
                {
                    events.Publish(IoMonitorMethods.PortsTopic, IoMonitorJson.Serialize(last with { StreamId = streamId }), PluginEventAudience.Of(owner));
                }
            }
        }

        /// <summary>Removes a stream; returns how many are left.</summary>
        public int Remove(string streamId)
        {
            lock (_gate)
            {
                _streamIds.Remove(streamId);
                return _streamIds.Count;
            }
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

        private async Task LoopAsync()
        {
            var ct = _stop.Token;
            IReadOnlyList<IoPort> last = [];
            while (!ct.IsCancellationRequested)
            {
                var started = time.GetTimestamp();
                string? error = null;
                try
                {
                    last = await ReadAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (DevicesNotAvailableException ex)
                {
                    // The device left the view of these pages (or was removed): tell them and end.
                    Publish(last, ex.Message);
                    gone(this);
                    return;
                }
#pragma warning disable CA1031 // A failed read is shown on the page; the next read tries again.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    error = ex is OperationCanceledException ? DeviceMessages.Timeout(options.RequestTimeout) : ex.Message;
                }

                Publish(last, error);

                // success: keep the read cadence; failure: the next read 1 s after it (a timeout already took 3 s)
                var wait = error is null ? options.PollInterval - time.GetElapsedTime(started) : options.RetryDelay;
                if (wait > TimeSpan.Zero)
                {
                    try
                    {
                        await Task.Delay(wait, time, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            }
        }

        private async Task<IReadOnlyList<IoPort>> ReadAsync(CancellationToken ct)
        {
            IVapixClient client;
            try
            {
                // per read, through the call of a page's user (its view): the factory replaces (and disposes) its client
                // when the address, credentials or certificate change; it caches it, so it is never disposed here
                client = await Reader().CreateAsync(deviceId, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not DevicesNotAvailableException)
            {
                throw new InvalidOperationException(DeviceMessages.Unreachable(ex.Message), ex);
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, IoMonitorPluginInfo.Path)
            {
                Content = new StringContent("""{"apiVersion":"1.0","context":"oadm","method":"getPorts"}""", Encoding.UTF8, "application/json"),
            };
            request.Options.Set(VapixRequestOptions.Timeout, options.RequestTimeout);
            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(DeviceMessages.ForHttpStatus((int)response.StatusCode) ?? DeviceMessages.ServerError((int)response.StatusCode));
            }

            return ParsePorts(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        }

        /// <summary>The VAPIX clients of one of the streams' users (all of them share the device scope).</summary>
        private IVapixClientFactory Reader()
        {
            lock (_gate)
            {
                return _streamIds.Values.FirstOrDefault()?.Vapix ?? throw new OperationCanceledException();
            }
        }

        /// <summary>Only changes go to the pages: the ports or the error differ from the last push.</summary>
        private void Publish(IReadOnlyList<IoPort> ports, string? error)
        {
            var json = IoMonitorJson.Serialize(new PortsEvent(string.Empty, ports, error, DateTimeOffset.MinValue));
            lock (_gate)
            {
                if (json == _lastJson)
                {
                    return;
                }

                _lastJson = json;
                _last = new PortsEvent(string.Empty, ports, error, time.GetUtcNow());
                foreach (var (id, owner) in _streamIds)
                {
                    events.Publish(IoMonitorMethods.PortsTopic, IoMonitorJson.Serialize(_last with { StreamId = id }), PluginEventAudience.Of(owner));
                }
            }
        }
    }
}
