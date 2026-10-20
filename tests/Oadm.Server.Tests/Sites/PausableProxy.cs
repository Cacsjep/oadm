using System.Net;
using System.Net.Sockets;

namespace Oadm.Server.Tests.Sites;

/// <summary>
/// A TCP forwarder on loopback between a site node and the server's site port. <see cref="Pause"/> holds the bytes of one
/// or both directions while the connections stay open: a node that is connected but does not answer, or an answer that is
/// late.
/// </summary>
internal sealed class PausableProxy : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly IPEndPoint _target;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<TcpClient> _clients = [];
    private readonly Lock _lock = new();
    private TaskCompletionSource _toServer = Open();
    private TaskCompletionSource _toNode = Open();
    private Task _loop = Task.CompletedTask;

    public PausableProxy(IPEndPoint target)
    {
        _target = target;
        _listener.Start();
        _loop = Task.Run(AcceptAsync);
    }

    /// <summary>What the node types as the server address.</summary>
    public string Address => "127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>Holds what the node sends (<paramref name="toServer"/>) and / or what the server sends.</summary>
    public void Pause(bool toServer = true, bool toNode = true)
    {
        lock (_lock)
        {
            if (toServer && _toServer.Task.IsCompleted)
            {
                _toServer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            if (toNode && _toNode.Task.IsCompleted)
            {
                _toNode = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
    }

    public void Resume()
    {
        lock (_lock)
        {
            _toServer.TrySetResult();
            _toNode.TrySetResult();
        }
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient node;
            try
            {
                node = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            var server = new TcpClient();
            try
            {
                await server.ConnectAsync(_target, _stop.Token);
            }
            catch (Exception e) when (e is OperationCanceledException or SocketException)
            {
                node.Dispose();
                server.Dispose();
                continue;
            }

            lock (_lock)
            {
                _clients.Add(node);
                _clients.Add(server);
            }

            _ = PumpAsync(node.GetStream(), server.GetStream(), toServer: true, node, server);
            _ = PumpAsync(server.GetStream(), node.GetStream(), toServer: false, node, server);
        }
    }

    private async Task PumpAsync(NetworkStream from, NetworkStream to, bool toServer, TcpClient a, TcpClient b)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (true)
            {
                var read = await from.ReadAsync(buffer, _stop.Token);
                if (read == 0)
                {
                    break;
                }

                Task gate;
                lock (_lock)
                {
                    gate = toServer ? _toServer.Task : _toNode.Task;
                }

                await gate.WaitAsync(_stop.Token);
                await to.WriteAsync(buffer.AsMemory(0, read), _stop.Token);
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException or SocketException)
        {
        }

        a.Dispose();
        b.Dispose();
    }

    private static TaskCompletionSource Open()
    {
        var open = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        open.SetResult();
        return open;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        await _loop;
        lock (_lock)
        {
            foreach (var client in _clients)
            {
                client.Dispose();
            }
        }

        _stop.Dispose();
    }
}
