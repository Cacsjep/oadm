using Microsoft.Extensions.Logging;

using Oadm.Plugins.Ocr.Model;
using Oadm.Plugins.Shared.Vision;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Ocr.Frames;

/// <summary>
/// The pictures of a camera through its VAPIX client, so the device's route (sites) is used (docs/spec/ocr.md "Readers"):
/// Text recognition's <see cref="IFrameSource"/> over the frame source both vision add-ons share (<see cref="CameraFrames"/>
/// in plugins/Shared/Vision). While watching, the newest picture wins; a stream that drops, stalls or cannot be opened is
/// opened again after a pause that starts at <see cref="FirstReopenPause"/> and doubles up to
/// <see cref="OcrLimits.MaxReopenPause"/>. All time goes through the <see cref="TimeProvider"/>. Read only.
/// </summary>
public sealed class VapixFrameSource : IFrameSource
{
    /// <summary>The pause before the first reopen.</summary>
    public static readonly TimeSpan FirstReopenPause = CameraFrames.FirstReopenPause;

    /// <summary>A stream that sent pictures and then sends none for this long is dropped and opened again (decision
    /// 2026-11-03: 3 s, three missed pictures at the lowest rate of 1 per second).</summary>
    public static readonly TimeSpan StallTimeout = CameraFrames.StallTimeout;

    /// <summary>The camera must answer a request (a single picture, or the stream's headers) within this time.</summary>
    public static readonly TimeSpan RequestTimeout = CameraFrames.RequestTimeout;

    private readonly CameraFrames _frames;

    /// <summary>A source that asks <paramref name="vapix"/> (the plugin context's factory) for each camera's client.</summary>
    public VapixFrameSource(IVapixClientFactory vapix, TimeProvider time, ILogger? logger = null) =>
        _frames = new CameraFrames(vapix, time, logger);

    /// <summary>Tests: a reopen pause started (after the timer is set).</summary>
    internal Action<TimeSpan>? PauseStarted
    {
        get => _frames.PauseStarted;
        set => _frames.PauseStarted = value;
    }

    /// <summary>Tests: a picture was handed on (after the stall timer was set again).</summary>
    internal Action? FrameReceived
    {
        get => _frames.FrameReceived;
        set => _frames.FrameReceived = value;
    }

    /// <summary>The single picture request: <c>axis-cgi/jpg/image.cgi?resolution=WxH&amp;camera=N</c>.</summary>
    public static Uri SingleUri(FrameRequest request) => CameraFrames.SingleUri(Of(request));

    /// <summary>The stream request: <c>axis-cgi/mjpg/video.cgi?fps=N&amp;resolution=WxH&amp;camera=N</c>.</summary>
    public static Uri StreamUri(FrameRequest request, int fps) => CameraFrames.StreamUri(Of(request), fps);

    /// <summary>The pause before reopen number <paramref name="failures"/> (1 for the first): 1, 2, 4, ... seconds, at most 1 min.</summary>
    public static TimeSpan ReopenPause(int failures) => CameraFrames.ReopenPause(failures);

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">The camera refused or sent no picture (the reason in plain words).</exception>
    public Task<ReadOnlyMemory<byte>> SingleAsync(FrameRequest request, CancellationToken ct) => _frames.SingleAsync(Of(request), ct);

    /// <inheritdoc />
    /// <remarks>The sequence ends quietly when <paramref name="ct"/> is cancelled.</remarks>
    public IAsyncEnumerable<ReadOnlyMemory<byte>> WatchAsync(FrameRequest request, int fps, CancellationToken ct) =>
        WatchAsync(request, fps, static _ => { }, ct);

    /// <inheritdoc />
    /// <remarks>
    /// <paramref name="problem"/> hears why the stream gives no pictures (the camera's refusal, a resolution or video source it
    /// does not offer, no answer) and null once pictures flow again; only on a change.
    /// </remarks>
    public IAsyncEnumerable<ReadOnlyMemory<byte>> WatchAsync(FrameRequest request, int fps, Action<string?> problem, CancellationToken ct) =>
        _frames.WatchAsync(Of(request), fps, problem, ct);

    private static CameraFrameRequest Of(FrameRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new CameraFrameRequest(request.DeviceId, request.VideoSource, request.Resolution.Width, request.Resolution.Height);
    }
}

/// <summary>The texts of the picture source (the shared <see cref="CameraFrameTexts"/>).</summary>
public static class FrameTexts
{
    /// <summary>The camera answered without a picture.</summary>
    public const string NoPicture = CameraFrameTexts.NoPicture;

    /// <summary>The camera did not open the picture stream in time.</summary>
    public const string NoPictures = CameraFrameTexts.NoPictures;

    /// <summary>The camera refused the reader's resolution.</summary>
    public const string ResolutionNotOffered = CameraFrameTexts.ResolutionNotOffered;

    /// <summary>The camera refused the reader's video source.</summary>
    public const string SourceNotOffered = CameraFrameTexts.SourceNotOffered;

    /// <summary>
    /// What a camera's error text (an Axis CGI answers "Error: ..." in plain text) refused: the resolution or the video source;
    /// null for any other text, such as the web server's own page of a 403 or a 404, whatever words it holds.
    /// </summary>
    public static string? RefusalOf(ReadOnlySpan<byte> answer) => CameraFrameTexts.RefusalOf(answer);

    /// <summary>The problem a failed stream shows on the page: the camera's refusal in plain words.</summary>
    public static string ProblemOf(Exception error) => CameraFrameTexts.ProblemOf(error);
}
