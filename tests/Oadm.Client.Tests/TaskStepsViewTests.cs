using Google.Protobuf.WellKnownTypes;

using Oadm.Client.Api;
using Oadm.Client.Devices;
using Oadm.Client.Tasks;
using Oadm.Contracts.V1;

namespace Oadm.Client.Tests;

public sealed class TaskStepsViewTests
{
    private static readonly DateTime T0 = new(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);

    private static TaskStep Step(int index, string name, TaskStepState state, string detail = "", int progress = 0, double? startedSec = null, double? finishedSec = null)
    {
        var step = new TaskStep { Index = index, Name = name, State = state, Detail = detail, Progress = progress };
        if (startedSec is { } s)
        {
            step.Started = Timestamp.FromDateTime(T0.AddSeconds(s));
        }

        if (finishedSec is { } f)
        {
            step.Finished = Timestamp.FromDateTime(T0.AddSeconds(f));
        }

        return step;
    }

    private static TaskInfo Firmware(int currentStep, params TaskStep[] steps)
    {
        var task = new TaskInfo
        {
            Id = "t1",
            Name = "Upgrade AXIS OS",
            State = TaskState.Running,
            DeviceId = "d1",
            Progress = 41,
            CurrentStepIndex = currentStep,
        };
        task.Devices.Add(new TaskDeviceResult { DeviceId = "d1", State = TaskState.Running, Message = "Upload firmware - 37 of 82 MB" });
        task.Steps.AddRange(steps);
        return task;
    }

    [Fact]
    public void TheTasksPaneShowsTheCurrentStepWithItsProgress()
    {
        var row = new TaskRowViewModel(Firmware(
            3,
            Step(0, "Check compatibility", TaskStepState.Done),
            Step(1, "Read firmware status", TaskStepState.Done),
            Step(2, "Validate file", TaskStepState.Done),
            Step(3, "Upload firmware", TaskStepState.Running, "37 of 82 MB", 45),
            Step(4, "Install", TaskStepState.Pending),
            Step(5, "Verify version", TaskStepState.Pending)), new DeviceStore());

        Assert.Equal("Step 4/6 · Upload firmware · 45 %", row.CurrentStepText);
        Assert.Equal("Step 4/6 \u00B7 Upload firmware \u00B7 45 %\n37 of 82 MB", row.CurrentStepTooltip);
        Assert.Equal(3, row.CurrentStepIndex);

        // A failed task keeps pointing at the failed step, without a percentage.
        var failed = Firmware(1, Step(0, "Check compatibility", TaskStepState.Done), Step(1, "Read firmware status", TaskStepState.Failed, "Access denied"), Step(2, "Install", TaskStepState.Skipped));
        failed.State = TaskState.Failed;
        row.Update(failed);
        Assert.Equal("Step 2/3 · Read firmware status", row.CurrentStepText);
        Assert.Equal("Step 2/3 \u00B7 Read firmware status\nAccess denied", row.CurrentStepTooltip);

        // A successful task ends with the server's "Completed" step, which becomes the current step.
        var done = Firmware(
            3,
            Step(0, "Check compatibility", TaskStepState.Done, startedSec: 0, finishedSec: 1),
            Step(1, "Upload firmware", TaskStepState.Done, startedSec: 1, finishedSec: 60),
            Step(2, "Commit firmware", TaskStepState.Done, "AXIS OS 12.11.77", 100, 60, 61),
            Step(3, "Completed", TaskStepState.Done, progress: 100, startedSec: 61, finishedSec: 61));
        done.State = TaskState.Done;
        row.Update(done);
        Assert.Equal("Completed", row.CurrentStepText);
        Assert.Equal("Completed", row.CurrentStepTooltip);
        Assert.Equal(3, row.CurrentStepIndex);

        // Plugins without steps (and older servers) leave the column empty.
        row.Update(new TaskInfo { Id = "t1", Name = "x", State = TaskState.Done });
        Assert.Equal("", row.CurrentStepText);
        Assert.Null(row.CurrentStepTooltip);
        Assert.Equal(-1, row.CurrentStepIndex);
        Assert.Equal("3 steps planned", TaskRowViewModel.FormatCurrentStep(null, 3));
    }

    [Fact]
    public void DetailsListTheStepsAndFollowTheTaskLive()
    {
        var devices = new DeviceStore();
        var row = new TaskRowViewModel(Firmware(0, Step(0, "Check compatibility", TaskStepState.Running, startedSec: 0), Step(1, "Upload firmware", TaskStepState.Pending)), devices);
        var time = new FixedTime(T0.AddSeconds(3));
        using var details = new TaskDetailsViewModel(row, devices, time);

        Assert.Equal(2, details.Steps.Count);
        Assert.Equal("0 of 2 steps finished", details.StepsStatus);
        TaskStepRowViewModel first = details.Steps[0];
        Assert.Equal(("1", "Check compatibility", "Running", true, true), (first.Number, first.Name, first.StateText, first.IsStateAccent, first.IsRunning));
        Assert.Equal("3.0 s", first.DurationText);
        Assert.Equal("", details.Steps[1].DurationText);

        row.Update(Firmware(
            1,
            Step(0, "Check compatibility", TaskStepState.Done, "fwmgr 1.4", 100, 0, 1.5),
            Step(1, "Upload firmware", TaskStepState.Running, "37 of 82 MB", 45, 1.5),
            Step(2, "Install", TaskStepState.Pending)));

        Assert.Same(first, details.Steps[0]); // updated in place
        Assert.Equal(3, details.Steps.Count);
        Assert.Equal(("Done", true, false, "fwmgr 1.4", "1.5 s"), (first.StateText, first.IsStateOk, first.IsRunning, first.Detail, first.DurationText));
        Assert.Equal((45, "45 %", true), (details.Steps[1].Progress, details.Steps[1].ProgressText, details.Steps[1].IsRunning));
        Assert.Equal("1 of 3 steps finished", details.StepsStatus);

        TaskInfo failed = Firmware(1, Step(0, "Check compatibility", TaskStepState.Done, "", 100, 0, 1.5), Step(1, "Upload firmware", TaskStepState.Failed, "Upload rejected", 45, 1.5, 130));
        failed.State = TaskState.Failed;
        failed.Devices[0].State = TaskState.Failed;
        row.Update(failed);
        Assert.Equal(2, details.Steps.Count);
        Assert.Equal(("Failed", true, "2 min 08 s"), (details.Steps[1].StateText, details.Steps[1].IsStateError, details.Steps[1].DurationText));
        Assert.Contains(details.Rows, r => r.StateText == "Failed");
        Assert.Equal("Upgrade AXIS OS - Failed", details.Title);

        details.Dispose();
        row.Update(Firmware(0, Step(0, "Other", TaskStepState.Running)));
        Assert.Equal("Check compatibility", details.Steps[0].Name); // no longer following
    }

    [Fact]
    public void StepStatesMapToTheSharedChipKinds()
    {
        Assert.Equal(PillKind.Neutral, TaskStepRowViewModel.ToKind(TaskStepState.Pending));
        Assert.Equal(PillKind.Accent, TaskStepRowViewModel.ToKind(TaskStepState.Running));
        Assert.Equal(PillKind.Ok, TaskStepRowViewModel.ToKind(TaskStepState.Done));
        Assert.Equal(PillKind.Warning, TaskStepRowViewModel.ToKind(TaskStepState.Warning));
        Assert.Equal(PillKind.Neutral, TaskStepRowViewModel.ToKind(TaskStepState.Skipped));
        Assert.Equal(PillKind.Error, TaskStepRowViewModel.ToKind(TaskStepState.Failed));
        Assert.Equal("12 s", TaskStepRowViewModel.FormatDuration(TimeSpan.FromSeconds(12.7)));
        Assert.Equal("1 h 03 min", TaskStepRowViewModel.FormatDuration(TimeSpan.FromMinutes(63)));
        Assert.Equal("0.0 s", TaskStepRowViewModel.FormatDuration(TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public async Task TheFakeServerSimulatesMultiStepTasks()
    {
        using var seeded = new FakeOadmApi();
        IReadOnlyList<TaskInfo> tasks = await seeded.ListTasksAsync(CancellationToken.None);

        TaskInfo firmware = tasks.Single(t => t.PluginId == "oadm.firmware");
        Assert.Equal("Upload firmware", firmware.Steps[firmware.CurrentStepIndex].Name);
        Assert.EndsWith("of 82 MB", firmware.Steps[firmware.CurrentStepIndex].Detail, StringComparison.Ordinal);
        TaskInfo failed = tasks.Single(t => t.State == TaskState.Failed);
        Assert.Equal(
            ["Done", "Done", "Done", "Failed", "Skipped"],
            failed.Steps.Select(s => TaskStepRowViewModel.ToText(s.State)).ToArray());

        using var api = new FakeOadmApi(tick: TimeSpan.FromMilliseconds(5), seedSampleData: false);
        string id = (await api.RunTaskAsync("oadm.restart", [Guid.NewGuid().ToString()], null, "tester", CancellationToken.None)).Single();
        TaskInfo? done = null;
        for (int i = 0; i < 400 && done is null; i++)
        {
            await Task.Delay(10);
            done = (await api.ListTasksAsync(CancellationToken.None)).SingleOrDefault(t => t.Id == id && t.State == TaskState.Done);
        }

        Assert.NotNull(done);
        Assert.Equal("Restart device", done.Name);
        Assert.Equal(
            ["Check device", "Send restart", "Wait for the device to go offline", "Wait for the device to come back", "Verify device", "Completed"],
            done.Steps.Select(s => s.Name).ToArray());
        Assert.All(done.Steps, s => Assert.Equal(TaskStepState.Done, s.State));
        Assert.Equal(done.Steps.Count - 1, done.CurrentStepIndex);
        Assert.Equal("Completed", TaskRowViewModel.FormatCurrentStep(done.Steps[done.CurrentStepIndex], done.Steps.Count));
    }

    private sealed class FixedTime(DateTime utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utc);
    }
}
