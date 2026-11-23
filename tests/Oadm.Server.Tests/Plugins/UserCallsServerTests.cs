using Oadm.Core.Auth;
using Oadm.Core.Plugins;
using Oadm.Server.Tests.DeviceGroups;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Tasks;

namespace Oadm.Server.Tests.Plugins;

/// <summary>
/// The server's <see cref="ICorePluginContext.UserCalls"/> (docs/spec/schedules.md "Host: running as the owner"): a bundled
/// plugin gets a stored user's current call with the role and the device group of now, tasks as that user; a disabled or
/// unknown account none; a plugin that is not bundled no user calls at all.
/// </summary>
public sealed class UserCallsServerTests
{
    private static CancellationToken Ct => CancellationToken.None;

    private static async Task<ICorePluginContext> StartAsync(CanaryTests.Canary canary, string id, bool bundled)
    {
        var plugin = new ContextPlugin(id);
        Assert.True(canary.Host.Get<PluginRegistry>().RegisterCorePlugin(plugin, new PluginOrigin(id, "1.0.0", null) { MinSdkVersion = SdkInfo.Version, Bundled = bundled }));
        await canary.Host.Get<CorePluginHost>().StartAllAsync(Ct);
        return plugin.Context!;
    }

    [Fact]
    public async Task ABundledPluginRunsAsTheUserWithTheRoleAndGroupOfNow()
    {
        await using var canary = await CanaryTests.Canary.StartAsync();
        var ctx = await StartAsync(canary, "test.user-calls", bundled: true);

        var call = await ctx.UserCalls!.ForUserAsync(canary.User.UserName, Ct);

        Assert.NotNull(call);
        Assert.Equal((canary.User.UserName, string.Empty, false), (call.UserName, call.SessionId, call.HasAllDevices));
        Assert.True(call.Has(PermissionKeys.Task(CanaryTests.CanaryTaskPlugin.PluginId)));
        Assert.Equal([canary.Inside], (await call.Devices.ListAsync(Ct)).Select(d => d.Id));
        await Assert.ThrowsAsync<DevicesNotAvailableException>(() => call.Tasks.RunAsync(CanaryTests.CanaryTaskPlugin.PluginId, [canary.Outside], null, "x", Ct));

        var ids = await call.Tasks.RunAsync(CanaryTests.CanaryTaskPlugin.PluginId, [canary.Inside], null, "Schedule Night (canary)", Ct);

        var engine = canary.Host.Get<Core.Tasks.TaskEngine>();
        await engine.WaitForCompletionAsync(Assert.Single(ids), Ct).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(canary.User.UserName, (await engine.GetAsync(ids[0], Ct))!.Owner); // the user, never what the plugin says
    }

    [Fact]
    public async Task ARoleOrGroupChangeShowsOnTheNextCallAndADisabledAccountHasNone()
    {
        await using var canary = await CanaryTests.Canary.StartAsync();
        var ctx = await StartAsync(canary, "test.user-calls", bundled: true);
        var users = canary.Host.Get<UserStore>();
        Assert.True((await ctx.UserCalls!.ForUserAsync(canary.User.UserName, Ct))!.Has(PermissionKeys.Task(CanaryTests.CanaryTaskPlugin.PluginId)));

        await users.UpdateAsync(canary.User.UserId, BuiltInRoles.ViewerId, null, null, DeviceScope.All, null, Ct);
        var next = (await ctx.UserCalls.ForUserAsync(canary.User.UserName, Ct))!;
        Assert.False(next.Has(HostPermissions.DevicesAddRemove));
        Assert.True(next.HasAllDevices);
        Assert.Equal(2, (await next.Devices.ListAsync(Ct)).Count);

        await users.UpdateAsync(canary.User.UserId, null, disabled: true, null, null, null, Ct);
        Assert.Null(await ctx.UserCalls.ForUserAsync(canary.User.UserName, Ct));
        Assert.Null(await ctx.UserCalls.ForUserAsync("nobody", Ct));
    }

    [Fact]
    public async Task APluginThatIsNotBundledGetsNoUserCalls()
    {
        await using var canary = await CanaryTests.Canary.StartAsync();

        var ctx = await StartAsync(canary, "test.third-party", bundled: false);

        Assert.Null(ctx.UserCalls);
    }

    private sealed class ContextPlugin(string id) : ICorePlugin
    {
        public string Id => id;

        public string DisplayName => id;

        public string? IconKey => null;

        public IReadOnlyList<ITaskPlugin> TaskPlugins => [];

        public ICorePluginContext? Context { get; private set; }

        public Task StartAsync(ICorePluginContext ctx, CancellationToken ct)
        {
            Context = ctx;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

        public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) => Task.FromResult<string?>(null);
    }
}
