using System.Globalization;

namespace Oadm.Core.LiveView.Rtsp;

/// <summary>The ONVIF metadata media section of an SDP description (<c>m=application ... vnd.onvif.metadata</c>).</summary>
public sealed record SdpMetadataTrack(int PayloadType, string EncodingName, int ClockRate, string? Control)
{
    /// <summary>The RTP encoding name of ONVIF metadata (RFC 4566 rtpmap, ONVIF Streaming Specification 5.2.1.1).</summary>
    public const string OnvifMetadata = "vnd.onvif.metadata";

    /// <summary>Returns the first <c>application</c> media of the SDP that carries ONVIF metadata, or null.</summary>
    public static SdpMetadataTrack? Parse(string sdp)
    {
        ArgumentNullException.ThrowIfNull(sdp);
        SdpMetadataTrack? found = null;
        var inApplication = false;
        var payloadTypes = new List<int>();
        string? control = null;
        string? encoding = null;
        int? payloadType = null;
        var clockRate = 90000;

        bool Finish()
        {
            if (inApplication && encoding is not null && payloadType is not null
                && encoding.Equals(OnvifMetadata, StringComparison.OrdinalIgnoreCase))
            {
                found = new SdpMetadataTrack(payloadType.Value, encoding, clockRate, control);
                return true;
            }

            return false;
        }

        foreach (var raw in sdp.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("m=", StringComparison.Ordinal))
            {
                if (Finish())
                {
                    return found;
                }

                inApplication = line.StartsWith("m=application", StringComparison.Ordinal);
                payloadTypes.Clear();
                control = null;
                encoding = null;
                payloadType = null;
                clockRate = 90000;
                if (inApplication)
                {
                    foreach (var part in line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(3))
                    {
                        if (int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var pt))
                        {
                            payloadTypes.Add(pt);
                        }
                    }
                }

                continue;
            }

            if (!inApplication)
            {
                continue;
            }

            if (line.StartsWith("a=control:", StringComparison.Ordinal))
            {
                control = line["a=control:".Length..].Trim();
            }
            else if (line.StartsWith("a=rtpmap:", StringComparison.Ordinal) && encoding is null)
            {
                // a=rtpmap:98 vnd.onvif.metadata/90000
                var value = line["a=rtpmap:".Length..];
                var space = value.IndexOf(' ', StringComparison.Ordinal);
                if (space > 0 && int.TryParse(value[..space], NumberStyles.None, CultureInfo.InvariantCulture, out var pt) && payloadTypes.Contains(pt))
                {
                    var parts = value[(space + 1)..].Split('/');
                    if (parts[0].Trim().Equals(OnvifMetadata, StringComparison.OrdinalIgnoreCase))
                    {
                        encoding = parts[0].Trim();
                        payloadType = pt;
                        if (parts.Length > 1 && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var rate))
                        {
                            clockRate = rate;
                        }
                    }
                }
            }
        }

        Finish();
        return found;
    }

    /// <summary>Resolves the media control URL against Content-Base (or the request URL), RFC 2326 C.1.1.</summary>
    public Uri ResolveControl(Uri baseUri) => SdpVideoTrack.ResolveControl(baseUri, Control);
}
