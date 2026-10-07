using System.Runtime.CompilerServices;
using System.Threading.Channels;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Core.LiveView.Rtsp;

namespace Oadm.Core.LiveView;

public sealed class LiveViewHubOptions
{
    /// <summary>Frames queued per viewer before it counts as slow and skips to the next keyframe.</summary>
    public int QueueCapacity { get; init; } = 60;

    /// <summary>Frames of the current GOP kept for viewers that join a running stream.</summary>
    public int GopCacheLimit { get; init; } = 300;
}

/// <summary>
/// Shares camera streams between viewers: one upstream connection per (device, codec, resolution,
/// fps), opened on the first subscription and closed when the last viewer leaves. A viewer that
/// joins a running stream first receives the cached frames since the last keyframe, so it shows a
/// picture immediately. Viewers never slow down the upstream: a viewer whose queue is full loses
/// its queued frames and resumes at the next keyframe (decoders never see a broken reference).
/// </summary>
public sealed partial class LiveViewHub : IAsyncDisposable
{
    private readonly ILiveVideoSourceFactory _factory;
    private readonly ILogger _logger;
    private readonly LiveViewHubOptions _options;
    private readonly Lock _gate = new();
    private readonly Dictionary<LiveStreamKey, Upstream> _upstreams = [];
    private bool _disposed;

    public LiveViewHub(ILiveVideoSourceFactory factory, ILogger<LiveViewHub>? logger = null, LiveViewHubOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _options = options ?? new LiveViewHubOptions();
    }

    /// <summary>Open upstream camera connections (diagnostics and tests).</summary>
    public int ActiveUpstreams
    {
        get
        {
            lock (_gate)
            {
                return _upstreams.Count;
            }
        }
    }

    /// <summary>
    /// Negotiates codec (H.265 &gt; H.264 among camera and client codecs) and resolution, then joins
    /// or opens the upstream. When a codec fails to open, the next one in the list is tried.
    /// </summary>
    /// <param name="camera">1-based VAPIX camera (view area, sensor, channel); 0 = the first enabled source.</param>
    /// <exception cref="LiveViewException">No common codec, unknown source, or every codec failed to open.</exception>
    /// <exception cref="KeyNotFoundException">The device does not exist.</exception>
    public async Task<LiveViewSubscription> SubscribeAsync(
        Guid deviceId,
        int maxWidth,
        int maxHeight,
        int fps,
        IReadOnlyCollection<VideoCodecKind> clientCodecs,
        CancellationToken ct,
        int camera = 0)
    {
        ArgumentNullException.ThrowIfNull(clientCodecs);
        var capabilities = await _factory.GetCapabilitiesAsync(deviceId, ct).ConfigureAwait(false);
        var order = LiveViewNegotiation.Order(capabilities.Codecs, clientCodecs);
        if (order.Count == 0)
        {
            throw new LiveViewException(LiveViewError.NotSupported,
                $"No common video codec (camera: {string.Join(", ", capabilities.Codecs)}; viewer: {string.Join(", ", clientCodecs)}).");
        }

        var source = capabilities.FindSource(camera)
            ?? throw new LiveViewException(LiveViewError.NotSupported, $"The device has no video source {camera}.");
        var size = LiveViewNegotiation.ChooseResolution(source.Resolutions.Count > 0 ? source.Resolutions : capabilities.Resolutions, maxWidth, maxHeight);
        var clampedFps = LiveViewNegotiation.ClampFps(fps);
        Exception? failure = null;
        foreach (var codec in order)
        {
            var key = new LiveStreamKey(deviceId, codec, size, clampedFps, source.Camera);
            var subscription = Attach(key);
            try
            {
                await subscription.Upstream.Started.Task.WaitAsync(ct).ConfigureAwait(false);
                return subscription;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                failure = ex;
                LogOpenFailed(_logger, deviceId, codec, ex.Message);
                await subscription.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                await subscription.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        throw failure is LiveViewException ? failure : new LiveViewException(LiveViewError.Protocol, failure?.Message ?? "The stream could not be opened.", failure!);
    }

    public async ValueTask DisposeAsync()
    {
        List<Upstream> all;
        lock (_gate)
        {
            _disposed = true;
            all = [.. _upstreams.Values];
            _upstreams.Clear();
        }

        foreach (var upstream in all)
        {
            await upstream.StopAsync().ConfigureAwait(false);
        }
    }

    private LiveViewSubscription Attach(LiveStreamKey key)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_upstreams.TryGetValue(key, out var upstream))
            {
                upstream = new Upstream(this, key);
                _upstreams[key] = upstream;
                upstream.Start();
                LogUpstreamOpening(_logger, key.DeviceId, key.Codec, key.Size, key.Fps);
            }

            return upstream.AddSubscriber();
        }
    }

    /// <summary>Called by a subscription; closes the upstream when its last viewer leaves.</summary>
    internal async ValueTask DetachAsync(LiveViewSubscription subscription)
    {
        Upstream? stop = null;
        var upstream = subscription.Upstream;
        lock (_gate)
        {
            upstream.Subscribers.Remove(subscription);
            if (upstream.Subscribers.Count == 0)
            {
                if (_upstreams.TryGetValue(upstream.Key, out var current) && current == upstream)
                {
                    _upstreams.Remove(upstream.Key);
                }

                stop = upstream;
            }
        }

        if (stop is not null)
        {
            LogUpstreamClosing(_logger, stop.Key.DeviceId, stop.Key.Codec);
            await stop.StopAsync().ConfigureAwait(false);
        }
    }

    private void Forget(Upstream upstream)
    {
        lock (_gate)
        {
            if (_upstreams.TryGetValue(upstream.Key, out var current) && current == upstream)
            {
                _upstreams.Remove(upstream.Key);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Live view {DeviceId}: opening {Codec} {Size} @ {Fps} fps")]
    private static partial void LogUpstreamOpening(ILogger logger, Guid deviceId, VideoCodecKind codec, Oadm.Core.Vapix.ImageSize size, int fps);

    [LoggerMessage(Level = LogLevel.Information, Message = "Live view {DeviceId}: closing {Codec} stream, no viewers left")]
    private static partial void LogUpstreamClosing(ILogger logger, Guid deviceId, VideoCodecKind codec);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Live view {DeviceId}: {Codec} could not be opened: {Reason}")]
    private static partial void LogOpenFailed(ILogger logger, Guid deviceId, VideoCodecKind codec, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Live view {DeviceId}: stream ended: {Reason}")]
    private static partial void LogStreamEnded(ILogger logger, Guid deviceId, string reason);

    /// <summary>One camera connection and its viewers. All mutable state is guarded by the hub lock.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "The token source is disposed when RunAsync ends.")]
    internal sealed class Upstream(LiveViewHub hub, LiveStreamKey key)
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly List<LiveVideoFrame> _gop = [];
        private Task _run = Task.CompletedTask;

        public LiveStreamKey Key { get; } = key;

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<LiveViewSubscription> Subscribers { get; } = [];

        public void Start() => _run = Task.Run(RunAsync);

        public LiveViewSubscription AddSubscriber()
        {
            var subscription = new LiveViewSubscription(hub, this, hub._options.QueueCapacity);
            Subscribers.Add(subscription);
            foreach (var frame in _gop)
            {
                subscription.Offer(frame);
            }

            return subscription;
        }

        public async Task StopAsync()
        {
            try
            {
                await _cts.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            try
            {
                await _run.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or LiveViewException)
            {
                // reported to the viewers already
            }
        }

        private async Task RunAsync()
        {
            Exception? error = null;
            ILiveVideoSource? source = null;
            var ct = _cts.Token;
            try
            {
                source = await hub._factory.OpenAsync(Key, ct).ConfigureAwait(false);
                Started.TrySetResult();
                await foreach (var frame in source.ReadFramesAsync(ct).ConfigureAwait(false))
                {
                    lock (hub._gate)
                    {
                        if (frame.IsKeyframe || _gop.Count >= hub._options.GopCacheLimit)
                        {
                            _gop.Clear();
                        }

                        if (frame.IsKeyframe || _gop.Count > 0)
                        {
                            _gop.Add(frame);
                        }

                        foreach (var subscription in Subscribers)
                        {
                            subscription.Offer(frame);
                        }
                    }
                }

                error = new LiveViewException(LiveViewError.Unreachable, "The camera closed the video stream.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                error = null;
            }
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                if (source is not null)
                {
                    await source.DisposeAsync().ConfigureAwait(false);
                }

                hub.Forget(this);
                Started.TrySetException(error ?? new OperationCanceledException("The stream was closed."));
                List<LiveViewSubscription> subscribers;
                lock (hub._gate)
                {
                    subscribers = [.. Subscribers];
                    _gop.Clear();
                }

                if (error is not null && Started.Task.IsCompletedSuccessfully)
                {
                    LogStreamEnded(hub._logger, Key.DeviceId, error.Message);
                }

                foreach (var subscription in subscribers)
                {
                    subscription.Complete(error);
                }

                _cts.Dispose();
            }
        }
    }
}

/// <summary>A viewer of a shared live stream. Dispose it to leave; the last viewer closes the camera connection.</summary>
public sealed class LiveViewSubscription : IAsyncDisposable
{
    private readonly LiveViewHub _hub;
    private readonly int _capacity;
    private readonly Channel<LiveVideoFrame> _queue = Channel.CreateUnbounded<LiveVideoFrame>(new UnboundedChannelOptions { SingleWriter = true });
    private bool _needKeyframe = true;
    private int _disposed;

    internal LiveViewSubscription(LiveViewHub hub, LiveViewHub.Upstream upstream, int capacity)
    {
        _hub = hub;
        Upstream = upstream;
        _capacity = Math.Max(1, capacity);
    }

    public LiveStreamKey Key => Upstream.Key;

    /// <summary>Frames this viewer skipped because it was too slow (or joined between keyframes).</summary>
    public int DroppedFrames { get; private set; }

    internal LiveViewHub.Upstream Upstream { get; }

    /// <summary>Frames until the stream ends; throws the upstream error when the camera stream failed.</summary>
    public async IAsyncEnumerable<LiveVideoFrame> ReadAllAsync([EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var frame in _queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            yield return frame;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _queue.Writer.TryComplete();
            await _hub.DetachAsync(this).ConfigureAwait(false);
        }
    }

    /// <summary>Called under the hub lock for every frame.</summary>
    internal void Offer(LiveVideoFrame frame)
    {
        if (_needKeyframe && !frame.IsKeyframe)
        {
            DroppedFrames++;
            return;
        }

        if (_queue.Reader.Count >= _capacity)
        {
            // Slow viewer: drop what is queued and resume at the next keyframe.
            while (_queue.Reader.TryRead(out _))
            {
                DroppedFrames++;
            }

            if (!frame.IsKeyframe)
            {
                _needKeyframe = true;
                DroppedFrames++;
                return;
            }
        }

        _needKeyframe = false;
        _queue.Writer.TryWrite(frame);
    }

    internal void Complete(Exception? error) => _queue.Writer.TryComplete(error);
}
