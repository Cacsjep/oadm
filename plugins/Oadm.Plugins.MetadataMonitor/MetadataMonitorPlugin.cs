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
/// keep-alives (closed client) or when the plugin stops. Device groups (SDK 0.4.0): a stream is a job of its user
/// (<see cref="ICorePluginContext.Jobs"/>: another user's stream id answers like an unknown one), opened through the user's
/// call (<see cref="ICallContext.EventStreams"/>), its messages go only to the page's session, and it ends with Error when
/// the device leaves the user's view.
/// </summary>
public sealed partial class MetadataMonitorPlugin : ICorePlugin, IAsyncDisposable
{
    private readonly MetadataMonitorOptions _options;
    private readonly ConcurrentDictionary<string, MonitorSession> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, IPluginJob> _jobs = new(StringComparer.Ordinal);
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

        foreach (var job in _jobs.Values.ToList())
        {
            job.Cancel();
        }
    }

    /// <summary>Only through the user's call (<see cref="InvokeAsync(ICallContext, string, string?, CancellationToken)"/>).</summary>
    public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) =>
        throw new NotSupportedException("Call the Metadata Monitor plugin with the user's call.");

    public async Task<string?> InvokeAsync(ICallContext caller, string method, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        _ = _ctx ?? throw new InvalidOperationException("The Metadata Monitor plugin is not running.");
        switch (method)
        {
            case MetadataMethods.Start:
                return MetadataJson.Serialize(await StartStreamAsync(caller, MetadataJson.Deserialize<StartRequest>(payloadJson), ct).ConfigureAwait(false));
            case MetadataMethods.GetRtspPort:
                var portDevice = MetadataJson.Deserialize<RtspPortRequest>(payloadJson).DeviceId;
                if (!caller.HasAllDevices && await caller.Devices.FindAsync(portDevice, ct).ConfigureAwait(false) is null)
                {
                    throw new DevicesNotAvailableException();
                }

                var port = caller.EventStreams is { } portStreams ? await portStreams.GetRtspPortAsync(portDevice, ct).ConfigureAwait(false) : null;
                return MetadataJson.Serialize(new RtspPortReply(port));
            case MetadataMethods.Stop:
                var stopped = FindJob(caller, MetadataJson.Deserialize<StreamRequest>(payloadJson).StreamId);
                await StopStreamAsync(stopped.Id).ConfigureAwait(false); // closed when Stop answers
                stopped.Cancel();
                return null;
            case MetadataMethods.KeepAlive:
                if (_sessions.TryGetValue(FindJob(caller, MetadataJson.Deserialize<StreamRequest>(payloadJson).StreamId).Id, out var session))
                {
                    session.KeepAlive();
                }

                return null;
            default:
                throw new ArgumentException($"Unknown method '{method}'.", nameof(method));
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None).ConfigureAwait(false);

    /// <summary>The refusal text for a device status, or null when the stream may be opened.</summary>
    internal static string? RefusalFor(DeviceStatus status) => status switch
    {
        DeviceStatus.CertificateChanged => DeviceMessages.CertificateChanged,
        DeviceStatus.CredentialsRequired => "Credentials required - the device rejects the stored credentials",
        DeviceStatus.PasswordNotSet => "Password not set - the device is in factory default",
        DeviceStatus.SiteOffline => Oadm.Sdk.Sites.SiteTexts.SiteOffline(null),
        _ => null,
    };

    /// <summary>The refusal text for <paramref name="device"/>: "Site offline: Building B" while its site is offline (nothing is sent).</summary>
    internal static string? RefusalFor(IDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return device.Status == DeviceStatus.SiteOffline ? Oadm.Sdk.Sites.SiteTexts.SiteOffline(device.SiteName) : RefusalFor(device.Status);
    }


    /// <summary>The user's stream job; another user's id and an unknown one answer alike.</summary>
    private IPluginJob FindJob(ICallContext caller, string? streamId) =>
        (streamId is null ? null : Jobs.Find(caller, streamId)) ?? throw new KeyNotFoundException(MetadataMethods.StreamEnded);

    private IPluginJobs Jobs => _ctx?.Jobs ?? throw new InvalidOperationException("This OADM server cannot run the Metadata Monitor.");

    private async Task<StartReply> StartStreamAsync(ICallContext caller, StartRequest request, CancellationToken ct)
    {
        var deviceId = request.DeviceId;
        var ctx = _ctx!;
        var jobs = Jobs;
        var device = deviceId == Guid.Empty ? null : await caller.Devices.FindAsync(deviceId, ct).ConfigureAwait(false);
        if (device is null)
        {
            // A user with a device group: a device outside the group and an unknown one answer alike.
            return caller.HasAllDevices ? new StartReply(null, DeviceMessages.NoLongerAvailable(1)) : throw new DevicesNotAvailableException();
        }

        if (caller.EventStreams is null || ctx.Events is not { } events)
        {
            return new StartReply(null, "This OADM server cannot open device event streams");
        }

        if (RefusalFor(device) is { } refusal)
        {
            return new StartReply(null, refusal);
        }

        // The stream uses the RTSP port set with "OADM > RTSP port" (Optics Control); Start never changes it.
        if (_sessions.Count >= _options.MaxStreams)
        {
            return new StartReply(null, $"Too many event streams are open ({_options.MaxStreams}); stop one first");
        }

        // The stream is a job of the user: it reads through the user's view (job.Owner) and ends on Stop, without
        // keep-alives, when the plugin stops and when the user's devices, role or session change (the host cancels it).
        var label = string.IsNullOrEmpty(device.Model) ? device.Address : $"{device.Model} ({device.Address})";
        var opened = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var job = jobs.Start(caller, "stream", async (job, token) =>
        {
            var session = new MonitorSession(job.Id, deviceId, label, job.Owner.EventStreams!, events, PluginEventAudience.Of(job.Owner), _options, Logger);
            _sessions[session.Id] = session;
            _jobs[job.Id] = job;
            try
            {
                string? error;
                try
                {
                    error = await session.StartAsync(token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    opened.TrySetException(ex);
                    return;
                }

                opened.TrySetResult(error);
                if (error is null)
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, _options.Time, token).ConfigureAwait(false);
                }
            }
            finally
            {
                opened.TrySetCanceled(CancellationToken.None);
                _jobs.TryRemove(job.Id, out _);
                await StopStreamAsync(job.Id).ConfigureAwait(false);
            }
        });
        string? failed;
        try
        {
            failed = await opened.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new StartReply(null, "The Metadata Monitor plugin is turned off.");
        }
        catch
        {
            job.Cancel();
            throw;
        }

        return failed is null ? new StartReply(job.Id, null) : new StartReply(null, failed);
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
                    if (_jobs.TryGetValue(session.Id, out var job))
                    {
                        job.Cancel();
                    }
                }
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Metadata Monitor: the page watching {Device} is gone, stopping its event stream")]
    private static partial void LogLeaseExpired(ILogger logger, string device);
}
