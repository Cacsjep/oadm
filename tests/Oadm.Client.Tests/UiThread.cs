using System.Runtime.ExceptionServices;

using Oadm.Client.Infrastructure;

namespace Oadm.Client.Tests;

/// <summary>
/// A stand-in for the Avalonia UI thread in view model tests: one dedicated thread with its own
/// <see cref="SynchronizationContext"/> and a work queue. In the client a view model, its posted updates and the
/// continuations of its awaits all run on the one UI thread. With <see cref="ImmediateUiDispatcher"/> and no
/// synchronization context, those continuations run on thread-pool threads and race with the test thread (e.g. a
/// collection changed while the test enumerates it). Build the view model with this dispatcher and run the test body
/// with <see cref="RunAsync(Func{Task})"/>: everything then runs on this thread, one item after another.
/// Never block on this thread (<c>.Result</c>, <c>Wait()</c>) on work that needs it; await instead.
/// </summary>
internal sealed class UiThread : IUiDispatcher, IDisposable, IAsyncDisposable
{
    private readonly Queue<(SendOrPostCallback Callback, object? State)> _queue = new();
    private readonly object _gate = new(); // Monitor.Wait / Pulse need a monitor, not a Lock
    private readonly Thread _thread;
    private readonly List<Exception> _errors = [];
    private bool _ended;

    public UiThread()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "Test UI thread" };
        _thread.Start();
    }

    /// <summary>True on this thread.</summary>
    public bool CheckAccess() => Thread.CurrentThread == _thread;

    /// <summary>Like <c>AvaloniaUiDispatcher</c>: inline on this thread, else queued to it.</summary>
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (CheckAccess())
        {
            action();
        }
        else
        {
            Enqueue(_ => action(), null);
        }
    }

    /// <summary>Runs an async test body on this thread; its awaits continue here. Exceptions reach the caller.</summary>
    public Task RunAsync(Func<Task> body) => RunAsync(async () =>
    {
        await body().ConfigureAwait(true);
        return true;
    });

    /// <inheritdoc cref="RunAsync(Func{Task})"/>
    public async Task<T> RunAsync<T>(Func<Task<T>> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(_ => _ = RunBodyAsync(body, done), null);
        T result = await done.Task.ConfigureAwait(false);
        ThrowPostedErrors();
        return result;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_ended)
            {
                return;
            }

            _ended = true;
            Monitor.PulseAll(_gate);
        }

        if (!CheckAccess())
        {
            _thread.Join(TimeSpan.FromSeconds(10));
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private static async Task RunBodyAsync<T>(Func<Task<T>> body, TaskCompletionSource<T> done)
    {
        try
        {
            done.TrySetResult(await body().ConfigureAwait(true));
        }
        catch (Exception ex)
        {
            done.TrySetException(ex);
        }
    }

    private void Enqueue(SendOrPostCallback callback, object? state)
    {
        lock (_gate)
        {
            if (_ended)
            {
                return; // like a closed app: work posted after the end is dropped
            }

            _queue.Enqueue((callback, state));
            Monitor.Pulse(_gate);
        }
    }

    private void ThrowPostedErrors()
    {
        Exception? first;
        lock (_gate)
        {
            first = _errors.Count == 0 ? null : _errors[0];
            _errors.Clear();
        }

        if (first is not null)
        {
            ExceptionDispatchInfo.Capture(first).Throw();
        }
    }

    private void Loop()
    {
        SynchronizationContext.SetSynchronizationContext(new Context(this));
        while (true)
        {
            (SendOrPostCallback Callback, object? State) item;
            lock (_gate)
            {
                while (_queue.Count == 0 && !_ended)
                {
                    Monitor.Wait(_gate);
                }

                if (_ended)
                {
                    return;
                }

                item = _queue.Dequeue();
            }

            try
            {
                item.Callback(item.State);
            }
            catch (Exception ex)
            {
                // A posted action that throws: on the real UI thread the crash handler shows it; here the test fails.
                lock (_gate)
                {
                    _errors.Add(ex);
                }
            }
        }
    }

    private sealed class Context(UiThread owner) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => owner.Enqueue(d, state);

        public override void Send(SendOrPostCallback d, object? state)
        {
            if (owner.CheckAccess())
            {
                d(state);
                return;
            }

            throw new NotSupportedException("Send to the test UI thread would block; post instead.");
        }

        public override SynchronizationContext CreateCopy() => this;
    }
}
