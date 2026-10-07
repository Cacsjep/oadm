using Oadm.Contracts.V1;

namespace Oadm.Client.Api;

/// <summary>
/// Recorded access units for <see cref="FakeOadmApi.WatchLiveViewAsync"/>, embedded as
/// FakeLiveView.h264.au / FakeLiveView.h265.au (written by RtpRecorderTests.WriteFakeLiveViewResources
/// from the RTP fixtures). Per unit: keyframe flag byte, 32-bit big-endian length, Annex B data.
/// </summary>
internal static class FakeLiveVideo
{
    private static readonly Lazy<IReadOnlyList<(bool Keyframe, byte[] Data)>> H264 = new(() => Read("FakeLiveView.h264.au"));
    private static readonly Lazy<IReadOnlyList<(bool Keyframe, byte[] Data)>> H265 = new(() => Read("FakeLiveView.h265.au"));

    public static IReadOnlyList<(bool Keyframe, byte[] Data)> Load(VideoCodec codec) => codec == VideoCodec.H265 ? H265.Value : H264.Value;

    private static List<(bool Keyframe, byte[] Data)> Read(string name)
    {
        using var stream = typeof(FakeLiveVideo).Assembly.GetManifestResourceStream("Oadm.Client.Api." + name)
            ?? throw new InvalidOperationException($"Embedded resource {name} missing.");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        var bytes = ms.ToArray();
        var units = new List<(bool, byte[])>();
        for (var i = 0; i + 5 <= bytes.Length;)
        {
            var keyframe = bytes[i] == 1;
            var length = (bytes[i + 1] << 24) | (bytes[i + 2] << 16) | (bytes[i + 3] << 8) | bytes[i + 4];
            i += 5;
            units.Add((keyframe, bytes.AsSpan(i, length).ToArray()));
            i += length;
        }

        return units;
    }
}
