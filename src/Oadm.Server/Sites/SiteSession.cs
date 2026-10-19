using System.Diagnostics;
using System.Net.Sockets;

using Grpc.Core;

using Oadm.Contracts.SiteNode;
using Oadm.Core.Sites;

namespace Oadm.Server.Sites;

/// <summary>
/// The connections of one bound site's node (docs/sites.md, "Two connections per node"): the <b>control</b> link (Watch,
/// the heartbeat, requests) and up to 8 <b>worker</b> links that carry every <c>Dial</c>, so a large transfer never
/// delays the heartbeat. Outbound the node opens worker links when asked (<c>OpenConnections</c>); inbound the server
/// opens them. The session ends when the control link ends or stops answering.
/// </summary>
internal sealed class SiteSession : IAsyncDisposable
{
    private readonly SiteManager _manager;
    private readonly SiteOptions _options;
    private readonly Lock _lock = new();
    private readonly List<Worker> _workers = [];
    private readonly CancellationTokenSource _life = new();
    private readonly Func<CancellationToken, Task<NodeLink>>? _dialWorker;
    private TaskCompletionSource _workerAdded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task _run = Task.CompletedTask;
    private int _opening;

    /// <param name="dialWorker">Inbound: opens a new worker link to the node; null = outbound (the node opens them).</param>
    public SiteSession(SiteManager manager, Guid siteId, NodeLink control, SiteOptions options, Func<CancellationToken, Task<NodeLink>>? dialWorker)
    {
        _manager = manager;
        SiteId = siteId;
        Control = control;
        _options = options;
        _dialWorker = dialWorker;
    }

    public Guid SiteId { get; }

    public NodeLink Control { get; }

    /// <summary>The first Watch answer arrived: the node serves this server.</summary>
    public bool Ready { get; private set; }

    public bool IsEnded => _life.IsCancellationRequested;

    public CancellationToken Lifetime => _life.Token;

    public int WorkerCount
    {
        get
        {
            lock (_lock)
            {
                return _workers.Count;
            }
        }
    }

    /// <summary>Starts Watch and the heartbeat; the session ends with the control link.</summary>
    public void Start() => _run = Task.Run(RunAsync);

    /// <summary>A worker link the node opened (outbound) for this site.</summary>
    public void AddWorker(NodeLink link)
    {
        lock (_lock)
        {
            if (_life.IsCancellationRequested || _workers.Count >= _options.MaxWorkers)
            {
                _ = link.DisposeAsync().AsTask();
                return;
            }

            var worker = new Worker(link);
            _workers.Add(worker);
            _ = link.Closed.ContinueWith(_ => RemoveWorker(worker), TaskScheduler.Default);
            _workerAdded.TrySetResult();
        }
    }

    /// <summary>
    /// One TCP connection to <paramref name="host"/>:<paramref name="port"/> through the node: the node's device errors as
    /// <see cref="SocketException"/>, its own problems as <see cref="SiteException"/>.
    /// </summary>
    public async Task<Stream> DialAsync(string siteName, string host, int port, SiteDialKind kind, TimeSpan timeout, CancellationToken ct)
    {
        var worker = await WorkerAsync(siteName, ct).ConfigureAwait(false);
        var life = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
        AsyncDuplexStreamingCall<DialMessage, DialMessage>? call = null;
        try
        {
            call = worker.Link.Client.Dial(cancellationToken: life.Token);
            await call.RequestStream.WriteAsync(new DialMessage
            {
                Open = new DialOpen
                {
                    Host = host,
                    Port = port,
                    Kind = kind == SiteDialKind.Permanent ? DialKind.Permanent : DialKind.Short,
                    TimeoutMs = (int)Math.Clamp(timeout.TotalMilliseconds, 100, int.MaxValue),
                },
            }, ct).ConfigureAwait(false);

            // The node stops connecting at the timeout; a node that does not answer at all is a site problem.
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(timeout + _options.CallTimeout);
            bool answered;
            try
            {
                answered = await call.ResponseStream.MoveNext(wait.Token).ConfigureAwait(false);
            }
            catch (RpcException) when (wait.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                throw SiteException.For(SiteId, siteName, SiteProblem.Busy);
            }

            if (!answered || call.ResponseStream.Current.KindCase != DialMessage.KindOneofCase.Result)
            {
                throw SiteException.For(SiteId, siteName, SiteProblem.Offline);
            }

            var result = call.ResponseStream.Current.Result;
            if (result.Error != DialError.None)
            {
                throw Error(result, siteName, port);
            }

            Interlocked.Increment(ref worker.Open);
            var stream = new DialStream(call, life, () => Interlocked.Decrement(ref worker.Open));
            call = null;
            return stream;
        }
        catch (RpcException e) when (!ct.IsCancellationRequested)
        {
            throw e.StatusCode == StatusCode.FailedPrecondition
                ? new SiteException(SiteId, SiteProblem.NotSupported, e.Status.Detail)
                : SiteException.For(SiteId, siteName, SiteProblem.Offline);
        }
        finally
        {
            if (call is not null)
            {
                call.Dispose();
                life.Dispose();
            }
        }
    }

    private SiteException ErrorOf(DialError error, string siteName, int port) => error switch
    {
        DialError.SiteOutsideNetworks => SiteException.For(SiteId, siteName, SiteProblem.OutsideNetworks),
        DialError.SitePortNotAllowed => SiteException.For(SiteId, siteName, SiteProblem.PortNotAllowed, port),
        DialError.SiteBusy => SiteException.For(SiteId, siteName, SiteProblem.Busy),
        _ => SiteException.For(SiteId, siteName, SiteProblem.NotSupported),
    };

    private Exception Error(DialResult result, string siteName, int port) => result.Error switch
    {
        DialError.DeviceRefused => new SocketException((int)SocketError.ConnectionRefused),
        DialError.DeviceTimedOut => new SocketException((int)SocketError.TimedOut),
        DialError.DeviceUnreachable => new SocketException((int)SocketError.HostUnreachable),
        DialError.DeviceReset => new SocketException((int)SocketError.ConnectionReset),
        DialError.DeviceHostNotFound => new SocketException((int)SocketError.HostNotFound),
        _ => ErrorOf(result.Error, siteName, port),
    };

    /// <summary>The least busy worker; a new one when none is free (the node opens it outbound, the server inbound).</summary>
    private async Task<Worker> WorkerAsync(string siteName, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct, _life.Token);
        limit.CancelAfter(_options.WorkerWait);
        try
        {
            while (true)
            {
                Task added;
                Worker? best;
                lock (_lock)
                {
                    best = _workers.Where(w => !w.Link.IsClosed).MinBy(w => Volatile.Read(ref w.Open));
                    if (best is not null && (Volatile.Read(ref best.Open) < _options.DialsPerWorker || _workers.Count >= _options.MaxWorkers))
                    {
                        return best;
                    }

                    if (_workerAdded.Task.IsCompleted)
                    {
                        _workerAdded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    }

                    added = _workerAdded.Task;
                }

                OpenWorker();
                if (best is not null)
                {
                    return best; // busy, but a new one is on its way
                }

                await added.WaitAsync(limit.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw SiteException.For(SiteId, siteName, _life.IsCancellationRequested ? SiteProblem.Offline : SiteProblem.Busy);
        }
    }

    // One opening at a time: outbound asks the node, inbound dials it.
    private void OpenWorker()
    {
        if (Interlocked.Exchange(ref _opening, 1) == 1)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                if (_dialWorker is not null)
                {
                    AddWorker(await _dialWorker(_life.Token).ConfigureAwait(false));
                }
                else
                {
                    await Control.Client.OpenConnectionsAsync(new OpenConnectionsRequest { Count = 1 },
                        deadline: DateTime.UtcNow + _options.CallTimeout, cancellationToken: _life.Token).ConfigureAwait(false);
                }
            }
#pragma warning disable CA1031 // A failed opening is tried again by the next dial.
            catch (Exception)
#pragma warning restore CA1031
            {
            }
            finally
            {
                Volatile.Write(ref _opening, 0);
            }
        });
    }

    private void RemoveWorker(Worker worker)
    {
        lock (_lock)
        {
            _workers.Remove(worker);
        }

        _manager.SessionChanged(SiteId);
    }

    /// <summary>Asks the node for ports it has not allowed yet (shown in the node app until allowed or dismissed).</summary>
    public async Task RequestPortAsync(int port, string reason)
    {
        try
        {
            var request = new RequestPortsRequest();
            request.Ports.Add(new PortRule { Direction = PortDirection.Connect, Protocol = Protocol.Tcp, Port = port, Reason = reason });
            await Control.Client.RequestPortsAsync(request, deadline: DateTime.UtcNow + _options.CallTimeout, cancellationToken: _life.Token).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Asked again with the next dial to that port after a reconnect.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    private async Task RunAsync()
    {
        var watch = Task.Run(WatchAsync);
        var heartbeat = Task.Run(HeartbeatAsync);
        await Task.WhenAny(watch, heartbeat, Control.Closed).ConfigureAwait(false);
        await EndAsync().ConfigureAwait(false);
        await Quiet(watch).ConfigureAwait(false);
        await Quiet(heartbeat).ConfigureAwait(false);
        _manager.SessionEnded(this);
    }

    private async Task WatchAsync()
    {
        using var call = Control.Client.Watch(new WatchRequest(), cancellationToken: _life.Token);
        while (await call.ResponseStream.MoveNext(_life.Token).ConfigureAwait(false))
        {
            Ready = true;
            _manager.OnStatus(this, call.ResponseStream.Current);
        }
    }

    private async Task HeartbeatAsync()
    {
        while (!_life.IsCancellationRequested)
        {
            await Task.Delay(_options.HeartbeatInterval, _manager.Time, _life.Token).ConfigureAwait(false);
            var started = Stopwatch.GetTimestamp();
            HelloReply hello;
            try
            {
                hello = await Control.Client.HelloAsync(new HelloRequest(), deadline: DateTime.UtcNow + _options.CallTimeout, cancellationToken: _life.Token).ConfigureAwait(false);
            }
            catch (RpcException) when (!_life.IsCancellationRequested)
            {
                if (_manager.AnsweredLongAgo(SiteId))
                {
                    return; // the node stopped answering: the session ends
                }

                continue;
            }

            _manager.OnHeartbeat(this, hello, Stopwatch.GetElapsedTime(started));
            if (hello.State != NodeState.Bound)
            {
                return;
            }
        }
    }

    /// <summary>Closes every link of the session.</summary>
    public async Task EndAsync()
    {
        try
        {
            await _life.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }

        List<Worker> workers;
        lock (_lock)
        {
            workers = [.. _workers];
            _workers.Clear();
        }

        foreach (var worker in workers)
        {
            await worker.Link.DisposeAsync().ConfigureAwait(false);
        }

        await Control.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await EndAsync().ConfigureAwait(false);
        await Quiet(_run).ConfigureAwait(false);
        _life.Dispose();
    }

    private static async Task Quiet(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
#pragma warning disable CA1031 // The session is over; its loops end with whatever the dead link threw.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    private sealed class Worker(NodeLink link)
    {
        public NodeLink Link { get; } = link;

        public int Open;
    }
}
