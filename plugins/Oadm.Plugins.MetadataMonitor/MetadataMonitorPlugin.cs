using System.Collections.Concurrent;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Plugins.MetadataMonitor.Streaming;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.MetadataMonitor;

/// <summary>
/// Core plugin "Metadata Monitor", a port of the AXIS Metadata Monitor tool: the page picks one camera and watches its
/// event stream live. The server opens the RTSP event stream with the stored credentials
/// (<see cref="ICorePluginContext.EventStreams"/>), parses every notification and pushes the messages to the page
/// (<see cref="MetadataMethods"/>). Read-only for devices. A stream ends on Stop, when its page stops sending
/// keep-alives (closed client) or when the plugin stops.
/// </summary>
public sealed partial class MetadataMonitorPlugin : ICorePlugin, IAsyncDisposable
{
    private readonly MetadataMonitorOptions _options;
    private readonly ConcurrentDictionary<string, MonitorSession> _sessions = new(StringComparer.Ordinal);
    private ICorePluginContext? _ctx;
    private CancellationTokenSource? _running;
    private Task _leaseLoop = Task.CompletedTask;

    public MetadataMonitorPlugin()
        : this(new MetadataMonitorOptions())
    {
    }

    public MetadataMonitorPlugin(MetadataMonitorOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public string Id => MetadataMonitorPluginInfo.PluginId;

    public string DisplayName => MetadataMonitorPluginInfo.DisplayName;

    public string? IconKey => MetadataMonitorPluginInfo.IconKey;
    public CorePluginGroup Group => CorePluginGroup.Monitoring;

    public IReadOnlyList<ITaskPlugin> TaskPlugins => [];

    /// <summary>Running streams (tests, diagnostics).</summary>
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
        _ = _ctx ?? throw new InvalidOperationException("The Metadata Monitor plugin is not running.");
        switch (method)
        {
            case MetadataMethods.Start:
                return MetadataJson.Serialize(await StartStreamAsync(MetadataJson.Deserialize<StartRequest>(payloadJson), ct).ConfigureAwait(false));
            case MetadataMethods.GetRtspPort:
                var portDevice = MetadataJson.Deserialize<RtspPortRequest>(payloadJson).DeviceId;
                var port = _ctx.EventStreams is { } portStreams ? await portStreams.GetRtspPortAsync(portDevice, ct).ConfigureAwait(false) : null;
                return MetadataJson.Serialize(new RtspPortReply(port));
            case MetadataMethods.Stop:
                await StopStreamAsync(MetadataJson.Deserialize<StreamRequest>(payloadJson).StreamId).ConfigureAwait(false);
                return null;
            case MetadataMethods.KeepAlive:
                var id = MetadataJson.Deserialize<StreamRequest>(payloadJson).StreamId;
                if (id is not null && _sessions.TryGetValue(id, out var session))
                {
                    session.KeepAlive();
                }

                return null;
            default:
                throw new ArgumentException($"Unknown method '{method}'.", nameof(method));
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None).ConfigureAwait(false);

    /// <summary>A port outside 1..65535.</summary>
    public const string RtspPortError = "Enter a port from 1 to 65535.";

    /// <summary>The refusal text for a device status, or null when the stream may be opened.</summary>
    internal static string? RefusalFor(DeviceStatus status) => status switch
    {
        DeviceStatus.CertificateChanged => DeviceMessages.CertificateChanged,
        DeviceStatus.CredentialsRequired => "Credentials required - the device rejects the stored credentials",
        DeviceStatus.PasswordNotSet => "Password not set - the device is in factory default",
        _ => null,
    };

    private async Task<StartReply> StartStreamAsync(StartRequest request, CancellationToken ct)
    {
        var deviceId = request.DeviceId;
        var ctx = _ctx!;
        if (ctx.EventStreams is not { } streams || ctx.Events is not { } events)
        {
            return new StartReply(null, "This OADM server cannot open device event streams");
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

        if (request.RtspPort is < 1 or > 65535)
        {
            return new StartReply(null, RtspPortError);
        }

        try
        {
            await streams.SetRtspPortAsync(deviceId, request.RtspPort, ct).ConfigureAwait(false);
        }
        catch (NotSupportedException) when (request.RtspPort is null or 554)
        {
            // a host without stored ports: the default port needs nothing stored
        }
        catch (NotSupportedException)
        {
            return new StartReply(null, "This OADM server cannot use another RTSP port");
        }

        if (_sessions.Count >= _options.MaxStreams)
        {
            return new StartReply(null, $"Too many event streams are open ({_options.MaxStreams}); stop one first");
        }

        var label = string.IsNullOrEmpty(device.Model) ? device.Address : $"{device.Model} ({device.Address})";
        var session = new MonitorSession(Guid.NewGuid().ToString("N"), deviceId, label, streams, events, _options, Logger);
        _sessions[session.Id] = session;
        var error = await session.StartAsync(ct).ConfigureAwait(false);
        if (error is not null)
        {
            _sessions.TryRemove(session.Id, out _);
            await session.DisposeAsync().ConfigureAwait(false);
            return new StartReply(null, error);
        }

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
        using var timer = new PeriodicTimer(_options.LeaseCheckInterval, _options.Time);
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            foreach (var session in _sessions.Values)
            {
                if (session.SinceKeepAlive > _options.LeaseTimeout)
                {
                    LogLeaseExpired(Logger, session.DeviceLabel);
                    await StopStreamAsync(session.Id).ConfigureAwait(false);
                }
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Metadata Monitor: the page watching {Device} is gone, stopping its event stream")]
    private static partial void LogLeaseExpired(ILogger logger, string device);
}
