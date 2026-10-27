using Oadm.Plugins.LoadTest.Client;
using Oadm.Sdk.Client.Controls;

namespace Oadm.Plugins.LoadTest.Tests.Client;

/// <summary>Renders the page offscreen like the client shell, OADM_SCREENSHOT_DIR writes PNGs.</summary>
public sealed class PageScreenshotTests
{
    /// <summary>Dark by default; OADM_TEST_APPEARANCE=Light writes the light files in a second run.</summary>
    [Fact]
    public async Task Page_renders_every_state()
    {
        var suffix = Headless.Appearance;
        await Headless.Session.Dispatch(() =>
        {
            var ctx = new FakePageContext();
            ctx.Answers[LoadTestMethods.GetStream] = json =>
            {
                var request = LoadTestJson.Deserialize<StreamRequest>(json);
                var index = int.Parse(request.StreamId.Split('/')[1], System.Globalization.CultureInfo.InvariantCulture);
                return LoadTestJson.Serialize(Samples.Stream(Samples.Row(index)));
            };

            // Nothing chosen.
            var vm = new LoadTestViewModel(ctx);
            var view = new LoadTestView();
            var window = Headless.Host(view);
            window.Show();
            view.DataContext = vm; // after attaching: no calls, deterministic content
            Headless.Pump();
            Assert.True(vm.ShowChooseFirst);
            Headless.Capture(window, $"load-test-empty-{suffix}.png");

            // Chosen, the plan.
            vm.DeviceChoice.Set(DeviceChoice.AllDevices);
            vm.ApplyPlan(Samples.Plan());
            Headless.Pump();
            Assert.True(vm.ShowPlan);
            Assert.Equal(2, vm.LeftOut.Count);
            Headless.Capture(window, $"load-test-plan-{suffix}.png");

            // Running: batch 2 in Ramp 75 %.
            var second = Samples.BatchSeconds + (2 * Samples.PhaseSeconds) + 50;
            vm.ShowDetail(Samples.RunningDetail(second));
            Headless.Pump();
            Assert.True(vm.IsRunning);
            Headless.Capture(window, $"load-test-running-{suffix}.png");

            // Completed with failures.
            vm.ShowDetail(Samples.CompletedDetail());
            Headless.Pump();
            Assert.True(vm.IsResult);
            Assert.Equal(150, vm.Rows.Count);
            Assert.True(vm.Rows[0].Badness >= vm.Rows[^1].Badness);
            Headless.Capture(window, $"load-test-result-{suffix}.png");

            // The worst stream's own graphs in the side panel.
            vm.SelectedRow = vm.Rows.First(r => r.LowLight.Result == PhaseResult.Failed);
            Headless.Pump();
            Assert.NotNull(vm.Detail);
            Assert.False(vm.Detail!.IsLoading);
            Headless.Capture(window, $"load-test-stream-{suffix}.png");
            window.Close();

            // The run list.
            var runs = vm.RunsViewModel;
            runs.Apply(Samples.RunList());
            var list = new RunListWindow { DataContext = runs };
            Oadm.Client.App.ApplyCrispText(list);
            list.Show();
            runs.Selected = runs.Runs[0];
            Headless.Pump();
            Headless.Capture(list, $"load-test-runs-{suffix}.png");
            list.Close();
            vm.Dispose();
        }, CancellationToken.None);
    }
}
