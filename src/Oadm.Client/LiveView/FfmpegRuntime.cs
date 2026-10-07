using System.Runtime.InteropServices;

using FFmpeg.AutoGen;

namespace Oadm.Client.LiveView;

/// <summary>Outcome of loading the FFmpeg libraries.</summary>
public sealed record FfmpegStatus(bool IsAvailable, string? Directory, string? Version, IReadOnlyList<string> Decoders, string? Error);

/// <summary>
/// Finds and loads the FFmpeg decoder libraries (avutil, swresample, avcodec, swscale) that ship
/// with the client: inside the single-file exe they are extracted by the host and listed in
/// NATIVE_DLL_SEARCH_DIRECTORIES; in a normal build they sit in runtimes/&lt;rid&gt;/native.
/// OADM_FFMPEG_DIR overrides the location, which also lets users replace the LGPL libraries.
/// Loads once per process; never throws.
/// </summary>
public static class FfmpegRuntime
{
    public const string DirectoryVariable = "OADM_FFMPEG_DIR";

    private static readonly Lazy<FfmpegStatus> Loaded = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    public static FfmpegStatus Status => Loaded.Value;

    /// <summary>Directories searched for the libraries, in order.</summary>
    public static IEnumerable<string> CandidateDirectories()
    {
        var overridden = Environment.GetEnvironmentVariable(DirectoryVariable);
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            yield return overridden;
        }

        if (AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") is string searchDirectories)
        {
            foreach (var dir in searchDirectories.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                yield return dir;
            }
        }

        yield return AppContext.BaseDirectory;
        yield return Path.Combine(AppContext.BaseDirectory, "runtimes", RuntimeInformation.RuntimeIdentifier, "native");
    }

    /// <summary>File name of avcodec on this platform, e.g. avcodec-63.dll, libavcodec.so.63, libavcodec.63.dylib.</summary>
    public static string AvcodecFileName()
    {
        var version = ffmpeg.LibraryVersionMap["avcodec"];
        if (OperatingSystem.IsWindows())
        {
            return $"avcodec-{version}.dll";
        }

        return OperatingSystem.IsMacOS() ? $"libavcodec.{version}.dylib" : $"libavcodec.so.{version}";
    }

    private static FfmpegStatus Load()
    {
        string? directory = null;
        try
        {
            var file = AvcodecFileName();
            directory = CandidateDirectories().FirstOrDefault(d => File.Exists(Path.Combine(d, file)));
            if (directory is null)
            {
                return new FfmpegStatus(false, null, null, [], $"{file} not found");
            }

            ffmpeg.RootPath = directory;
            var version = ffmpeg.av_version_info();
            ffmpeg.av_log_set_level(ffmpeg.AV_LOG_QUIET);
            var decoders = new List<string>();
            unsafe
            {
                if (ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_H264) != null)
                {
                    decoders.Add("h264");
                }

                if (ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_HEVC) != null)
                {
                    decoders.Add("hevc");
                }
            }

            // swscale is needed for the BGRA conversion; load it now so a missing library shows up here.
            _ = ffmpeg.swscale_version();
            return new FfmpegStatus(decoders.Count > 0, directory, version, decoders, decoders.Count > 0 ? null : "no H.264/H.265 decoder in this FFmpeg build");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or NotSupportedException or InvalidOperationException or KeyNotFoundException)
        {
            return new FfmpegStatus(false, directory, null, [], ex.Message);
        }
    }
}
