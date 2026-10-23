using Oadm.Core.Auth;
using Oadm.Core.Devices;
using Oadm.Core.Plugins;
using Oadm.Core.Tasks;
using Oadm.Core.Tests.Tasks;
using Oadm.Sdk.Testing;

namespace Oadm.Core.Tests.DeviceGroups;

/// <summary>
/// The background boundary (docs/spec/device-scope.md, Enforcement): the caller of a call never flows into task runs, the
/// handlers of host events or work started detached, so nothing outlives the call with the user's rights.
/// </summary>
public sealed class CallerBoundaryTests
{
    private static AuthenticatedCaller Caller() =>
        new(Guid.NewGuid(), "anna", BuiltInRoles.OperatorId, PermissionSet.All, "local", "pc", "hash");

    [Fact]
    public async Task SuppressRunDetachedAndRaiseClearTheCaller()
    {
        using (CallerContext.Use(Caller()))
        {
            Assert.NotNull(CallerContext.Current);
            using (CallerContext.Suppress())
            {
                Assert.Null(CallerContext.Current);
            }

            Assert.NotNull(CallerContext.Current);
            AuthenticatedCaller? inside = Caller();
            await CallerContext.RunDetached(() =>
            {
                inside = CallerContext.Current;
                return Task.CompletedTask;
            });
            Assert.Null(inside);

            AuthenticatedCaller? handler = Caller();
            EventHandler raise = (_, _) => handler = CallerContext.Current;
            CallerContext.Raise(raise, null, EventArgs.Empty);
            Assert.Null(handler);
            Assert.NotNull(CallerContext.Current);
        }
    }

    [Fact]
    public async Task ATaskStartedInAUsersCallRunsWithoutTheCaller()
    {
        var devices = new FakeDeviceRepository();
        var registry = new PluginRegistry();
        AuthenticatedCaller? seen = Caller();
        Assert.True(registry.RegisterTaskPlugin(new DelegateTaskPlugin("t.caller", (_, _, _) =>
        {
            seen = CallerContext.Current;
            return Task.CompletedTask;
        }), new PluginOrigin("test", "1.0.0", null)));
        var store = new InMemoryTaskStore();
        await using var engine = new TaskEngine(store, registry, devices, new FakeVapixClientFactory());
        var id = devices.Add();

        Guid taskId;
        using (CallerContext.Use(Caller()))
        {
            taskId = (await engine.RunAsync("t.caller", [id], null, "x", CancellationToken.None))[0];
        }

        await Eventually.TrueAsync(async () => (await store.GetAsync(taskId, CancellationToken.None))?.State == TaskState.Done, "the task to finish");
        Assert.Null(seen);
        Assert.StartsWith("anna@pc", (await store.GetAsync(taskId, CancellationToken.None))!.Owner, StringComparison.Ordinal); // the owner is still the user
    }

    [Fact]
    public void TheHandlersOfTheDeviceFeedNeverSeeTheCaller()
    {
        var feed = new DeviceChangeFeed();
        AuthenticatedCaller? seen = Caller();
        var calls = 0;
        feed.Changed += (_, _) =>
        {
            seen = CallerContext.Current;
            calls++;
        };
        using (CallerContext.Use(Caller()))
        {
            feed.Publish(new DeviceChange(DeviceChangeKind.Updated, Guid.NewGuid(), new Device { Serial = "ACCC8E000001" }));
            Assert.Null(seen);
            seen = Caller();
            feed.PublishMany([new DeviceChange(DeviceChangeKind.Updated, Guid.NewGuid(), new Device { Serial = "ACCC8E000002" })]);
            Assert.Null(seen);
        }

        Assert.Equal(2, calls);
    }
}
