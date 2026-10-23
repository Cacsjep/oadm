using Oadm.Core.Auth;
using Oadm.Core.Plugins;
using Oadm.Core.Tasks;
using Oadm.Core.Tests.DeviceGroups;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

namespace Oadm.Core.Tests.Plugins;

/// <summary>A user's task plugin query (<see cref="TaskPluginQueries"/> with the caller): only the view, only packages built for the call context.</summary>
public sealed class TaskQueryCallTests : DeviceGroupTestBase
{
    [Fact]
    public async Task AGroupUsersQueryReachesOnlyItsViewAndOldPackagesAreRefused()
    {
        var b = Sites.Add("Building B");
        var inside = await AddDeviceAsync("ACCC8E000001", siteId: b);
        var outside = await AddDeviceAsync("ACCC8E000002");
        var group = (await SaveGroupAsync("B", sites: [b])).Group;
        var registry = new PluginRegistry();
        var plugin = new QueryPlugin("q.new");
        var old = new QueryPlugin("q.old");
        Assert.True(registry.RegisterTaskPlugin(plugin, new PluginOrigin("new", "1.0.0", null) { MinSdkVersion = "0.4.0", DeclaredSiteSupport = Sdk.Sites.SiteSupport.Full }));
        Assert.True(registry.RegisterTaskPlugin(old, new PluginOrigin("old", "1.0.0", null) { MinSdkVersion = "0.3.0", DeclaredSiteSupport = Sdk.Sites.SiteSupport.Full }));
        var vapix = new CountingVapix();
        var queries = new TaskPluginQueries(registry, Devices, vapix);
        var anna = await CallerAsync("anna", DeviceScope.Group(group.Id));
        var root = await CallerAsync("root", DeviceScope.All);

        // The device: in the view, else like an unknown one; nothing is sent.
        Assert.Equal("1 anna", await queries.QueryAsync(anna, "q.new", inside.Id, "count", null, Ct));
        var refused = await Assert.ThrowsAsync<DevicesNotAvailableException>(() => queries.QueryAsync(anna, "q.new", outside.Id, "count", null, Ct));
        var unknown = await Assert.ThrowsAsync<DevicesNotAvailableException>(() => queries.QueryAsync(anna, "q.new", Guid.NewGuid(), "count", null, Ct));
        Assert.Equal(refused.Message, unknown.Message);
        Assert.Equal([inside.Id], vapix.Created);

        // Other devices of the payload through the call.
        await Assert.ThrowsAsync<DevicesNotAvailableException>(() => queries.QueryAsync(anna, "q.new", inside.Id, "source", outside.Id.ToString(), Ct));
        Assert.Equal([inside.Id, inside.Id], vapix.Created);

        // A package before SDK 0.4.0: refused for the group user, unchanged for users with all devices.
        await Assert.ThrowsAsync<DeviceAccessRefusedException>(() => queries.QueryAsync(anna, "q.old", inside.Id, "count", null, Ct));
        Assert.Equal("2 root", await queries.QueryAsync(root, "q.old", outside.Id, "count", null, Ct));
        Assert.Equal("2 root", await queries.QueryAsync(root, "q.new", outside.Id, "count", null, Ct));
    }

    [Fact]
    public async Task TheAddressCheckSeesEveryDeviceOnTheRouteWithoutNamingIt()
    {
        var b = Sites.Add("Building B");
        var inside = await AddDeviceAsync("ACCC8E000011", siteId: b);
        await AddDeviceAsync("ACCC8E000012", siteId: b); // outside the group, same network
        await AddDeviceAsync("ACCC8E000013"); // this server: another network
        var group = (await SaveGroupAsync("One", serials: ["ACCC8E000011"])).Group;
        var registry = new PluginRegistry();
        Assert.True(registry.RegisterTaskPlugin(new QueryPlugin("q.new"), new PluginOrigin("new", "1.0.0", null) { MinSdkVersion = "0.4.0", DeclaredSiteSupport = Sdk.Sites.SiteSupport.Full }));
        var queries = new TaskPluginQueries(registry, Devices, new CountingVapix());
        var anna = await CallerAsync("anna", DeviceScope.Group(group.Id));

        Assert.Equal("1 anna", await queries.QueryAsync(anna, "q.new", inside.Id, "count", null, Ct));
        Assert.Equal("10.0.0.11,10.0.0.12", await queries.QueryAsync(anna, "q.new", inside.Id, "addresses", "10.0.0.11,10.0.0.12,10.0.0.13,10.0.0.14,bogus", Ct));
    }

    private async Task<PluginCaller> CallerAsync(string name, DeviceScope scope)
    {
        var user = await Users.AddAsync(name, "pw", BuiltInRoles.OperatorId, scope, Ct);
        var caller = CallerOf(user);
        return new PluginCaller(caller, await Views.ForAsync(caller, Ct));
    }

    private sealed class QueryPlugin(string id) : ITaskPlugin, ITaskPluginQuery
    {
        public string Id => id;

        public string DisplayName => "Query";

        public string? IconKey => null;

        public bool ShowInToolbar => false;

        public bool RequiresDialog => true;

        public bool CanRun(IDeviceInfo device) => true;

        public Task ExecuteAsync(ITaskExecutionContext ctx, IDeviceInfo device, string? payloadJson, CancellationToken ct) => Task.CompletedTask;

        public Task<string?> QueryAsync(ITaskQueryContext ctx, IDeviceInfo device, string method, string? payloadJson, CancellationToken ct) =>
            Task.FromResult<string?>("without the call");

        public async Task<string?> QueryAsync(ITaskQueryContext ctx, ICallContext caller, IDeviceInfo device, string method, string? payloadJson, CancellationToken ct)
        {
            if (method == "addresses")
            {
                var used = await ctx.ManagedAddressesAsync(payloadJson!.Split(','), ct);
                return string.Join(",", used.Order(StringComparer.Ordinal));
            }

            if (method == "source")
            {
                await caller.Vapix.CreateAsync(Guid.Parse(payloadJson!), ct);
                return null;
            }

            return $"{(await ctx.Devices!.ListAsync(ct)).Count} {caller.UserName}";
        }
    }

    private sealed class CountingVapix : IVapixClientFactory
    {
        public List<Guid> Created { get; } = [];

        public Task<IVapixClient> CreateAsync(Guid deviceId, CancellationToken ct)
        {
            lock (Created)
            {
                Created.Add(deviceId);
            }

            return Task.FromResult<IVapixClient>(new Tasks.FakeVapixClient());
        }
    }
}
