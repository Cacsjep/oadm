using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Plugins.Ocr.Model;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Ocr.Frames;

/// <summary>
/// The pictures of a camera through its VAPIX client, so the device's route (sites) is used (docs/spec/ocr.md "Readers"):
/// a single picture from <c>/axis-cgi/jpg/image.cgi</c> and the MJPEG stream of <c>/axis-cgi/mjpg/video.cgi</c> as a
/// streamed answer. While watching, the newest picture wins: a consumer that is busy gets the latest one next, older ones
/// are dropped, never queued. A stream that drops, stalls or cannot be opened is opened again after a pause that starts at
/// <see cref="FirstReopenPause"/> and doubles up to <see cref="OcrLimits.MaxReopenPause"/>; a stream that delivered a
/// picture starts the pauses anew. All time goes through the <see cref="TimeProvider"/>. Read only.
/// </summary>
public sealed partial class VapixFrameSource : IFrameSource
{
    /// <summary>The pause before the first reopen.</summary>
    public static readonly TimeSpan FirstReopenPause = TimeSpan.FromSeconds(1);

    /// <summary>A stream that sent pictures and then sends none for this long is dropped and opened again (decision
    /// 2026-11-03: 3 s, three missed pictures at the lowest rate of 1 per second).</summary>
    public static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(3);

    /// <summary>The camera must answer a request (a single picture, or the stream's headers) within this time.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    private readonly IVapixClientFactory _vapix;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    /// <summary>A source that asks <paramref name="vapix"/> (the plugin context's factory) for each camera's client.</summary>
    public VapixFrameSource(IVapixClientFactory vapix, TimeProvider time, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(vapix);
        ArgumentNullException.ThrowIfNull(time);
        _vapix = vapix;
        _time = time;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Tests: a reopen pause started (after the timer is set).</summary>
    internal Action<TimeSpan>? PauseStarted { get; set; }

    /// <summary>Tests: a picture was handed on (after the stall timer was set again).</summary>
    internal Action? FrameReceived { get; set; }

    /// <summary>The single picture request: <c>axis-cgi/jpg/image.cgi?resolution=WxH&amp;camera=N</c>.</summary>
    public static Uri SingleUri(FrameRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new Uri(string.Create(CultureInfo.InvariantCulture,
            $"/axis-cgi/jpg/image.cgi?resolution={request.Resolution.Width}x{request.Resolution.Height}&camera={request.VideoSource}"), UriKind.Relative);
    }

    /// <summary>The stream request: <c>axis-cgi/mjpg/video.cgi?fps=N&amp;resolution=WxH&amp;camera=N</c>.</summary>
    public static Uri StreamUri(FrameRequest request, int fps)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new Uri(string.Create(CultureInfo.InvariantCulture,
            $"/axis-cgi/mjpg/video.cgi?fps={fps}&resolution={request.Resolution.Width}x{request.Resolution.Height}&camera={request.VideoSource}"), UriKind.Relative);
    }

    /// <summary>The pause before reopen number <paramref name="failures"/> (1 for the first): 1, 2, 4, ... seconds, at most 1 min.</summary>
    public static TimeSpan ReopenPause(int failures)
    {
        var seconds = FirstReopenPause.TotalSeconds * Math.Pow(2, Math.Clamp(failures - 1, 0, 30));
        return TimeSpan.FromSeconds(Math.Min(seconds, OcrLimits.MaxReopenPause.TotalSeconds));
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">The camera refused or sent no picture (the reason in plain words).</exception>
    public async Task<ReadOnlyMemory<byte>> SingleAsync(FrameRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var client = await _vapix.CreateAsync(request.DeviceId, ct).ConfigureAwait(false);
        using var message = new HttpRequestMessage(HttpMethod.Get, SingleUri(request));
        message.Options.Set(VapixRequestOptions.Timeout, RequestTimeout);
        using var response = await client.SendAsync(message, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        var jpeg = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        if (jpeg.Length < 3 || jpeg[0] != 0xFF || jpeg[1] != 0xD8 || jpeg.Length > MjpegParser.MaxFrameBytes)
        {
            throw new InvalidOperationException(FrameTexts.RefusalOf(jpeg.AsSpan(0, Math.Min(jpeg.Length, MaxErrorBytes))) ?? FrameTexts.NoPicture);
        }

        return jpeg;
    }

    /// <inheritdoc />
    /// <remarks>The sequence ends quietly when <paramref name="ct"/> is cancelled.</remarks>
    public IAsyncEnumerable<ReadOnlyMemory<byte>> WatchAsync(FrameRequest request, int fps, CancellationToken ct) =>
        WatchAsync(request, fps, static _ => { }, ct);

    /// <inheritdoc />
    /// <remarks>
    /// <paramref name="problem"/> hears why the stream gives no pictures (the camera's refusal, a resolution or video source it
    /// does not offer, no answer) and null once pictures flow again; only on a change.
    /// </remarks>
    public async IAsyncEnumerable<ReadOnlyMemory<byte>> WatchAsync(FrameRequest request, int fps, Action<string?> problem, [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(problem);
        ArgumentOutOfRangeException.ThrowIfLessThan(fps, 1);
        var newest = Channel.CreateBounded<ReadOnlyMemory<byte>>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pump = PumpAsync(request, fps, newest.Writer, new ProblemReport(problem), stop.Token);
        try
        {
            while (await WaitAsync(newest.Reader, ct).ConfigureAwait(false))
            {
                while (newest.Reader.TryRead(out var frame))
                {
                    yield return frame;
                }
            }
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            await pump.ConfigureAwait(false);
        }
    }

    private static async Task<bool> WaitAsync(ChannelReader<ReadOnlyMemory<byte>> reader, CancellationToken ct)
    {
        try
        {
            return await reader.WaitToReadAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>Opens the stream again and again until <paramref name="ct"/> ends; never throws.</summary>
    private async Task PumpAsync(FrameRequest request, int fps, ChannelWriter<ReadOnlyMemory<byte>> newest, ProblemReport problem, CancellationToken ct)
    {
        await Task.Yield();
        var failures = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var delivered = false;
                try
                {
                    delivered = await StreamOnceAsync(request, fps, newest, problem, ct).ConfigureAwait(false);
                    LogStreamEnded(_logger, request.DeviceId);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
#pragma warning disable CA1031 // Every failure of one stream is logged and told; the stream is opened again after a pause.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    LogStreamFailed(_logger, request.DeviceId, ex.Message);
                    problem.Set(FrameTexts.ProblemOf(ex));
                }

                failures = delivered ? 1 : failures + 1;
                var pause = ReopenPause(failures);
                var wait = Task.Delay(pause, _time, ct);
                PauseStarted?.Invoke(pause);
                await wait.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Stopped.
        }
        finally
        {
            newest.TryComplete();
        }
    }

    /// <summary>One stream until it ends, stalls or fails; true when it delivered at least one picture.</summary>
    private async Task<bool> StreamOnceAsync(FrameRequest request, int fps, ChannelWriter<ReadOnlyMemory<byte>> newest, ProblemReport problem, CancellationToken ct)
    {
        var client = await _vapix.CreateAsync(request.DeviceId, ct).ConfigureAwait(false);
        using var message = new HttpRequestMessage(HttpMethod.Get, StreamUri(request, fps));
        message.Options.Set(VapixRequestOptions.Timeout, RequestTimeout);
        message.Options.Set(VapixRequestOptions.StreamResponse, true);

        // Through a site the stream takes none of the site's large transfer slots (AXIS OS upgrades and applications need
        // them); the readers limit their streams per site themselves (ReaderRules.MaxWatchingPerSite).
        message.Options.Set(VapixRequestOptions.LiveStream, true);

        // Opening (login, a site's relay, the camera starting the stream) may take up to RequestTimeout; once pictures
        // flow, a gap longer than StallTimeout reopens the stream.
        using var stall = new CancellationTokenSource(RequestTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, stall.Token);
        var delivered = false;
        try
        {
            using var response = await client.SendAsync(message, linked.Token).ConfigureAwait(false);
            await EnsureSuccessAsync(response, linked.Token).ConfigureAwait(false);
            if (MjpegParser.BoundaryOf(response.Content.Headers.ContentType) is not { } boundary)
            {
                throw new InvalidOperationException(await RefusalOfAsync(response, linked.Token).ConfigureAwait(false) ?? FrameTexts.NoPicture);
            }

            await using var body = await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
            await foreach (var frame in MjpegParser.ReadFramesAsync(body, boundary, MjpegParser.MaxFrameBytes, linked.Token).ConfigureAwait(false))
            {
                stall.CancelAfter(StallTimeout);
                delivered = true;
                problem.Set(null);
                newest.TryWrite(frame);
                FrameReceived?.Invoke();
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && stall.IsCancellationRequested)
        {
            if (delivered)
            {
                LogStreamStalled(_logger, request.DeviceId, (int)StallTimeout.TotalSeconds);
            }
            else
            {
                // The camera did not open the stream (or sent no first picture) within the request's time.
                LogStreamNotOpened(_logger, request.DeviceId, (int)RequestTimeout.TotalSeconds);
                problem.Set(FrameTexts.NoPictures);
            }
        }

        return delivered;
    }

    /// <summary>Bytes of a camera's error answer read at most (its text names what it refused).</summary>
    private const int MaxErrorBytes = 1024;

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            throw new InvalidOperationException(await RefusalOfAsync(response, ct).ConfigureAwait(false)
                ?? DeviceMessages.ForHttpStatus(status) ?? DeviceMessages.ServerError(status));
        }
    }

    /// <summary>What the camera's error text refused (<see cref="FrameTexts.RefusalOf"/>), from the first bytes of its answer.</summary>
    private static async Task<string?> RefusalOfAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var buffer = new byte[MaxErrorBytes];
            var read = 0;
            while (read < buffer.Length && await body.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false) is var n and > 0)
            {
                read += n;
            }

            return FrameTexts.RefusalOf(buffer.AsSpan(0, read));
        }
        catch (IOException)
        {
            return null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "The picture stream of {DeviceId} ended; opening it again.")]
    private static partial void LogStreamEnded(ILogger logger, Guid deviceId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The picture stream of {DeviceId} failed: {Reason}")]
    private static partial void LogStreamFailed(ILogger logger, Guid deviceId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The picture stream of {DeviceId} sent nothing for {Seconds} s.")]
    private static partial void LogStreamStalled(ILogger logger, Guid deviceId, int seconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The picture stream of {DeviceId} did not open within {Seconds} s.")]
    private static partial void LogStreamNotOpened(ILogger logger, Guid deviceId, int seconds);

    /// <summary>Tells a stream's problem only when it changed.</summary>
    private sealed class ProblemReport(Action<string?> report)
    {
        private string? _last;

        public void Set(string? problem)
        {
            if (!string.Equals(_last, problem, StringComparison.Ordinal))
            {
                _last = problem;
                report(problem);
            }
        }
    }
}

/// <summary>The texts of the picture source.</summary>
public static class FrameTexts
{
    /// <summary>The camera answered without a picture.</summary>
    public const string NoPicture = "The camera sent no picture.";

    /// <summary>The camera did not open the picture stream in time.</summary>
    public const string NoPictures = "The camera sends no pictures.";

    /// <summary>The camera refused the reader's resolution.</summary>
    public const string ResolutionNotOffered = "The camera does not offer this resolution. Choose another one.";

    /// <summary>The camera refused the reader's video source.</summary>
    public const string SourceNotOffered = "The camera does not offer this video source. Choose another one.";

    /// <summary>
    /// What a camera's error text (an Axis CGI answers "Error: ..." in plain text) refused: the resolution or the video source;
    /// null for any other text.
    /// </summary>
    public static string? RefusalOf(ReadOnlySpan<byte> answer)
    {
        if (answer.Length == 0 || (answer.Length > 1 && answer[0] == 0xFF && answer[1] == 0xD8))
        {
            return null;
        }

        var text = System.Text.Encoding.ASCII.GetString(answer);
        if (text.Contains("resolution", StringComparison.OrdinalIgnoreCase))
        {
            return ResolutionNotOffered;
        }

        return text.Contains("camera", StringComparison.OrdinalIgnoreCase) || text.Contains("source", StringComparison.OrdinalIgnoreCase)
            ? SourceNotOffered
            : null;
    }

    /// <summary>The problem a failed stream shows on the page: the camera's refusal in plain words.</summary>
    public static string ProblemOf(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error switch
        {
            InvalidOperationException refused => refused.Message,
            TimeoutException or OperationCanceledException => DeviceMessages.TimedOut,
            _ when error.InnerException is TimeoutException => DeviceMessages.TimedOut,
            _ => DeviceMessages.NotAnswering,
        };
    }
}
