using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

using FFmpeg.AutoGen;

using Oadm.Contracts.V1;

namespace Oadm.Client.LiveView;

/// <summary>
/// FFmpeg decoders for H.264 and H.265 (see <see cref="FfmpegRuntime"/>): on the graphics card when the GPU decoding test
/// passed for the codec and the setting is on (<see cref="GpuDecodingService"/>), else on the processor.
/// </summary>
public sealed class FfmpegVideoDecoderFactory(GpuDecodingService? gpu = null) : IVideoDecoderFactory
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

    public IVideoDecoder Create(VideoCodec codec)
    {
        var status = gpu?.Status ?? GpuDecodingStatus.NotChecked;
        return status.Decodes(codec)
            ? new FfmpegVideoDecoder(codec, status.Device)
            : new FfmpegVideoDecoder(codec) { GpuProblem = status.WarningFor(codec) };
    }
}

/// <summary>
/// libavcodec decoder for one stream (low delay, slice threading so a frame comes out as soon as its access unit went in),
/// converted to BGRA with swscale into a new <see cref="WriteableBitmap"/>. With a hardware device ("d3d11va", "vaapi",
/// "videotoolbox", ...) the pictures are decoded on the graphics card (the shared <see cref="HwDevice"/>) and copied back for
/// the conversion. When the graphics card fails on this stream (no hardware format, a copy that fails, no picture after two
/// keyframes) the decoder reopens on the processor and says why in <see cref="GpuProblem"/>; it never throws on bad data.
/// </summary>
public sealed unsafe class FfmpegVideoDecoder : IVideoDecoder
{
    /// <summary>Keyframes without a picture from the graphics card before the decoder switches to the processor.</summary>
    private const int KeyframesBeforeFallback = 2;

    /// <summary>AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX (libavcodec/codec.h): the decoder takes a hardware device context.</summary>
    private const int HwDeviceContextMethod = 0x01;

    private readonly AVCodecContext_get_format _getFormat;
    private AVCodecContext* _context;
    private AVPacket* _packet;
    private AVFrame* _frame;
    private AVFrame* _swFrame;
    private SwsContext* _sws;
    private AVFrame* _bgra;
    private AVPixelFormat _hwFormat = AVPixelFormat.AV_PIX_FMT_NONE;
    private string? _hwDevice;
    private bool _hwRefused;
    private int _hwKeyframes;
    private bool _disposed;

    /// <param name="hwDevice">FFmpeg device type for decoding on the graphics card, or null for the processor.</param>
    public FfmpegVideoDecoder(VideoCodec codec, string? hwDevice = null)
    {
        if (!FfmpegRuntime.Status.IsAvailable)
        {
            throw new InvalidOperationException("Video decoder not available: " + FfmpegRuntime.Status.Error);
        }

        Codec = codec;
        _getFormat = GetFormat;
        _packet = ffmpeg.av_packet_alloc();
        _frame = ffmpeg.av_frame_alloc();
        _swFrame = ffmpeg.av_frame_alloc();
        if (hwDevice is not null && !Open(hwDevice))
        {
            Close();
            _fallbackProblem ??= $"The graphics card cannot decode this stream; the processor decodes it. {GpuDecodingTexts.TurnOff}";
        }

        if (_context == null && !Open(null))
        {
            Dispose();
            throw new InvalidOperationException($"No {codec} decoder could be opened.");
        }
    }

    public VideoCodec Codec { get; }

    /// <summary>Decode errors so far (diagnostics).</summary>
    public int Errors { get; private set; }

    /// <summary>Pictures the decoder produced so far, and the size of the last one (diagnostics, --check-decoder).</summary>
    public int DecodedFrames { get; private set; }

    /// <summary>Pictures that came from the graphics card.</summary>
    public int GpuFrames { get; private set; }

    public (int Width, int Height) LastSize { get; private set; }

    /// <summary>True while the last picture came from the graphics card.</summary>
    public bool UsesGpu { get; private set; }

    /// <summary>Why this stream is not decoded on the graphics card although it should be (the live view's warning); null otherwise.</summary>
    public string? GpuProblem
    {
        get => _fallbackProblem ?? _initialProblem;
        init => _initialProblem = value;
    }

    private readonly string? _initialProblem;
    private string? _fallbackProblem;

    /// <summary>False decodes without creating bitmaps (no Avalonia platform needed; used by --check-decoder).</summary>
    public bool ConvertToBitmap { get; init; } = true;

    /// <summary>False keeps a failing graphics card instead of switching to the processor (the GPU decoding test).</summary>
    public bool FallBackToProcessor { get; init; } = true;

    public LiveImage? Decode(LiveViewFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var data = frame.Data.Span;
        if (data.IsEmpty || _context == null)
        {
            return null;
        }

        var picturesBefore = GpuFrames;
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
                return CheckGpu(frame, picturesBefore, "the graphics card refused the stream") ? Decode(frame) : null;
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

            var picture = _frame;
            var fromGpu = _hwFormat != AVPixelFormat.AV_PIX_FMT_NONE && (AVPixelFormat)_frame->format == _hwFormat;
            if (fromGpu)
            {
                ffmpeg.av_frame_unref(_swFrame);
                var copied = ffmpeg.av_hwframe_transfer_data(_swFrame, _frame, 0);
                if (copied < 0)
                {
                    Errors++;
                    ffmpeg.av_frame_unref(_frame);
                    latest?.Dispose();
                    return SwitchToProcessor("copying the picture from the graphics card failed: " + HwDevice.ErrorText(copied)) ? Decode(frame) : null;
                }

                picture = _swFrame;
                GpuFrames++;
            }

            DecodedFrames++;
            UsesGpu = fromGpu;
            LastSize = (picture->width, picture->height);
            if (ConvertToBitmap)
            {
                latest?.Dispose();
                latest = Convert(picture);
            }

            ffmpeg.av_frame_unref(_frame);
            ffmpeg.av_frame_unref(_swFrame);
        }

        if (latest is null && CheckGpu(frame, picturesBefore, _hwRefused ? "the graphics card does not decode this stream" : "no picture from the graphics card"))
        {
            return Decode(frame);
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
        Close();
        var frame = _frame;
        ffmpeg.av_frame_free(&frame);
        _frame = null;
        var swFrame = _swFrame;
        ffmpeg.av_frame_free(&swFrame);
        _swFrame = null;
        var packet = _packet;
        ffmpeg.av_packet_free(&packet);
        _packet = null;
        var bgra = _bgra;
        ffmpeg.av_frame_free(&bgra);
        _bgra = null;
    }

    /// <summary>Opens the codec, on <paramref name="hwDevice"/> or on the processor (null); false when it cannot.</summary>
    private bool Open(string? hwDevice)
    {
        var id = Codec == VideoCodec.H265 ? AVCodecID.AV_CODEC_ID_HEVC : AVCodecID.AV_CODEC_ID_H264;
        var decoder = ffmpeg.avcodec_find_decoder(id);
        if (decoder == null)
        {
            return false;
        }

        _hwFormat = AVPixelFormat.AV_PIX_FMT_NONE;
        _hwRefused = false;
        _hwKeyframes = 0;
        _hwDevice = null;
        _context = ffmpeg.avcodec_alloc_context3(decoder);
        _context->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;
        _context->thread_type = ffmpeg.FF_THREAD_SLICE;
        _context->thread_count = Math.Clamp(Environment.ProcessorCount, 1, 4);
        if (hwDevice is not null)
        {
            var device = HwDevice.Acquire(hwDevice, out var type);
            if (device == null)
            {
                _fallbackProblem ??= GpuProblem ?? GpuDecodingTexts.Generic;
                return false;
            }

            for (var i = 0; ; i++)
            {
                var config = ffmpeg.avcodec_get_hw_config(decoder, i);
                if (config == null)
                {
                    break;
                }

                if ((config->methods & HwDeviceContextMethod) != 0 && config->device_type == type)
                {
                    _hwFormat = config->pix_fmt;
                    break;
                }
            }

            if (_hwFormat == AVPixelFormat.AV_PIX_FMT_NONE)
            {
                ffmpeg.av_buffer_unref(&device);
                return false;
            }

            _context->hw_device_ctx = device; // the context owns this reference
            _context->get_format = _getFormat;
            _hwDevice = hwDevice;
        }

        if (ffmpeg.avcodec_open2(_context, decoder, null) < 0)
        {
            Close();
            return false;
        }

        return true;
    }

    private void Close()
    {
        if (_sws != null)
        {
            ffmpeg.sws_freeContext(_sws);
            _sws = null;
        }

        if (_context != null)
        {
            var context = _context;
            ffmpeg.avcodec_free_context(&context);
            _context = null;
        }
    }

    /// <summary>
    /// On the graphics card: counts keyframes without a picture from it; after <see cref="KeyframesBeforeFallback"/> (or when it
    /// refused the format) switches to the processor. True when the frame should be decoded again on the processor.
    /// </summary>
    private bool CheckGpu(LiveViewFrame frame, int picturesBefore, string reason)
    {
        if (_hwDevice is null || GpuFrames > picturesBefore || GpuFrames > 0)
        {
            return false;
        }

        if (frame.Keyframe)
        {
            _hwKeyframes++;
        }

        return (_hwRefused || _hwKeyframes >= KeyframesBeforeFallback) && SwitchToProcessor(reason);
    }

    private bool SwitchToProcessor(string reason)
    {
        if (!FallBackToProcessor || _hwDevice is null)
        {
            _fallbackProblem ??= reason;
            return false;
        }

        _fallbackProblem = $"The graphics card failed on this stream ({reason}); the processor decodes it. {GpuDecodingTexts.TurnOff}";
        Close();
        UsesGpu = false;
        return Open(null);
    }

    /// <summary>Chooses the graphics card's format when the decoder offers it, else the software format (the last one).</summary>
    private AVPixelFormat GetFormat(AVCodecContext* context, AVPixelFormat* formats)
    {
        var last = AVPixelFormat.AV_PIX_FMT_NONE;
        for (var p = formats; *p != AVPixelFormat.AV_PIX_FMT_NONE; p++)
        {
            if (*p == _hwFormat)
            {
                return *p;
            }

            last = *p;
        }

        // The card does not take this stream (size, profile): FFmpeg decodes it on the processor, which the warning says.
        _hwRefused = true;
        UsesGpu = false;
        _fallbackProblem ??= $"The graphics card cannot decode this stream; the processor decodes it. {GpuDecodingTexts.TurnOff}";
        return last;
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

        if (_bgra == null)
        {
            _bgra = ffmpeg.av_frame_alloc();
        }

        var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using (var buffer = bitmap.Lock())
        {
            if (!BgraConversion.Convert(_sws, frame, _bgra, (byte*)buffer.Address, buffer.RowBytes))
            {
                Errors++;
            }
        }

        return new LiveImage(bitmap, width, height);
    }
}

/// <summary>
/// Converts a decoded picture to BGRA for a bitmap. swscale's fast routines write whole blocks of pixels: for a width that
/// is not a multiple of 16 they wrote 8 to 32 bytes past the end of a buffer of exactly width x height x 4 bytes (a heap
/// overflow). So swscale writes into FFmpeg's own picture (rows padded to 64 bytes), and only width x 4 bytes of each row
/// are copied into the bitmap.
/// </summary>
internal static unsafe class BgraConversion
{
    /// <param name="scratch">A frame of the caller, reused while the size stays (freed by the caller).</param>
    /// <returns>False when the scratch picture could not be allocated (nothing written).</returns>
    public static bool Convert(SwsContext* sws, AVFrame* source, AVFrame* scratch, byte* destination, int destinationStride)
    {
        var width = source->width;
        var height = source->height;
        if (scratch->width != width || scratch->height != height || scratch->format != (int)AVPixelFormat.AV_PIX_FMT_BGRA || scratch->data[0] == null)
        {
            ffmpeg.av_frame_unref(scratch);
            scratch->width = width;
            scratch->height = height;
            scratch->format = (int)AVPixelFormat.AV_PIX_FMT_BGRA;
            if (ffmpeg.av_frame_get_buffer(scratch, 64) < 0)
            {
                ffmpeg.av_frame_unref(scratch);
                return false;
            }
        }

        var dst = new byte*[4] { scratch->data[0], null, null, null };
        var dstStride = new int[4] { scratch->linesize[0], 0, 0, 0 };
        ffmpeg.sws_scale(sws, source->data.ToArray(), source->linesize.ToArray(), 0, height, dst, dstStride);
        var rowBytes = width * 4;
        for (var y = 0; y < height; y++)
        {
            Buffer.MemoryCopy(scratch->data[0] + ((long)y * scratch->linesize[0]), destination + ((long)y * destinationStride), rowBytes, rowBytes);
        }

        return true;
    }
}
