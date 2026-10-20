using Avalonia.Threading;

namespace Oadm.Client.Infrastructure;

/// <summary>Marshals work to the UI thread. Abstracted so view models are testable without Avalonia.</summary>
public interface IUiDispatcher
{
    void Post(Action action);

    /// <summary>
    /// Runs <paramref name="action"/> on the UI thread as a job of its own below the priority of rendering, so frames
    /// (the start splash) are drawn before it; completes when it ran. Splits a long piece of UI work into parts.
    /// </summary>
    Task RunInBackgroundAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            try
            {
                action();
                done.TrySetResult();
            }
#pragma warning disable CA1031 // handed to the caller through the task
            catch (Exception ex)
#pragma warning restore CA1031
            {
                done.TrySetException(ex);
            }
        });
        return done.Task;
    }
}

public sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    public void Post(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }

    public Task RunInBackgroundAsync(Action action) => Dispatcher.UIThread.InvokeAsync(action, DispatcherPriority.Background).GetTask();
}

/// <summary>Runs actions inline, serialized by a lock. Used by tests.</summary>
public sealed class ImmediateUiDispatcher : IUiDispatcher
{
    private readonly Lock _gate = new();

    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_gate)
        {
            action();
        }
    }
}
