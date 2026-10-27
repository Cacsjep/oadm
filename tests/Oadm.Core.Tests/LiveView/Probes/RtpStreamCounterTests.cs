using Microsoft.Extensions.Time.Testing;

using Oadm.Core.LiveView.Probes;
using Oadm.Core.LiveView.Rtp;

namespace Oadm.Core.Tests.LiveView.Probes;

/// <summary>The counters of a measured stream from synthetic RTP packets: loss, reorders, duplicates, frames, jitter.</summary>
public sealed class RtpStreamCounterTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 27, 10, 0, 0, TimeSpan.Zero));

    [Fact]
    public void AGapCountsAsLostOnceFiftyNewerPacketsArrived()
    {
        var counter = Counter();
        AddRange(counter, 0, 9);
        AddRange(counter, 12, 59); // 10 and 11 missing: 49 and 48 newer numbers so far
        Assert.Equal(0, counter.Read().LostPackets);

        counter.Add(Packet(60));
        Assert.Equal(1, counter.Read().LostPackets);
        counter.Add(Packet(61));
        Assert.Equal(2, counter.Read().LostPackets);
        Assert.Equal(60, counter.Read().Packets);
    }

    [Fact]
    public void TheSequenceWrapsAt65535WithoutFalseLoss()
    {
        var counter = Counter();
        AddRange(counter, 65530, 65535);
        AddRange(counter, 0, 100);
        Assert.Equal(new { Packets = 107L, Lost = 0L }, new { counter.Read().Packets, Lost = counter.Read().LostPackets });

        // A gap right at the wrap is lost like any other.
        var wrapped = Counter();
        AddRange(wrapped, 65530, 65534);
        AddRange(wrapped, 1, 100); // 65535 and 0 missing
        Assert.Equal(2, wrapped.Read().LostPackets);
    }

    [Fact]
    public void AReorderInsideTheWindowIsNotLostAndADuplicateIsIgnored()
    {
        var counter = Counter();
        AddRange(counter, 0, 9);
        AddRange(counter, 11, 40);
        counter.Add(Packet(10)); // 30 packets late: a reorder
        counter.Add(Packet(10)); // and its duplicate
        counter.Add(Packet(40)); // a duplicate of the newest
        counter.Add(Packet(20)); // a duplicate inside the window
        AddRange(counter, 41, 200);

        var read = counter.Read();
        Assert.Equal(0, read.LostPackets);
        Assert.Equal(201, read.Packets);
        Assert.Equal(201 * 100, read.Bytes);
    }

    [Fact]
    public void APacketLaterThanTheWindowStaysLost()
    {
        var counter = Counter();
        AddRange(counter, 0, 4);
        AddRange(counter, 6, 70);
        Assert.Equal(1, counter.Read().LostPackets);

        counter.Add(Packet(5)); // 65 packets late
        AddRange(counter, 71, 80);
        Assert.Equal(1, counter.Read().LostPackets);
        Assert.Equal(80, counter.Read().Packets);
    }

    [Fact]
    public void AFarBackwardJumpConfirmedByTheNextPacketRestartsTheSequence()
    {
        var counter = Counter();
        AddRange(counter, 30000, 30010);
        AddRange(counter, 10, 100); // the camera restarted its numbering: 10 is set aside, 11 confirms it

        var read = counter.Read();
        Assert.Equal(0, read.LostPackets);
        Assert.Equal(11 + 90, read.Packets);
    }

    [Fact]
    public void PacketsOfAnotherSourceAreIgnored()
    {
        var counter = Counter();
        AddRange(counter, 0, 9);
        counter.Add(Packet(10, ssrc: 99));
        Assert.Equal(10, counter.Read().Packets);
    }

    [Fact]
    public void JitterFollowsRfc3550()
    {
        // 25 fps, 90 kHz: D(1,2) = (3600 - 0) - (3000 - 0) = 600, J = 600 / 16 = 37.5;
        // D(2,3) = |(6300 - 6000) - 600| = 300, J = 37.5 + (300 - 37.5) / 16 = 53.90625 RTP units = 0.598958 ms.
        var counter = Counter();
        counter.Add(Packet(0, timestamp: 0));
        _time.Advance(TimeSpan.FromMilliseconds(40));
        counter.Add(Packet(1, timestamp: 3000));
        Assert.Equal(37.5 / 90, counter.Read().JitterMs, 9);
        _time.Advance(TimeSpan.FromMilliseconds(30));
        counter.Add(Packet(2, timestamp: 6000));

        Assert.Equal(53.90625 / 90, counter.Read().JitterMs, 9);
    }

    [Fact]
    public void FramesAreCountedByMarkerAndExpectedByRtpTime()
    {
        var counter = Counter(fps: 25);
        ushort seq = 0;
        for (var frame = 0; frame < 10; frame++)
        {
            if (frame is 3 or 7)
            {
                continue; // the camera sent no frame there
            }

            counter.Add(Packet(seq++, timestamp: (uint)(frame * 3600)));
            counter.Add(Packet(seq++, timestamp: (uint)(frame * 3600), marker: true));
        }

        var read = counter.Read();
        Assert.Equal(8, read.Frames);
        Assert.Equal(10, read.ExpectedFrames);
        Assert.Equal(16, read.Packets);
        Assert.Equal(0, read.LateFrames);
    }

    [Fact]
    public void AFrameMoreThanTwoFrameTimesLateIsLate()
    {
        var counter = Counter(fps: 25); // 40 ms frames: late beyond 80 ms
        ushort seq = 0;
        for (var frame = 0; frame < 10; frame++)
        {
            counter.Add(Packet(seq++, timestamp: (uint)(frame * 3600), marker: true));
            _time.Advance(TimeSpan.FromMilliseconds(40));
        }

        // Frame 10 is due now (400 ms): 79 ms late is still in time, frame 11 (due 440 ms) 81 ms late is late.
        _time.Advance(TimeSpan.FromMilliseconds(79));
        counter.Add(Packet(seq++, timestamp: 10 * 3600, marker: true));
        _time.Advance(TimeSpan.FromMilliseconds(42));
        counter.Add(Packet(seq++, timestamp: 11 * 3600, marker: true));

        var read = counter.Read();
        Assert.Equal(12, read.Frames);
        Assert.Equal(12, read.ExpectedFrames);
        Assert.Equal(1, read.LateFrames);
        Assert.Equal(_time.GetUtcNow(), read.LastPacketUtc);
        Assert.Equal(_time.GetUtcNow() - TimeSpan.FromMilliseconds(521), counter.StartedUtc);
    }

    [Fact]
    public void TheRtpTimeWrapsAt32Bits()
    {
        var counter = Counter(fps: 25);
        var start = uint.MaxValue - 3599;
        for (var frame = 0; frame < 4; frame++)
        {
            counter.Add(Packet((ushort)frame, timestamp: unchecked(start + (uint)(frame * 3600)), marker: true));
        }

        Assert.Equal(4, counter.Read().ExpectedFrames);
    }

    [Fact]
    public void NothingIsCountedBeforeTheFirstPacket()
    {
        var counter = Counter();
        Assert.Equal(default, counter.Read());
        Assert.Null(counter.StartedUtc);
        Assert.Null(counter.SinceLastPacket);
        Assert.Null(counter.ReadForReport());
    }

    [Fact]
    public void TheReportStateCarriesTheHighestSequenceWithItsWrapsAndTheLoss()
    {
        var counter = Counter();
        AddRange(counter, 65000, 65535);
        AddRange(counter, 0, 9);
        AddRange(counter, 11, 99);

        var state = counter.ReadForReport()!.Value;
        Assert.Equal(1u, state.Ssrc);
        Assert.Equal(65536u + 99, state.ExtendedHighestSequence);
        Assert.Equal(536 + 100, state.Expected);
        Assert.Equal(1, state.Lost);
    }

    private RtpStreamCounter Counter(double fps = 30) => new(fps, 90000, _time);

    private static void AddRange(RtpStreamCounter counter, int first, int last)
    {
        for (var seq = first; seq <= last; seq++)
        {
            counter.Add(Packet((ushort)seq));
        }
    }

    private static RtpPacket Packet(ushort seq, uint timestamp = 0, bool marker = false, uint ssrc = 1) =>
        new(marker, 96, seq, timestamp, ssrc, new byte[100]);
}
