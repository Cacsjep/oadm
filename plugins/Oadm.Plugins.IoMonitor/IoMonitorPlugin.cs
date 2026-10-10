using System.Collections.Concurrent;
using System.Diagnostics;
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

    /// <summary>After a failed read the next one waits this long (an unreachable device is not asked 5 times a second).</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Pages reading a device at the same time (each 5 requests per second).</summary>
    public int MaxStreams { get; init; } = 20;
}

/// <summary>
/// Core plugin "I/O Monitor" (off by default, user decision 2026-10-10): the page picks one device with inputs or
/// outputs and shows their states live. While the page shows the device the server reads the ports every 200 ms
/// (<c>getPorts</c> of the I/O port management API, stored credentials) and pushes them to the page when something
/// changed. Read-only for devices; no events, no actions.
/// </summary>
public sealed partial class IoMonitorPlugin : ICorePlugin, IAsyncDisposable
{
    private readonly IoMonitorOptions _options;
    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private ICorePluginContext? _ctx;
    private CancellationTokenSource? _running;
    private Task _leaseLoop = Task.CompletedTask;

    public IoMonitorPlugin()
        : this(new IoMonitorOptions())
    {
    }

    public IoMonitorPlugin(IoMonitorOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public string Id => IoMonitorPluginInfo.PluginId;

    public string DisplayName => IoMonitorPluginInfo.DisplayName;

    public string? IconKey => IoMonitorPluginInfo.IconKey;

    public CorePluginGroup Group => CorePluginGroup.Monitoring;

    public IReadOnlyList<ITaskPlugin> TaskPlugins => [];

    /// <summary>Running streams (tests).</summary>
    public int StreamCount => _sessions.Count;

    private ILogger Logger => _ctx?.Logger ?? NullLogger.Instance;

    public async Task StartAsync(ICorePluginContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        await StopAsync(ct).ConfigureAwait(false);
        _ctx = ctx;
        _running = new CancellationTokenSource();
        var token = _running.Token;
        _leaseLoop = Task.Run(() => ExpireLeasesAsync(token), CancellationToken.None);
    }

    public async Task StopAsync(CancellationToken ct)
    {
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

        foreach (var id in _sessions.Keys.ToList())
        {
            await StopStreamAsync(id).ConfigureAwait(false);
        }
    }

    public async Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct)
    {
        _ = _ctx ?? throw new InvalidOperationException("The I/O Monitor plugin is not running.");
        switch (method)
        {
            case IoMonitorMethods.Start:
                return IoMonitorJson.Serialize(await StartStreamAsync(IoMonitorJson.Deserialize<StartRequest>(payloadJson).DeviceId, ct).ConfigureAwait(false));
            case IoMonitorMethods.Stop:
                await StopStreamAsync(IoMonitorJson.Deserialize<StreamRequest>(payloadJson).StreamId).ConfigureAwait(false);
                return null;
            case IoMonitorMethods.KeepAlive:
                if (IoMonitorJson.Deserialize<StreamRequest>(payloadJson).StreamId is { } id && _sessions.TryGetValue(id, out var session))
                {
                    session.KeepAlive();
                }

                return null;
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
        _ => null,
    };

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

    private async Task<StartReply> StartStreamAsync(Guid deviceId, CancellationToken ct)
    {
        var ctx = _ctx!;
        if (ctx.Events is not { } events)
        {
            return new StartReply(null, "This OADM server cannot push live updates");
        }

        var device = deviceId == Guid.Empty ? null : await ctx.Devices.FindAsync(deviceId, ct).ConfigureAwait(false);
        if (device is null)
        {
            return new StartReply(null, DeviceMessages.Removed);
        }

        if (RefusalFor(device.Status) is { } refusal)
        {
            return new StartReply(null, refusal);
        }

        if (!device.Apis.Supports(IoMonitorPluginInfo.ApiId, "1.0"))
        {
            return new StartReply(null, "The device has no inputs or outputs to show");
        }

        if (_sessions.Count >= _options.MaxStreams)
        {
            return new StartReply(null, $"Too many devices are shown at the same time ({_options.MaxStreams}); close one first");
        }

        IVapixClient vapix;
        try
        {
            vapix = await ctx.Vapix.CreateAsync(deviceId, ct).ConfigureAwait(false); // cached by the factory, never disposed here
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return new StartReply(null, DeviceMessages.Unreachable(ex.Message));
        }

        var session = new Session(Guid.NewGuid().ToString("N"), vapix, events, _options);
        _sessions[session.Id] = session;
        session.Start();
        LogStarted(Logger, device.Address);
        return new StartReply(session.Id, null);
    }

    private async Task StopStreamAsync(string? streamId)
    {
        if (streamId is not null && _sessions.TryRemove(streamId, out var session))
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task ExpireLeasesAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_options.LeaseCheckInterval);
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            foreach (var session in _sessions.Values.Where(s => s.SinceKeepAlive > _options.LeaseTimeout).ToList())
            {
                await StopStreamAsync(session.Id).ConfigureAwait(false);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "I/O Monitor: reading the ports of {Address}")]
    private static partial void LogStarted(ILogger logger, string address);

    /// <summary>One page watching one device: reads every <see cref="IoMonitorOptions.PollInterval"/>, publishes changes.</summary>
    private sealed class Session(string id, IVapixClient vapix, IPluginEvents events, IoMonitorOptions options) : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Stopwatch _sinceKeepAlive = Stopwatch.StartNew();
        private Task _loop = Task.CompletedTask;
        private string? _lastJson;

        public string Id { get; } = id;

        public TimeSpan SinceKeepAlive => _sinceKeepAlive.Elapsed;

        public void KeepAlive() => _sinceKeepAlive.Restart();

        public void Start() => _loop = Task.Run(LoopAsync, CancellationToken.None);

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
                var started = Stopwatch.GetTimestamp();
                string? error = null;
                try
                {
                    last = await ReadAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
#pragma warning disable CA1031 // A failed read is shown on the page; the next read tries again.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    error = ex is OperationCanceledException ? DeviceMessages.Timeout(options.RequestTimeout) : ex.Message;
                }

                Publish(last, error);
                var wait = (error is null ? options.PollInterval : options.RetryDelay) - Stopwatch.GetElapsedTime(started);
                if (wait > TimeSpan.Zero)
                {
                    try
                    {
                        await Task.Delay(wait, ct).ConfigureAwait(false);
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
            using var request = new HttpRequestMessage(HttpMethod.Post, IoMonitorPluginInfo.Path)
            {
                Content = new StringContent("""{"apiVersion":"1.0","context":"oadm","method":"getPorts"}""", Encoding.UTF8, "application/json"),
            };
            request.Options.Set(VapixRequestOptions.Timeout, options.RequestTimeout);
            using var response = await vapix.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(DeviceMessages.ForHttpStatus((int)response.StatusCode) ?? DeviceMessages.ServerError((int)response.StatusCode));
            }

            return ParsePorts(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        }

        /// <summary>Only changes go to the page: the ports or the error differ from the last push.</summary>
        private void Publish(IReadOnlyList<IoPort> ports, string? error)
        {
            var json = IoMonitorJson.Serialize(new PortsEvent(Id, ports, error, DateTimeOffset.MinValue));
            if (json == _lastJson)
            {
                return;
            }

            _lastJson = json;
            events.Publish(IoMonitorMethods.PortsTopic, IoMonitorJson.Serialize(new PortsEvent(Id, ports, error, DateTimeOffset.UtcNow)));
        }
    }
}
