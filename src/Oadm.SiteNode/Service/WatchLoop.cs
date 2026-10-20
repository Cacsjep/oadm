using Oadm.Contracts.SiteNode;
using Oadm.SiteNode.Feed;

namespace Oadm.SiteNode.Service;

/// <summary>
/// The sending side of <c>Watch</c>: the status at once, then after every change; a change of the connection counts
/// alone at most every <see cref="SiteNodeGrpcService.CountsInterval"/>. A Reset is checked right before every send, so
/// one announced while the counts wait still goes out as the reset notice (the server shows the site not bound, never
/// offline). Ends when the call is cancelled.
/// </summary>
internal static class WatchLoop
{
    public static async Task RunAsync(Wakeup changed, Func<bool> takeOtherChange, Func<NodeStatus> status, ResetNotice resetNotice,
        NodeOptions options, Func<NodeStatus, CancellationToken, Task> write, CancellationToken ct)
    {
        try
        {
            var sentAt = options.Time.GetTimestamp();
            await write(status(), ct).ConfigureAwait(false);
            while (!ct.IsCancellationRequested)
            {
                await changed.WaitAsync(ct).ConfigureAwait(false);
                if (!resetNotice.Pending && !takeOtherChange())
                {
                    // Only connection counts changed: at most once a second.
                    var wait = SiteNodeGrpcService.CountsInterval - options.Time.GetElapsedTime(sentAt);
                    if (wait > TimeSpan.Zero)
                        await Task.Delay(wait, options.Time, ct).ConfigureAwait(false);
                }

                changed.Drain();
                if (resetNotice.Pending)
                {
                    // Reset: the last message; the server marks the site not bound and ends this call.
                    await write(new NodeStatus { State = NodeState.UnboundOpen, Reset = true, Version = options.Version }, ct).ConfigureAwait(false);
                    await Task.Delay(Timeout.InfiniteTimeSpan, options.Time, ct).ConfigureAwait(false);
                }

                sentAt = options.Time.GetTimestamp();
                await write(status(), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }
}
