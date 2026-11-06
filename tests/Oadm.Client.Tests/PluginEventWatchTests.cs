using System.Runtime.CompilerServices;
using System.Threading.Channels;

using Microsoft.Extensions.Time.Testing;

using Oadm.Sdk.Client;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;

namespace Oadm.Client.Tests;

/// <summary>
/// The watch of a plugin page (<see cref="PluginEventWatch"/>): subscribe first, read, apply the events, and after a drop
/// wait, read again and watch again. The waits run on a fake clock.
/// </summary>
public sealed class PluginEventWatchTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task The_page_subscribes_before_it_reads_and_gets_what_was_published_during_the_read()
    {
        var server = new Server();
        var readMayEnd = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource();

        var run = server.RunAsync(
            async ct =>
            {
                server.Note("read");
                server.Publish("during the read");
                await readMayEnd.Task.WaitAsync(ct);
                server.Note("read done");
            },
            item => server.Note("event " + item.Topic),
            new PluginEventWatchOptions { Time = new RecordingTime() },
            stop.Token);
        await Eventually.TrueAsync(() => server.Has("read"), "the read");
        readMayEnd.SetResult();
        await Eventually.TrueAsync(() => server.Has("event during the read"), "the event of the read");

        Assert.Equal(["subscribed", "read", "read done", "event during the read"], server.Notes);
        await stop.CancelAsync();
        await run.WaitAsync(Limit);
    }

    [Fact]
    public async Task After_a_drop_the_page_waits_reads_again_and_watches_again()
    {
        var server = new Server();
        var time = new RecordingTime();
        var reads = 0;
        var seen = new List<string>();
        using var stop = new CancellationTokenSource();

        var run = server.RunAsync(
            _ =>
            {
                Interlocked.Increment(ref reads);
                return Task.CompletedTask;
            },
            item =>
            {
                lock (seen)
                {
                    seen.Add(item.Topic);
                }
            },
            new PluginEventWatchOptions { RetryDelay = TimeSpan.FromSeconds(3), Time = time },
            stop.Token);
        await Eventually.TrueAsync(() => Volatile.Read(ref reads) == 1 && server.Subscriptions == 1, "the first read");
        server.Drop();
        await Eventually.TrueAsync(() => time.Waits.Count == 1, "the wait after the drop");

        Assert.Equal(TimeSpan.FromSeconds(3), time.Waits[0]);
        Assert.Equal(1, Volatile.Read(ref reads)); // the clock did not move: nothing is read before the wait ended
        Assert.Equal(0, server.Open);

        time.Advance(TimeSpan.FromSeconds(3));
        await Eventually.TrueAsync(() => Volatile.Read(ref reads) == 2 && server.Subscriptions == 2, "the read after the reconnect");
        server.Publish("after the reconnect");
        await Eventually.TrueAsync(() => Snapshot(seen).Contains("after the reconnect"), "an event of the new stream");

        await stop.CancelAsync();
        await run.WaitAsync(Limit);
    }

    [Fact]
    public async Task A_failed_read_ends_its_subscription_tells_the_page_and_is_tried_again()
    {
        var server = new Server();
        var time = new RecordingTime();
        var reads = 0;
        var failures = new List<Exception>();
        using var stop = new CancellationTokenSource();

        var run = server.RunAsync(
            _ => Interlocked.Increment(ref reads) == 1 ? throw new InvalidOperationException("The server did not answer.") : Task.CompletedTask,
            _ => { },
            new PluginEventWatchOptions
            {
                Time = time,
                Failed = ex =>
                {
                    lock (failures)
                    {
                        failures.Add(ex);
                    }
                },
            },
            stop.Token);
        await Eventually.TrueAsync(() => time.Waits.Count == 1, "the wait after the failed read");

        Assert.Equal("The server did not answer.", Assert.Single(Snapshot(failures)).Message);
        Assert.Equal(PluginEventWatch.DefaultRetryDelay, time.Waits[0]);
        Assert.Equal(0, server.Open); // the stream that waited for its first event is closed

        time.Advance(PluginEventWatch.DefaultRetryDelay);
        await Eventually.TrueAsync(() => Volatile.Read(ref reads) == 2 && server.Open == 1, "the second read");

        await stop.CancelAsync();
        await run.WaitAsync(Limit);
        Assert.Single(Snapshot(failures)); // the cancellation that ends the watch is no failure
    }

    [Fact]
    public async Task An_event_the_page_cannot_apply_counts_as_a_drop()
    {
        var server = new Server();
        var time = new RecordingTime();
        var reads = 0;
        var failures = 0;
        using var stop = new CancellationTokenSource();

        var run = server.RunAsync(
            _ =>
            {
                Interlocked.Increment(ref reads);
                return Task.CompletedTask;
            },
            _ => throw new FormatException("Not JSON."),
            new PluginEventWatchOptions { Time = time, Failed = _ => Interlocked.Increment(ref failures) },
            stop.Token);
        await Eventually.TrueAsync(() => server.Subscriptions == 1, "the subscription");
        server.Publish("broken");
        await Eventually.TrueAsync(() => time.Waits.Count == 1, "the wait");

        Assert.Equal(1, Volatile.Read(ref failures));
        time.Advance(PluginEventWatch.DefaultRetryDelay);
        await Eventually.TrueAsync(() => Volatile.Read(ref reads) == 2, "the read after the wait");

        await stop.CancelAsync();
        await run.WaitAsync(Limit);
    }

    [Fact]
    public async Task With_a_longest_wait_the_wait_doubles_up_to_it_and_starts_again_after_a_read_that_worked()
    {
        var server = new Server();
        var time = new RecordingTime();
        var fail = true;
        using var stop = new CancellationTokenSource();

        var run = server.RunAsync(
            _ => Volatile.Read(ref fail) ? throw new InvalidOperationException("No answer.") : Task.CompletedTask,
            _ => { },
            new PluginEventWatchOptions { RetryDelay = TimeSpan.FromSeconds(1), MaxRetryDelay = TimeSpan.FromSeconds(15), Time = time },
            stop.Token);
        for (var round = 1; round <= 6; round++)
        {
            var count = round;
            await Eventually.TrueAsync(() => time.Waits.Count == count, $"wait {count}");
            if (round == 6)
            {
                Volatile.Write(ref fail, false); // the next read works
            }

            time.Advance(time.Waits[round - 1]);
        }

        await Eventually.TrueAsync(() => server.Open == 1, "the watch after the read that worked");
        server.End(); // an ended stream: the wait is the first one again
        await Eventually.TrueAsync(() => time.Waits.Count == 7, "the wait after the ended stream");

        Assert.Equal(
            [1, 2, 4, 8, 15, 15, 1],
            time.Waits.Select(w => (int)w.TotalSeconds));
        await stop.CancelAsync();
        await run.WaitAsync(Limit);
    }

    [Fact]
    public async Task A_host_without_events_is_read_at_the_pace_of_the_wait()
    {
        var host = new HostWithoutEvents();
        var time = new RecordingTime();
        var reads = 0;
        using var stop = new CancellationTokenSource();

        var run = host.RunAsync(
            _ =>
            {
                Interlocked.Increment(ref reads);
                return Task.CompletedTask;
            },
            _ => { },
            new PluginEventWatchOptions { Time = time },
            stop.Token);
        await Eventually.TrueAsync(() => time.Waits.Count == 1, "the first wait");
        Assert.Equal(1, Volatile.Read(ref reads));
        time.Advance(PluginEventWatch.DefaultRetryDelay);
        await Eventually.TrueAsync(() => time.Waits.Count == 2, "the second wait");

        Assert.Equal(2, Volatile.Read(ref reads));
        await stop.CancelAsync();
        await run.WaitAsync(Limit);
    }

    [Fact]
    public async Task Cancelling_ends_the_watch_and_its_stream_without_an_exception()
    {
        var server = new Server();
        var time = new RecordingTime();
        using var watching = new CancellationTokenSource();
        using var waiting = new CancellationTokenSource();

        var whileWatching = server.RunAsync(_ => Task.CompletedTask, _ => { }, new PluginEventWatchOptions { Time = time }, watching.Token);
        await Eventually.TrueAsync(() => server.Open == 1, "the stream");
        await watching.CancelAsync();
        await whileWatching.WaitAsync(Limit);
        Assert.Equal(0, server.Open);

        var whileWaiting = new HostWithoutEvents().RunAsync(_ => Task.CompletedTask, _ => { }, new PluginEventWatchOptions { Time = time }, waiting.Token);
        await Eventually.TrueAsync(() => time.Waits.Count == 1, "the wait");
        await waiting.CancelAsync();
        await whileWaiting.WaitAsync(Limit);

        Assert.Equal(1, server.Subscriptions);
    }

    [Fact]
    public async Task The_next_event_waits_until_the_page_applied_the_one_before()
    {
        var server = new Server();
        var firstMayEnd = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource();

        var run = server.RunAsync(
            _ => Task.CompletedTask,
            async item =>
            {
                server.Note(item.Topic + " starts");
                if (item.Topic == "first")
                {
                    await firstMayEnd.Task;
                }

                server.Note(item.Topic + " ends");
            },
            new PluginEventWatchOptions { Time = new RecordingTime() },
            stop.Token);
        await Eventually.TrueAsync(() => server.Open == 1, "the stream");
        server.Publish("first");
        server.Publish("second");
        await Eventually.TrueAsync(() => server.Has("first starts"), "the first event");
        firstMayEnd.SetResult();
        await Eventually.TrueAsync(() => server.Has("second ends"), "the second event");

        Assert.Equal(["subscribed", "first starts", "first ends", "second starts", "second ends"], server.Notes);
        await stop.CancelAsync();
        await run.WaitAsync(Limit);
    }

    [Fact]
    public async Task A_page_that_only_applies_events_watches_again_after_a_drop()
    {
        var server = new Server();
        var seen = new List<string>();
        using var stop = new CancellationTokenSource();

        var run = server.RunAsync(
            item =>
            {
                lock (seen)
                {
                    seen.Add(item.Topic);
                }
            },
            TimeSpan.FromMilliseconds(1),
            stop.Token);
        await Eventually.TrueAsync(() => server.Open == 1, "the stream");
        server.Publish("before");
        await Eventually.TrueAsync(() => Snapshot(seen).Contains("before"), "the first event");
        server.Drop();
        await Eventually.TrueAsync(() => server.Subscriptions == 2 && server.Open == 1, "the second stream");
        server.Publish("after");
        await Eventually.TrueAsync(() => Snapshot(seen).Contains("after"), "the event of the second stream");

        Assert.Equal(["before", "after"], Snapshot(seen));
        await stop.CancelAsync();
        await run.WaitAsync(Limit);
    }

    private static List<T> Snapshot<T>(List<T> list)
    {
        lock (list)
        {
            return [.. list];
        }
    }

    /// <summary>The fake clock, with the waits that were started on it.</summary>
    private sealed class RecordingTime : FakeTimeProvider
    {
        private readonly List<TimeSpan> _waits = [];

        public IReadOnlyList<TimeSpan> Waits
        {
            get
            {
                lock (_waits)
                {
                    return [.. _waits];
                }
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_waits)
            {
                _waits.Add(dueTime);
            }

            return base.CreateTimer(callback, state, dueTime, period);
        }
    }

    /// <summary>A server whose event streams the test publishes to, drops and ends.</summary>
    private sealed class Server : ICorePluginClientContext
    {
        private readonly object _gate = new();
        private readonly List<string> _notes = [];
        private Channel<PluginEvent>? _latest;
        private int _subscriptions;
        private int _open;

        public IReadOnlyList<string> Notes
        {
            get
            {
                lock (_gate)
                {
                    return [.. _notes];
                }
            }
        }

        public int Subscriptions => Volatile.Read(ref _subscriptions);

        /// <summary>The streams that are subscribed and not closed.</summary>
        public int Open => Volatile.Read(ref _open);

        public bool Has(string note)
        {
            lock (_gate)
            {
                return _notes.Contains(note);
            }
        }

        public void Note(string note)
        {
            lock (_gate)
            {
                _notes.Add(note);
            }
        }

        public void Publish(string topic) => Latest().Writer.TryWrite(new PluginEvent(topic, null));

        public void Drop() => Latest().Writer.TryComplete(new IOException("The connection dropped."));

        public void End() => Latest().Writer.TryComplete();

        public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) => Task.FromResult<string?>(null);

        public async IAsyncEnumerable<PluginEvent> WatchEventsAsync([EnumeratorCancellation] CancellationToken ct)
        {
            var stream = Channel.CreateUnbounded<PluginEvent>();
            lock (_gate)
            {
                _latest = stream;
                if (_subscriptions == 0)
                {
                    _notes.Add("subscribed");
                }

                _subscriptions++;
                _open++;
            }

            try
            {
                await foreach (var item in stream.Reader.ReadAllAsync(ct))
                {
                    yield return item;
                }
            }
            finally
            {
                Interlocked.Decrement(ref _open);
            }
        }

        private Channel<PluginEvent> Latest()
        {
            lock (_gate)
            {
                return _latest ?? throw new InvalidOperationException("No page watches.");
            }
        }
    }

    /// <summary>An older host or fake mode: the SDK's empty sequence of events.</summary>
    private sealed class HostWithoutEvents : ICorePluginClientContext
    {
        public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) => Task.FromResult<string?>(null);
    }
}
