using Oadm.Sdk.Devices;
using Oadm.Sdk.Testing;
using Oadm.Sdk.Vapix;

namespace Oadm.Samples.UpnpName.Tests;

public sealed class UpnpNameTaskPluginTests
{
    private readonly UpnpNameTaskPlugin _plugin = new();

    /// <summary>A camera with the UPnP name parameter (Oadm.Sdk.Testing answers param.cgi list and update).</summary>
    private static FakeCamera Camera() => new() { Parameters = { [UpnpName.Parameter] = "AXIS P3265-V - B8A44F631339" } };

    /// <summary>A managed device whose cached API list has param.cgi.</summary>
    private static FakeDevice Device(DeviceStatus status = DeviceStatus.Ok) => new() { Status = status, Apis = [new("param-cgi", "1.0")] };

    [Fact]
    public async Task Sets_and_verifies_the_name()
    {
        var camera = Camera();
        var ctx = new FakeTaskContext(camera);

        await StepRun.RunAsync(ctx.Steps, () => _plugin.ExecuteAsync(ctx, Device(), """{"name":"Entrance"}""", CancellationToken.None));

        Assert.Equal("Entrance", camera.Parameters[UpnpName.Parameter]);
        Assert.Equal(
            ["Check compatibility: Done", "Read the current name: Done", "Set the name: Done", "Verify the name: Done", "Completed: Done"],
            StepRun.Lines(ctx.Steps));
    }

    [Fact]
    public async Task The_same_name_is_not_written_again()
    {
        var camera = Camera();
        camera.Parameters[UpnpName.Parameter] = "Entrance";
        var ctx = new FakeTaskContext(camera);

        await StepRun.RunAsync(ctx.Steps, () => _plugin.ExecuteAsync(ctx, Device(), """{"name":"Entrance"}""", CancellationToken.None));

        Assert.Equal(0, camera.Writes);
        Assert.Equal("Set the name: Skipped", StepRun.Lines(ctx.Steps)[2]);
    }

    [Fact]
    public async Task A_device_without_the_API_is_not_changed()
    {
        var camera = Camera();
        camera.Apis = [];
        var ctx = new FakeTaskContext(camera);

        await Assert.ThrowsAsync<DeviceNotCompatibleException>(() =>
            StepRun.RunAsync(ctx.Steps, () => _plugin.ExecuteAsync(ctx, Device(), """{"name":"Entrance"}""", CancellationToken.None)));

        Assert.Equal(0, camera.Writes);
        Assert.Equal("Check compatibility: Failed", StepRun.Lines(ctx.Steps)[0]);
    }

    [Fact]
    public async Task An_empty_name_fails_before_any_request()
    {
        var camera = Camera();
        var ctx = new FakeTaskContext(camera);

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            _plugin.ExecuteAsync(ctx, Device(), """{"name":" "}""", CancellationToken.None));

        Assert.Contains("Nothing was changed", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, camera.Writes);
    }

    [Fact]
    public void The_menu_entry_says_why_it_cannot_run()
    {
        Assert.True(_plugin.CanRun(Device()));
        Assert.False(_plugin.CanRun(Device(DeviceStatus.Unreachable)));
        Assert.Equal("The device does not answer", _plugin.NotSupportedReason(Device(DeviceStatus.Unreachable)));
        Assert.False(_plugin.CanRun(Device() with { Apis = [] }));
    }

    [Fact]
    public void The_task_name_says_what_happens() =>
        Assert.Equal("Set UPnP name Entrance", _plugin.GetTaskName("""{"name":"Entrance"}"""));
}
