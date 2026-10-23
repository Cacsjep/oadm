using System.Runtime.CompilerServices;

using Oadm.Sdk.Devices;

namespace Oadm.Core.DeviceGroups;

/// <summary>
/// The device event streams of one view (a user's call, <c>ICallContext.EventStreams</c>): a stream opens only for a device
/// of the view (any other id: <see cref="DevicesNotAvailableException"/>, the device is never contacted), and an open stream
/// ends with <see cref="DevicesNotAvailableException"/> when its device leaves the view (<see cref="DeviceViewWatcher"/>).
/// RTSP ports are read and stored only for devices of the view. A view with all devices behaves like the inner streams.
/// </summary>
public sealed class ViewedEventStreams(IDeviceEventStreams inner, DeviceView view, DeviceViewWatcher watcher) : IDeviceEventStreams
{
    public async Task<IDeviceEventSource> OpenAsync(Guid deviceId, CancellationToken ct)
    {
        if (view.HasAllDevices)
        {
            return await inner.OpenAsync(deviceId, ct).ConfigureAwait(false);
        }

        var lease = await watcher.WatchAsync(await view.GetAsync([deviceId], ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        try
        {
            var source = await inner.OpenAsync(deviceId, ct).ConfigureAwait(false);
            return new LeasedSource(source, lease);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    public async Task<int?> GetRtspPortAsync(Guid deviceId, CancellationToken ct)
    {
        await view.GetAsync([deviceId], ct).ConfigureAwait(false);
        return await inner.GetRtspPortAsync(deviceId, ct).ConfigureAwait(false);
    }

    public async Task SetRtspPortAsync(Guid deviceId, int? port, CancellationToken ct)
    {
        await view.GetAsync([deviceId], ct).ConfigureAwait(false);
        await inner.SetRtspPortAsync(deviceId, port, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<Guid, int>> GetRtspPortsAsync(IReadOnlyCollection<Guid> deviceIds, CancellationToken ct)
    {
        var viewed = await view.GetAsync(deviceIds, ct).ConfigureAwait(false);
        return await inner.GetRtspPortsAsync(viewed.Ids, ct).ConfigureAwait(false);
    }

    public async Task SetRtspPortsAsync(IReadOnlyCollection<Guid> deviceIds, int? port, IReadOnlyCollection<Guid>? managed, CancellationToken ct)
    {
        var viewed = await view.GetAsync(deviceIds, ct).ConfigureAwait(false);
        // Without all devices the ports of other devices are never touched (the clean-up of removed devices is system work).
        await inner.SetRtspPortsAsync(viewed.Ids, port, view.HasAllDevices ? managed : null, ct).ConfigureAwait(false);
    }

    /// <summary>An event source that ends when its device leaves the view.</summary>
    private sealed class LeasedSource(IDeviceEventSource inner, DeviceViewLease lease) : IDeviceEventSource
    {
        public int LostDocuments => inner.LostDocuments;

        public async IAsyncEnumerable<DeviceMetadataDocument> ReadAsync([EnumeratorCancellation] CancellationToken ct)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.Left);
            var enumerator = inner.ReadAsync(linked.Token).GetAsyncEnumerator(linked.Token);
            await using (enumerator.ConfigureAwait(false))
            {
                while (true)
                {
                    bool more;
                    try
                    {
                        more = await enumerator.MoveNextAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is OperationCanceledException or DeviceStreamException && lease.HasLeft && !ct.IsCancellationRequested)
                    {
                        throw new DevicesNotAvailableException(DeviceMessages.NoLongerAvailable(1), ex);
                    }

                    if (!more)
                    {
                        yield break;
                    }

                    if (lease.HasLeft)
                    {
                        throw new DevicesNotAvailableException(1);
                    }

                    yield return enumerator.Current;
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            lease.Dispose();
            await inner.DisposeAsync().ConfigureAwait(false);
        }
    }
}
