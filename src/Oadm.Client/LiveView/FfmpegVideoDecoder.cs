using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

using FFmpeg.AutoGen;

using Oadm.Contracts.V1;

namespace Oadm.Client.LiveView;

/// <summary>FFmpeg decoders for H.264 and H.265 (see <see cref="FfmpegRuntime"/>).</summary>
public sealed class FfmpegVideoDecoderFactory : IVideoDecoderFactory
{
    public IReadOnlyList<VideoCodec> SupportedCodecs
    {
        get
        {
            var status = FfmpegRuntime.Status;
            var codecs = new List<VideoCodec>();
            if (status.Decoders.Contains("hevc"))
            {
                codecs.Add(VideoCodec.H265);
            }

            if (status.Decoders.Contains("h264"))
            {
                codecs.Add(VideoCodec.H264);
            }

            return codecs;
        }
    }

    public string? UnavailableReason => FfmpegRuntime.Status.IsAvailable ? null : FfmpegRuntime.Status.Error;

    public IVideoDecoder Create(VideoCodec codec) => new FfmpegVideoDecoder(codec);
}

/// <summary>
/// libavcodec decoder for one stream (low delay, slice threading so a frame comes out as soon as
/// its access unit went in), converted to BGRA with swscale into a new <see cref="WriteableBitmap"/>.
/// </summary>
public sealed unsafe class FfmpegVideoDecoder : IVideoDecoder
{
    private AVCodecContext* _context;
    private AVPacket* _packet;
    private AVFrame* _frame;
    private SwsContext* _sws;
    private bool _disposed;

    public FfmpegVideoDecoder(VideoCodec codec)
    {
        if (!FfmpegRuntime.Status.IsAvailable)
        {
            throw new InvalidOperationException("Video decoder not available: " + FfmpegRuntime.Status.Error);
        }

        Codec = codec;
        var id = codec == VideoCodec.H265 ? AVCodecID.AV_CODEC_ID_HEVC : AVCodecID.AV_CODEC_ID_H264;
        var decoder = ffmpeg.avcodec_find_decoder(id);
        if (decoder == null)
        {
            throw new InvalidOperationException($"No {codec} decoder in this FFmpeg build.");
        }

        _context = ffmpeg.avcodec_alloc_context3(decoder);
        _context->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;
        _context->thread_type = ffmpeg.FF_THREAD_SLICE;
        _context->thread_count = Math.Clamp(Environment.ProcessorCount, 1, 4);
        var result = ffmpeg.avcodec_open2(_context, decoder, null);
        if (result < 0)
        {
            Dispose();
            throw new InvalidOperationException($"avcodec_open2 failed ({result}).");
        }

        _packet = ffmpeg.av_packet_alloc();
        _frame = ffmpeg.av_frame_alloc();
    }

    public VideoCodec Codec { get; }

    /// <summary>Decode errors so far (diagnostics).</summary>
    public int Errors { get; private set; }

    /// <summary>Pictures the decoder produced so far, and the size of the last one (diagnostics, --check-decoder).</summary>
    public int DecodedFrames { get; private set; }

    public (int Width, int Height) LastSize { get; private set; }

    /// <summary>False decodes without creating bitmaps (no Avalonia platform needed; used by --check-decoder).</summary>
    public bool ConvertToBitmap { get; init; } = true;

    public LiveImage? Decode(LiveViewFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var data = frame.Data.Span;
        if (data.IsEmpty)
        {
            return null;
        }

        fixed (byte* p = data)
        {
            _packet->data = p;
            _packet->size = data.Length;
            _packet->pts = frame.RtpTimestamp;
            _packet->flags = frame.Keyframe ? ffmpeg.AV_PKT_FLAG_KEY : 0;
            var sent = ffmpeg.avcodec_send_packet(_context, _packet);
            _packet->data = null;
            _packet->size = 0;
            if (sent < 0 && sent != ffmpeg.AVERROR(ffmpeg.EAGAIN))
            {
                Errors++;
                return null;
            }
        }

        LiveImage? latest = null;
        while (true)
        {
            var received = ffmpeg.avcodec_receive_frame(_context, _frame);
            if (received < 0)
            {
                if (received != ffmpeg.AVERROR(ffmpeg.EAGAIN) && received != ffmpeg.AVERROR_EOF)
                {
                    Errors++;
                }

                break;
            }

            DecodedFrames++;
            LastSize = (_frame->width, _frame->height);
            if (ConvertToBitmap)
            {
                latest?.Dispose();
                latest = Convert(_frame);
            }

            ffmpeg.av_frame_unref(_frame);
        }

        return latest;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_sws != null)
        {
            ffmpeg.sws_freeContext(_sws);
            _sws = null;
        }

        var frame = _frame;
        ffmpeg.av_frame_free(&frame);
        _frame = null;
        var packet = _packet;
        ffmpeg.av_packet_free(&packet);
        _packet = null;
        var context = _context;
        ffmpeg.avcodec_free_context(&context);
        _context = null;
    }

    private LiveImage? Convert(AVFrame* frame)
    {
        var width = frame->width;
        var height = frame->height;
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        _sws = ffmpeg.sws_getCachedContext(
            _sws, width, height, (AVPixelFormat)frame->format,
            width, height, AVPixelFormat.AV_PIX_FMT_BGRA,
            (int)SwsFlags.SWS_BILINEAR, null, null, null);
        if (_sws == null)
        {
            Errors++;
            return null;
        }

        var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using (var buffer = bitmap.Lock())
        {
            var dst = new byte*[4] { (byte*)buffer.Address, null, null, null };
            var dstStride = new int[4] { buffer.RowBytes, 0, 0, 0 };
            ffmpeg.sws_scale(_sws, frame->data.ToArray(), frame->linesize.ToArray(), 0, height, dst, dstStride);
        }

        return new LiveImage(bitmap, width, height);
    }
}
