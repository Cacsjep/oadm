namespace Oadm.Plugins.Vision.Pipelines;

/// <summary>
/// Runs work one at a time in the order it was handed over: the frames of a pipeline through its evaluator (which takes one
/// frame at a time, in order), with its counter resets between them. A failed piece never stops the ones after it.
/// </summary>
internal sealed class SerialWork
{
    private readonly Lock _gate = new();
    private Task _tail = Task.CompletedTask;

    /// <summary>Runs <paramref name="work"/> after everything handed over before it.</summary>
    public Task<T> RunAsync<T>(Func<Task<T>> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        lock (_gate)
        {
            var next = AfterAsync(_tail, work);
            _tail = next;
            return next;
        }
    }

    /// <summary>Runs <paramref name="work"/> after everything handed over before it.</summary>
    public Task RunAsync(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        return RunAsync(() =>
        {
            work();
            return Task.FromResult(true);
        });
    }

    /// <summary>Ends when everything handed over so far ended.</summary>
    public Task IdleAsync()
    {
        lock (_gate)
        {
            return _tail.ContinueWith(static _ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private static async Task<T> AfterAsync<T>(Task before, Func<Task<T>> work)
    {
        try
        {
            await before.ConfigureAwait(false);
        }
#pragma warning disable CA1031 // The one before failed for its own caller; this one runs anyway.
        catch (Exception)
#pragma warning restore CA1031
        {
        }

        return await work().ConfigureAwait(false);
    }
}
