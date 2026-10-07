using Oadm.Plugins.NtpServer.Protocol;
using Oadm.Plugins.NtpServer.Serving;
using Oadm.Plugins.NtpServer.Upstream;

namespace Oadm.Plugins.NtpServer.Tests;

public sealed class CodecTests
{
    [Theory]
    [InlineData("2026-10-07T18:02:11.1234567Z")]
    [InlineData("1970-01-01T00:00:00Z")]
    [InlineData("2036-02-07T06:28:15.9990000Z")]
    [InlineData("2036-02-07T06:28:16Z")]
    [InlineData("2040-06-01T12:00:00.5Z")]
    public void Timestamp_round_trips_within_a_microsecond(string iso)
    {
        var time = DateTime.Parse(iso, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal);
        var decoded = NtpTimestamp.ToDateTime(NtpTimestamp.FromDateTime(time), time);
        Assert.InRange((decoded - time).Duration().TotalMilliseconds, 0, 0.001);
    }

    [Fact]
    public void Era_wrap_2036_decodes_into_the_era_closest_to_the_pivot()
    {
        var afterWrap = NtpTimestamp.Era1.AddSeconds(100);
        var encoded = NtpTimestamp.FromDateTime(afterWrap);
        Assert.Equal(100UL, encoded >> 32); // the seconds counter wrapped

        // Decoded with a pivot just before the wrap, it is still after the wrap (not 1900).
        Assert.Equal(afterWrap, NtpTimestamp.ToDateTime(encoded, NtpTimestamp.Era1.AddSeconds(-50)));
        // A value just before the wrap, read with a pivot after it, stays in era 0.
        var beforeWrap = NtpTimestamp.Era1.AddSeconds(-30);
        Assert.Equal(beforeWrap, NtpTimestamp.ToDateTime(NtpTimestamp.FromDateTime(beforeWrap), afterWrap));
        Assert.Equal(new DateTime(2036, 2, 7, 6, 28, 16, DateTimeKind.Utc), NtpTimestamp.Era1);
    }

    [Fact]
    public void Short_format_clamps()
    {
        Assert.Equal(0u, NtpTimestamp.ToShort(-1));
        Assert.Equal(0u, NtpTimestamp.ToShort(double.NaN));
        Assert.Equal(65536u, NtpTimestamp.ToShort(1));
        Assert.Equal(uint.MaxValue, NtpTimestamp.ToShort(1e9));
        Assert.Equal(0.5, NtpTimestamp.FromShort(NtpTimestamp.ToShort(0.5)), 4);
    }

    [Fact]
    public void Packet_round_trips_every_field()
    {
        var packet = new NtpPacket
        {
            Leap = NtpLeap.Unsynchronized,
            Version = 4,
            Mode = NtpMode.Server,
            Stratum = 3,
            Poll = 10,
            Precision = -23,
            RootDelay = 0x00010203,
            RootDispersion = 0x04050607,
            ReferenceId = NtpPacket.AsciiId("LOCL"),
            ReferenceTimestamp = 0x0102030405060708,
            OriginateTimestamp = 0x1112131415161718,
            ReceiveTimestamp = 0x2122232425262728,
            TransmitTimestamp = 0x3132333435363738,
        };
        var bytes = new byte[NtpPacket.Length];
        packet.Write(bytes);

        Assert.Equal(0xE4, bytes[0]); // LI 3, VN 4, mode 4
        Assert.True(NtpPacket.TryRead(bytes, out var read));
        Assert.Equal(packet, read);
        Assert.Equal("LOCL", NtpPacket.AsciiCode(read.ReferenceId));
    }

    [Fact]
    public void Invalid_input_is_rejected()
    {
        Assert.False(NtpPacket.TryRead(new byte[47], out _));
        Assert.False(NtpPacket.TryRead([], out _));
        Assert.Throws<ArgumentException>(() => new NtpPacket().Write(new byte[40]));
        Assert.Null(NtpPacket.AsciiCode(0x7F000001)); // an IPv4 reference id is not text
        Assert.Equal("RATE", NtpPacket.AsciiCode(NtpPacket.AsciiId("RATE")));
    }

    [Fact]
    public void Reference_id_is_the_IPv4_address_or_an_MD5_prefix_for_IPv6()
    {
        Assert.Equal(0x0A000011u, UpstreamSample.ReferenceIdOf(System.Net.IPAddress.Parse("10.0.0.17")));
        var v6 = UpstreamSample.ReferenceIdOf(System.Net.IPAddress.Parse("2001:db8::1"));
        Assert.NotEqual(0u, v6);
        Assert.Equal(v6, UpstreamSample.ReferenceIdOf(System.Net.IPAddress.Parse("2001:db8::1")));
    }
}

public sealed class ResponderTests
{
    private static readonly DateTime Now = new(2026, 10, 7, 18, 2, 11, 500, DateTimeKind.Utc);

    private static byte[] Request(byte version = 4, NtpMode mode = NtpMode.Client, ulong transmit = 0xE1B2C3D4_00000001, int length = NtpPacket.Length)
    {
        var bytes = new byte[Math.Max(length, NtpPacket.Length)];
        new NtpPacket { Version = version, Mode = mode, Poll = 7, TransmitTimestamp = transmit }.Write(bytes);
        return bytes[..length];
    }

    [Fact]
    public void Local_mode_answers_as_a_valid_synchronized_server_stratum_10_LOCL()
    {
        var response = new byte[NtpPacket.Length];
        Assert.True(NtpResponder.TryBuildResponse(Request(), Now, TimeSourceState.Local, response, out _));
        Assert.True(NtpPacket.TryRead(response, out var answer));

        Assert.Equal(NtpLeap.NoWarning, answer.Leap);
        Assert.Equal(NtpMode.Server, answer.Mode);
        Assert.Equal(4, answer.Version);
        Assert.Equal(10, answer.Stratum);
        Assert.Equal("LOCL", NtpPacket.AsciiCode(answer.ReferenceId));
        Assert.Equal(7, answer.Poll); // = request poll
        Assert.Equal(NtpResponder.Precision, answer.Precision);
        Assert.InRange(answer.Precision, -32, -1);
        Assert.Equal(0xE1B2C3D4_00000001UL, answer.OriginateTimestamp);
        Assert.Equal(NtpTimestamp.FromDateTime(Now), answer.ReceiveTimestamp);
        Assert.Equal(answer.ReceiveTimestamp, answer.ReferenceTimestamp); // reference = now
        Assert.Equal(0u, answer.RootDelay);
        Assert.InRange(NtpTimestamp.FromShort(answer.RootDispersion), 0.005, 0.02);
    }

    [Fact]
    public void Version_3_requests_get_version_3_answers()
    {
        var response = new byte[NtpPacket.Length];
        Assert.True(NtpResponder.TryBuildResponse(Request(version: 3), Now, TimeSourceState.Local, response, out _));
        NtpPacket.TryRead(response, out var answer);
        Assert.Equal(3, answer.Version);
    }

    [Fact]
    public void Upstream_mode_answers_with_upstream_stratum_plus_one_and_its_address()
    {
        var sample = new UpstreamSample(new System.Net.IPEndPoint(System.Net.IPAddress.Parse("10.0.0.1"), 123), 2, 0.010, 0.020, 0.001, 0.004, Now.AddSeconds(-100));
        var state = sample.ToState("10.0.0.1");
        var response = new byte[NtpPacket.Length];
        Assert.True(NtpResponder.TryBuildResponse(Request(), Now, state, response, out _));
        NtpPacket.TryRead(response, out var answer);

        Assert.Equal(3, answer.Stratum);
        Assert.Equal(0x0A000001u, answer.ReferenceId);
        Assert.Equal(NtpTimestamp.FromDateTime(Now.AddSeconds(-100)), answer.ReferenceTimestamp);
        Assert.InRange(NtpTimestamp.FromShort(answer.RootDelay), 0.0139, 0.0141); // 10 ms + 4 ms round trip
        Assert.True(NtpTimestamp.FromShort(answer.RootDispersion) > 0.022); // + half the round trip + 100 s x 15 ppm
    }

    [Theory]
    [InlineData(NtpMode.Control)]
    [InlineData(NtpMode.Private)]
    [InlineData(NtpMode.Broadcast)]
    [InlineData(NtpMode.SymmetricActive)]
    [InlineData(NtpMode.Server)]
    public void Other_modes_are_ignored(NtpMode mode)
    {
        Assert.False(NtpResponder.TryBuildResponse(Request(mode: mode), Now, TimeSourceState.Local, new byte[48], out _));
    }

    [Fact]
    public void Short_packets_other_versions_and_empty_transmit_are_ignored()
    {
        Assert.False(NtpResponder.TryBuildResponse(Request(length: 47), Now, TimeSourceState.Local, new byte[48], out _));
        Assert.False(NtpResponder.TryBuildResponse(Request(version: 2), Now, TimeSourceState.Local, new byte[48], out _));
        Assert.False(NtpResponder.TryBuildResponse(Request(version: 5), Now, TimeSourceState.Local, new byte[48], out _));
        Assert.False(NtpResponder.TryBuildResponse(Request(transmit: 0), Now, TimeSourceState.Local, new byte[48], out _));
    }

    [Fact]
    public void Requests_with_extension_fields_get_a_48_byte_answer()
    {
        var request = new byte[NtpPacket.Length + 20];
        Request().CopyTo(request, 0);
        var response = new byte[NtpPacket.Length];
        Assert.True(NtpResponder.TryBuildResponse(request, Now, TimeSourceState.Local, response, out _));
    }

    [Fact]
    public void Kiss_of_death_is_stratum_0_with_the_code()
    {
        NtpPacket.TryRead(Request(), out var request);
        var response = new byte[NtpPacket.Length];
        NtpResponder.BuildKissOfDeath(request, Now, "RATE", response);
        NtpPacket.TryRead(response, out var kiss);
        Assert.Equal(0, kiss.Stratum);
        Assert.Equal(NtpLeap.Unsynchronized, kiss.Leap);
        Assert.Equal("RATE", NtpPacket.AsciiCode(kiss.ReferenceId));
        Assert.Equal(request.TransmitTimestamp, kiss.OriginateTimestamp);
    }

    [Fact]
    public void Transmit_timestamp_is_overwritten_before_sending()
    {
        var response = new byte[NtpPacket.Length];
        NtpResponder.TryBuildResponse(Request(), Now, TimeSourceState.Local, response, out _);
        NtpPacket.WriteTransmitTimestamp(response, NtpTimestamp.FromDateTime(Now.AddMilliseconds(1)));
        NtpPacket.TryRead(response, out var answer);
        Assert.Equal(NtpTimestamp.FromDateTime(Now.AddMilliseconds(1)), answer.TransmitTimestamp);
    }
}
