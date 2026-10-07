using Microsoft.Extensions.Time.Testing;

using Oadm.Sdk.Plugins;

namespace Oadm.Core.Tests.Tasks;

public sealed class TaskStepListTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));

    private static string Shape(TaskStepList steps) =>
        string.Join(", ", steps.Snapshot().Select(s => $"{s.Name}:{s.State}"));

    [Fact]
    public void PlannedStepsArePendingAndActivatedByName()
    {
        var steps = new TaskStepList(_time);
        steps.Plan(["Check device", "Send restart", "Verify device"]);
        Assert.Equal("Check device:Pending, Send restart:Pending, Verify device:Pending", Shape(steps));
        Assert.Equal(-1, steps.CurrentIndex);
        Assert.Equal(0, steps.Progress);

        using (var step = steps.Begin("Check device"))
        {
            Assert.Equal(TaskStepState.Running, step.State);
            Assert.Equal(0, steps.CurrentIndex);
            _time.Advance(TimeSpan.FromSeconds(2));
        }

        var first = steps.Snapshot()[0];
        Assert.Equal(TaskStepState.Done, first.State);
        Assert.Equal(100, first.Progress);
        Assert.Equal(TimeSpan.FromSeconds(2), first.FinishedUtc - first.StartedUtc);
        Assert.Equal(33, steps.Progress);
    }

    [Fact]
    public void UnplannedStepsAreInsertedBeforeThePendingOnesAndOutOfOrderStepsMoveUp()
    {
        var steps = new TaskStepList(_time);
        steps.Plan(["A", "B", "C"]);
        steps.Begin("A").Complete();
        steps.Begin("Extra").Complete();
        steps.Begin("C").Skip("not needed");
        steps.Begin("B").Dispose();

        Assert.Equal("A:Done, Extra:Done, C:Skipped, B:Done", Shape(steps));
        Assert.Equal([0, 1, 2, 3], steps.Snapshot().Select(s => s.Index));
        Assert.Equal("not needed", steps.Snapshot()[2].Detail);
    }

    [Fact]
    public void BeginningTheNextStepCompletesTheRunningOne()
    {
        var steps = new TaskStepList(_time);
        var a = steps.Begin("A");
        var b = steps.Begin("B");
        Assert.Equal(TaskStepState.Done, a.State);
        Assert.Equal(TaskStepState.Running, b.State);
        Assert.Equal(1, steps.CurrentIndex);

        // Ending an already ended step changes nothing.
        a.Fail("too late");
        Assert.Equal("A:Done, B:Running", Shape(steps));
        Assert.Null(steps.Snapshot()[0].Detail);
    }

    [Fact]
    public void AnExceptionEscapingAUsingBlockFailsTheStepItEnded()
    {
        var steps = new TaskStepList(_time);
        steps.Plan(["Send restart", "Wait"]);
        try
        {
            using var step = steps.Begin("Send restart");
            throw new InvalidOperationException("Connection refused");
        }
        catch (InvalidOperationException ex)
        {
            // What the host does with an exception escaping the plugin.
            Assert.True(steps.FailCurrent(ex.Message));
        }

        steps.Close(succeeded: false, "Not run: an earlier step failed.");
        Assert.Equal("Send restart:Failed, Wait:Skipped", Shape(steps));
        Assert.Equal("Connection refused", steps.Snapshot()[0].Detail);
        Assert.Equal("Not run: an earlier step failed.", steps.Snapshot()[1].Detail);
        Assert.Equal(0, steps.CurrentIndex); // the failed step stays the current one
    }

    [Fact]
    public void AnExplicitlyCompletedStepIsNotFailedByALaterException()
    {
        var steps = new TaskStepList(_time);
        using (var step = steps.Begin("A"))
        {
            step.Complete("ok");
        }

        Assert.False(steps.FailCurrent("boom"));
        Assert.Equal("A:Done", Shape(steps));
        Assert.Equal("ok", steps.Snapshot()[0].Detail);
    }

    [Fact]
    public void WarnEndsTheStepAndReportsATaskWarning()
    {
        var warnings = new List<string>();
        var steps = new TaskStepList(_time, warnings.Add);
        steps.Begin("Verify").Warn("Version differs");
        Assert.Equal("Verify:Warning", Shape(steps));
        Assert.Equal(["Version differs"], warnings);
        Assert.Equal(100, steps.Progress);
    }

    [Fact]
    public void ProgressIsDerivedFromStepsWithTheRunningStepsOwnProgress()
    {
        var steps = new TaskStepList(_time);
        steps.Plan(["A", "B", "C", "D"]);
        steps.Begin("A").Complete();
        var b = steps.Begin("B");
        b.ReportProgress(50, "40 of 80 MB");
        Assert.Equal(37, steps.Progress); // (1 + 0.5) / 4
        Assert.Equal("40 of 80 MB", steps.Snapshot()[1].Detail);
        b.ReportProgress(250);
        Assert.Equal(100, steps.Snapshot()[1].Progress);
        Assert.Null(new TaskStepList().Progress);
    }

    [Fact]
    public void CloseAfterAFailureAppendsNoCompletedStep()
    {
        var steps = new TaskStepList(_time);
        steps.Plan(["A", "B"]);
        steps.Begin("A");
        steps.FailCurrent("Refused");
        steps.Close(succeeded: false, "Not run: an earlier step failed.");

        Assert.Equal("A:Failed, B:Skipped", Shape(steps));
        Assert.Equal(0, steps.CurrentIndex);
    }

    [Fact]
    public void TheCompletedStepIsAddedEvenWhenTheListIsFull()
    {
        var steps = new TaskStepList(_time);
        for (var i = 0; i < TaskStepList.MaxSteps; i++)
        {
            steps.Begin("S" + i).Complete();
        }

        steps.Close(succeeded: true, "Not run.");

        Assert.Equal(TaskStepList.MaxSteps + 1, steps.Count);
        Assert.Equal(TaskStepList.CompletedStepName, steps.Snapshot()[^1].Name);
        Assert.Equal(100, steps.Progress);
    }

    [Fact]
    public void CloseCompletesTheRunningStepOnSuccessAndIgnoresLaterChanges()
    {
        var changes = 0;
        var steps = new TaskStepList(_time);
        steps.Changed += (_, _) => changes++;
        steps.Plan(["A", "B"]);
        var a = steps.Begin("A");
        steps.Close(succeeded: true, "Not run.");
        Assert.Equal("A:Done, B:Skipped, Completed:Done", Shape(steps));
        Assert.Equal(2, steps.CurrentIndex);
        var before = changes;

        a.Fail("late");
        steps.Begin("C").Complete();
        steps.Plan(["D"]);
        Assert.Equal("A:Done, B:Skipped, Completed:Done", Shape(steps));
        Assert.Equal(before, changes);
    }

    [Fact]
    public void StepCountAndTextLengthsAreBounded()
    {
        var steps = new TaskStepList(_time);
        for (var i = 0; i < TaskStepList.MaxSteps + 5; i++)
        {
            using var step = steps.Begin("Step " + i);
        }

        Assert.Equal(TaskStepList.MaxSteps, steps.Count);
        var extra = steps.Begin(new string('n', 500));
        Assert.Equal(TaskStepState.Running, extra.State); // a detached handle still behaves like a step
        extra.Complete();
        Assert.Equal(TaskStepState.Done, extra.State);

        var other = new TaskStepList(_time);
        other.Begin(new string('n', 500)).Fail(new string('d', 5000));
        Assert.Equal(TaskStepList.MaxNameLength, other.Snapshot()[0].Name.Length);
        Assert.Equal(TaskStepList.MaxDetailLength, other.Snapshot()[0].Detail!.Length);
        Assert.Throws<ArgumentException>(() => other.Begin(" "));
    }

    [Fact]
    public void CurrentIndexOfPrefersRunningThenLastStarted()
    {
        var t = _time.GetUtcNow();
        Assert.Equal(-1, TaskStepList.CurrentIndexOf([]));
        Assert.Equal(1, TaskStepList.CurrentIndexOf([
            new(0, "A", TaskStepState.Done, null, 100, t, t),
            new(1, "B", TaskStepState.Failed, "x", 0, t, t),
            new(2, "C", TaskStepState.Skipped, "Not run.", 0, null, null),
        ]));
        Assert.Equal(1, TaskStepList.CurrentIndexOf([
            new(0, "A", TaskStepState.Done, null, 100, t, t),
            new(1, "B", TaskStepState.Running, null, 10, t, null),
        ]));
        Assert.Equal(0, TaskStepList.CurrentIndexOf([new(0, "A", TaskStepState.Skipped, "x", 0, null, null)]));
    }

    [Fact]
    public async Task StepAsyncCompletesFailsAndRethrows()
    {
        var context = new StepContext(_time);
        Assert.Equal(42, await context.StepAsync("Read", _ => Task.FromResult(42)));
        await context.StepAsync("Explicit", step =>
        {
            step.Skip("not applicable");
            return Task.CompletedTask;
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.StepAsync("Write", _ => throw new InvalidOperationException("denied")));
        await Assert.ThrowsAsync<OperationCanceledException>(() => context.StepAsync("Wait", _ => throw new OperationCanceledException()));
        context.SkipStep("Later", "keep unchanged");

        Assert.Equal("Read:Done, Explicit:Skipped, Write:Failed, Wait:Failed, Later:Skipped", Shape(context.Steps));
        Assert.Equal(["not applicable", "denied", "Cancelled.", "keep unchanged"], context.Steps.Snapshot().Skip(1).Select(s => s.Detail));
    }

    [Fact]
    public void TheDefaultContextImplementationKeepsPluginsWorking()
    {
        // Third-party contexts that predate steps get a working, detached step.
        ITaskExecutionContext context = new LegacyContext();
        context.PlanSteps("A");
        using var step = context.BeginStep("A");
        step.Warn("careful");
        Assert.Equal(TaskStepState.Warning, step.State);
        Assert.Equal(["careful"], ((LegacyContext)context).Warnings);
    }

    private sealed class StepContext(TimeProvider time) : LegacyContext
    {
        public TaskStepList Steps { get; } = new(time);

        public override void PlanSteps(params string[] names) => Steps.Plan(names);

        public override ITaskStep BeginStep(string name) => Steps.Begin(name);
    }

    private class LegacyContext : ITaskExecutionContext
    {
        public List<string> Warnings { get; } = [];

        public Guid TaskId { get; } = Guid.NewGuid();

        public Sdk.Vapix.IVapixClient Vapix => throw new NotSupportedException();

        public Microsoft.Extensions.Logging.ILogger Logger => Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        public ICorePlugin? Owner => null;

        public IUploadedFiles Files => throw new NotSupportedException();

        public void ReportProgress(int percent, string? message = null)
        {
        }

        public void ReportWarning(string message) => Warnings.Add(message);

        public void Log(TaskLogLevel level, string message)
        {
        }

        public virtual void PlanSteps(params string[] names) => ((ITaskExecutionContext)new Bare(this)).PlanSteps(names);

        public virtual ITaskStep BeginStep(string name) => ((ITaskExecutionContext)new Bare(this)).BeginStep(name);
    }

    /// <summary>A context without step members, so the interface defaults run.</summary>
    private sealed class Bare(LegacyContext inner) : ITaskExecutionContext
    {
        public Guid TaskId => inner.TaskId;

        public Sdk.Vapix.IVapixClient Vapix => inner.Vapix;

        public Microsoft.Extensions.Logging.ILogger Logger => inner.Logger;

        public ICorePlugin? Owner => null;

        public IUploadedFiles Files => inner.Files;

        public void ReportProgress(int percent, string? message = null) => inner.ReportProgress(percent, message);

        public void ReportWarning(string message) => inner.ReportWarning(message);

        public void Log(TaskLogLevel level, string message) => inner.Log(level, message);
    }
}
