using Avalonia.Threading;

namespace Oadm.Client.Infrastructure;

/// <summary>Marshals work to the UI thread. Abstracted so view models are testable without Avalonia.</summary>
public interface IUiDispatcher
{
    void Post(Action action);
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
