using System.Net;

using Oadm.Plugins.LoadTest.Checking;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.LoadTest.Tests.Server;

/// <summary>
/// The only write (docs/spec/load-test.md, "The exposure always goes back", CLAUDE.md device safety): the exposure
/// is read and kept before Low light, set to its maximum, and written back after the phase, on Cancel, when a camera drops
/// out, when the plugin stops; a failed restore stays with the run as a warning.
/// </summary>
public sealed class ExposureTests
{
    private const string Parameter = "ImageSource.I0.Sensor.ExposureValue";

    private static string Exposure(Rig rig, FakeDevice camera) => rig.Ctx.Cameras[camera.Id].Parameters[Parameter];

    /// <summary>Runs the four phases before Low light and waits until Low light began (the exposure is up).</summary>
    private static async Task IntoLowLightAsync(Rig rig, int phaseSeconds)
    {
        await rig.AdvanceAsync(4 * phaseSeconds);
        await rig.SettleAsync();
    }

    [Fact]
    public async Task The_exposure_is_at_its_maximum_only_during_low_light()
    {
        await using var rig = await new Rig(phaseSeconds: 1).StartAsync();
        var camera = rig.Camera("10.0.0.1");
        Assert.Equal(Parameter, ExposureKeeper.ParameterOf(1));

        await rig.StartCheckAsync([camera]);
        await rig.AdvanceAsync(3);
        await rig.SettleAsync();
        Assert.Equal("50", Exposure(rig, camera));
        await rig.AdvanceAsync(1);
        await rig.SettleAsync();
        Assert.Equal("100", Exposure(rig, camera));
        await rig.RunToEndAsync();

        Assert.Equal("50", Exposure(rig, camera));
        var fake = rig.Ctx.Cameras[camera.Id];
        Assert.Equal(2, fake.Writes);
        Assert.Contains($"GET /axis-cgi/param.cgi?action=list&group={Parameter}", fake.Requests);
        Assert.Equal(2, fake.Requests.Count(r => r == "POST /axis-cgi/param.cgi"));
        Assert.Empty((await rig.DetailAsync(rig.Service.ListRuns(rig.Admin).Runs[0].RunId)).Summary.RestoreWarnings);
    }

    [Fact]
    public async Task Cancel_in_low_light_writes_the_exposure_back()
    {
        await using var rig = await new Rig(phaseSeconds: 2).StartAsync();
        var camera = rig.Camera("10.0.0.1");

        await rig.StartCheckAsync([camera]);
        await IntoLowLightAsync(rig, 2);
        Assert.Equal("100", Exposure(rig, camera));
        var cancelled = await rig.CallAsync<RunSummary>(rig.Admin, LoadTestMethods.Cancel);

        Assert.Equal(RunState.Cancelled, cancelled.State);
        Assert.Equal("50", Exposure(rig, camera));
        Assert.Empty(cancelled.RestoreWarnings);
    }

    [Fact]
    public async Task A_camera_that_drops_out_in_low_light_gets_its_exposure_back_at_once()
    {
        await using var rig = await new Rig(phaseSeconds: 3).StartAsync();
        var drops = rig.Camera("10.0.0.1");
        var stays = rig.Camera("10.0.0.2");

        var started = await rig.StartCheckAsync([drops, stays], batchSize: 2);
        await IntoLowLightAsync(rig, 3);
        Assert.Equal(("100", "100"), (Exposure(rig, drops), Exposure(rig, stays)));
        rig.Probes.Open.Single(p => p.DeviceId == drops.Id).Fail();
        await rig.AdvanceAsync(1);
        await rig.SettleAsync();

        Assert.Equal(("50", "100"), (Exposure(rig, drops), Exposure(rig, stays)));
        await rig.RunToEndAsync();
        Assert.Equal("50", Exposure(rig, stays));
        var row = (await rig.DetailAsync(started.RunId)).Streams.Single(r => r.DeviceId == drops.Id);
        Assert.Equal(PhaseResult.Interrupted, row.Phases[(int)CheckPhase.LowLight].Result);
    }

    [Fact]
    public async Task A_failed_restore_stays_with_the_run_with_the_original_value()
    {
        await using var rig = await new Rig(phaseSeconds: 1).StartAsync();
        var camera = rig.Camera("10.0.0.1");
        rig.Ctx.Cameras[camera.Id].Respond = (request, body) =>
            request.Method == HttpMethod.Post && body?.EndsWith("=50", StringComparison.Ordinal) == true
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("") }
                : null;

        var started = await rig.StartCheckAsync([camera]);
        await rig.RunToEndAsync();

        var expected = "10.0.0.1: exposure 50 was not written back";
        Assert.Equal([expected], (await rig.DetailAsync(started.RunId)).Summary.RestoreWarnings);
        Assert.Equal([expected], rig.Service.ListRuns(rig.Admin).Runs[0].RestoreWarnings);
        Assert.Equal(PhaseResult.Passed, (await rig.DetailAsync(started.RunId)).Streams[0].Phases[(int)CheckPhase.LowLight].Result);
    }

    [Fact]
    public async Task Stopping_the_plugin_in_low_light_writes_the_exposure_back_and_keeps_the_run()
    {
        var rig = await new Rig(phaseSeconds: 2).StartAsync();
        await using (rig)
        {
            var camera = rig.Camera("10.0.0.1");
            var started = await rig.StartCheckAsync([camera]);
            await IntoLowLightAsync(rig, 2);
            Assert.Equal("100", Exposure(rig, camera));

            await rig.Plugin.StopAsync(CancellationToken.None);

            Assert.Equal("50", Exposure(rig, camera));
            Assert.All(rig.Probes.Opened, p => Assert.True(p.IsDisposed));
            await using var again = new LoadTestPlugin(rig.Options);
            await again.StartAsync(rig.Ctx, CancellationToken.None);
            var runs = LoadTestJson.Deserialize<RunList>(await rig.Ctx.InvokeAsync(again, rig.Admin, LoadTestMethods.ListRuns, null));
            var run = Assert.Single(runs.Runs);
            Assert.Equal(started.RunId, run.RunId);
            Assert.Equal(RunState.Failed, run.State);
            Assert.Equal(Verdicts.PluginStopped, run.Verdict);
            Assert.Null(runs.RunningId);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("auto")]
    [InlineData("250")]
    public async Task A_camera_without_a_usable_exposure_value_is_not_written_and_low_light_is_NA(string? value)
    {
        await using var rig = await new Rig(phaseSeconds: 1).StartAsync();
        var camera = rig.Camera("10.0.0.1");
        var fake = rig.Ctx.Cameras[camera.Id];
        fake.Parameters.Clear();
        if (value is not null)
        {
            fake.Parameters[Parameter] = value;
        }

        var started = await rig.StartCheckAsync([camera]);
        await rig.RunToEndAsync();

        Assert.Equal(0, fake.Writes);
        Assert.DoesNotContain(fake.Requests, r => r.StartsWith("POST", StringComparison.Ordinal));
        var detail = await rig.DetailAsync(started.RunId);
        Assert.Equal(PhaseResult.NotApplicable, detail.Streams[0].Phases[(int)CheckPhase.LowLight].Result);
        Assert.Equal(Verdicts.Carries, detail.Summary.Verdict);
        Assert.Single(rig.Probes.Opened); // it still streamed in Low light, as in Normal
        Assert.Equal(5, detail.Graphs.TotalMbps.Count(v => v > 0));
    }

    [Fact]
    public async Task A_refused_write_is_NA_and_the_original_is_written_back_anyway()
    {
        await using var rig = await new Rig(phaseSeconds: 1).StartAsync();
        var camera = rig.Camera("10.0.0.1");
        var fake = rig.Ctx.Cameras[camera.Id];
        fake.Respond = (request, body) => body?.EndsWith("=100", StringComparison.Ordinal) == true
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("# Error: Error setting '" + Parameter + "' to '100'!") }
            : null;

        var started = await rig.StartCheckAsync([camera]);
        await rig.RunToEndAsync();

        Assert.Equal(PhaseResult.NotApplicable, (await rig.DetailAsync(started.RunId)).Streams[0].Phases[(int)CheckPhase.LowLight].Result);
        Assert.Equal(1, fake.Writes); // the restore of 50
        Assert.Equal("50", fake.Parameters[Parameter]);
    }
}
