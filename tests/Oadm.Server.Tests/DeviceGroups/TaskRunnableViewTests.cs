using Oadm.Core.Plugins;
using Oadm.Server.Tasks;

namespace Oadm.Server.Tests.DeviceGroups;

/// <summary>The runnable sets of the context menu for a device group: intersected with the view before they are encoded.</summary>
public sealed class TaskRunnableViewTests
{
    [Fact]
    public void RunnableSetsAndReasonsKeepOnlyTheViewsDevicesAndMostDevicesFirst()
    {
        var a = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid()).ToArray();
        var plugin = new RegisteredTaskPlugin(new TaskRunnableViewPlugin(), null, new PluginOrigin("test", "1.0.0", null));
        var entry = new TaskPluginRunnable(
            plugin,
            [a[0], a[1]],
            [a[2], a[3], a[4], a[5]],
            [new NotRunnableReason("Needs the API x", [a[2], a[3], a[4]]), new NotRunnableReason("Site offline", [a[5]])]);

        var within = TaskPluginRunnableCache.Within(entry, new HashSet<Guid> { a[1], a[4], a[5] });

        Assert.Equal([a[1]], within.Runnable);
        Assert.Equal(["Needs the API x", "Site offline"], within.Reasons.Select(r => r.Reason));
        Assert.Equal([a[4], a[5]], within.NotRunnable);
        var proto = Mapping.Mappers.ToProto(within, compact: true);
        var text = proto.ToString();
        Assert.All(new[] { a[0], a[2], a[3] }, id => Assert.DoesNotContain(id.ToString(), text, StringComparison.Ordinal));

        // A reason no device of the view has is dropped; the larger one comes first.
        var only = TaskPluginRunnableCache.Within(entry, new HashSet<Guid> { a[5] });
        Assert.Equal(["Site offline"], only.Reasons.Select(r => r.Reason));
        Assert.Empty(TaskPluginRunnableCache.Within(entry, new HashSet<Guid>()).NotRunnable);
    }

    private sealed class TaskRunnableViewPlugin : Sdk.Plugins.ITaskPlugin
    {
        public string Id => "test.within";

        public string DisplayName => "Within";

        public string? IconKey => null;

        public bool ShowInToolbar => false;

        public bool RequiresDialog => false;

        public bool CanRun(Sdk.Devices.IDeviceInfo device) => true;

        public Task ExecuteAsync(Sdk.Plugins.ITaskExecutionContext ctx, Sdk.Devices.IDeviceInfo device, string? payloadJson, CancellationToken ct) => Task.CompletedTask;
    }
}
