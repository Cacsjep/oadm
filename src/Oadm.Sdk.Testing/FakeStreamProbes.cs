using System.Collections.Concurrent;

using Oadm.Sdk.Devices;

namespace Oadm.Sdk.Testing;

/// <summary>
/// The server's measuring video streams (<see cref="IDeviceStreamProbes"/>) for plugin tests: the video sources per device
/// (<see cref="Sources"/>, or a failure in <see cref="ListFailures"/>), and one scriptable <see cref="FakeStreamProbe"/> per
/// opened stream. <see cref="Configure"/> sets how the probes of a device and camera behave (transport, counters per
/// second, start delay, stall, failure); every probe opened so far is in <see cref="Opened"/>. Counters follow the
/// <see cref="Time"/> given (a <c>FakeTimeProvider</c> in tests): a probe reports the seconds that passed since it started.
/// Unknown devices throw <see cref="KeyNotFoundException"/> like the server.
/// </summary>
public sealed class FakeStreamProbes : IDeviceStreamProbes
{
    private readonly ConcurrentDictionary<(Guid Device, int Camera), Action<FakeStreamProbe>> _setups = new();
    private readonly ConcurrentQueue<FakeStreamProbe> _opened = new();
    private int _listCalls;
    private readonly ConcurrentDictionary<Guid, int> _listCallsByDevice = new();
    private int _opensWaiting;

    /// <param name="time">The clock of the probes' counters (a <c>FakeTimeProvider</c>); null = the system clock.</param>
    public FakeStreamProbes(TimeProvider? time = null)
    {
        Time = time ?? TimeProvider.System;
    }

    /// <summary>The clock the probes count seconds with.</summary>
    public TimeProvider Time { get; }

    /// <summary>The video sources of each device (<see cref="ListSourcesAsync"/>); a device without an entry is unknown.</summary>
    public ConcurrentDictionary<Guid, IReadOnlyList<StreamProbeSource>> Sources { get; } = new();

    /// <summary>A device whose source list fails with this exception (e.g. a <see cref="DeviceStreamException"/>).</summary>
    public ConcurrentDictionary<Guid, Exception> ListFailures { get; } = new();

    /// <summary>A device whose streams fail to open with this exception.</summary>
    public ConcurrentDictionary<Guid, Exception> OpenFailures { get; } = new();

    /// <summary>Every probe opened so far, in order (also the disposed ones).</summary>
    public IReadOnlyList<FakeStreamProbe> Opened => [.. _opened];

    /// <summary>The probes that are open now (opened and not disposed).</summary>
    public IReadOnlyList<FakeStreamProbe> Open => [.. _opened.Where(p => !p.IsDisposed)];

    /// <summary>
    /// Opens that wait now on their <see cref="FakeStreamProbe.OpenGate"/> or <see cref="FakeStreamProbe.StartDelay"/>: a test
    /// that steps the clock waits until every other open has completed.
    /// </summary>
    public int OpensWaiting => Volatile.Read(ref _opensWaiting);

    /// <summary>How often <see cref="ListSourcesAsync"/> was called.</summary>
    public int ListCalls => Volatile.Read(ref _listCalls);

    /// <summary>How often the sources of <paramref name="deviceId"/> were asked.</summary>
    public int ListCallsFor(Guid deviceId) => _listCallsByDevice.GetValueOrDefault(deviceId);

    /// <summary>How many probes of <paramref name="deviceId"/> were opened (also those whose open failed).</summary>
    public int OpensFor(Guid deviceId) => _opened.Count(p => p.DeviceId == deviceId) + _openRefusals.GetValueOrDefault(deviceId);

    private readonly ConcurrentDictionary<Guid, int> _openRefusals = new();

    /// <summary>Adds a device's sources: by default one 1920x1080 source at 30 fps with H.265 and H.264.</summary>
    public FakeStreamProbes Add(Guid deviceId, params StreamProbeSource[] sources)
    {
        Sources[deviceId] = sources.Length > 0 ? sources : [Source()];
        return this;
    }

    /// <summary>A source for <see cref="Add"/>.</summary>
    public static StreamProbeSource Source(int camera = 1, int width = 1920, int height = 1080, int fps = 30, params string[] codecs) =>
        new(camera, "View Area " + camera, width, height, fps, codecs.Length > 0 ? codecs : ["h265", "h264"]);

    /// <summary>
    /// How every probe of <paramref name="deviceId"/> (and <paramref name="camera"/>, 0 = every camera) behaves once opened;
    /// the action runs before <see cref="OpenAsync"/> returns. A later call replaces it.
    /// </summary>
    public FakeStreamProbes Configure(Guid deviceId, Action<FakeStreamProbe> setup, int camera = 0)
    {
        ArgumentNullException.ThrowIfNull(setup);
        _setups[(deviceId, camera)] = setup;
        return this;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<StreamProbeSource>> ListSourcesAsync(Guid deviceId, CancellationToken ct)
    {
        Interlocked.Increment(ref _listCalls);
        _listCallsByDevice.AddOrUpdate(deviceId, 1, (_, n) => n + 1);
        ct.ThrowIfCancellationRequested();
        if (ListFailures.TryGetValue(deviceId, out var failure))
        {
            return Task.FromException<IReadOnlyList<StreamProbeSource>>(failure);
        }

        return Sources.TryGetValue(deviceId, out var sources)
            ? Task.FromResult(sources)
            : Task.FromException<IReadOnlyList<StreamProbeSource>>(new KeyNotFoundException($"No device {deviceId}."));
    }

    /// <inheritdoc />
    public async Task<IDeviceStreamProbe> OpenAsync(Guid deviceId, StreamProbeRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Sources.TryGetValue(deviceId, out var sources))
        {
            throw new KeyNotFoundException($"No device {deviceId}.");
        }

        if (OpenFailures.TryGetValue(deviceId, out var failure))
        {
            _openRefusals.AddOrUpdate(deviceId, 1, (_, n) => n + 1);
            throw failure;
        }

        var source = sources.FirstOrDefault(s => s.Camera == request.Camera)
            ?? throw new DeviceStreamException(DeviceStreamError.NotSupported, $"The device has no camera {request.Camera}.");
        var probe = new FakeStreamProbe(this, deviceId, request, source);
        if (_setups.TryGetValue((deviceId, request.Camera), out var setup) || _setups.TryGetValue((deviceId, 0), out setup))
        {
            setup(probe);
        }

        _opened.Enqueue(probe);
        if (probe.OpenGate is not null || probe.StartDelay > TimeSpan.Zero)
        {
            Interlocked.Increment(ref _opensWaiting);
            try
            {
                if (probe.OpenGate is { } gate)
                {
                    await gate.Task.WaitAsync(ct).ConfigureAwait(false);
                }

                if (probe.StartDelay > TimeSpan.Zero)
                {
                    await Task.Delay(probe.StartDelay, Time, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                probe.MarkDisposed(); // a cancelled open never streams
                throw;
            }
            finally
            {
                Interlocked.Decrement(ref _opensWaiting);
            }
        }

        if (probe.OpenFailure is { } openFailure)
        {
            probe.MarkDisposed();
            throw openFailure;
        }

        probe.Start();
        return probe;
    }
}

/// <summary>What a <see cref="FakeStreamProbe"/> receives in one second (deltas, not cumulative).</summary>
/// <param name="Packets">RTP packets received.</param>
/// <param name="LostPackets">Packets missing in the sequence.</param>
/// <param name="Frames">Complete frames.</param>
/// <param name="ExpectedFrames">Frames the asked rate gives for the second.</param>
/// <param name="LateFrames">Frames that came late.</param>
/// <param name="Bytes">Payload bytes.</param>
/// <param name="JitterMs">The jitter at the end of the second.</param>
public readonly record struct FakeStreamSecond(long Packets, long LostPackets, long Frames, long ExpectedFrames, long LateFrames, long Bytes, double JitterMs)
{
    /// <summary>A healthy second: 30 frames of 30, 300 packets, 4 Mbit/s, 2 ms jitter.</summary>
    public static FakeStreamSecond Healthy(int fps = 30, double mbps = 4) => new(fps * 10, 0, fps, fps, 0, (long)(mbps * 1_000_000 / 8), 2);

    /// <summary>A second without any packet (a stall).</summary>
    public static FakeStreamSecond Silent(int fps = 30) => new(0, 0, 0, fps, 0, 0, 0);
}

/// <summary>
/// One opened stream of <see cref="FakeStreamProbes"/>. Its counters are the sum of <see cref="Script"/> over the whole
/// seconds since it started (by <see cref="FakeStreamProbes.Time"/>); by default every second is
/// <see cref="FakeStreamSecond.Healthy"/>. Set the behaviour in <see cref="FakeStreamProbes.Configure"/>: <see cref="Transport"/>,
/// <see cref="Script"/>, <see cref="StartDelay"/> or <see cref="OpenGate"/> (the open waits), <see cref="OpenFailure"/> (the
/// open throws); at any time <see cref="Fail"/> breaks the stream (<see cref="Completion"/> faults).
/// </summary>
public sealed class FakeStreamProbe : IDeviceStreamProbe
{
    private readonly FakeStreamProbes _owner;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _gate = new();
    private int _counted;
    private StreamProbeCounters _counters;
    private int _disposed;

    internal FakeStreamProbe(FakeStreamProbes owner, Guid deviceId, StreamProbeRequest request, StreamProbeSource source)
    {
        _owner = owner;
        DeviceId = deviceId;
        Request = request;
        Codec = request.Codec ?? (source.Codecs.Count > 0 ? source.Codecs[0] : "h264");
        Width = request.Width > 0 ? request.Width : source.Width;
        Height = request.Height > 0 ? request.Height : source.Height;
        Fps = request.Fps > 0 ? request.Fps : source.MaxFps;
        Transport = request.PreferUdp ? StreamProbeTransport.Udp : StreamProbeTransport.Tcp;
    }

    /// <summary>The device of the stream.</summary>
    public Guid DeviceId { get; }

    /// <summary>What the plugin asked for.</summary>
    public StreamProbeRequest Request { get; }

    /// <inheritdoc />
    public StreamProbeTransport Transport { get; set; }

    /// <summary>
    /// UDP was blocked and the probe fell back to TCP (<see cref="IDeviceStreamProbe.UdpBlocked"/>); set
    /// <see cref="Transport"/> to TCP too.
    /// </summary>
    public bool UdpBlocked { get; set; }

    /// <inheritdoc />
    public string Codec { get; set; }

    /// <inheritdoc />
    public int Width { get; set; }

    /// <inheritdoc />
    public int Height { get; set; }

    /// <inheritdoc />
    public int Fps { get; set; }

    /// <inheritdoc />
    public DateTimeOffset StartedUtc { get; private set; }

    /// <summary>What arrives in second n (0-based since the start); default <see cref="FakeStreamSecond.Healthy"/> at <see cref="Fps"/>.</summary>
    public Func<int, FakeStreamSecond>? Script { get; set; }

    /// <summary>The open waits this long on <see cref="FakeStreamProbes.Time"/> before the stream starts.</summary>
    public TimeSpan StartDelay { get; set; }

    /// <summary>The open waits until the test completes this gate.</summary>
    public TaskCompletionSource? OpenGate { get; set; }

    /// <summary>The open throws this after its wait (the device refused the stream).</summary>
    public Exception? OpenFailure { get; set; }

    /// <summary>True once the plugin disposed the probe (RTSP TEARDOWN).</summary>
    public bool IsDisposed => Volatile.Read(ref _disposed) == 1;

    /// <summary>How often <see cref="Read"/> was called.</summary>
    public int Reads { get; private set; }

    /// <inheritdoc />
    public Task Completion => _completion.Task;

    /// <summary>Breaks the stream: <see cref="Completion"/> faults with a <see cref="DeviceStreamException"/> (the device closed it).</summary>
    public void Fail(string message = "The device closed the stream.") =>
        _completion.TrySetException(new DeviceStreamException(DeviceStreamError.Unreachable, message));

    /// <inheritdoc />
    public StreamProbeCounters Read()
    {
        lock (_gate)
        {
            Reads++;
            if (Completion.IsCompleted)
            {
                return _counters;
            }

            var seconds = (int)Math.Floor((_owner.Time.GetUtcNow() - StartedUtc).TotalSeconds);
            for (; _counted < seconds; _counted++)
            {
                var s = Script?.Invoke(_counted) ?? FakeStreamSecond.Healthy(Fps);
                _counters = new StreamProbeCounters(
                    _counters.Packets + s.Packets,
                    _counters.LostPackets + s.LostPackets,
                    _counters.Frames + s.Frames,
                    _counters.ExpectedFrames + s.ExpectedFrames,
                    _counters.LateFrames + s.LateFrames,
                    _counters.Bytes + s.Bytes,
                    s.Packets > 0 ? s.JitterMs : _counters.JitterMs,
                    s.Packets > 0 ? StartedUtc.AddSeconds(_counted + 1) : _counters.LastPacketUtc);
            }

            return _counters;
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        MarkDisposed();
        _completion.TrySetResult();
        return ValueTask.CompletedTask;
    }

    internal void Start() => StartedUtc = _owner.Time.GetUtcNow();

    internal void MarkDisposed() => Interlocked.Exchange(ref _disposed, 1);
}
