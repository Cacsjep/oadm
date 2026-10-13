using Oadm.Sdk.Devices;
using Oadm.Sdk.Vapix;
using Oadm.Tests.Shared;

namespace Oadm.Samples.UpnpName.Tests;

public sealed class UpnpNameTaskPluginTests
{
    private readonly UpnpNameTaskPlugin _plugin = new();

    [Fact]
    public async Task Sets_and_verifies_the_name()
    {
        var camera = new FakeCamera();
        var ctx = new FakeTaskContext(camera);

        await StepRun.RunAsync(ctx.Steps, () => _plugin.ExecuteAsync(ctx, new FakeDevice(), """{"name":"Entrance"}""", CancellationToken.None));

        Assert.Equal("Entrance", camera.Parameters[UpnpName.Parameter]);
        Assert.Equal(
            ["Check compatibility: Done", "Read the current name: Done", "Set the name: Done", "Verify the name: Done", "Completed: Done"],
            StepRun.Lines(ctx.Steps));
    }

    [Fact]
    public async Task The_same_name_is_not_written_again()
    {
        var camera = new FakeCamera();
        camera.Parameters[UpnpName.Parameter] = "Entrance";
        var ctx = new FakeTaskContext(camera);

        await StepRun.RunAsync(ctx.Steps, () => _plugin.ExecuteAsync(ctx, new FakeDevice(), """{"name":"Entrance"}""", CancellationToken.None));

        Assert.Equal(0, camera.Writes);
        Assert.Equal("Set the name: Skipped", StepRun.Lines(ctx.Steps)[2]);
    }

    [Fact]
    public async Task A_device_without_the_API_is_not_changed()
    {
        var camera = new FakeCamera { Apis = [] };
        var ctx = new FakeTaskContext(camera);

        await Assert.ThrowsAsync<DeviceNotCompatibleException>(() =>
            StepRun.RunAsync(ctx.Steps, () => _plugin.ExecuteAsync(ctx, new FakeDevice(), """{"name":"Entrance"}""", CancellationToken.None)));

        Assert.Equal(0, camera.Writes);
        Assert.Equal("Check compatibility: Failed", StepRun.Lines(ctx.Steps)[0]);
    }

    [Fact]
    public async Task An_empty_name_fails_before_any_request()
    {
        var camera = new FakeCamera();
        var ctx = new FakeTaskContext(camera);

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            _plugin.ExecuteAsync(ctx, new FakeDevice(), """{"name":" "}""", CancellationToken.None));

        Assert.Contains("Nothing was changed", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, camera.Writes);
    }

    [Fact]
    public void The_menu_entry_says_why_it_cannot_run()
    {
        Assert.True(_plugin.CanRun(new FakeDevice()));
        Assert.False(_plugin.CanRun(new FakeDevice(DeviceStatus.Unreachable)));
        Assert.Equal("The device does not answer", _plugin.NotSupportedReason(new FakeDevice(DeviceStatus.Unreachable)));
        Assert.False(_plugin.CanRun(new FakeDevice(ApiList: [])));
    }

    [Fact]
    public void The_task_name_says_what_happens() =>
        Assert.Equal("Set UPnP name Entrance", _plugin.GetTaskName("""{"name":"Entrance"}"""));
}
