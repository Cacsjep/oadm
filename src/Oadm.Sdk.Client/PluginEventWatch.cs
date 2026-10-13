using Oadm.Sdk.Plugins;

namespace Oadm.Sdk.Client;

/// <summary>The live events of a plugin page, watched again after the connection drops.</summary>
public static class PluginEventWatch
{
    /// <summary>Wait before watching again after the stream broke.</summary>
    public static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Hands every event of <see cref="ICorePluginClientContext.WatchEventsAsync"/> to <paramref name="handle"/> (on the
    /// calling thread's context, usually the UI thread) until <paramref name="ct"/> is cancelled; a broken stream is watched
    /// again after <paramref name="retryDelay"/>. Never throws (cancellation ends it).
    /// </summary>
    public static async Task RunAsync(this ICorePluginClientContext ctx, Action<PluginEvent> handle, TimeSpan? retryDelay, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(handle);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                IAsyncEnumerator<PluginEvent>? events = null;
                try
                {
                    events = ctx.WatchEventsAsync(ct).GetAsyncEnumerator(ct);
                    while (await events.MoveNextAsync().ConfigureAwait(true))
                    {
                        handle(events.Current);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
#pragma warning disable CA1031 // The connection dropped: watch again after a short delay.
                catch (Exception)
#pragma warning restore CA1031
                {
                }
                finally
                {
                    if (events is not null)
                    {
                        try
                        {
                            await events.DisposeAsync().ConfigureAwait(true);
                        }
#pragma warning disable CA1031 // A broken stream may also fail to close; the next watch starts anyway.
                        catch (Exception)
#pragma warning restore CA1031
                        {
                        }
                    }
                }

                await Task.Delay(retryDelay ?? DefaultRetryDelay, ct).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
            // page hidden
        }
    }
}
