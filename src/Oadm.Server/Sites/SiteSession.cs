using System.Diagnostics;
using System.Net.Sockets;

using Grpc.Core;

using Oadm.Contracts.SiteNode;
using Oadm.Core.Sites;

namespace Oadm.Server.Sites;

/// <summary>
/// The connections of one bound site's node (docs/sites.md, "Two connections per node"): the <b>control</b> link (Watch,
/// the heartbeat, requests) and up to 8 <b>worker</b> links that carry every <c>Dial</c>, so a large transfer never
/// delays the heartbeat. The node opens worker links when asked (<c>OpenConnections</c>). The session ends when the control link ends or stops answering.
/// </summary>
internal sealed partial class SiteSession : IAsyncDisposable
{
    private readonly SiteManager _manager;
    private readonly SiteOptions _options;
    private readonly Lock _lock = new();
    private readonly List<Worker> _workers = [];
    private readonly CancellationTokenSource _life = new();
    private TaskCompletionSource _workerAdded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task _run = Task.CompletedTask;
    private int _opening;

    public SiteSession(SiteManager manager, Guid siteId, NodeLink control, SiteOptions options)
    {
        _manager = manager;
        SiteId = siteId;
        Control = control;
        _options = options;
        Started = manager.Time.GetTimestamp();
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
    /// <remarks>
    /// Every site verdict arrives before <paramref name="timeout"/>, which is also the caller's limit for the connect: the
    /// node is given <paramref name="timeout"/> less a margin (a device that does not answer comes back as its own
    /// <see cref="SocketError.TimedOut"/>), the wait for a worker link and for the node's answer end inside the timeout as
    /// "Site ... is busy" (otherwise the caller's timeout would end them as a device that does not answer). A cancellation
    /// by <paramref name="ct"/> is an <see cref="OperationCanceledException"/>, never a raw <see cref="RpcException"/>.
    /// </remarks>
    public async Task<Stream> DialAsync(string siteName, string host, int port, SiteDialKind kind, TimeSpan timeout, CancellationToken ct)
    {
        var margin = Margin(timeout);
        var worker = await WorkerAsync(siteName, timeout - margin, ct).ConfigureAwait(false);
        var life = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
        AsyncDuplexStreamingCall<DialMessage, DialMessage>? call = null;
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        wait.CancelAfter(timeout - (margin / 2));
        var opened = false;
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
                    TimeoutMs = (int)Math.Clamp((timeout - margin).TotalMilliseconds, 100, int.MaxValue),
                },
            }, wait.Token).ConfigureAwait(false);

            // The node stops connecting at its timeout and answers; a node that does not answer in time is a site problem.
            if (!await call.ResponseStream.MoveNext(wait.Token).ConfigureAwait(false)
                || call.ResponseStream.Current.KindCase != DialMessage.KindOneofCase.Result)
            {
                throw Lost(siteName);
            }

            var result = call.ResponseStream.Current.Result;
            if (result.Error != DialError.None)
            {
                throw Error(result, siteName, port);
            }

            Interlocked.Increment(ref worker.Open);
            var stream = new DialStream(call, life, () => Interlocked.Decrement(ref worker.Open), () => Lost(siteName));
            opened = true;
            return stream;
        }
        catch (Exception e) when (e is RpcException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
            if (ct.IsCancellationRequested)
            {
                throw new OperationCanceledException("The connection through the site was cancelled.", e, ct);
            }

            throw e switch
            {
                _ when _life.IsCancellationRequested => Lost(siteName),
                RpcException { StatusCode: StatusCode.FailedPrecondition or StatusCode.Unimplemented } rpc =>
                    string.IsNullOrEmpty(rpc.Status.Detail) ? SiteException.For(SiteId, siteName, SiteProblem.NotSupported) : new SiteException(SiteId, SiteProblem.NotSupported, rpc.Status.Detail),
                RpcException { StatusCode: StatusCode.ResourceExhausted or StatusCode.DeadlineExceeded } => SiteException.For(SiteId, siteName, SiteProblem.Busy),
                _ when wait.IsCancellationRequested => SiteException.For(SiteId, siteName, SiteProblem.Busy), // the node did not answer in time
                _ => Lost(siteName), // the worker link closed or the call ended: the node is gone
            };
        }
        finally
        {
            if (!opened)
            {
                call?.Dispose();
                life.Dispose();
            }
        }
    }

    /// <summary>"Site offline: ..." for a link or session that ended.</summary>
    private SiteException Lost(string siteName) => SiteException.For(SiteId, siteName, SiteProblem.Offline);

    /// <summary>
    /// How much earlier than <paramref name="timeout"/> the node must answer: twice the round trip and half a second, at
    /// most a third of the timeout.
    /// </summary>
    internal TimeSpan Margin(TimeSpan timeout)
    {
        var margin = (_manager.RoundTrip(SiteId) * 2) + TimeSpan.FromMilliseconds(500);
        return margin < timeout / 3 ? margin : timeout / 3;
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

    /// <summary>
    /// The least busy worker; a new one when none is free (the node opens it). The wait for
    /// a new one has its own limit (<see cref="SiteOptions.WorkerWait"/>, at most <paramref name="within"/>, so it ends
    /// before the caller's timeout): then the site is busy, never a device that does not answer.
    /// </summary>
    private async Task<Worker> WorkerAsync(string siteName, TimeSpan within, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct, _life.Token);
        var wait = within == Timeout.InfiniteTimeSpan || within >= _options.WorkerWait ? _options.WorkerWait : within;
        limit.CancelAfter(wait > TimeSpan.Zero ? wait : TimeSpan.FromMilliseconds(1));
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

    // One opening at a time: the node is asked for one more worker link.
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
                await Control.Client.OpenConnectionsAsync(new OpenConnectionsRequest { Count = 1 },
                    deadline: DateTime.UtcNow + _options.CallTimeout, cancellationToken: _life.Token).ConfigureAwait(false);
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

    /// <summary>
    /// Asks the node for ports it has not allowed yet (shown in the node app until allowed or dismissed). Bounded by the
    /// caller's <paramref name="ct"/> (a poll never waits longer than its own timeout). False when the node did not get
    /// it: then it is asked again with the next dial to that port.
    /// </summary>
    public async Task<bool> RequestPortAsync(int port, string reason, CancellationToken ct)
    {
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct, _life.Token);
            var request = new RequestPortsRequest();
            request.Ports.Add(new PortRule { Direction = PortDirection.Connect, Protocol = Protocol.Tcp, Port = port, Reason = reason });
            await Control.Client.RequestPortsAsync(request, deadline: DateTime.UtcNow + _options.CallTimeout, cancellationToken: limit.Token).ConfigureAwait(false);
            return true;
        }
#pragma warning disable CA1031 // Not delivered: asked again with the next dial to that port.
        catch (Exception)
#pragma warning restore CA1031
        {
            return false;
        }
    }

    /// <summary>
    /// When this session started (<see cref="TimeProvider.GetTimestamp"/>): a new session is judged by its own answers, never
    /// by the last session's.
    /// </summary>
    public long Started { get; }

    /// <summary>
    /// True when the node answers a Hello on the control link now (within the round trip twice and 3 s). The control link
    /// carries no transfers, so a node that is merely busy still answers.
    /// </summary>
    public async Task<bool> AnswersAsync(CancellationToken ct)
    {
        if (IsEnded || Control.IsClosed)
        {
            return false;
        }

        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct, _life.Token);
            var deadline = DateTime.UtcNow + (_manager.RoundTrip(SiteId) * 2) + TimeSpan.FromSeconds(3);
            await Control.Client.HelloAsync(new HelloRequest(), deadline: deadline, cancellationToken: limit.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception e) when (e is RpcException or ObjectDisposedException or InvalidOperationException || (e is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return false;
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
                if (_manager.AnsweredLongAgo(this))
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
