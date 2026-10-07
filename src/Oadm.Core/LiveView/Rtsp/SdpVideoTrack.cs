using System.Globalization;

namespace Oadm.Core.LiveView.Rtsp;

/// <summary>The first video media section of an SDP description (RFC 4566), as far as RTSP playback needs it.</summary>
public sealed record SdpVideoTrack(
    int PayloadType,
    string EncodingName,
    int ClockRate,
    string? Control,
    IReadOnlyDictionary<string, string> FormatParameters)
{
    /// <summary>Parses the SDP and returns its first video track, or null when there is none.</summary>
    public static SdpVideoTrack? Parse(string sdp)
    {
        ArgumentNullException.ThrowIfNull(sdp);
        var inVideo = false;
        int? payloadType = null;
        string? encoding = null;
        var clockRate = 90000;
        string? control = null;
        var fmtp = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in sdp.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("m=", StringComparison.Ordinal))
            {
                if (inVideo)
                {
                    break; // only the first video section
                }

                inVideo = line.StartsWith("m=video", StringComparison.Ordinal);
                if (inVideo)
                {
                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 4 && int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var pt))
                    {
                        payloadType = pt;
                    }
                }

                continue;
            }

            if (!inVideo)
            {
                continue;
            }

            if (line.StartsWith("a=control:", StringComparison.Ordinal))
            {
                control = line["a=control:".Length..].Trim();
            }
            else if (line.StartsWith("a=rtpmap:", StringComparison.Ordinal))
            {
                // a=rtpmap:96 H264/90000
                var value = line["a=rtpmap:".Length..];
                var space = value.IndexOf(' ', StringComparison.Ordinal);
                if (space > 0 && int.TryParse(value[..space], NumberStyles.None, CultureInfo.InvariantCulture, out var pt) && pt == payloadType)
                {
                    var codec = value[(space + 1)..].Split('/');
                    encoding = codec[0].Trim();
                    if (codec.Length > 1 && int.TryParse(codec[1], NumberStyles.None, CultureInfo.InvariantCulture, out var rate))
                    {
                        clockRate = rate;
                    }
                }
            }
            else if (line.StartsWith("a=fmtp:", StringComparison.Ordinal))
            {
                var value = line["a=fmtp:".Length..];
                var space = value.IndexOf(' ', StringComparison.Ordinal);
                if (space > 0)
                {
                    foreach (var pair in value[(space + 1)..].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        var eq = pair.IndexOf('=', StringComparison.Ordinal);
                        if (eq > 0)
                        {
                            fmtp[pair[..eq].Trim()] = pair[(eq + 1)..].Trim();
                        }
                    }
                }
            }
        }

        if (payloadType is null || encoding is null)
        {
            return null;
        }

        return new SdpVideoTrack(payloadType.Value, encoding, clockRate, control, fmtp);
    }

    /// <summary>Codec of the track, or null when the live view cannot handle it.</summary>
    public VideoCodecKind? Codec => EncodingName.ToUpperInvariant() switch
    {
        "H264" => VideoCodecKind.H264,
        "H265" or "HEVC" => VideoCodecKind.H265,
        _ => null,
    };

    /// <summary>Parameter sets from sprop-parameter-sets (H.264) or sprop-vps/sps/pps (H.265), raw NAL units.</summary>
    public IReadOnlyList<byte[]> ParameterSets()
    {
        var result = new List<byte[]>();
        var keys = Codec == VideoCodecKind.H265 ? new[] { "sprop-vps", "sprop-sps", "sprop-pps" } : ["sprop-parameter-sets"];
        foreach (var key in keys)
        {
            if (!FormatParameters.TryGetValue(key, out var value))
            {
                continue;
            }

            foreach (var item in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                try
                {
                    result.Add(Convert.FromBase64String(item));
                }
                catch (FormatException)
                {
                    // ignore a damaged entry; in-band parameter sets still work
                }
            }
        }

        return result;
    }

    /// <summary>Resolves the track control URL against Content-Base (or the request URL), RFC 2326 C.1.1.</summary>
    public Uri ResolveControl(Uri baseUri)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        if (string.IsNullOrEmpty(Control) || Control == "*")
        {
            return baseUri;
        }

        if (Uri.TryCreate(Control, UriKind.Absolute, out var absolute))
        {
            return absolute;
        }

        var text = baseUri.ToString();
        if (!text.EndsWith('/'))
        {
            text += "/";
        }

        return new Uri(text + Control, UriKind.Absolute);
    }
}
