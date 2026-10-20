using System.Net.Sockets;

using Google.Protobuf;

using Grpc.Core;

using Oadm.Contracts.SiteNode;

namespace Oadm.SiteNode.Devices;

/// <summary>
/// One <c>Dial</c> call after its first message: open the device connection, answer with the result, then copy bytes both
/// ways until either side ends. The device socket and its place in the dial limits are released on every path, also
/// when the answer cannot be written (the server went away).
/// </summary>
public static class DialPipe
{
    public static async Task RunAsync(Dialer dialer, DialOpen open, string server, IAsyncStreamReader<DialMessage> requests, IAsyncStreamWriter<DialMessage> responses, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(dialer);
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(responses);
        var (result, connection) = await dialer.OpenAsync(open, server, ct).ConfigureAwait(false);
        using (connection)
        {
            await responses.WriteAsync(new DialMessage { Result = result }, ct).ConfigureAwait(false);
            if (connection is null)
                return;
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var socket = connection.Socket;
            var up = Task.Run(() => UpAsync(requests, socket, stop.Token), CancellationToken.None);
            var down = Task.Run(() => DownAsync(socket, responses, stop.Token), CancellationToken.None);
            var first = await Task.WhenAny(up, down).ConfigureAwait(false);
            if (first.IsFaulted || first.IsCanceled)
                await stop.CancelAsync().ConfigureAwait(false);
            try
            {
                await Task.WhenAll(up, down).ConfigureAwait(false);
            }
            catch (Exception e) when (e is SocketException or IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException or RpcException)
            {
                await stop.CancelAsync().ConfigureAwait(false);
                // The socket closes with the connection; the server sees the end of the call.
            }
        }
    }

    private static async Task UpAsync(IAsyncStreamReader<DialMessage> requests, Socket socket, CancellationToken ct)
    {
        while (await requests.MoveNext(ct).ConfigureAwait(false))
        {
            var message = requests.Current;
            if (message.KindCase == DialMessage.KindOneofCase.Data && !message.Data.IsEmpty)
            {
                await socket.SendAsync(message.Data.Memory, SocketFlags.None, ct).ConfigureAwait(false);
            }
            else if (message.KindCase == DialMessage.KindOneofCase.End)
            {
                socket.Shutdown(SocketShutdown.Send);
                return;
            }
        }

        // The server ended its stream without a half-close: the same for the device.
        socket.Shutdown(SocketShutdown.Send);
    }

    private static async Task DownAsync(Socket socket, IAsyncStreamWriter<DialMessage> responses, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await socket.ReceiveAsync(buffer, SocketFlags.None, ct).ConfigureAwait(false);
            if (read == 0)
            {
                await responses.WriteAsync(new DialMessage { End = true }, ct).ConfigureAwait(false);
                return;
            }

            await responses.WriteAsync(new DialMessage { Data = ByteString.CopyFrom(buffer, 0, read) }, ct).ConfigureAwait(false);
        }
    }
}
