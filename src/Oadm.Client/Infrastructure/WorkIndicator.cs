using CommunityToolkit.Mvvm.ComponentModel;

namespace Oadm.Client.Infrastructure;

/// <summary>
/// The progress row of a page for an action on many devices (remove, start tasks, delete all, export): shown when the action
/// still runs after <see cref="ShowAfter"/>, so a quick one does not flash it, and hidden when it ends (CLAUDE.md, loading
/// feedback: anything that can take longer than 1 s at 30,000 devices shows that it is working). UI thread only.
/// </summary>
public sealed partial class WorkIndicator : ObservableObject
{
    private int _running;

    /// <summary>Time before the row shows (tests set zero: at once).</summary>
    public TimeSpan ShowAfter { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>The row is shown.</summary>
    [ObservableProperty]
    public partial bool IsActive { get; private set; }

    /// <summary>What runs, e.g. "Removing 30,000 devices".</summary>
    [ObservableProperty]
    public partial string Text { get; private set; } = string.Empty;

    /// <summary>Runs <paramref name="work"/> with the row shown while it lasts.</summary>
    public async Task<T> RunAsync<T>(string text, Func<Task<T>> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        var id = ++_running;
        var task = work();
        if (!task.IsCompleted)
        {
            if (ShowAfter > TimeSpan.Zero)
            {
                await Task.WhenAny(task, Task.Delay(ShowAfter)).ConfigureAwait(true);
            }

            if (!task.IsCompleted && id == _running)
            {
                Text = text;
                IsActive = true;
            }
        }

        try
        {
            return await task.ConfigureAwait(true);
        }
        finally
        {
            if (id == _running)
            {
                IsActive = false;
            }
        }
    }

    /// <summary>Runs <paramref name="work"/> with the row shown while it lasts.</summary>
    public Task RunAsync(string text, Func<Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        return RunAsync(text, async () =>
        {
            await work().ConfigureAwait(true);
            return true;
        });
    }
}
