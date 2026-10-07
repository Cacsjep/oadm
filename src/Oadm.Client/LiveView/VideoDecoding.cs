using Avalonia.Media;

using Oadm.Contracts.V1;

namespace Oadm.Client.LiveView;

/// <summary>A decoded picture ready to show. Disposing it releases the bitmap.</summary>
public sealed class LiveImage(IImage image, int width, int height) : IDisposable
{
    public IImage Image { get; } = image;

    public int Width { get; } = width;

    public int Height { get; } = height;

    public void Dispose() => (Image as IDisposable)?.Dispose();
}

/// <summary>Decodes one video stream. Used from one background thread at a time.</summary>
public interface IVideoDecoder : IDisposable
{
    /// <summary>
    /// Feeds one access unit and returns the newest picture it produced, or null when it produced
    /// none (decoder delay, or damaged data the decoder skipped). Never throws on bad data.
    /// </summary>
    LiveImage? Decode(LiveViewFrame frame);
}

/// <summary>Creates decoders and reports which codecs the client can decode.</summary>
public interface IVideoDecoderFactory
{
    /// <summary>Decodable codecs; empty when the decoder library is not available.</summary>
    IReadOnlyList<VideoCodec> SupportedCodecs { get; }

    /// <summary>Why decoding is unavailable (null when available).</summary>
    string? UnavailableReason { get; }

    IVideoDecoder Create(VideoCodec codec);
}
