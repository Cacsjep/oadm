using Grpc.Core;

using Microsoft.Extensions.Logging;

using Oadm.Client.Api;
using Oadm.Contracts.V1;
using Oadm.Sdk.Client;

namespace Oadm.Client.DeviceWeb;

/// <summary>
/// <see cref="IDeviceWebAccess"/> of one plugin page: asks the server whether the device can be opened (and so writes the
/// audit entry), then starts a <see cref="DeviceWebRelay"/>. <see cref="Dispose"/> (page closed) stops every relay it started.
/// </summary>
public sealed class DeviceWebAccess(IOadmApi api, ILogger logger) : IDeviceWebAccess, IDisposable
{
    private readonly Lock _gate = new();
    private readonly List<DeviceWebRelay> _relays = [];
    private bool _disposed;

    public async Task<IDeviceWebSession> OpenAsync(Guid deviceId, CancellationToken ct)
    {
        OpenDeviceWebReply reply;
        try
        {
            reply = await api.OpenDeviceWebAsync(deviceId.ToString(), ct).ConfigureAwait(false);
        }
        catch (RpcException ex) when (ex.StatusCode is StatusCode.NotFound or StatusCode.FailedPrecondition or StatusCode.Unavailable)
        {
            throw new DeviceWebRefusedException(ex.Status.Detail, ex);
        }

        if (!reply.Ok)
        {
            throw new DeviceWebRefusedException(reply.Message);
        }

        var relay = new DeviceWebRelay(api, deviceId, logger);
        lock (_gate)
        {
            if (_disposed)
            {
                relay.Dispose();
                throw new ObjectDisposedException(nameof(DeviceWebAccess));
            }

            _relays.RemoveAll(r => r.IsDisposed);
            _relays.Add(relay);
        }

        return relay;
    }

    public void Dispose()
    {
        List<DeviceWebRelay> relays;
        lock (_gate)
        {
            _disposed = true;
            relays = [.. _relays];
            _relays.Clear();
        }

        foreach (var relay in relays)
        {
            relay.Dispose();
        }
    }
}
