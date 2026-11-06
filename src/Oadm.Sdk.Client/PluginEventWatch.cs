using Oadm.Sdk.Plugins;

namespace Oadm.Sdk.Client;

/// <summary>
/// How one page watches its live events (<see cref="PluginEventWatch"/>): the waits and where the time comes from. A page
/// keeps one as a property, so a test passes its own (a short wait or a fake time) instead of changing a static.
/// </summary>
public sealed class PluginEventWatchOptions
{
    /// <summary>Wait before reading and watching again after the stream ended or broke.</summary>
    public TimeSpan RetryDelay { get; init; } = PluginEventWatch.DefaultRetryDelay;

    /// <summary>
    /// Null: every wait is <see cref="RetryDelay"/>. Else the wait doubles after each round up to this, and starts again at
    /// <see cref="RetryDelay"/> once a round's read worked (without a read: once an event arrived).
    /// </summary>
    public TimeSpan? MaxRetryDelay { get; init; }

    /// <summary>
    /// A read that failed, an event that could not be applied or a stream that broke (never the cancellation that ends
    /// the watch), for a page that shows the reason; the watch goes on after the wait. Called on the context of the
    /// watch, usually the UI thread.
    /// </summary>
    public Action<Exception>? Failed { get; init; }

    /// <summary>The clock of the waits; tests pass a fake one.</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;
}

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
    public static Task RunAsync(this ICorePluginClientContext ctx, Action<PluginEvent> handle, TimeSpan? retryDelay, CancellationToken ct) =>
        ctx.RunAsync(null, handle, new PluginEventWatchOptions { RetryDelay = retryDelay ?? DefaultRetryDelay }, ct);

    /// <summary>
    /// Keeps a page in step with the server: subscribes to the events first, then runs <paramref name="read"/> (the page's
    /// full state, so nothing published during the read is lost), then hands every event to <paramref name="handle"/>. When
    /// the read fails or the stream ends or breaks, it waits (<see cref="PluginEventWatchOptions.RetryDelay"/>) and does all
    /// of it again, so the page reads again after every reconnect; a host without events is read at that pace. Runs on the
    /// calling thread's context (usually the UI thread) until <paramref name="ct"/> is cancelled: cancel it when the page is
    /// hidden or disposed. Never throws (cancellation ends it).
    /// </summary>
    /// <param name="ctx">The page's context.</param>
    /// <param name="read">Reads the page's state; null for a page that only applies events.</param>
    /// <param name="handle">Applies one event.</param>
    /// <param name="options">The waits and the clock; null: 2 s on the system clock.</param>
    /// <param name="ct">Ends the watch.</param>
    public static Task RunAsync(this ICorePluginClientContext ctx, Func<CancellationToken, Task>? read, Action<PluginEvent> handle, PluginEventWatchOptions? options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(handle);
        return WatchAsync(
            ctx,
            read,
            item =>
            {
                handle(item);
                return Task.CompletedTask;
            },
            options ?? new PluginEventWatchOptions(),
            ct);
    }

    /// <summary>
    /// <see cref="RunAsync(ICorePluginClientContext, Func{CancellationToken, Task}?, Action{PluginEvent}, PluginEventWatchOptions?, CancellationToken)"/>
    /// for a page whose events need a call of their own: the next event waits until <paramref name="handle"/> ended.
    /// </summary>
    public static Task RunAsync(this ICorePluginClientContext ctx, Func<CancellationToken, Task>? read, Func<PluginEvent, Task> handle, PluginEventWatchOptions? options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(handle);
        return WatchAsync(ctx, read, handle, options ?? new PluginEventWatchOptions(), ct);
    }

    private static async Task WatchAsync(ICorePluginClientContext ctx, Func<CancellationToken, Task>? read, Func<PluginEvent, Task> handle, PluginEventWatchOptions options, CancellationToken ct)
    {
        var delay = options.RetryDelay;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // The round's own token: a failed read ends the subscription that is still waiting for its first event.
                using var round = CancellationTokenSource.CreateLinkedTokenSource(ct);
                IAsyncEnumerator<PluginEvent>? events = null;
                Task<bool>? next = null;
                try
                {
                    events = ctx.WatchEventsAsync(round.Token).GetAsyncEnumerator(round.Token);
                    next = events.MoveNextAsync().AsTask(); // subscribe first: nothing published during the read is lost
                    if (read is not null)
                    {
                        await read(ct).ConfigureAwait(true);
                        delay = options.RetryDelay;
                    }

                    while (await next.ConfigureAwait(true))
                    {
                        if (read is null)
                        {
                            delay = options.RetryDelay;
                        }

                        await handle(events.Current).ConfigureAwait(true);
                        next = events.MoveNextAsync().AsTask();
                    }
                }
#pragma warning disable CA1031 // Whatever the cancelled read or stream threw (a server call: the channel's error): the watch ends.
                catch (Exception) when (ct.IsCancellationRequested)
#pragma warning restore CA1031
                {
                    return;
                }
#pragma warning disable CA1031 // The read failed or the connection dropped: read and watch again after the wait.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    options.Failed?.Invoke(ex);
                }
                finally
                {
                    await CloseAsync(round, events, next).ConfigureAwait(true);
                }

                await Task.Delay(delay, options.Time, ct).ConfigureAwait(true);
                if (options.MaxRetryDelay is { } max)
                {
                    delay = delay + delay < max ? delay + delay : max;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // page hidden
        }
    }

    /// <summary>Ends a round's subscription: a move that still waits is cancelled first (a stream cannot close during one).</summary>
    private static async Task CloseAsync(CancellationTokenSource round, IAsyncEnumerator<PluginEvent>? events, Task<bool>? next)
    {
        try
        {
            await round.CancelAsync().ConfigureAwait(true);
            if (next is not null)
            {
                await next.ConfigureAwait(true);
            }
        }
#pragma warning disable CA1031 // The cancelled or broken move: the round is over either way.
        catch (Exception)
#pragma warning restore CA1031
        {
        }

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
}
