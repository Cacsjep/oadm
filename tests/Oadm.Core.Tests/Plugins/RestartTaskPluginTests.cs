using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Core.Tests.Tasks;
using Oadm.Plugins.Restart;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;
using Oadm.Tests.Shared;

namespace Oadm.Core.Tests.Plugins;

public sealed class RestartTaskPluginTests
{
    [Fact]
    public void TheTaskNameSaysWhatHappens() =>
        Assert.Equal("Restart device", ((Oadm.Sdk.Plugins.ITaskPlugin)new Oadm.Plugins.Restart.RestartTaskPlugin()).GetTaskName(null));

    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(5);

    [Fact]
    public void DeclaresToolbarActionWithoutDialog()
    {
        var plugin = new RestartTaskPlugin();
        Assert.Equal("oadm.restart", plugin.Id);
        Assert.True(plugin.ShowInToolbar);
        Assert.False(plugin.RequiresDialog);
        Assert.True(plugin.CanRun(new FakeDevice(Guid.NewGuid(), DeviceStatus.Ok)));
        Assert.False(plugin.CanRun(new FakeDevice(Guid.NewGuid(), DeviceStatus.Unreachable)));
        Assert.False(plugin.CanRun(new FakeDevice(Guid.NewGuid(), DeviceStatus.CredentialsRequired)));
    }

    [Fact]
    public async Task ChecksRestartsWaitsForTheDeviceToGoDownAndComeBackAndVerifies()
    {
        // Ping 1: check, 2: still up after the restart command, 3-5: down (rebooting), 6: back.
        var vapix = new FakeVapixClient { Answers = n => n is 1 or 2 or >= 6 };
        var ctx = new RecordingContext(vapix);
        var plugin = new RestartTaskPlugin(Poll, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(1), TimeProvider.System);

        await StepRun.RunAsync(ctx.Steps, () => plugin.ExecuteAsync(ctx, new FakeDevice(Guid.NewGuid()), null, CancellationToken.None));

        Assert.Equal(1, vapix.RestartCalls);
        Assert.Equal(6, vapix.Pings);
        Assert.Equal(
            [
                "Check device: Done",
                "Send restart: Done",
                "Wait for the device to go offline: Done",
                "Wait for the device to come back: Done",
                "Verify device: Warning", // the fake reports a serial other than the fake device's
                "Completed: Done",
            ],
            StepRun.Lines(ctx.Steps));
        Assert.Equal("M3106, AXIS OS 11.0.0", StepRun.Detail(ctx.Steps, "Check device"));
        Assert.Empty(ctx.Reports); // progress comes from the steps
        Assert.Single(ctx.Warnings);
    }

    [Fact]
    public async Task VerifyIsDoneWhenTheSameDeviceCameBack()
    {
        var vapix = new FakeVapixClient { Answers = n => n is 1 or >= 3 };
        var ctx = new RecordingContext(vapix);
        var plugin = new RestartTaskPlugin(Poll, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(1), TimeProvider.System);

        await StepRun.RunAsync(ctx.Steps, () => plugin.ExecuteAsync(ctx, new SerialDevice("ACCC8E000001"), null, CancellationToken.None));

        Assert.Equal(["Verify device: Done", "Completed: Done"], StepRun.Lines(ctx.Steps)[^2..]);
        Assert.Equal("M3106, AXIS OS 11.0.0", StepRun.Detail(ctx.Steps, "Verify device"));
        Assert.Empty(ctx.Warnings);
    }

    [Fact]
    public async Task AnUnreachableDeviceFailsTheCheckAndIsNotRestarted()
    {
        var vapix = new FakeVapixClient { Answers = _ => false };
        var ctx = new RecordingContext(vapix);
        var plugin = new RestartTaskPlugin(Poll, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(1), TimeProvider.System);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            StepRun.RunAsync(ctx.Steps, () => plugin.ExecuteAsync(ctx, new FakeDevice(Guid.NewGuid()), null, CancellationToken.None)));

        Assert.Equal(0, vapix.RestartCalls);
        Assert.Equal(
            [
                "Check device: Failed",
                "Send restart: Skipped",
                "Wait for the device to go offline: Skipped",
                "Wait for the device to come back: Skipped",
                "Verify device: Skipped",
            ],
            StepRun.Lines(ctx.Steps));
        Assert.Contains("Nothing was changed", StepRun.Detail(ctx.Steps, "Check device"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TimesOutWhenTheDeviceNeverComesBack()
    {
        var vapix = new FakeVapixClient { Answers = n => n == 1 };
        var ctx = new RecordingContext(vapix);
        var plugin = new RestartTaskPlugin(Poll, TimeSpan.FromMilliseconds(150), TimeSpan.FromSeconds(1), TimeProvider.System);

        var ex = await Assert.ThrowsAsync<TimeoutException>(() =>
            StepRun.RunAsync(ctx.Steps, () => plugin.ExecuteAsync(ctx, new FakeDevice(Guid.NewGuid()), null, CancellationToken.None)));

        Assert.Contains("did not come back", ex.Message, StringComparison.Ordinal);
        Assert.True(vapix.Pings > 2);
        Assert.Equal("Wait for the device to come back: Failed", StepRun.Lines(ctx.Steps)[3]);
        Assert.Equal("Verify device: Skipped", StepRun.Lines(ctx.Steps)[4]);
    }

    [Fact]
    public async Task TimesOutWhenTheDeviceNeverGoesDown()
    {
        var vapix = new FakeVapixClient { Answers = _ => true };
        var ctx = new RecordingContext(vapix);
        var plugin = new RestartTaskPlugin(Poll, TimeSpan.FromMilliseconds(150), TimeSpan.FromSeconds(1), TimeProvider.System);

        var ex = await Assert.ThrowsAsync<TimeoutException>(() =>
            StepRun.RunAsync(ctx.Steps, () => plugin.ExecuteAsync(ctx, new FakeDevice(Guid.NewGuid()), null, CancellationToken.None)));

        Assert.Contains("did not restart", ex.Message, StringComparison.Ordinal);
        Assert.Equal("Wait for the device to go offline: Failed", StepRun.Lines(ctx.Steps)[2]);
        Assert.Equal("Wait for the device to come back: Skipped", StepRun.Lines(ctx.Steps)[3]);
    }

    [Fact]
    public async Task HonoursCancellation()
    {
        var vapix = new FakeVapixClient { Answers = n => n == 1 };
        var ctx = new RecordingContext(vapix);
        var plugin = new RestartTaskPlugin(Poll, TimeSpan.FromMinutes(3), TimeSpan.FromSeconds(1), TimeProvider.System);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            StepRun.RunAsync(ctx.Steps, () => plugin.ExecuteAsync(ctx, new FakeDevice(Guid.NewGuid()), null, cts.Token)));

        Assert.Contains("Wait for the device to come back: Failed", StepRun.Lines(ctx.Steps));
        Assert.Equal("Cancelled.", StepRun.Detail(ctx.Steps, "Wait for the device to come back"));
    }

    private sealed class SerialDevice(string serial) : IDeviceInfo
    {
        public Guid Id { get; } = Guid.NewGuid();
        public string Serial => serial;
        public string Address => "192.0.2.1";
        public string? HostName => null;
        public string? Model => "M3106";
        public string? FirmwareVersion => "11.0.0";
        public DeviceStatus Status => DeviceStatus.Ok;
        public DeviceCategory Category => DeviceCategory.Camera;
        public bool HasVideo => true;
        public IReadOnlyList<DeviceApi> Apis => [];
    }

    private sealed class RecordingContext : ITaskExecutionContext
    {
        public RecordingContext(IVapixClient vapix)
        {
            Vapix = vapix;
            Steps = new TaskStepList(onWarning: Warnings.Add);
        }

        public List<(int Percent, string? Message)> Reports { get; } = [];

        public List<string> Warnings { get; } = [];

        public TaskStepList Steps { get; }

        public Guid TaskId { get; } = Guid.NewGuid();

        public IVapixClient Vapix { get; }

        public ILogger Logger => NullLogger.Instance;

        public ICorePlugin? Owner => null;

        public void ReportProgress(int percent, string? message = null) => Reports.Add((percent, message));

        public IUploadedFiles Files => throw new NotSupportedException();

        public void ReportWarning(string message) => Warnings.Add(message);

        public void Log(TaskLogLevel level, string message)
        {
        }

        public void PlanSteps(params string[] names) => Steps.Plan(names);

        public ITaskStep BeginStep(string name) => Steps.Begin(name);
    }
}
