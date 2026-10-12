using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

using FFmpeg.AutoGen;

using Microsoft.Extensions.Logging;

using Oadm.Client.Api;

using Oadm.Contracts.V1;

namespace Oadm.Client.LiveView;

/// <summary>Where the GPU decoding test stands.</summary>
public enum GpuDecodingState
{
    /// <summary>Not tested (yet), or not testable in this process (tests): decoding uses the processor, no warning.</summary>
    NotChecked,

    Checking,

    /// <summary>The test decoded both clips (or one codec) on the graphics card.</summary>
    Available,

    /// <summary>The test failed: decoding uses the processor and the live view shows <see cref="GpuDecodingStatus.Problem"/>.</summary>
    Unavailable,

    /// <summary>Turned off in Settings: the processor decodes, no warning.</summary>
    Off,
}

/// <summary>The result of the GPU decoding test.</summary>
/// <param name="Device">FFmpeg device type that worked ("d3d11va", "vaapi", "videotoolbox", "cuda").</param>
/// <param name="Codecs">The codecs the graphics card decoded in the test.</param>
/// <param name="Problem">What the user can do when it does not work (orange warning in the live view).</param>
public sealed record GpuDecodingStatus(GpuDecodingState State, string? Device, IReadOnlySet<VideoCodec> Codecs, string? Problem)
{
    public static GpuDecodingStatus NotChecked { get; } = new(GpuDecodingState.NotChecked, null, new HashSet<VideoCodec>(), null);

    public static GpuDecodingStatus Off { get; } = new(GpuDecodingState.Off, null, new HashSet<VideoCodec>(), null);

    /// <summary>The graphics card decodes <paramref name="codec"/>.</summary>
    public bool Decodes(VideoCodec codec) => State == GpuDecodingState.Available && Device is not null && Codecs.Contains(codec);

    /// <summary>The warning for a stream of <paramref name="codec"/> on the processor although GPU decoding is on; null otherwise.</summary>
    public string? WarningFor(VideoCodec codec) => State switch
    {
        GpuDecodingState.Unavailable => Problem,
        GpuDecodingState.Available when !Codecs.Contains(codec) => string.Create(CultureInfo.InvariantCulture,
            $"The graphics card cannot decode {(codec == VideoCodec.H265 ? "H.265" : "H.264")}; the processor decodes it. {GpuDecodingTexts.TurnOff}"),
        _ => null,
    };
}

/// <summary>The user texts of GPU decoding (one phrase per concept).</summary>
public static class GpuDecodingTexts
{
    public const string TurnOff = "To hide this, turn off \"Decode video on the graphics card\" in Settings.";

    public const string ChipText = "GPU decoding not available";

    public const string LinuxDriver = "Install the video driver of the graphics card (Intel: intel-media-va-driver, AMD: mesa-va-drivers, "
        + "NVIDIA: the NVIDIA driver) or turn off \"Decode video on the graphics card\" in Settings.";

    public const string NoDevice = "No graphics card for video decoding found. Turn off \"Decode video on the graphics card\" in Settings.";

    public const string TestStopped = "The graphics card driver stopped the decoding test. Update the driver or turn off "
        + "\"Decode video on the graphics card\" in Settings.";

    public const string Generic = "The graphics card cannot decode the video. Update the driver or turn off "
        + "\"Decode video on the graphics card\" in Settings.";
}

/// <summary>
/// One FFmpeg hardware device for the whole client (all streams share it). Created only after the GPU decoding test passed
/// in a separate process, so a driver that crashes never takes the client down. On Linux the bundled FFmpeg carries VA-API
/// with the driver path of its build machine: <see cref="PrepareEnvironment"/> points it at the system's driver folders
/// (through libc setenv; .NET's environment does not reach native code on Unix).
/// </summary>
public static unsafe class HwDevice
{
    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, IntPtr> Devices = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Overrides the device types to try ("vaapi,cuda"; support and tests).</summary>
    public const string DevicesVariable = "OADM_GPU_DEVICES";

    /// <summary>The device types to try on this platform, best first (or those of <see cref="DevicesVariable"/>).</summary>
    public static IReadOnlyList<string> Candidates()
    {
        var overridden = Environment.GetEnvironmentVariable(DevicesVariable);
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            return overridden.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        return OperatingSystem.IsWindows() ? ["d3d11va", "dxva2"]
            : OperatingSystem.IsMacOS() ? ["videotoolbox"]
            : ["vaapi", "cuda"];
    }

    /// <summary>Linux: the VA-API driver folders of the system, unless the user set LIBVA_DRIVERS_PATH. Idempotent.</summary>
    public static void PrepareEnvironment()
    {
        if (!OperatingSystem.IsLinux() || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LIBVA_DRIVERS_PATH")))
        {
            return;
        }

        string[] folders = ["/usr/lib/x86_64-linux-gnu/dri", "/usr/lib/aarch64-linux-gnu/dri", "/usr/lib64/dri", "/usr/lib/dri", "/usr/local/lib/dri"];
        var existing = folders.Where(Directory.Exists).ToList();
        if (existing.Count > 0)
        {
            var path = string.Join(':', existing);
            _ = setenv("LIBVA_DRIVERS_PATH", path, 0);
            Environment.SetEnvironmentVariable("LIBVA_DRIVERS_PATH", path);
        }
    }

    /// <summary>Opens the device of <paramref name="name"/>; null and the reason when it cannot.</summary>
    public static AVBufferRef* TryCreate(string name, out string? error)
    {
        error = null;
        var type = ffmpeg.av_hwdevice_find_type_by_name(name);
        if (type == AVHWDeviceType.AV_HWDEVICE_TYPE_NONE)
        {
            error = $"{name} is not in this FFmpeg build";
            return null;
        }

        AVBufferRef* device = null;
        var result = ffmpeg.av_hwdevice_ctx_create(&device, type, null, null, 0);
        if (result < 0 || device == null)
        {
            error = $"{name}: {ErrorText(result)}";
            return null;
        }

        return device;
    }

    /// <summary>
    /// The shared device of <paramref name="name"/> (a new reference the caller unrefs), or null when it cannot be opened (a
    /// failure is remembered for the process: one try per device type).
    /// </summary>
    public static AVBufferRef* Acquire(string name, out AVHWDeviceType type)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        lock (Gate)
        {
            if (!Devices.TryGetValue(name, out var device))
            {
                device = (IntPtr)TryCreate(name, out _);
                Devices[name] = device;
            }

            type = device == IntPtr.Zero ? AVHWDeviceType.AV_HWDEVICE_TYPE_NONE : ffmpeg.av_hwdevice_find_type_by_name(name);
            return device == IntPtr.Zero ? null : ffmpeg.av_buffer_ref((AVBufferRef*)device);
        }
    }

    public static string ErrorText(int error)
    {
        const int Size = 256;
        var buffer = stackalloc byte[Size];
        ffmpeg.av_strerror(error, buffer, Size);
        return Marshal.PtrToStringUTF8((IntPtr)buffer) ?? error.ToString(CultureInfo.InvariantCulture);
    }

    [DllImport("libc", EntryPoint = "setenv", CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern int setenv(string name, string value, int overwrite);
}

/// <summary>
/// <c>Oadm.Client --check-gpu-decoding</c>: the GPU decoding test, run by the client in a separate process at start. Opens
/// the first device type that works and decodes the embedded H.264 and H.265 clips with it. Prints "device=", one line per
/// codec ("h264=ok" or "h264=failed: reason") and, when nothing works, "problem=". Always exits 0 when it got that far; a
/// crash or a hang of the driver shows up in the client as "the driver stopped the test".
/// </summary>
public static class GpuDecodingCheck
{
    public const string Argument = "--check-gpu-decoding";

    public static int Run(TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);
        HwDevice.PrepareEnvironment();
        var runtime = FfmpegRuntime.Status;
        if (!runtime.IsAvailable)
        {
            output.WriteLine("problem=" + LiveVideoViewModel.DecoderUnavailableText);
            return 0;
        }

        var errors = new List<string>();
        foreach (var name in HwDevice.Candidates())
        {
            unsafe
            {
                var device = HwDevice.TryCreate(name, out var error);
                if (device == null)
                {
                    errors.Add(error ?? name);
                    continue;
                }

                ffmpeg.av_buffer_unref(&device);
            }

            var results = new List<(VideoCodec Codec, string? Error)>();
            foreach (var codec in new[] { VideoCodec.H264, VideoCodec.H265 })
            {
                results.Add((codec, TestCodec(name, codec)));
            }

            if (results.Any(r => r.Error is null))
            {
                output.WriteLine("device=" + name);
                foreach (var (codec, error2) in results)
                {
                    output.WriteLine($"{Key(codec)}={(error2 is null ? "ok" : "failed: " + error2)}");
                }

                return 0;
            }

            errors.AddRange(results.Select(r => $"{name} {Key(r.Codec)}: {r.Error}"));
        }

        output.WriteLine("errors=" + string.Join("; ", errors));
        output.WriteLine("problem=" + ProblemFor(errors));
        return 0;
    }

    public static string Key(VideoCodec codec) => codec == VideoCodec.H265 ? "h265" : "h264";

    /// <summary>Decodes the embedded clip on the device; null when every picture came from the graphics card.</summary>
    private static string? TestCodec(string device, VideoCodec codec)
    {
        try
        {
            var units = FakeLiveVideo.Load(codec);
            using var decoder = new FfmpegVideoDecoder(codec, device) { ConvertToBitmap = false, FallBackToProcessor = false };
            foreach (var (keyframe, data) in units)
            {
                decoder.Decode(new LiveViewFrame { Codec = codec, Keyframe = keyframe, Data = Google.Protobuf.ByteString.CopyFrom(data) });
            }

            return decoder.GpuFrames >= units.Count - 1 && decoder.LastSize == (640, 360)
                ? null
                : decoder.GpuProblem ?? string.Create(CultureInfo.InvariantCulture, $"{decoder.GpuFrames} of {units.Count} pictures");
        }
#pragma warning disable CA1031 // Any failure means: not on this device.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return ex.Message;
        }
    }

    private static string ProblemFor(List<string> errors)
    {
        if (OperatingSystem.IsLinux())
        {
            return Directory.Exists("/dev/dri") && Directory.EnumerateFiles("/dev/dri", "renderD*").Any()
                ? GpuDecodingTexts.LinuxDriver
                : GpuDecodingTexts.NoDevice;
        }

        return errors.Count == 0 ? GpuDecodingTexts.NoDevice : GpuDecodingTexts.Generic;
    }
}

/// <summary>
/// Runs the GPU decoding test once per client start (and again when the setting is turned on) in a separate process and
/// keeps the result. Follows the client setting "Decode video on the graphics card". Never throws; a test that crashes,
/// hangs (<see cref="Timeout"/>) or cannot start leaves the processor decoding with a warning.
/// </summary>
/// <param name="command">The test's command line (tests); default <see cref="TestCommand"/>.</param>
public sealed partial class GpuDecodingService(ILogger<GpuDecodingService> logger, Func<(string File, string[] Arguments)?>? command = null)
{
    /// <summary>How long the test may take (the first start of a driver can be slow).</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private readonly Lock _gate = new();
    private Task? _running;
    private bool _enabled = true;

    public GpuDecodingStatus Status { get; private set; } = GpuDecodingStatus.NotChecked;

    /// <summary>Raised after <see cref="Status"/> changed (on a background thread).</summary>
    public event EventHandler? Changed;

    /// <summary>The setting; turning it on starts the test when it has not run.</summary>
    public void SetEnabled(bool enabled)
    {
        lock (_gate)
        {
            _enabled = enabled;
            if (!enabled)
            {
                Status = GpuDecodingStatus.Off; // under the lock: a test that ends now cannot overwrite it
            }
        }

        if (!enabled)
        {
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (Status.State == GpuDecodingState.Off)
        {
            Publish(GpuDecodingStatus.NotChecked);
        }

        _ = StartAsync();
    }

    /// <summary>Starts the test in the background unless it ran or runs; returns when it finished.</summary>
    public Task StartAsync()
    {
        lock (_gate)
        {
            if (!_enabled || Status.State is GpuDecodingState.Available or GpuDecodingState.Unavailable)
            {
                return _running ?? Task.CompletedTask;
            }

            return _running ??= Task.Run(RunAsync);
        }
    }

    /// <summary>The command that runs the test: the client itself (single-file exe or "dotnet Oadm.Client.dll"); null in other hosts (tests).</summary>
    public static (string File, string[] Arguments)? TestCommand()
    {
        var process = Environment.ProcessPath;
        if (string.IsNullOrEmpty(process))
        {
            return null;
        }

        var name = Path.GetFileNameWithoutExtension(process);
        if (name.Equals("Oadm.Client", StringComparison.OrdinalIgnoreCase))
        {
            return (process, [GpuDecodingCheck.Argument]);
        }

        // "dotnet Oadm.Client.dll" only when the client is the program: on Linux and macOS a test host also runs as
        // "dotnet", with Oadm.Client.dll next to it.
        var dll = Path.Combine(AppContext.BaseDirectory, "Oadm.Client.dll");
        var isClient = string.Equals(System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name, "Oadm.Client", StringComparison.OrdinalIgnoreCase);
        return isClient && name.Equals("dotnet", StringComparison.OrdinalIgnoreCase) && File.Exists(dll) ? (process, [dll, GpuDecodingCheck.Argument]) : null;
    }

    /// <summary>Reads the test's output (also tests).</summary>
    public static GpuDecodingStatus Parse(string output, int exitCode)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in (output ?? "").Split('\n'))
        {
            var line = raw.Trim();
            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0)
            {
                values[line[..eq]] = line[(eq + 1)..];
            }
        }

        if (values.TryGetValue("device", out var device))
        {
            var codecs = new HashSet<VideoCodec>();
            foreach (var codec in new[] { VideoCodec.H264, VideoCodec.H265 })
            {
                if (values.TryGetValue(GpuDecodingCheck.Key(codec), out var result) && result == "ok")
                {
                    codecs.Add(codec);
                }
            }

            if (codecs.Count > 0)
            {
                return new GpuDecodingStatus(GpuDecodingState.Available, device, codecs, null);
            }
        }

        var problem = values.GetValueOrDefault("problem");
        return new GpuDecodingStatus(GpuDecodingState.Unavailable, null, new HashSet<VideoCodec>(),
            string.IsNullOrEmpty(problem) ? (exitCode == 0 ? GpuDecodingTexts.Generic : GpuDecodingTexts.TestStopped) : problem);
    }

    private async Task RunAsync()
    {
        var test = (command ?? TestCommand)();
        if (test is null)
        {
            lock (_gate)
            {
                _running = null;
            }

            return; // not the client (tests): no test, the processor decodes without a warning
        }

        Publish(Status with { State = GpuDecodingState.Checking });
        GpuDecodingStatus result;
        try
        {
            result = await RunTestAsync(test.Value.File, test.Value.Arguments).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // The test must never take the client down.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogTestFailed(logger, ex.Message);
            result = new GpuDecodingStatus(GpuDecodingState.Unavailable, null, new HashSet<VideoCodec>(), GpuDecodingTexts.TestStopped);
        }

        lock (_gate)
        {
            _running = null;
            if (!_enabled)
            {
                return;
            }

            Status = result;
        }

        var codecs = string.Join(", ", result.Codecs);
        LogResult(logger, result.State, result.Device ?? "-", codecs, result.Problem ?? "-");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task<GpuDecodingStatus> RunTestAsync(string file, string[] arguments)
    {
        var start = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("The test did not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(Timeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            LogTestHung(logger);
            return new GpuDecodingStatus(GpuDecodingState.Unavailable, null, new HashSet<VideoCodec>(), GpuDecodingTexts.TestStopped);
        }

        string text;
        try
        {
            // A helper the driver started may keep the output open after the test ended.
            text = await output.WaitAsync(Timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            LogTestHung(logger);
            return new GpuDecodingStatus(GpuDecodingState.Unavailable, null, new HashSet<VideoCodec>(), GpuDecodingTexts.TestStopped);
        }

        if (process.ExitCode != 0 && !text.Contains("device=", StringComparison.Ordinal))
        {
            LogTestCrashed(logger, process.ExitCode);
            return new GpuDecodingStatus(GpuDecodingState.Unavailable, null, new HashSet<VideoCodec>(), GpuDecodingTexts.TestStopped);
        }

        if (logger.IsEnabled(LogLevel.Information) && text.Split('\n').FirstOrDefault(l => l.StartsWith("errors=", StringComparison.Ordinal)) is { } errors)
        {
            var list = errors[7..].Trim();
            LogTestErrors(logger, list);
        }

        return Parse(text, process.ExitCode);
    }

    private void Publish(GpuDecodingStatus status)
    {
        Status = status;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "GPU decoding: {State}, device {Device}, codecs {Codecs}, problem {Problem}")]
    private static partial void LogResult(ILogger logger, GpuDecodingState state, string device, string codecs, string problem);

    [LoggerMessage(Level = LogLevel.Warning, Message = "GPU decoding test could not run: {Reason}")]
    private static partial void LogTestFailed(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "GPU decoding test did not finish in time and was stopped")]
    private static partial void LogTestHung(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "GPU decoding test ended with exit code {ExitCode} (driver crash)")]
    private static partial void LogTestCrashed(ILogger logger, int exitCode);

    [LoggerMessage(Level = LogLevel.Information, Message = "GPU decoding test: {Errors}")]
    private static partial void LogTestErrors(ILogger logger, string errors);
}
