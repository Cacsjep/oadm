using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

using Oadm.Core.LiveView;
using Oadm.Core.LiveView.Rtsp;
using Oadm.Sdk.Devices;

namespace Oadm.Tests.Shared;

/// <summary>
/// Video and event streams that only record which device was asked for (device group tests: nothing may reach a device
/// outside the view). Live video always fails ("recording fake"); an event stream opens and delivers nothing.
/// </summary>
internal sealed class RecordingStreams : ILiveVideoSourceFactory, IDeviceEventStreams
{
    private readonly ConcurrentQueue<Guid> _opened = new();

    /// <summary>Every device a video or event stream was asked for, in order.</summary>
    public IReadOnlyCollection<Guid> Opened => _opened;

    /// <summary>How often a stream of <paramref name="deviceId"/> was asked for.</summary>
    public int OpenedFor(Guid deviceId) => _opened.Count(id => id == deviceId);

    public Task<LiveViewCapabilities> GetCapabilitiesAsync(Guid deviceId, CancellationToken ct)
    {
        _opened.Enqueue(deviceId);
        throw new KeyNotFoundException("recording fake");
    }

    public Task<ILiveVideoSource> OpenAsync(LiveStreamKey key, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);
        _opened.Enqueue(key.DeviceId);
        throw new KeyNotFoundException("recording fake");
    }

    public Task<IDeviceEventSource> OpenAsync(Guid deviceId, CancellationToken ct)
    {
        _opened.Enqueue(deviceId);
        return Task.FromResult<IDeviceEventSource>(new NoEvents());
    }

    private sealed class NoEvents : IDeviceEventSource
    {
        public int LostDocuments => 0;

        public async IAsyncEnumerable<DeviceMetadataDocument> ReadAsync([EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
