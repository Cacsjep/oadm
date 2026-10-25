using Microsoft.Extensions.Time.Testing;

using Oadm.Plugins.SnapshotReport.Report;

using Testing = Oadm.Sdk.Testing;

namespace Oadm.Plugins.SnapshotReport.Tests;

/// <summary>
/// The life of a report in memory (<see cref="ReportJobs"/>): dropped once downloaded, else after its lifetime counted from
/// its end (asking for its status does not keep it), with the user's job when the host ends it, at most two built at once per
/// user, and a start that fails leaves no job waiting.
/// </summary>
public sealed class ReportJobLifetimeTests
{
    [Fact]
    public async Task A_finished_report_nobody_reads_again_is_dropped_after_its_lifetime()
    {
        var (ctx, camera, _) = Setup();
        var time = new FakeTimeProvider();
        using var service = new SnapshotService(ctx.Devices, ctx.Vapix, timeout: TimeSpan.FromHours(1));
        using var reports = new ReportJobs(service, time);
        var started = reports.Start(Request(camera.Id), "1.0");
        await Testing.Eventually.TrueAsync(() => reports.Status(started.JobId).State == ReportJobStates.Done, "the report is ready");

        time.Advance(ReportJobs.Lifetime + ReportJobs.ExpiryInterval);
        await Testing.Eventually.TrueAsync(() => reports.Count == 0, "the report was dropped");
        Assert.Equal(ReportJobs.NotFound, Assert.Throws<KeyNotFoundException>(() => reports.Status(started.JobId)).Message);
    }

    [Fact]
    public async Task The_hosts_end_of_the_users_job_drops_the_finished_report()
    {
        var (ctx, camera, _) = Setup();
        using var service = new SnapshotService(ctx.Devices, ctx.Vapix, timeout: TimeSpan.FromHours(1));
        using var reports = new ReportJobs(service, new FakeTimeProvider());
        var admin = ctx.CallAsAllDevices();
        var started = await reports.StartAsync(admin, ctx.FakeJobs, Request(camera.Id), "1.0", CancellationToken.None);
        await Testing.Eventually.TrueAsync(() => reports.Status(admin, ctx.FakeJobs, started.JobId).State == ReportJobStates.Done, "the report is ready");
        var job = Assert.Single(ctx.FakeJobs.All);
        Assert.False(job.Completion.IsCompleted); // the job lasts as long as the report

        job.Cancel(); // the user logged out, or got other devices
        await Testing.Eventually.TrueAsync(() => job.Completion.IsCompleted, "the job ended");
        Assert.Equal(0, reports.Count);
    }

    [Fact]
    public async Task One_user_builds_at_most_two_reports_at_once()
    {
        var (ctx, camera, gate) = Setup(hold: true);
        using var service = new SnapshotService(ctx.Devices, ctx.Vapix, timeout: TimeSpan.FromHours(1));
        using var reports = new ReportJobs(service, new FakeTimeProvider());
        var admin = ctx.CallAsAllDevices();
        string Message(string id) => reports.Status(admin, ctx.FakeJobs, id).Message!;
        var first = await reports.StartAsync(admin, ctx.FakeJobs, Request(camera.Id), "1.0", CancellationToken.None);
        var second = await reports.StartAsync(admin, ctx.FakeJobs, Request(camera.Id), "1.0", CancellationToken.None);
        var third = await reports.StartAsync(admin, ctx.FakeJobs, Request(camera.Id), "1.0", CancellationToken.None);

        // Two take their snapshots (held), one waits its turn (the jobs start in any order); another user's report does not
        // wait for them.
        string[] ids = [first.JobId, second.JobId, third.JobId];
        await Testing.Eventually.TrueAsync(() => ids.Count(id => Message(id) == ReportJobs.WaitingForEarlier) == 1
            && ids.All(id => Message(id) != "Read video sources"), "one report waits, two run");
        var waiting = ids.Single(id => Message(id) == ReportJobs.WaitingForEarlier);
        var running = ids.First(id => id != waiting);
        var other = ctx.CallAsAllDevices("other");
        var theirs = await reports.StartAsync(other, ctx.FakeJobs, Request(camera.Id), "1.0", CancellationToken.None);
        await Testing.Eventually.TrueAsync(() => reports.Status(other, ctx.FakeJobs, theirs.JobId).Message != "Read video sources", "the other user's report runs");
        Assert.NotEqual(ReportJobs.WaitingForEarlier, reports.Status(other, ctx.FakeJobs, theirs.JobId).Message);

        reports.Delete(admin, ctx.FakeJobs, running); // one ended: the waiting one takes its turn
        await Testing.Eventually.TrueAsync(() => Message(waiting) != ReportJobs.WaitingForEarlier, "the waiting report started");
        gate.SetResult();
        await Testing.Eventually.TrueAsync(() => reports.Status(admin, ctx.FakeJobs, waiting).State == ReportJobStates.Done, "the waiting report is ready");
    }

    [Fact]
    public async Task A_downloaded_report_is_dropped_at_once_and_a_second_download_says_it_is_gone()
    {
        var (ctx, camera, _) = Setup();
        using var service = new SnapshotService(ctx.Devices, ctx.Vapix, timeout: TimeSpan.FromHours(1));
        using var reports = new ReportJobs(service, new FakeTimeProvider());
        var admin = ctx.CallAsAllDevices();
        var started = await reports.StartAsync(admin, ctx.FakeJobs, Request(camera.Id), "1.0", CancellationToken.None);
        await Testing.Eventually.TrueAsync(() => reports.Status(admin, ctx.FakeJobs, started.JobId).State == ReportJobStates.Done, "the report is ready");
        var job = Assert.Single(ctx.FakeJobs.All);

        long offset = 0;
        ReportChunk chunk;
        do
        {
            chunk = reports.Read(admin, ctx.FakeJobs, started.JobId, offset);
            offset += Convert.FromBase64String(chunk.DataBase64).Length;
        }
        while (!chunk.Eof);

        Assert.Equal(0, reports.Count); // dropped with its last chunk, no delete needed
        await Testing.Eventually.TrueAsync(() => job.Completion.IsCompleted, "the job ended");
        Assert.Equal(ReportJobs.NotFound, Assert.Throws<KeyNotFoundException>(() => reports.Read(admin, ctx.FakeJobs, started.JobId, 0)).Message);
    }

    [Fact]
    public async Task Asking_for_the_status_does_not_keep_a_finished_report_beyond_its_lifetime()
    {
        var (ctx, camera, _) = Setup();
        var time = new FakeTimeProvider();
        using var service = new SnapshotService(ctx.Devices, ctx.Vapix, timeout: TimeSpan.FromHours(1));
        using var reports = new ReportJobs(service, time);
        var started = reports.Start(Request(camera.Id), "1.0");
        await Testing.Eventually.TrueAsync(() => reports.Status(started.JobId).State == ReportJobStates.Done, "the report is ready");

        time.Advance(ReportJobs.Lifetime - TimeSpan.FromMinutes(1));
        Assert.Equal(ReportJobStates.Done, reports.Status(started.JobId).State); // still there, asked for again
        time.Advance(TimeSpan.FromMinutes(1) + ReportJobs.ExpiryInterval);

        await Testing.Eventually.TrueAsync(() => reports.Count == 0, "the report was dropped 30 minutes after it finished");
    }

    [Fact]
    public async Task One_user_keeps_every_finished_report_until_it_is_downloaded()
    {
        var (ctx, camera, _) = Setup();
        var time = new FakeTimeProvider();
        using var service = new SnapshotService(ctx.Devices, ctx.Vapix, timeout: TimeSpan.FromHours(1));
        using var reports = new ReportJobs(service, time);
        var admin = ctx.CallAsAllDevices();
        var ids = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            var started = await reports.StartAsync(admin, ctx.FakeJobs, Request(camera.Id), "1.0", CancellationToken.None);
            await Testing.Eventually.TrueAsync(() => reports.Status(admin, ctx.FakeJobs, started.JobId).State == ReportJobStates.Done, "the report is ready");
            ids.Add(started.JobId);
            time.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.All(ids, id => Assert.Equal(ReportJobStates.Done, reports.Status(admin, ctx.FakeJobs, id).State));
        Assert.Equal(5, reports.Count);
    }

    [Fact]
    public async Task Finished_reports_beyond_the_byte_budget_drop_the_oldest_but_never_the_one_that_just_finished()
    {
        var (ctx, camera, _) = Setup();
        var time = new FakeTimeProvider();
        using var service = new SnapshotService(ctx.Devices, ctx.Vapix, timeout: TimeSpan.FromHours(1));
        using var reports = new ReportJobs(service, time, maxFinishedBytes: 1); // every PDF is larger
        var first = ctx.CallAsAllDevices();
        var second = ctx.CallAsAllDevices("other");
        var theirs = await reports.StartAsync(first, ctx.FakeJobs, Request(camera.Id), "1.0", CancellationToken.None);
        await Testing.Eventually.TrueAsync(() => reports.Status(first, ctx.FakeJobs, theirs.JobId).State == ReportJobStates.Done, "the first report is ready");
        Assert.Equal(1, reports.Count); // alone over the budget: kept
        time.Advance(TimeSpan.FromSeconds(1));

        var mine = await reports.StartAsync(second, ctx.FakeJobs, Request(camera.Id), "1.0", CancellationToken.None);
        await Testing.Eventually.TrueAsync(() => reports.Status(second, ctx.FakeJobs, mine.JobId).State == ReportJobStates.Done, "the second report is ready");
        await Testing.Eventually.TrueAsync(() => reports.Count == 1, "the older report was dropped");
        Assert.Throws<KeyNotFoundException>(() => reports.Status(first, ctx.FakeJobs, theirs.JobId));
    }

    [Fact]
    public async Task A_start_that_fails_after_the_job_started_leaves_no_job_waiting()
    {
        var (ctx, camera, _) = Setup();
        using var service = new SnapshotService(ctx.Devices, ctx.Vapix, timeout: TimeSpan.FromHours(1));
        var reports = new ReportJobs(service, new FakeTimeProvider());
        reports.Dispose(); // the plugin stopped while a call came in

        await Assert.ThrowsAsync<ObjectDisposedException>(() => reports.StartAsync(ctx.CallAsAllDevices(), ctx.FakeJobs, Request(camera.Id), "1.0", CancellationToken.None));
        var job = Assert.Single(ctx.FakeJobs.All);
        await Testing.Eventually.TrueAsync(() => job.Completion.IsCompleted, "the job ended");
    }

    private static ReportRequest Request(Guid device) => new() { Site = "Site", Items = [new ReportItem { DeviceId = device, Camera = 1 }] };

    /// <summary>One camera; with <paramref name="hold"/> its requests wait until the returned gate opens.</summary>
    private static (Testing.FakeCoreContext Ctx, Testing.FakeDevice Camera, TaskCompletionSource Gate) Setup(bool hold = false)
    {
        var ctx = new Testing.FakeCoreContext();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!hold)
        {
            gate.SetResult();
        }

        var camera = ctx.Add(new Testing.FakeDevice { Address = "10.0.0.48" }, new HeldCamera(gate.Task));
        return (ctx, camera, gate);
    }

    /// <summary>A camera whose video source list waits for the gate (the first request of a report).</summary>
    private sealed class HeldCamera(Task gate) : Testing.FakeCamera, Oadm.Sdk.Vapix.IVapixClient
    {
        public async Task<IReadOnlyList<Oadm.Sdk.Vapix.VideoSource>> GetVideoSourcesAsync(CancellationToken ct)
        {
            await gate.WaitAsync(ct);
            throw new NotSupportedException("No video sources in this test.");
        }
    }
}
