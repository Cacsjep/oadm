using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

using Oadm.Plugins.NtpServer.Protocol;

namespace Oadm.Plugins.NtpServer.Upstream;

/// <summary>Timeouts and intervals of the upstream client (defaults per spec; tests shorten them).</summary>
public sealed record UpstreamOptions
{
    /// <summary>Hard timeout of one query (send to answer).</summary>
    public TimeSpan QueryTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Hard timeout of resolving the host name.</summary>
    public TimeSpan DnsTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>How long a resolved address is reused.</summary>
    public TimeSpan DnsTtl { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Poll interval after a good answer (grows to <see cref="MaxPoll"/> while answers stay good).</summary>
    public TimeSpan MinPoll { get; init; } = TimeSpan.FromSeconds(64);

    public TimeSpan MaxPoll { get; init; } = TimeSpan.FromSeconds(1024);

    /// <summary>Good answers in a row before the poll interval doubles.</summary>
    public int GoodAnswersBeforeLongerPoll { get; init; } = 4;

    /// <summary>First retry after a failure; doubles per failure up to the poll interval (2, 4, 8 ... s).</summary>
    public TimeSpan FirstRetry { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Failures in a row after which the upstream counts as not reachable (local mode, warning).</summary>
    public int FailuresBeforeUnreachable { get; init; } = 3;
}

public enum UpstreamFailure
{
    Timeout = 0,
    KissOfDeath = 1,
    InvalidAnswer = 2,
    DnsFailure = 3,
    Network = 4,
}

/// <summary>A query failed; <see cref="Exception.Message"/> is the user text ("No answer within 2 s").</summary>
public sealed class UpstreamException : Exception
{
    public UpstreamException(UpstreamFailure failure, string message, string? kissCode = null, Exception? inner = null)
        : base(message, inner)
    {
        Failure = failure;
        KissCode = kissCode;
    }

    public UpstreamException()
    {
    }

    public UpstreamException(string message)
        : base(message)
    {
    }

    public UpstreamException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public UpstreamFailure Failure { get; }

    /// <summary>"RATE", "DENY", "RSTR" for a Kiss-o'-Death.</summary>
    public string? KissCode { get; }
}

/// <summary>One good upstream answer.</summary>
/// <param name="Address">Upstream address that answered.</param>
/// <param name="Stratum">Upstream stratum (1..15).</param>
/// <param name="RootDelaySeconds">Upstream root delay.</param>
/// <param name="RootDispersionSeconds">Upstream root dispersion.</param>
/// <param name="OffsetSeconds">Upstream clock minus server clock.</param>
/// <param name="DelaySeconds">Round trip.</param>
/// <param name="TimeUtc">Server time when the answer arrived.</param>
public sealed record UpstreamSample(
    IPEndPoint Address,
    byte Stratum,
    double RootDelaySeconds,
    double RootDispersionSeconds,
    double OffsetSeconds,
    double DelaySeconds,
    DateTime TimeUtc)
{
    /// <summary>"3 ms off, 12 ms round trip".</summary>
    public string Describe() => string.Create(CultureInfo.InvariantCulture,
        $"{Serving.RequestLog.FormatOffset(Math.Abs(OffsetSeconds) * 1000).TrimStart('+')} off, {DelaySeconds * 1000:0} ms round trip");

    /// <summary>
    /// The state to serve: stratum + 1, reference id = upstream address (IPv4) or the first 4 bytes of the MD5 of the
    /// IPv6 address (RFC 5905), root delay and dispersion accumulated.
    /// </summary>
    public Serving.TimeSourceState ToState(string host) => new(
        (byte)Math.Min(15, Stratum + 1),
        ReferenceIdOf(Address.Address),
        TimeUtc,
        RootDelaySeconds + Math.Max(0, DelaySeconds),
        RootDispersionSeconds + (Math.Max(0, DelaySeconds) / 2) + Math.Pow(2, Serving.NtpResponder.Precision),
        host);

    public static uint ReferenceIdOf(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var a = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        Span<byte> bytes = stackalloc byte[16];
        a.TryWriteBytes(bytes, out var written);
        if (a.AddressFamily == AddressFamily.InterNetwork)
        {
            return BinaryPrimitives.ReadUInt32BigEndian(bytes);
        }

#pragma warning disable CA5351 // RFC 5905 defines the IPv6 reference id as the first 32 bits of the MD5 hash; not used for security.
        var hash = MD5.HashData(bytes[..written]);
#pragma warning restore CA5351
        return BinaryPrimitives.ReadUInt32BigEndian(hash);
    }
}

/// <summary>
/// SNTP client for the upstream (same codec): one request, the answer must come from the queried address and carry our
/// transmit timestamp as originate (stray, late and spoofed packets are ignored), then it is sanity checked. A fresh
/// socket per query, so an answer that arrives after the timeout can never be taken for a later query.
/// </summary>
public static class UpstreamClient
{
    public static async Task<UpstreamSample> QueryAsync(IPEndPoint server, TimeSpan timeout, TimeProvider time, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(time);
        using var socket = new Socket(server.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(server.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0));
        if (OperatingSystem.IsWindows())
        {
            socket.IOControl(unchecked((int)0x9800000C), [0, 0, 0, 0], null);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var request = new byte[NtpPacket.Length];
        var buffer = new byte[1024];
        var remote = new SocketAddress(server.AddressFamily);
        try
        {
            // Random low 16 bits of the fraction (15 µs) make the originate check hard to spoof.
            var t1 = time.GetUtcNow().UtcDateTime;
            var origin = (NtpTimestamp.FromDateTime(t1) & ~0xFFFFUL) | (ushort)RandomNumberGenerator.GetInt32(0, 0x10000);
            new NtpPacket { Version = 4, Mode = NtpMode.Client, Poll = 6, TransmitTimestamp = origin }.Write(request);
            t1 = NtpTimestamp.ToDateTime(origin, t1);
            await socket.SendToAsync(request, SocketFlags.None, server, cts.Token).ConfigureAwait(false);

            while (true)
            {
                int length;
                try
                {
                    length = await socket.ReceiveFromAsync(buffer, SocketFlags.None, remote, cts.Token).ConfigureAwait(false);
                }
                catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.MessageSize)
                {
                    continue; // ICMP from an earlier packet or an oversized stray datagram
                }

                var t4 = time.GetUtcNow().UtcDateTime;
                var from = (IPEndPoint)server.Create(remote);
                if (!SameEndpoint(from, server) || !NtpPacket.TryRead(buffer.AsSpan(0, length), out var answer)
                    || answer.Mode != NtpMode.Server || answer.OriginateTimestamp != origin)
                {
                    continue; // not our answer
                }

                return Validate(server, answer, t1, t4, timeout);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new UpstreamException(UpstreamFailure.Timeout, "No answer within " + Seconds(timeout));
        }
        catch (SocketException ex)
        {
            throw new UpstreamException(UpstreamFailure.Network, ex.SocketErrorCode switch
            {
                SocketError.NetworkUnreachable or SocketError.HostUnreachable => "Network unreachable",
                _ => "Network error: " + ex.SocketErrorCode,
            }, inner: ex);
        }
    }

    private static bool SameEndpoint(IPEndPoint a, IPEndPoint b) =>
        a.Port == b.Port && Normalize(a.Address).Equals(Normalize(b.Address));

    private static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    internal static UpstreamSample Validate(IPEndPoint server, in NtpPacket answer, DateTime t1, DateTime t4, TimeSpan timeout)
    {
        if (answer.Stratum == 0)
        {
            var code = NtpPacket.AsciiCode(answer.ReferenceId) ?? "?";
            var why = code switch
            {
                "RATE" => " (too many requests)",
                "DENY" or "RSTR" => " (access denied)",
                _ => string.Empty,
            };
            throw new UpstreamException(UpstreamFailure.KissOfDeath, "The server refused the request" + why, code);
        }

        if (answer.Leap == NtpLeap.Unsynchronized || answer.Stratum > 15)
        {
            throw new UpstreamException(UpstreamFailure.InvalidAnswer, "The server is not synchronized");
        }

        if (answer.TransmitTimestamp == 0 || answer.ReceiveTimestamp == 0)
        {
            throw new UpstreamException(UpstreamFailure.InvalidAnswer, "The server sent an answer without timestamps");
        }

        var t2 = NtpTimestamp.ToDateTime(answer.ReceiveTimestamp, t1);
        var t3 = NtpTimestamp.ToDateTime(answer.TransmitTimestamp, t1);
        var delay = ((t4 - t1) - (t3 - t2)).TotalSeconds;
        var roundTrip = (t4 - t1).TotalSeconds;
        if (roundTrip > timeout.TotalSeconds || delay > timeout.TotalSeconds)
        {
            throw new UpstreamException(UpstreamFailure.InvalidAnswer, "The answer took longer than " + Seconds(timeout));
        }

        var offset = (((t2 - t1) + (t3 - t4)).TotalSeconds) / 2;
        return new UpstreamSample(server, answer.Stratum, NtpTimestamp.FromShort(answer.RootDelay), NtpTimestamp.FromShort(answer.RootDispersion),
            offset, Math.Max(0, delay), t4);
    }

    internal static string Seconds(TimeSpan value) =>
        value.TotalSeconds >= 1
            ? value.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture) + " s"
            : value.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture) + " ms";
}
