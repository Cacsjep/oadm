using Oadm.Sdk.Plugins;

namespace Oadm.Tests.Shared;

/// <summary>
/// Runs a task plugin against a <see cref="TaskStepList"/> exactly like the server's task engine ends it
/// (linked into the plugin test projects): success closes the list, an exception fails the current step
/// and skips the pending ones, then is rethrown.
/// </summary>
internal static class StepRun
{
    public static async Task RunAsync(TaskStepList steps, Func<Task> execute)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(execute);
        try
        {
            await execute().ConfigureAwait(false);
            steps.Close(succeeded: true, "Not run.");
        }
        catch (OperationCanceledException)
        {
            steps.FailCurrent("Cancelled.");
            steps.Close(succeeded: false, "Not run: the task was cancelled.");
            throw;
        }
        catch (Exception ex)
        {
            steps.FailCurrent(ex.Message);
            steps.Close(succeeded: false, "Not run: an earlier step failed.");
            throw;
        }
    }

    /// <summary>"Name: State" per step, for readable sequence assertions.</summary>
    public static string[] Lines(TaskStepList steps) =>
        [.. steps.Snapshot().Select(s => $"{s.Name}: {s.State}")];

    /// <summary>The detail of the named step (result, skip reason or error).</summary>
    public static string? Detail(TaskStepList steps, string name) =>
        steps.Snapshot().Single(s => s.Name == name).Detail;
}
