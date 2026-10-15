using NSubstitute;

using Oadm.Client.Devices.Toolbar;
using Oadm.Client.Infrastructure;
using Oadm.Client.Plugins;

namespace Oadm.Client.Tests;

/// <summary>The progress row of a page for actions on many devices: only when the action takes a moment, gone when it ends.</summary>
public sealed class WorkIndicatorTests
{
    [Fact]
    public async Task A_quick_action_shows_nothing()
    {
        var work = new WorkIndicator { ShowAfter = TimeSpan.FromSeconds(5) };
        var shown = false;
        work.PropertyChanged += (_, e) => shown |= e.PropertyName == nameof(WorkIndicator.IsActive) && work.IsActive;

        Assert.Equal(7, await work.RunAsync("Removing 3 devices", () => Task.FromResult(7)));
        Assert.False(shown);
        Assert.False(work.IsActive);
    }

    [Fact]
    public async Task A_longer_action_shows_its_text_until_it_ends()
    {
        var work = new WorkIndicator { ShowAfter = TimeSpan.Zero };
        var gate = new TaskCompletionSource();
        var run = work.RunAsync("Removing 30,000 devices", () => gate.Task);

        Assert.True(work.IsActive);
        Assert.Equal("Removing 30,000 devices", work.Text);
        gate.SetResult();
        await run;
        Assert.False(work.IsActive);
    }

    [Fact]
    public async Task A_failing_action_hides_the_row_and_passes_the_error_on()
    {
        var work = new WorkIndicator { ShowAfter = TimeSpan.Zero };
        var gate = new TaskCompletionSource();
        var run = work.RunAsync("Deleting the tasks", () => gate.Task);
        gate.SetException(new InvalidOperationException("refused"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => run);
        Assert.False(work.IsActive);
    }

    [Fact]
    public async Task Removing_devices_runs_with_the_progress_row_of_the_devices_page()
    {
        using var f = new DevicesFixture();
        f.SeedDevices(TestSupport.Device("d1", "ACCC8E000001", "10.0.0.1", "P3265-V", Oadm.Contracts.V1.DeviceStatus.Ok));
        var gate = new TaskCompletionSource();
        f.Api.RemoveDevicesAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns(gate.Task);
        f.Devices.Work.ShowAfter = TimeSpan.Zero;

        var remove = f.Devices.ToolbarContext.RemoveDevicesAsync([Guid.NewGuid(), Guid.NewGuid()], CancellationToken.None);
        Assert.True(f.Devices.Work.IsActive);
        Assert.Equal("Removing 2 devices", f.Devices.Work.Text);
        gate.SetResult();
        await remove;
        Assert.False(f.Devices.Work.IsActive);
        Assert.Equal("Removing 30,000 devices", ToolbarContext.RemovingText(30_000).Replace(' ', ' ').Replace('.', ','));
        Assert.Equal("Starting tasks on 30,000 devices", TaskPluginRunner.StartingText(30_000).Replace(' ', ' ').Replace('.', ','));
    }
}
