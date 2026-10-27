using System.Numerics;

using Oadm.Core.LiveView.Rtp;
using Oadm.Sdk.Devices;

namespace Oadm.Core.LiveView.Probes;

/// <summary>
/// The counters of one measured video stream (<see cref="StreamProbeCounters"/>) from its RTP packets, nothing decoded:
/// <list type="bullet">
/// <item>Loss from sequence gaps (16-bit wrap): a missing packet counts as lost once 50 newer ones arrived
/// (<see cref="ReorderWindow"/>), so a reorder inside that window is never lost; duplicates are ignored. A far backward
/// jump followed by its next number restarts the sequence (RFC 3550 A.1), never a loss.</item>
/// <item>Frames: the RTP marker ends an access unit. Expected frames from the RTP time of the stream and the asked
/// frame rate.</item>
/// <item>Late frames: a frame whose marker arrives more than 2 frame times later than its RTP time predicts, measured
/// against the quickest frame so far (the first frame, unless a later one came quicker).</item>
/// <item>Interarrival jitter, RFC 3550 6.4.1.</item>
/// </list>
/// One writer (the receive loop) and any readers; a lock that is almost never contended (one read per second).
/// </summary>
public sealed class RtpStreamCounter
{
    /// <summary>A missing packet becomes lost once this many newer packets arrived.</summary>
    public const int ReorderWindow = 50;

    private const ulong WindowMask = (1UL << ReorderWindow) - 1;

    private readonly Lock _gate = new();
    private readonly TaskCompletionSource _firstPacket = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TimeProvider _time;
    private readonly int _clockRate;
    private double _lateLimit;
    private double _fps;

    private bool _started;
    private uint _ssrc;
    private long _maxSeq;
    private long _baseSeq;
    private ulong _window = WindowMask;
    private int? _badSeq;

    private long _packets;
    private long _lost;
    private long _frames;
    private long _late;
    private long _bytes;

    private long _firstTs;
    private long _maxTs;
    private long _firstArrival;
    private long _lastArrival;
    private DateTimeOffset _startedUtc;

    private double _jitter;
    private double _previousTransit;
    private double? _quickestFrame;

    /// <param name="fps">The asked frame rate (expected frames, late frames).</param>
    /// <param name="clockRate">The RTP clock (90 kHz for video).</param>
    /// <param name="time">The arrival clock (tests: a fake one).</param>
    public RtpStreamCounter(double fps, int clockRate = 90000, TimeProvider? time = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fps);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(clockRate);
        _fps = fps;
        _clockRate = clockRate;
        _lateLimit = 2.0 * clockRate / fps;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The frame rate the counter expects.</summary>
    public double Fps
    {
        get
        {
            lock (_gate)
            {
                return _fps;
            }
        }
    }

    /// <summary>Completes when the first packet arrived.</summary>
    public Task FirstPacket => _firstPacket.Task;

    /// <summary>When the first packet arrived; null before.</summary>
    public DateTimeOffset? StartedUtc
    {
        get
        {
            lock (_gate)
            {
                return _started ? _startedUtc : null;
            }
        }
    }

    /// <summary>Time since the last packet arrived; null before the first.</summary>
    public TimeSpan? SinceLastPacket
    {
        get
        {
            lock (_gate)
            {
                return _started ? _time.GetElapsedTime(_lastArrival) : null;
            }
        }
    }

    /// <summary>Counts one RTP packet of the stream; packets of another source (SSRC) than the first are ignored.</summary>
    public void Add(in RtpPacket packet)
    {
        var arrival = _time.GetTimestamp();
        lock (_gate)
        {
            if (!_started)
            {
                Start(packet, arrival);
                Count(packet, arrival, packet.Timestamp);
                return;
            }

            if (packet.Ssrc != _ssrc)
            {
                return;
            }

            var delta = (short)(ushort)(packet.SequenceNumber - (ushort)_maxSeq);
            if (delta > 0)
            {
                _badSeq = null;
                Advance(delta);
                _maxSeq += delta;
            }
            else if (delta == 0)
            {
                return; // duplicate of the newest
            }
            else if (-delta < ReorderWindow)
            {
                var bit = 1UL << -delta;
                if ((_window & bit) != 0)
                {
                    return; // duplicate
                }

                _window |= bit; // a reorder: not lost
            }
            else
            {
                // Far behind: lost long ago, or the camera restarted its sequence (two packets in a row tell).
                if (_badSeq == packet.SequenceNumber)
                {
                    _badSeq = null;
                    _maxSeq = packet.SequenceNumber;
                    _baseSeq = _maxSeq - (_packets + _lost);
                    _window = WindowMask;
                }
                else
                {
                    _badSeq = (ushort)(packet.SequenceNumber + 1);
                    return;
                }
            }

            Count(packet, arrival, ExtendTimestamp(packet.Timestamp));
        }
    }

    /// <summary>The counters since the first packet.</summary>
    public StreamProbeCounters Read()
    {
        lock (_gate)
        {
            if (!_started)
            {
                return default;
            }

            var expected = (long)Math.Round((_maxTs - _firstTs) * _fps / _clockRate) + 1;
            return new StreamProbeCounters(
                _packets,
                _lost,
                _frames,
                expected,
                _late,
                _bytes,
                _jitter * 1000.0 / _clockRate,
                _startedUtc + _time.GetElapsedTime(_firstArrival, _lastArrival));
        }
    }

    /// <summary>What a receiver report (RFC 3550 6.4.2) needs; null before the first packet.</summary>
    public RtcpReceiverState? ReadForReport()
    {
        lock (_gate)
        {
            if (!_started)
            {
                return null;
            }

            return new RtcpReceiverState(_ssrc, (uint)_maxSeq, _maxSeq - _baseSeq + 1, _lost, (uint)Math.Min(uint.MaxValue, _jitter));
        }
    }

    /// <summary>The camera announced a lower frame rate than asked (SDP): expect that one.</summary>
    internal void ExpectFps(double fps)
    {
        if (fps > 0)
        {
            lock (_gate)
            {
                _fps = fps;
                _lateLimit = 2.0 * _clockRate / fps;
            }
        }
    }

    private void Start(in RtpPacket packet, long arrival)
    {
        _started = true;
        _ssrc = packet.Ssrc;
        _maxSeq = packet.SequenceNumber;
        _baseSeq = _maxSeq;
        _firstTs = packet.Timestamp;
        _maxTs = _firstTs;
        _firstArrival = arrival;
        _startedUtc = _time.GetUtcNow();
        _firstPacket.TrySetResult();
    }

    /// <summary>The window moves <paramref name="delta"/> packets on: missing packets that leave it are lost.</summary>
    private void Advance(int delta)
    {
        if (delta >= ReorderWindow)
        {
            _lost += ReorderWindow - BitOperations.PopCount(_window & WindowMask);
            _lost += delta - ReorderWindow; // skipped packets that never were inside the window
            _window = 1;
            return;
        }

        var leaving = WindowMask & ~((1UL << (ReorderWindow - delta)) - 1);
        _lost += BitOperations.PopCount(~_window & leaving);
        _window = ((_window << delta) | 1) & WindowMask;
    }

    /// <summary>The 32-bit RTP time unwrapped against the newest one.</summary>
    private long ExtendTimestamp(uint timestamp)
    {
        var extended = _maxTs + (int)(timestamp - (uint)_maxTs);
        if (extended > _maxTs)
        {
            _maxTs = extended;
        }

        return extended;
    }

    private void Count(in RtpPacket packet, long arrival, long timestamp)
    {
        _packets++;
        _bytes += packet.Payload.Length;
        _lastArrival = arrival;

        // Transit in RTP units, relative to the first packet: arrival time since it minus RTP time since it.
        var arrived = _time.GetElapsedTime(_firstArrival, arrival).TotalSeconds * _clockRate;
        var transit = arrived - (timestamp - _firstTs);
        if (_packets > 1)
        {
            var d = Math.Abs(transit - _previousTransit);
            _jitter += (d - _jitter) / 16.0;
        }

        _previousTransit = transit;

        if (packet.Marker)
        {
            _frames++;
            if (_quickestFrame is not { } quickest || transit < quickest)
            {
                _quickestFrame = transit;
            }
            else if (transit - quickest > _lateLimit)
            {
                _late++;
            }
        }
    }
}

/// <summary>The receiver's side of a stream for an RTCP receiver report.</summary>
/// <param name="Ssrc">The camera's SSRC.</param>
/// <param name="ExtendedHighestSequence">The highest sequence number with its wrap count.</param>
/// <param name="Expected">Packets expected since the first one.</param>
/// <param name="Lost">Packets lost so far.</param>
/// <param name="Jitter">The interarrival jitter in RTP units.</param>
public readonly record struct RtcpReceiverState(uint Ssrc, uint ExtendedHighestSequence, long Expected, long Lost, uint Jitter);
