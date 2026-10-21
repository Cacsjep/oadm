namespace Oadm.SiteNode.Feed;

/// <summary>
/// Reset tells the bound server first (CLAUDE.md "# Sites", Binding 7): every open <c>Watch</c> of the server sends a last
/// <c>NodeStatus</c> with <c>reset</c>, the server marks the site not bound and ends the session. <see cref="AnnounceAsync"/>
/// waits until the server ended every Watch or the wait limit passed; without a Watch it returns at once.
/// </summary>
public sealed class ResetNotice(TimeProvider time, TimeSpan waitLimit)
{
    /// <summary>How long Reset waits for the server before it resets anyway.</summary>
    public static readonly TimeSpan DefaultWaitLimit = TimeSpan.FromSeconds(3);

    private readonly Lock _lock = new();
    private int _watches;
    private TaskCompletionSource? _announced;

    /// <summary>Raised when a Reset is announced: the open Watch calls send the notice.</summary>
    public event Action? Requested;

    /// <summary>True while a Reset waits for the server.</summary>
    public bool Pending
    {
        get
        {
            lock (_lock)
                return _announced is not null;
        }
    }

    /// <summary>A Watch of the bound server begins; dispose it when the call ends.</summary>
    public IDisposable Enter()
    {
        lock (_lock)
            _watches++;
        return new Watch(this);
    }

    /// <summary>True when the server ended every Watch after the notice; false without a Watch or after the wait limit.</summary>
    public async Task<bool> AnnounceAsync(CancellationToken ct)
    {
        TaskCompletionSource announced;
        lock (_lock)
        {
            if (_watches == 0)
                return false;
            announced = _announced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        try
        {
            NodeEvents.Raise(Requested);
            await announced.Task.WaitAsync(waitLimit, time, ct).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        finally
        {
            lock (_lock)
                _announced = null;
        }
    }

    private void Leave()
    {
        lock (_lock)
        {
            _watches--;
            if (_watches == 0)
                _announced?.TrySetResult();
        }
    }

    private sealed class Watch(ResetNotice notice) : IDisposable
    {
        private int _left;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _left, 1) == 0)
                notice.Leave();
        }
    }
}
