using System.Net;
using System.Threading.Channels;

using Microsoft.Extensions.Time.Testing;

using Oadm.Contracts.SiteNode;
using Oadm.Sdk.Testing;
using Oadm.SiteNode.Config;
using Oadm.SiteNode.Devices;
using Oadm.SiteNode.Feed;
using Oadm.SiteNode.Logging;
using Oadm.SiteNode.Networks;
using Oadm.SiteNode.Service;
using Oadm.SiteNode.Tests.Support;

namespace Oadm.SiteNode.Tests;

/// <summary>
/// The Watch stream and the change events behind it: a Reset announced while the counts wait still goes out as the reset
/// notice, and a listener that fails or arrives late never breaks the code that raised the event.
/// </summary>
public sealed class WatchLoopTests
{
    [Fact]
    public async Task AResetAnnouncedWhileTheCountsWaitGoesOutAsTheResetNotice()
    {
        var time = new TimerSignalingTime();
        var notice = new ResetNotice(time, TimeSpan.FromMinutes(5));
        var changed = new Wakeup();
        notice.Requested += changed.Set;
        var sent = Channel.CreateUnbounded<NodeStatus>();
        using var stop = new CancellationTokenSource();
        using var watching = notice.Enter();
        var options = new NodeOptions { Time = time, Version = "1.0.0" };

        var loop = WatchLoop.RunAsync(changed, () => false, () => new NodeStatus { State = NodeState.Bound, ShortConnections = 1 }, notice, options,
            (status, ct) => sent.Writer.WriteAsync(status, ct).AsTask(), stop.Token);
        Assert.False((await sent.Reader.ReadAsync()).Reset);

        // Only the counts changed: the loop waits for the rest of the second.
        var delaying = time.NextTimer();
        changed.Set();
        await delaying;
        var announce = notice.AnnounceAsync(CancellationToken.None);
        time.Advance(SiteNodeGrpcService.CountsInterval);

        var next = await sent.Reader.ReadAsync();
        Assert.True(next.Reset);
        Assert.Equal(NodeState.UnboundOpen, next.State);

        await stop.CancelAsync();
        await loop;
        watching.Dispose();
        Assert.True(await announce);
    }

    [Fact]
    public async Task OnlyCountChangesAreSentAtMostOnceASecondAndOtherChangesAtOnce()
    {
        var time = new TimerSignalingTime();
        var notice = new ResetNotice(time, TimeSpan.FromMinutes(5));
        var changed = new Wakeup();
        var other = 0;
        var sent = Channel.CreateUnbounded<NodeStatus>();
        using var stop = new CancellationTokenSource();
        var options = new NodeOptions { Time = time };

        var loop = WatchLoop.RunAsync(changed, () => Interlocked.Exchange(ref other, 0) != 0, () => new NodeStatus(), notice, options,
            (status, ct) => sent.Writer.WriteAsync(status, ct).AsTask(), stop.Token);
        await sent.Reader.ReadAsync();

        var delaying = time.NextTimer();
        changed.Set();
        await delaying;
        Assert.False(sent.Reader.TryRead(out _));
        time.Advance(SiteNodeGrpcService.CountsInterval);
        await sent.Reader.ReadAsync();

        Interlocked.Exchange(ref other, 1);
        changed.Set();
        await sent.Reader.ReadAsync(); // no wait for another change

        await stop.CancelAsync();
        await loop;
    }

    [Fact]
    public void AFailingListenerNeitherReachesTheRaiserNorStopsTheOthers()
    {
        var reached = 0;
        Action? handlers = null;
        handlers += () => throw new ObjectDisposedException("semaphore");
        handlers += () => reached++;

        NodeEvents.Raise(handlers);

        Assert.Equal(1, reached);
    }

    [Fact]
    public async Task AWakeupSetAfterTheLoopEndedNeverThrowsAndACancelledWaitKeepsTheSet()
    {
        var wakeup = new Wakeup();
        using (var cancelled = new CancellationTokenSource())
        {
            var waiting = wakeup.WaitAsync(cancelled.Token);
            await cancelled.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        }

        wakeup.Set();
        wakeup.Set();
        await wakeup.WaitAsync(CancellationToken.None); // the set was kept for the next wait
        Assert.False(wakeup.Drain());
    }

    [Fact]
    public async Task DialsKeepTheirCountsWhileListenersComeGoAndFail()
    {
        using var folder = new TempFolder();
        var store = new NodeStore(folder.Path);
        await using var echo = new EchoServer();
        store.Update(FakeCards.ChooseCameraCard);
        store.Update(c => Allow.Connect(c, echo.Port));
        await using var log = new ConnectionLog(Path.Combine(folder.Path, "logs"), TimeProvider.System);
        using var dialer = new Dialer(new PortPolicy(store), new NetworkScope(store, FakeCards.LoopbackSite(), allowLoopback: true),
            SystemDeviceConnector.Instance, new FakeDns(), log, TimeProvider.System, new DialLimits(ShortOpen: 4, ShortConnecting: 4, PermanentConnecting: 4));

        // Like a Watch that ended while a dial raised the event: its semaphore is gone, the handler throws.
        var disposed = new SemaphoreSlim(0);
        disposed.Dispose();
        void Late() => disposed.Release();
        using var churn = new CancellationTokenSource();
        var subscriber = Task.Run(async () =>
        {
            while (!churn.IsCancellationRequested)
            {
                dialer.CountsChanged += Late;
                await Task.Yield();
                dialer.CountsChanged -= Late;
            }
        });
        dialer.CountsChanged += Late;

        for (var i = 0; i < 40; i++)
        {
            var (result, connection) = await dialer.OpenAsync(new DialOpen { Host = "127.0.0.1", Port = echo.Port }, "s", CancellationToken.None);
            Assert.Equal(DialError.None, result.Error);
            connection!.Dispose();
        }

        await churn.CancelAsync();
        await subscriber;
        Assert.Equal(0, dialer.ShortConnections);
    }

    /// <summary>A fake clock that tells when the next timer (a Task.Delay) was created.</summary>
    private sealed class TimerSignalingTime : FakeTimeProvider
    {
        private readonly Lock _lock = new();
        private TaskCompletionSource? _next;

        public Task NextTimer()
        {
            lock (_lock)
                return (_next = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            TaskCompletionSource? next;
            lock (_lock)
            {
                next = _next;
                _next = null;
            }

            next?.TrySetResult();
            return timer;
        }
    }
}
