using System.Diagnostics;

using Oadm.Core.Tasks;
using Oadm.Sdk.Plugins;

using Xunit.Abstractions;

namespace Oadm.Core.Tests.Perf;

/// <summary>
/// Shared numbers and helpers of the scale tests (CLAUDE.md "HARD RULE, scale to thousands of devices").
/// Tests with <c>[Trait("Category", "Perf")]</c> run with <c>manage test perf</c>, not with the fast unit run.
/// Budgets are generous (CI machines are slow) but an O(n^2) regression at these sizes exceeds them by far.
/// </summary>
internal static class PerfScale
{
    public const string Category = "Category";
    public const string Perf = "Perf";

    public const int Devices = 5_000;
    public const int Tasks = 50_000;

    /// <summary>Runs <paramref name="action"/>, writes the time to the test output and asserts the budget.</summary>
    public static async Task<TimeSpan> MeasureAsync(ITestOutputHelper output, string what, TimeSpan budget, Func<Task> action)
    {
        var watch = Stopwatch.StartNew();
        await action();
        watch.Stop();
        output.WriteLine($"{what}: {watch.Elapsed.TotalMilliseconds:F0} ms (budget {budget.TotalMilliseconds:F0} ms)");
        Assert.True(watch.Elapsed < budget, $"{what} took {watch.Elapsed.TotalMilliseconds:F0} ms, budget {budget.TotalMilliseconds:F0} ms");
        return watch.Elapsed;
    }

    public static TimeSpan Measure(ITestOutputHelper output, string what, TimeSpan budget, Action action) =>
        MeasureAsync(output, what, budget, () =>
        {
            action();
            return Task.CompletedTask;
        }).GetAwaiter().GetResult();

    /// <summary>A finished task with one device result and three steps, like a typical history entry.</summary>
    public static TaskRecord FinishedTask(int index, Guid deviceId, Guid batchId, DateTimeOffset created, TaskState state = TaskState.Done)
    {
        var started = created.AddSeconds(1);
        var finished = created.AddSeconds(5);
        return new TaskRecord(
            Guid.NewGuid(), "oadm.restart", "Restart", state, "tester@pc", created, started, finished, 100, null,
            [new TaskDeviceRecord(deviceId, state, state == TaskState.Failed ? "Connection refused" : null, 100)], batchId)
        {
            Steps =
            [
                new TaskStepInfo(0, "Restart the device", TaskStepState.Done, null, 100, started, started.AddSeconds(1)),
                new TaskStepInfo(1, "Wait for the device to come back", TaskStepState.Done, "Back after 3 s", 100, started.AddSeconds(1), finished),
                new TaskStepInfo(2, TaskStepList.CompletedStepName, TaskStepState.Done, null, 100, finished, finished),
            ],
        };
    }

    /// <summary>50,000 finished tasks over 5,000 devices (10 batches), one second apart, oldest first.</summary>
    public static List<TaskRecord> History(int count = Tasks, int devices = Devices)
    {
        var deviceIds = Enumerable.Range(0, devices).Select(_ => Guid.NewGuid()).ToArray();
        var start = DateTimeOffset.UtcNow.AddDays(-30);
        var batches = new Dictionary<int, Guid>();
        var list = new List<TaskRecord>(count);
        for (var i = 0; i < count; i++)
        {
            var batch = i / devices;
            if (!batches.TryGetValue(batch, out var batchId))
            {
                batches[batch] = batchId = Guid.NewGuid();
            }

            list.Add(FinishedTask(i, deviceIds[i % devices], batchId, start.AddSeconds(i), i % 50 == 0 ? TaskState.Failed : TaskState.Done));
        }

        return list;
    }
}
