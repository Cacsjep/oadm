using Grpc.Core;

using Oadm.Contracts.V1;

namespace Oadm.Client.Api;

/// <summary>One DeviceWebService.Exchange call: frames to the server, frames from the server.</summary>
public interface IDeviceWebCall : IDisposable
{
    Task SendAsync(DeviceWebFrame frame, CancellationToken ct);

    /// <summary>No more frames from the client.</summary>
    Task CompleteAsync();

    IAsyncEnumerable<DeviceWebFrame> ReadAllAsync(CancellationToken ct);
}

/// <summary>The gRPC duplex call behind <see cref="IDeviceWebCall"/>.</summary>
internal sealed class GrpcDeviceWebCall(AsyncDuplexStreamingCall<DeviceWebFrame, DeviceWebFrame> call) : IDeviceWebCall
{
    private readonly SemaphoreSlim _write = new(1, 1);

    public async Task SendAsync(DeviceWebFrame frame, CancellationToken ct)
    {
        await _write.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await call.RequestStream.WriteAsync(frame, ct).ConfigureAwait(false);
        }
        finally
        {
            _write.Release();
        }
    }

    public async Task CompleteAsync()
    {
        await _write.WaitAsync().ConfigureAwait(false);
        try
        {
            await call.RequestStream.CompleteAsync().ConfigureAwait(false);
        }
        finally
        {
            _write.Release();
        }
    }

    public IAsyncEnumerable<DeviceWebFrame> ReadAllAsync(CancellationToken ct) => call.ResponseStream.ReadAllAsync(ct);

    public void Dispose()
    {
        call.Dispose();
        _write.Dispose();
    }
}
