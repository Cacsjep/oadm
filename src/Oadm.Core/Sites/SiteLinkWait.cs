namespace Oadm.Core.Sites;

/// <summary>
/// Tells a running task that one of its requests waits for the site link (a free large-transfer slot of the site,
/// docs/spec/sites.md, Large transfers). The task engine sets a reporter for the flow of each task device; the VAPIX client
/// calls it when it starts and ends such a wait. Outside a task there is no reporter: such a wait counts inside the
/// request's timeout.
/// </summary>
public static class SiteLinkWait
{
    /// <summary>The text the task shows while it waits (Wording: one phrase per concept).</summary>
    public const string Text = "Waiting for the site link";

    private static readonly AsyncLocal<Action<bool>?> CurrentReporter = new();

    /// <summary>The reporter of the current flow; null outside a task.</summary>
    public static Action<bool>? Current => CurrentReporter.Value;

    /// <summary>Sets <paramref name="reporter"/> (true = waiting, false = the wait ended) for the current flow until disposed.</summary>
    public static IDisposable Enter(Action<bool> reporter)
    {
        ArgumentNullException.ThrowIfNull(reporter);
        var previous = CurrentReporter.Value;
        CurrentReporter.Value = reporter;
        return new Scope(previous);
    }

    /// <summary>Calls the reporter and never lets it fail the request.</summary>
    internal static void Report(Action<bool>? reporter, bool waiting)
    {
        if (reporter is null)
        {
            return;
        }

        try
        {
            reporter(waiting);
        }
#pragma warning disable CA1031 // Showing the wait is information only.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    private sealed class Scope(Action<bool>? previous) : IDisposable
    {
        public void Dispose() => CurrentReporter.Value = previous;
    }
}
