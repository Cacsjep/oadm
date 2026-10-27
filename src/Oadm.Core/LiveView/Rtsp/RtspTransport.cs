using System.Globalization;
using System.Net;

namespace Oadm.Core.LiveView.Rtsp;

/// <summary>
/// The RTSP Transport header (RFC 2326 12.39) as far as OADM uses it: RTP interleaved on the RTSP connection
/// (<c>RTP/AVP/TCP;unicast;interleaved=0-1</c>, live view and event streams) or RTP over UDP to two local ports
/// (<c>RTP/AVP;unicast;client_port=50000-50001</c>, the stream probes).
/// </summary>
/// <param name="IsTcp">RTP/AVP/TCP: the packets come interleaved on the RTSP connection.</param>
/// <param name="Interleaved">The RTP channel (RTCP is the next one), for TCP.</param>
/// <param name="ClientRtpPort">The local RTP port (RTCP is the next one), for UDP.</param>
/// <param name="ServerRtpPort">The camera's RTP port, for UDP.</param>
/// <param name="ServerRtcpPort">The camera's RTCP port (receiver reports go there), for UDP.</param>
/// <param name="Source">The address the camera sends from (<c>source=</c>), when it says.</param>
/// <param name="Ssrc">The camera's SSRC (<c>ssrc=</c>, hex), when it says.</param>
public sealed record RtspTransport(
    bool IsTcp,
    byte? Interleaved = null,
    int? ClientRtpPort = null,
    int? ServerRtpPort = null,
    int? ServerRtcpPort = null,
    IPAddress? Source = null,
    uint? Ssrc = null)
{
    /// <summary>RTSP status 461 Unsupported Transport: the camera refuses the transport (e.g. no UDP).</summary>
    public const int UnsupportedTransport = 461;

    /// <summary><c>RTP/AVP/TCP;unicast;interleaved=0-1</c>.</summary>
    public static string Tcp(byte channel = 0) =>
        string.Create(CultureInfo.InvariantCulture, $"RTP/AVP/TCP;unicast;interleaved={channel}-{channel + 1}");

    /// <summary><c>RTP/AVP;unicast;client_port=50000-50001</c>: RTP to the even port, RTCP to the next one.</summary>
    public static string Udp(int rtpPort)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(rtpPort, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rtpPort, 65534);
        return string.Create(CultureInfo.InvariantCulture, $"RTP/AVP;unicast;client_port={rtpPort}-{rtpPort + 1}");
    }

    /// <summary>Parses the camera's answer; null when the header is missing or names no RTP transport.</summary>
    public static RtspTransport? Parse(string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return null;
        }

        // A server answers with one transport; take the first of a list.
        var parts = header.Split(',')[0].Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !parts[0].StartsWith("RTP/AVP", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var isTcp = parts[0].Equals("RTP/AVP/TCP", StringComparison.OrdinalIgnoreCase);
        byte? interleaved = null;
        int? client = null;
        int? serverRtp = null;
        int? serverRtcp = null;
        IPAddress? source = null;
        uint? ssrc = null;
        foreach (var part in parts.Skip(1))
        {
            var eq = part.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                continue;
            }

            var name = part[..eq];
            var value = part[(eq + 1)..];
            if (name.Equals("interleaved", StringComparison.OrdinalIgnoreCase))
            {
                interleaved = byte.TryParse(value.Split('-')[0], NumberStyles.None, CultureInfo.InvariantCulture, out var channel) ? channel : null;
            }
            else if (name.Equals("client_port", StringComparison.OrdinalIgnoreCase))
            {
                client = Ports(value).First;
            }
            else if (name.Equals("server_port", StringComparison.OrdinalIgnoreCase))
            {
                (serverRtp, serverRtcp) = Ports(value);
            }
            else if (name.Equals("source", StringComparison.OrdinalIgnoreCase))
            {
                source = IPAddress.TryParse(value, out var address) ? address : null;
            }
            else if (name.Equals("ssrc", StringComparison.OrdinalIgnoreCase))
            {
                ssrc = uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id) ? id : null;
            }
        }

        return new RtspTransport(isTcp, interleaved, client, serverRtp, serverRtcp, source, ssrc);
    }

    /// <summary>"6970-6971" or "6970": the RTP port and the RTCP port (the next one when only one is named).</summary>
    private static (int? First, int? Second) Ports(string value)
    {
        var range = value.Split('-', StringSplitOptions.TrimEntries);
        if (!int.TryParse(range[0], NumberStyles.None, CultureInfo.InvariantCulture, out var first) || first is < 1 or > 65535)
        {
            return (null, null);
        }

        var second = range.Length > 1 && int.TryParse(range[1], NumberStyles.None, CultureInfo.InvariantCulture, out var s) && s is >= 1 and <= 65535
            ? s
            : first < 65535 ? first + 1 : (int?)null;
        return (first, second);
    }
}
