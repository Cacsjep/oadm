using Microsoft.EntityFrameworkCore;

using Oadm.Core.Auth;
using Oadm.Core.Persistence;
using Oadm.Core.Plugins;
using Oadm.Core.Tasks;
using Oadm.Core.Tests.Persistence;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Tasks;
using Oadm.Sdk.Testing;

namespace Oadm.Core.Tests.Tasks;

/// <summary>
/// The audit entry of a task run with its outcome (docs/spec/security.md "Task outcome"): one entry, the device count while
/// it runs, the outcome once every task of the run has ended; one entry per task for <see cref="TaskAudit.EachTask"/>.
/// </summary>
#pragma warning disable CA1001 // Disposed by xUnit through IAsyncLifetime.DisposeAsync.
public sealed class TaskRunAuditTests : IAsyncLifetime
#pragma warning restore CA1001
{
    private readonly FakeDeviceRepository _devices = new();
    private readonly PluginRegistry _registry = new();
    private TestDatabase _db = null!;
    private AuditLog _log = null!;
    private TaskRunAudit _audit = null!;
    private TaskEngine _engine = null!;

    public async Task InitializeAsync()
    {
        _db = await TestDatabase.CreateAsync();
        _log = new AuditLog(_db.Get<IDbContextFactory<OadmDbContext>>(), TimeProvider.System);
        _audit = new TaskRunAudit(_log, _devices);
        _engine = new TaskEngine(new InMemoryTaskStore(), _registry, _devices, new FakeVapixClientFactory()) { Audit = _audit };
    }

    public async Task DisposeAsync()
    {
        await _engine.DisposeAsync();
        await _db.DisposeAsync();
    }

    // ---- the texts ----

    [Fact]
    public void TheOutcomeCountsEachEndInItsOwnWord()
    {
        var outcome = new TaskBatchOutcome(6, 3, 1, 0, 2, []);

        Assert.Equal("6 devices: 3 done, 1 done with warnings, 2 cancelled", TaskRunAudit.RunOutcome(outcome, new Dictionary<Guid, string>()));
    }

    [Fact]
    public void OneDeviceReadsWithoutACount()
    {
        Assert.Equal("1 device: done", TaskRunAudit.RunOutcome(new TaskBatchOutcome(1, 1, 0, 0, 0, []), new Dictionary<Guid, string>()));
    }

    [Fact]
    public void TheOutcomeNamesTheFailedDevicesWithTheirError()
    {
        var a = Failed("The password is wrong.");
        var outcome = new TaskBatchOutcome(3, 2, 0, 1, 0, [a]);
        var labels = new Dictionary<Guid, string> { [a.DeviceId] = "10.0.0.12, ACCC8E000012" };

        Assert.Equal("3 devices: 2 done, 1 failed (10.0.0.12, ACCC8E000012: The password is wrong)", TaskRunAudit.RunOutcome(outcome, labels));
    }

    [Fact]
    public void AtMostThreeFailuresAreNamedThenHowManyMore()
    {
        var failures = new[] { Failed("One"), Failed("Two"), Failed("Three") };
        var labels = failures.Select((f, i) => (f.DeviceId, $"10.0.0.{i + 1}")).ToDictionary(x => x.DeviceId, x => x.Item2);
        var outcome = new TaskBatchOutcome(1_200, 1_000, 0, 200, 0, failures);

        Assert.Equal(
            "1,200 devices: 1,000 done, 200 failed (10.0.0.1: One; 10.0.0.2: Two; 10.0.0.3: Three; and 197 more)",
            TaskRunAudit.RunOutcome(outcome, labels));
    }

    [Fact]
    public void ARemovedDeviceAndALongErrorAreNamedShortly()
    {
        var failed = Failed(new string('x', 400) + "\nnext line");
        var text = TaskRunAudit.RunOutcome(new TaskBatchOutcome(1, 0, 0, 1, 0, [failed]), new Dictionary<Guid, string>());

        Assert.StartsWith("1 device: failed (Removed device " + failed.DeviceId.ToString()[..8] + ": xxx", text, StringComparison.Ordinal);
        Assert.Contains("…)", text, StringComparison.Ordinal);
        Assert.True(text.Length < 200, text);
    }

    [Fact]
    public void ATaskOfItsOwnEntryNamesTheStepItFailedAt()
    {
        var failed = Failed("The password is too short.") with
        {
            Steps = [new TaskStepInfo(0, "Check that the camera is new", TaskStepState.Done, null, 100, null, null), new TaskStepInfo(1, "Set the password", TaskStepState.Failed, null, 0, null, null)],
        };

        Assert.Equal("10.0.0.12 (ACCC8E000012): failed at Set the password: The password is too short.", TaskRunAudit.EachOutcome("10.0.0.12 (ACCC8E000012)", failed));
        Assert.Equal("10.0.0.12 (ACCC8E000012): done", TaskRunAudit.EachOutcome("10.0.0.12 (ACCC8E000012)", Ended(TaskState.Done, null)));
        Assert.Equal("10.0.0.12 (ACCC8E000012): done with warnings: No time server.", TaskRunAudit.EachOutcome("10.0.0.12 (ACCC8E000012)", Ended(TaskState.DoneWithWarnings, "No time server.")));
        Assert.Equal("10.0.0.12 (ACCC8E000012): cancelled", TaskRunAudit.EachOutcome("10.0.0.12 (ACCC8E000012)", Ended(TaskState.Cancelled, "Cancelled.")));
    }

    // ---- the watch ----

    [Fact]
    public async Task TheWatchCompletesOnlyWhenTheLastTaskOfTheRunEnded()
    {
        var watch = new TaskBatchWatch();
        watch.Expect(3);
        watch.Ended(Ended(TaskState.Done, null));
        watch.Ended(Failed("Boom"));

        Assert.False(watch.Completion.IsCompleted);

        watch.Ended(Ended(TaskState.Cancelled, null));
        var outcome = await watch.Completion.WaitAsync(Eventually.DefaultTimeout);
        Assert.Equal((3, 1, 0, 1, 1), (outcome.Total, outcome.Done, outcome.DoneWithWarnings, outcome.Failed, outcome.Cancelled));
    }

    [Fact]
    public async Task AHundredThousandTaskEndsKeepOnlyCountsAndTheFirstFailures()
    {
        var watch = new TaskBatchWatch();
        watch.Expect(100_000);
        var done = Ended(TaskState.Done, null);
        var failed = Failed("Boom");
        for (var i = 0; i < 100_000; i++)
        {
            watch.Ended(i % 10 == 0 ? failed : done);
        }

        var outcome = await watch.Completion.WaitAsync(Eventually.DefaultTimeout);
        Assert.Equal(90_000, outcome.Done);
        Assert.Equal(10_000, outcome.Failed);
        Assert.Equal(TaskBatchWatch.NamedFailures, outcome.Failures.Count);
    }

    // ---- with the engine ----

    [Fact]
    public async Task ARunWritesOneEntryWithTheCountAndItsOutcomeWhenTheLastTaskEnded()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var devices = new[] { _devices.Add(), _devices.Add(), _devices.Add() };
        var failing = devices[1];
        Register("t.run", async (ctx, device, ct) =>
        {
            await gate.Task.WaitAsync(ct);
            if (device.Id == failing)
            {
                throw new InvalidOperationException("The password is wrong.");
            }
        });

        var ids = await _engine.RunAsync("t.run", devices, null, "tester", new TaskAudit("Test t.run") { UserName = "admin" }, CancellationToken.None);

        var entry = Assert.Single(await EntriesAsync());
        Assert.Equal(("admin", AuditActions.TaskRun, "Test t.run", "3 devices"), (entry.UserName, entry.Action, entry.Target, entry.Detail));

        gate.SetResult();
        foreach (var id in ids)
        {
            await _engine.WaitForCompletionAsync(id, CancellationToken.None).WaitAsync(Eventually.DefaultTimeout);
        }

        await Eventually.TrueAsync(async () => _audit.PendingWrites == 0 && (await SingleAsync()).Detail != "3 devices", "the outcome is written");
        var serial = failing.ToString("N")[..12].ToUpperInvariant();
        Assert.Equal($"3 devices: 2 done, 1 failed (192.0.2.1, {serial}: The password is wrong)", (await SingleAsync()).Detail);
        Assert.Single(await EntriesAsync());
    }

    [Fact]
    public async Task ARunOnThousandsOfDevicesUpdatesItsEntryOnce()
    {
        var devices = _devices.AddMany(3_000);
        Register("t.many", (_, _, _) => Task.CompletedTask);

        var ids = await _engine.RunAsync("t.many", devices, null, "tester", new TaskAudit("Test t.many"), CancellationToken.None);
        foreach (var id in ids)
        {
            await _engine.WaitForCompletionAsync(id, CancellationToken.None).WaitAsync(Eventually.DefaultTimeout);
        }

        await Eventually.TrueAsync(async () => (await SingleAsync()).Detail == "3,000 devices: 3,000 done", "the outcome is written");
        Assert.Equal("system", (await SingleAsync()).UserName);
        Assert.Equal(0, _audit.PendingWrites);
    }

    [Fact]
    public async Task ACancelledTaskCountsAsCancelled()
    {
        var device = _devices.Add();
        Register("t.cancel", async (_, _, ct) => await Task.Delay(Timeout.InfiniteTimeSpan, ct));

        var id = Assert.Single(await _engine.RunAsync("t.cancel", [device], null, "tester", new TaskAudit("Test t.cancel"), CancellationToken.None));
        await Eventually.TrueAsync(() => _engine.IsActive(id) == true, "the task runs");
        Assert.True(_engine.Cancel(id));
        await _engine.WaitForCompletionAsync(id, CancellationToken.None).WaitAsync(Eventually.DefaultTimeout);

        await Eventually.TrueAsync(async () => (await SingleAsync()).Detail == "1 device: cancelled", "the outcome is written");
    }

    [Fact]
    public async Task ARunWithoutAnAuditWritesNothing()
    {
        var device = _devices.Add();
        Register("t.quiet", (_, _, _) => Task.CompletedTask);

        var id = Assert.Single(await _engine.RunAsync("t.quiet", [device], null, "tester", CancellationToken.None));
        await _engine.WaitForCompletionAsync(id, CancellationToken.None).WaitAsync(Eventually.DefaultTimeout);

        Assert.Empty(await EntriesAsync());
    }

    [Fact]
    public async Task EachTaskGetsItsOwnEntryWithItsDeviceAndOutcome()
    {
        var devices = new[] { _devices.Add(), _devices.Add() };
        var failing = devices[0];
        Register("t.each", (ctx, device, _) =>
        {
            ctx.PlanSteps("Check that the camera is new", "Set the password");
            using (ctx.BeginStep("Check that the camera is new"))
            {
            }

            using (ctx.BeginStep("Set the password"))
            {
                if (device.Id == failing)
                {
                    throw new InvalidOperationException("The password is too short.");
                }
            }

            return Task.CompletedTask;
        });

        var audit = new TaskAudit("Provision with Lobby") { UserName = "Provisioning (admin)", EachTask = true };
        var ids = await _engine.RunAsync("t.each", devices, null, "Provisioning", audit, CancellationToken.None);
        foreach (var id in ids)
        {
            await _engine.WaitForCompletionAsync(id, CancellationToken.None).WaitAsync(Eventually.DefaultTimeout);
        }

        var serials = devices.Select(d => d.ToString("N")[..12].ToUpperInvariant()).ToArray();
        var expected = new[]
        {
            $"192.0.2.1 ({serials[0]}): failed at Set the password: The password is too short.",
            $"192.0.2.1 ({serials[1]}): done",
        };
        await Eventually.TrueAsync(async () => (await EntriesAsync()).Select(e => e.Detail).Order().SequenceEqual(expected.Order()), "both outcomes are written");
        Assert.All(await EntriesAsync(), e => Assert.Equal(("Provisioning (admin)", AuditActions.TaskRun, "Provision with Lobby"), (e.UserName, e.Action, e.Target)));
    }

    private async Task<AuditRecord> SingleAsync() => Assert.Single(await EntriesAsync());

    private async Task<IReadOnlyList<AuditRecord>> EntriesAsync() => (await _log.ListAsync(0, 0, CancellationToken.None)).Entries;

    private void Register(string id, Func<ITaskExecutionContext, Sdk.Devices.IDeviceInfo, CancellationToken, Task> execute) =>
        Assert.True(_registry.RegisterTaskPlugin(new DelegateTaskPlugin(id, execute), new PluginOrigin("test", "1.0.0", null)));

    private static TaskRecord Failed(string message) => Ended(TaskState.Failed, message);

    private static TaskRecord Ended(TaskState state, string? message)
    {
        var device = Guid.NewGuid();
        return new TaskRecord(Guid.NewGuid(), "t", "Test", state, "tester", DateTimeOffset.UnixEpoch, null, DateTimeOffset.UnixEpoch, 100, null,
            [new TaskDeviceRecord(device, state, message, 100)]);
    }
}
