using System.Globalization;

using Google.Protobuf;

using Oadm.Client.Api;
using Oadm.Contracts.V1;

namespace Oadm.Client.LiveView;

/// <summary>
/// <c>Oadm.Client --check-decoder</c>: loads the bundled FFmpeg and decodes the embedded recorded
/// H.264 and H.265 clips without opening a window, prints the result and returns 0 on success.
/// Used to verify a published single-file exe on a machine.
/// </summary>
internal static class DecoderSelfCheck
{
    public const string Argument = "--check-decoder";

    public static int Run(TextWriter output)
    {
        FfmpegStatus status = FfmpegRuntime.Status;
        output.WriteLine($"FFmpeg: {(status.IsAvailable ? "available" : "NOT available")}");
        output.WriteLine($"  version:   {status.Version ?? "-"}");
        output.WriteLine($"  directory: {status.Directory ?? "-"}");
        output.WriteLine($"  decoders:  {string.Join(", ", status.Decoders)}");
        if (!status.IsAvailable)
        {
            output.WriteLine($"  error:     {status.Error}");
            return 1;
        }

        var ok = true;
        foreach (VideoCodec codec in new[] { VideoCodec.H264, VideoCodec.H265 })
        {
            var units = FakeLiveVideo.Load(codec);
            using var decoder = new FfmpegVideoDecoder(codec) { ConvertToBitmap = false };
            foreach (var (keyframe, data) in units)
            {
                decoder.Decode(new LiveViewFrame { Codec = codec, Keyframe = keyframe, Data = ByteString.CopyFrom(data) });
            }

            var passed = decoder.DecodedFrames >= units.Count - 1 && decoder.LastSize == (640, 360);
            ok &= passed;
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{codec}: {decoder.DecodedFrames}/{units.Count} pictures, {decoder.LastSize.Width}x{decoder.LastSize.Height}, {decoder.Errors} errors: {(passed ? "OK" : "FAILED")}"));
        }

        return ok ? 0 : 1;
    }
}
