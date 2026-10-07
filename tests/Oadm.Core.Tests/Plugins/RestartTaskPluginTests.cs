using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Core.Tests.Tasks;
using Oadm.Plugins.Restart;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

namespace Oadm.Core.Tests.Plugins;

public sealed class RestartTaskPluginTests
{
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
    public async Task WaitsForTheDeviceToGoDownAndComeBack()
    {
        // Ping 1: still up, 2-4: down (rebooting), 5: back.
        var vapix = new FakeVapixClient { Answers = n => n is 1 or >= 5 };
        var ctx = new RecordingContext(vapix);
        var plugin = new RestartTaskPlugin(Poll, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(1), TimeProvider.System);

        await plugin.ExecuteAsync(ctx, new FakeDevice(Guid.NewGuid()), null, CancellationToken.None);

        Assert.Equal(1, vapix.RestartCalls);
        Assert.Equal(5, vapix.Pings);
        Assert.Equal((100, "Device is back online"), ctx.Reports[^1]);
        Assert.Equal(0, ctx.Reports[0].Percent);
        Assert.True(ctx.Reports.Select(r => r.Percent).SequenceEqual(ctx.Reports.Select(r => r.Percent).Order()), "progress must not go backwards");
    }

    [Fact]
    public async Task TimesOutWhenTheDeviceNeverComesBack()
    {
        var vapix = new FakeVapixClient { Answers = _ => false };
        var plugin = new RestartTaskPlugin(Poll, TimeSpan.FromMilliseconds(150), TimeSpan.FromSeconds(1), TimeProvider.System);

        var ex = await Assert.ThrowsAsync<TimeoutException>(() =>
            plugin.ExecuteAsync(new RecordingContext(vapix), new FakeDevice(Guid.NewGuid()), null, CancellationToken.None));

        Assert.Contains("did not come back", ex.Message, StringComparison.Ordinal);
        Assert.True(vapix.Pings > 1);
    }

    [Fact]
    public async Task TimesOutWhenTheDeviceNeverGoesDown()
    {
        var vapix = new FakeVapixClient { Answers = _ => true };
        var plugin = new RestartTaskPlugin(Poll, TimeSpan.FromMilliseconds(150), TimeSpan.FromSeconds(1), TimeProvider.System);

        var ex = await Assert.ThrowsAsync<TimeoutException>(() =>
            plugin.ExecuteAsync(new RecordingContext(vapix), new FakeDevice(Guid.NewGuid()), null, CancellationToken.None));

        Assert.Contains("did not restart", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HonoursCancellation()
    {
        var vapix = new FakeVapixClient { Answers = _ => false };
        var plugin = new RestartTaskPlugin(Poll, TimeSpan.FromMinutes(3), TimeSpan.FromSeconds(1), TimeProvider.System);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            plugin.ExecuteAsync(new RecordingContext(vapix), new FakeDevice(Guid.NewGuid()), null, cts.Token));
    }

    private sealed class RecordingContext(IVapixClient vapix) : ITaskExecutionContext
    {
        public List<(int Percent, string? Message)> Reports { get; } = [];

        public Guid TaskId { get; } = Guid.NewGuid();

        public IVapixClient Vapix { get; } = vapix;

        public ILogger Logger => NullLogger.Instance;

        public ICorePlugin? Owner => null;

        public void ReportProgress(int percent, string? message = null) => Reports.Add((percent, message));

        public IUploadedFiles Files => throw new NotSupportedException();

        public void ReportWarning(string message)
        {
        }

        public void Log(TaskLogLevel level, string message)
        {
        }
    }
}
