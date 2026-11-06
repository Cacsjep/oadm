namespace Oadm.Sdk.Client;

/// <summary>What a cancellation is to a <see cref="GuardedRun"/>.</summary>
public enum GuardedCancel
{
    /// <summary>A failure like every other: its text is returned.</summary>
    Report,

    /// <summary>No failure: the run ends without a text.</summary>
    Ignore,

    /// <summary>Not caught: an <see cref="OperationCanceledException"/> leaves the run.</summary>
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
    /// <param name="cancel">What a cancellation is: a failure with a text (the default), nothing, or an exception that leaves the run.</param>
    /// <param name="ct">
    /// With <see cref="GuardedCancel.Ignore"/> or <see cref="GuardedCancel.Throw"/>: whatever the call throws while this
    /// token is cancelled counts as the cancellation, a cancellation at any other time is a failure with a text. Null:
    /// every cancellation counts, also a server call that was cancelled.
    /// </param>
    public static async Task<string?> RunAsync(Func<Task> work, GuardedCancel cancel = GuardedCancel.Report, CancellationToken? ct = null)
    {
        ArgumentNullException.ThrowIfNull(work);
        try
        {
            await work().ConfigureAwait(true);
            return null;
        }
#pragma warning disable CA1031 // A cancelled server call throws the channel's own error, not an OperationCanceledException.
        catch (Exception ex) when (cancel != GuardedCancel.Report && IsCancellation(ex, ct))
#pragma warning restore CA1031
        {
            if (cancel == GuardedCancel.Ignore)
            {
                return null;
            }

            if (ex is OperationCanceledException)
            {
                throw;
            }

            throw new OperationCanceledException(ex.Message, ex, ct ?? CancellationToken.None);
        }
#pragma warning disable CA1031 // The helper's purpose: every failure of the call is a text for the page, never a crash.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return ServerErrors.Message(ex);
        }
    }

    /// <summary>
    /// With a token: whatever the call threw while the token is cancelled (a cancelled server call throws the channel's
    /// error, not an <see cref="OperationCanceledException"/>). Without: every cancellation, the channel's too.
    /// </summary>
    private static bool IsCancellation(Exception ex, CancellationToken? ct) =>
        ct is { } token ? token.IsCancellationRequested : ServerErrors.IsCancellation(ex);
}
