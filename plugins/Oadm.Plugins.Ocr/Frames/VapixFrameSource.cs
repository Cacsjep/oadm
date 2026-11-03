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
        EnsureSuccess(response);
        var jpeg = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        if (jpeg.Length < 3 || jpeg[0] != 0xFF || jpeg[1] != 0xD8 || jpeg.Length > MjpegParser.MaxFrameBytes)
        {
            throw new InvalidOperationException(FrameTexts.NoPicture);
        }

        return jpeg;
    }

    /// <inheritdoc />
    /// <remarks>The sequence ends quietly when <paramref name="ct"/> is cancelled.</remarks>
    public async IAsyncEnumerable<ReadOnlyMemory<byte>> WatchAsync(FrameRequest request, int fps, [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfLessThan(fps, 1);
        var newest = Channel.CreateBounded<ReadOnlyMemory<byte>>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pump = PumpAsync(request, fps, newest.Writer, stop.Token);
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
    private async Task PumpAsync(FrameRequest request, int fps, ChannelWriter<ReadOnlyMemory<byte>> newest, CancellationToken ct)
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
                    delivered = await StreamOnceAsync(request, fps, newest, ct).ConfigureAwait(false);
                    LogStreamEnded(_logger, request.DeviceId);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    LogStreamFailed(_logger, request.DeviceId, ex.Message);
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
    private async Task<bool> StreamOnceAsync(FrameRequest request, int fps, ChannelWriter<ReadOnlyMemory<byte>> newest, CancellationToken ct)
    {
        var client = await _vapix.CreateAsync(request.DeviceId, ct).ConfigureAwait(false);
        using var message = new HttpRequestMessage(HttpMethod.Get, StreamUri(request, fps));
        message.Options.Set(VapixRequestOptions.Timeout, RequestTimeout);
        message.Options.Set(VapixRequestOptions.StreamResponse, true);
        // Opening (login, a site's relay, the camera starting the stream) may take up to RequestTimeout; once pictures
        // flow, a gap longer than StallTimeout reopens the stream.
        using var stall = new CancellationTokenSource(RequestTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, stall.Token);
        var delivered = false;
        try
        {
            using var response = await client.SendAsync(message, linked.Token).ConfigureAwait(false);
            EnsureSuccess(response);
            var boundary = MjpegParser.BoundaryOf(response.Content.Headers.ContentType)
                ?? throw new InvalidOperationException(FrameTexts.NoPicture);
            await using var body = await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
            await foreach (var frame in MjpegParser.ReadFramesAsync(body, boundary, MjpegParser.MaxFrameBytes, linked.Token).ConfigureAwait(false))
            {
                stall.CancelAfter(StallTimeout);
                delivered = true;
                newest.TryWrite(frame);
                FrameReceived?.Invoke();
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && stall.IsCancellationRequested)
        {
            LogStreamStalled(_logger, request.DeviceId, (int)StallTimeout.TotalSeconds);
        }

        return delivered;
    }

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            throw new InvalidOperationException(DeviceMessages.ForHttpStatus(status) ?? DeviceMessages.ServerError(status));
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "The picture stream of {DeviceId} ended; opening it again.")]
    private static partial void LogStreamEnded(ILogger logger, Guid deviceId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The picture stream of {DeviceId} failed: {Reason}")]
    private static partial void LogStreamFailed(ILogger logger, Guid deviceId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The picture stream of {DeviceId} sent nothing for {Seconds} s.")]
    private static partial void LogStreamStalled(ILogger logger, Guid deviceId, int seconds);
}

/// <summary>The texts of the picture source.</summary>
public static class FrameTexts
{
    /// <summary>The camera answered without a picture.</summary>
    public const string NoPicture = "The camera sent no picture.";
}
