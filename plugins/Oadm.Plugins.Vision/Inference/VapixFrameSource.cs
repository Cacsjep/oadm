using Microsoft.Extensions.Logging;

using Oadm.Plugins.Vision.Model;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Vision.Inference;

/// <summary>
/// The cameras' pictures for Vision Center (docs/spec/vision-center.md "Pipelines"): Vision's <see cref="IFrameSource"/> over
/// the frame source both vision add-ons share (<see cref="CameraFrames"/>: VAPIX through the device's route, the MJPEG
/// stream reopened after a drop, the newest picture wins). Read only.
/// </summary>
public sealed class VapixFrameSource(IVapixClientFactory vapix, TimeProvider time, ILogger? logger = null) : IFrameSource
{
    private readonly CameraFrames _frames = new(vapix, time, logger);

    /// <inheritdoc />
    public Task<ReadOnlyMemory<byte>> SingleAsync(FrameRequest request, CancellationToken ct) => _frames.SingleAsync(Of(request), ct);

    /// <inheritdoc />
    public IAsyncEnumerable<ReadOnlyMemory<byte>> WatchAsync(FrameRequest request, int fps, Action<string?> problem, CancellationToken ct) =>
        _frames.WatchAsync(Of(request), fps, problem, ct);

    private static CameraFrameRequest Of(FrameRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new CameraFrameRequest(request.DeviceId, request.VideoSource, request.Resolution.Width, request.Resolution.Height);
    }
}
