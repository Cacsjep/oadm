namespace Oadm.Sdk.Client;

/// <summary>What a cancellation is to a <see cref="GuardedRun"/>.</summary>
public enum GuardedCancel
{
    /// <summary>A failure like every other: its text is returned.</summary>
    Report,

    /// <summary>No failure: the run ends without a text.</summary>
    Ignore,

    /// <summary>Not caught: the <see cref="OperationCanceledException"/> leaves the run.</summary>
    Throw,
}

/// <summary>
/// A server call of a page or dialog that must never crash it: whatever goes wrong comes back as the text to show
/// (<see cref="ServerErrors.Message"/>: the server's reason, else the exception's message).
/// </summary>
public static class GuardedRun
{
    /// <summary>
    /// Runs <paramref name="work"/> on the caller's context. Null when it ended, else the text of what went wrong.
    /// </summary>
    /// <param name="work">The call, with everything that belongs to it.</param>
    /// <param name="cancel">What a cancellation is: a failure with a text (the default), nothing, or thrown on.</param>
    /// <param name="ct">
    /// With <see cref="GuardedCancel.Ignore"/> or <see cref="GuardedCancel.Throw"/>: only a cancellation while this token
    /// is cancelled counts as one, any other is a failure with a text. Null: every cancellation counts.
    /// </param>
    public static async Task<string?> RunAsync(Func<Task> work, GuardedCancel cancel = GuardedCancel.Report, CancellationToken? ct = null)
    {
        ArgumentNullException.ThrowIfNull(work);
        var (_, error) = await GetAsync(
            async () =>
            {
                await work().ConfigureAwait(true);
                return true;
            },
            cancel,
            ct).ConfigureAwait(true);
        return error;
    }

    /// <summary>
    /// <see cref="RunAsync"/> for a call with an answer: the answer and no text, or the text of what went wrong (and after
    /// an ignored cancellation neither).
    /// </summary>
    /// <param name="read">The call.</param>
    /// <param name="cancel">What a cancellation is: a failure with a text (the default), nothing, or thrown on.</param>
    /// <param name="ct">The token whose cancellation counts; null: every cancellation counts.</param>
    public static async Task<(T? Value, string? Error)> GetAsync<T>(Func<Task<T>> read, GuardedCancel cancel = GuardedCancel.Report, CancellationToken? ct = null)
    {
        ArgumentNullException.ThrowIfNull(read);
        try
        {
            return (await read().ConfigureAwait(true), null);
        }
        catch (OperationCanceledException) when (cancel == GuardedCancel.Throw && IsCancelled(ct))
        {
            throw;
        }
        catch (OperationCanceledException) when (cancel == GuardedCancel.Ignore && IsCancelled(ct))
        {
            return (default, null);
        }
#pragma warning disable CA1031 // The helper's purpose: every failure of the call is a text for the page, never a crash.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return (default, ServerErrors.Message(ex));
        }
    }

    private static bool IsCancelled(CancellationToken? ct) => ct is not { } token || token.IsCancellationRequested;
}
