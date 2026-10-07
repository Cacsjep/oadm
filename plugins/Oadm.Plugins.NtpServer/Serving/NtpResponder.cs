using System.Diagnostics;

using Oadm.Plugins.NtpServer.Protocol;

namespace Oadm.Plugins.NtpServer.Serving;

/// <summary>
/// What the server answers with: the local clock (stratum 10, "LOCL", like chrony's <c>local stratum 10</c>) or the
/// last good upstream sample (stratum = upstream + 1, reference id = upstream address). Immutable: the serving loop
/// reads the current instance without locks, the upstream monitor swaps it.
/// </summary>
/// <param name="Stratum">1..15.</param>
/// <param name="ReferenceId">"LOCL" or the upstream reference id.</param>
/// <param name="ReferenceTimeUtc">Last upstream sync; ignored in local mode (reference time = now).</param>
/// <param name="RootDelaySeconds">Round trip to the primary reference.</param>
/// <param name="RootDispersionSeconds">Dispersion at <paramref name="ReferenceTimeUtc"/>; grows by 15 ppm per second after it.</param>
/// <param name="UpstreamHost">Null in local mode.</param>
public sealed record TimeSourceState(
    byte Stratum,
    uint ReferenceId,
    DateTime ReferenceTimeUtc,
    double RootDelaySeconds,
    double RootDispersionSeconds,
    string? UpstreamHost)
{
    /// <summary>Stratum of the local clock mode.</summary>
    public const byte LocalStratum = 10;

    /// <summary>Root dispersion announced in local mode (10 ms, like chrony's local reference).</summary>
    public const double LocalRootDispersionSeconds = 0.010;

    /// <summary>Frequency tolerance used for the dispersion growth after an upstream sync (RFC 5905 PHI).</summary>
    public const double Phi = 15e-6;

    public static readonly uint LocalReferenceId = NtpPacket.AsciiId("LOCL");

    public static TimeSourceState Local { get; } = new(LocalStratum, LocalReferenceId, DateTime.MinValue, 0, LocalRootDispersionSeconds, null);

    public bool IsLocal => UpstreamHost is null;
}

/// <summary>Builds server answers (pure, allocation free). RFC 5905 server mode, SNTPv4 compatible (RFC 4330).</summary>
public static class NtpResponder
{
    /// <summary>log2 of the clock resolution (Stopwatch), e.g. -23 for a 10 MHz counter.</summary>
    public static sbyte Precision { get; } = (sbyte)Math.Clamp(Math.Round(Math.Log2(1.0 / Stopwatch.Frequency)), -32, 0);

    /// <summary>
    /// Checks a request and writes the 48-byte answer into <paramref name="response"/>. False (nothing to send) for
    /// anything but a well-formed mode 3 request of version 3 or 4 with a transmit timestamp: control (6), private (7),
    /// broadcast and symmetric modes, short packets and other versions are ignored silently (no amplification). The
    /// transmit timestamp is set to the receive time; overwrite it right before sending
    /// (<see cref="NtpPacket.WriteTransmitTimestamp"/>).
    /// </summary>
    public static bool TryBuildResponse(ReadOnlySpan<byte> request, DateTime receiveUtc, TimeSourceState state, Span<byte> response, out NtpPacket parsed)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!IsValidRequest(request, out parsed))
        {
            return false;
        }

        var receive = NtpTimestamp.FromDateTime(receiveUtc);
        var dispersion = state.RootDispersionSeconds;
        ulong reference;
        if (state.IsLocal)
        {
            reference = receive;
        }
        else
        {
            var age = Math.Max(0, (receiveUtc - state.ReferenceTimeUtc).TotalSeconds);
            dispersion += age * TimeSourceState.Phi;
            reference = NtpTimestamp.FromDateTime(state.ReferenceTimeUtc);
        }

        new NtpPacket
        {
            Leap = NtpLeap.NoWarning,
            Version = parsed.Version,
            Mode = NtpMode.Server,
            Stratum = state.Stratum,
            Poll = parsed.Poll,
            Precision = Precision,
            RootDelay = NtpTimestamp.ToShort(state.RootDelaySeconds),
            RootDispersion = NtpTimestamp.ToShort(dispersion),
            ReferenceId = state.ReferenceId,
            ReferenceTimestamp = reference,
            OriginateTimestamp = parsed.TransmitTimestamp,
            ReceiveTimestamp = receive,
            TransmitTimestamp = receive,
        }.Write(response);
        return true;
    }

    /// <summary>A mode 3 request of version 3 or 4, at least 48 bytes, with a non-zero transmit timestamp.</summary>
    public static bool IsValidRequest(ReadOnlySpan<byte> request, out NtpPacket parsed) =>
        NtpPacket.TryRead(request, out parsed)
        && parsed.Mode == NtpMode.Client
        && parsed.Version is 3 or 4
        && parsed.TransmitTimestamp != 0;

    /// <summary>Kiss-o'-Death answer (stratum 0, LI 3, the ASCII <paramref name="code"/> as reference id, e.g. "RATE").</summary>
    public static void BuildKissOfDeath(in NtpPacket request, DateTime receiveUtc, string code, Span<byte> response)
    {
        var now = NtpTimestamp.FromDateTime(receiveUtc);
        new NtpPacket
        {
            Leap = NtpLeap.Unsynchronized,
            Version = request.Version,
            Mode = NtpMode.Server,
            Stratum = 0,
            Poll = request.Poll,
            Precision = Precision,
            ReferenceId = NtpPacket.AsciiId(code),
            OriginateTimestamp = request.TransmitTimestamp,
            ReceiveTimestamp = now,
            TransmitTimestamp = now,
        }.Write(response);
    }
}
