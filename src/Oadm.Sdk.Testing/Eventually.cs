namespace Oadm.Sdk.Testing;

/// <summary>
/// Waits for something another thread does (a published event, a state, a request), instead of a fixed delay: the
/// condition is checked every 10 ms until it holds or the timeout ends. The timeout is generous (30 s by default) because
/// a busy test machine may be slow; a condition that holds returns at once, so passing tests stay fast.
/// </summary>
public static class Eventually
{
    /// <summary>The default time a condition may take before the test fails.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Waits until <paramref name="condition"/> holds; throws <see cref="TimeoutException"/> naming <paramref name="what"/>.</summary>
    public static async Task TrueAsync(Func<bool> condition, string what, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(condition);
        var until = DateTime.UtcNow + (timeout ?? DefaultTimeout);
        while (!condition())
        {
            if (DateTime.UtcNow > until)
            {
                throw new TimeoutException($"Timed out waiting for {what}.");
            }

            await Task.Delay(10).ConfigureAwait(false);
        }
    }

    /// <summary>Waits until the asynchronous <paramref name="condition"/> holds.</summary>
    public static async Task TrueAsync(Func<Task<bool>> condition, string what, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(condition);
        var until = DateTime.UtcNow + (timeout ?? DefaultTimeout);
        while (!await condition().ConfigureAwait(false))
        {
            if (DateTime.UtcNow > until)
            {
                throw new TimeoutException($"Timed out waiting for {what}.");
            }

            await Task.Delay(10).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// For "nothing more happens" checks: keeps checking <paramref name="condition"/> for <paramref name="period"/> and
    /// throws as soon as it fails. Use it after the thing that could change was given its chance (prefer waiting for a
    /// positive sign that the work ended).
    /// </summary>
    public static async Task StaysTrueAsync(Func<bool> condition, string what, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(condition);
        var until = DateTime.UtcNow + period;
        do
        {
            if (!condition())
            {
                throw new InvalidOperationException($"No longer true: {what}.");
            }

            await Task.Delay(10).ConfigureAwait(false);
        }
        while (DateTime.UtcNow < until);
    }
}
