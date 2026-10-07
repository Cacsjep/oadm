using Oadm.Core.Plugins;
using Oadm.Core.Tasks;
using Oadm.Core.Vapix;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

namespace Oadm.Core.Tests.Tasks;

public sealed class TaskPluginQueriesTests
{
    private readonly FakeDeviceRepository _devices = new();
    private readonly FakeVapixClientFactory _vapix = new();
    private readonly PluginRegistry _registry = new();

    private TaskPluginQueries Queries(TimeSpan? timeout = null) =>
        new(_registry, _devices, _vapix) { Timeout = timeout ?? TaskPluginQueries.DefaultTimeout };

    [Fact]
    public async Task TheQueryGetsTheDeviceAVapixClientAndThePayload()
    {
        var device = _devices.Add();
        Register(async (ctx, d, method, payload, ct) =>
        {
            var apis = await ctx.Vapix.GetApiListAsync(ct);
            Assert.NotNull(ctx.Logger);
            return $"{d.Id}|{method}|{payload}|{apis.Count}";
        });

        var result = await Queries().QueryAsync("q.users", device, "listUsers", "{}", CancellationToken.None);

        Assert.Equal($"{device}|listUsers|{{}}|0", result);
    }

    [Fact]
    public async Task UnknownPluginOrDeviceIsNotFound()
    {
        Register((_, _, _, _, _) => Task.FromResult<string?>(null));

        Assert.Equal(TaskQueryError.NotFound, (await Fails("nope", _devices.Add())).Error);
        Assert.Equal(TaskQueryError.NotFound, (await Fails("q.users", Guid.NewGuid())).Error);
    }

    [Fact]
    public async Task APluginWithoutQueriesIsNotSupported()
    {
        _registry.RegisterTaskPlugin(new DelegateTaskPlugin("t.plain", (_, _, _) => Task.CompletedTask), new PluginOrigin("test", "1.0.0", null));

        var ex = await Fails("t.plain", _devices.Add());

        Assert.Equal(TaskQueryError.NotSupported, ex.Error);
    }

    [Fact]
    public async Task AnEmptyMethodIsInvalid()
    {
        Register((_, _, _, _, _) => Task.FromResult<string?>(null));

        var ex = await Assert.ThrowsAsync<TaskQueryException>(() => Queries().QueryAsync("q.users", _devices.Add(), " ", null, CancellationToken.None));

        Assert.Equal(TaskQueryError.InvalidArgument, ex.Error);
    }

    [Fact]
    public async Task AnIncompatibleDeviceIsAFailedPreconditionWithTheMessage()
    {
        Register((_, _, _, _, _) => throw new DeviceNotCompatibleException("user-management", "2.0", "1.2"));

        var ex = await Fails("q.users", _devices.Add());

        Assert.Equal(TaskQueryError.FailedPrecondition, ex.Error);
        Assert.Equal("Device has user-management 1.2, needs 2.0 or later. Nothing was changed.", ex.Message);
    }

    [Fact]
    public async Task ACertificateChangeBlocksQueries()
    {
        var called = false;
        Register((_, _, _, _, _) =>
        {
            called = true;
            return Task.FromResult<string?>(null);
        });

        var ex = await Fails("q.users", _devices.Add(DeviceStatus.CertificateChanged));

        Assert.Equal(TaskQueryError.FailedPrecondition, ex.Error);
        Assert.False(called);
    }

    [Fact]
    public async Task DeviceErrorsAreUnavailableOrFailedPrecondition()
    {
        Register((_, _, method, _, _) => method == "auth"
            ? throw new VapixAuthenticationException("401")
            : throw new VapixException("HTTP 500"));
        var device = _devices.Add();

        Assert.Equal(TaskQueryError.Unavailable, (await Fails("q.users", device, "x")).Error);
        var auth = await Fails("q.users", device, "auth");
        Assert.Equal(TaskQueryError.FailedPrecondition, auth.Error);
        Assert.Equal("The device rejected the stored credentials.", auth.Message);
    }

    [Fact]
    public async Task OtherPluginErrorsKeepTheirMessage()
    {
        Register((_, _, _, _, _) => throw new InvalidOperationException("boom"));

        var ex = await Fails("q.users", _devices.Add());

        Assert.Equal(TaskQueryError.Failed, ex.Error);
        Assert.Equal("boom", ex.Message);
    }

    [Fact]
    public async Task ASlowQueryTimesOut()
    {
        Register(async (_, _, _, _, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return null;
        });

        var ex = await Assert.ThrowsAsync<TaskQueryException>(
            () => Queries(TimeSpan.FromMilliseconds(50)).QueryAsync("q.users", _devices.Add(), "slow", null, CancellationToken.None));

        Assert.Equal(TaskQueryError.Timeout, ex.Error);
    }

    [Fact]
    public async Task CallerCancellationIsNotATimeout()
    {
        Register(async (_, _, _, _, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return null;
        });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Queries().QueryAsync("q.users", _devices.Add(), "slow", null, cts.Token));
    }

    private Task<TaskQueryException> Fails(string pluginId, Guid device, string method = "m") =>
        Assert.ThrowsAsync<TaskQueryException>(() => Queries().QueryAsync(pluginId, device, method, null, CancellationToken.None));

    private void Register(Func<ITaskQueryContext, IDeviceInfo, string, string?, CancellationToken, Task<string?>> query)
    {
        Assert.True(_registry.RegisterTaskPlugin(new QueryPlugin(query), new PluginOrigin("test", "1.0.0", null)));
    }

    private sealed class QueryPlugin(Func<ITaskQueryContext, IDeviceInfo, string, string?, CancellationToken, Task<string?>> query)
        : ITaskPlugin, ITaskPluginQuery
    {
        public string Id => "q.users";

        public string DisplayName => "Users";

        public string? IconKey => null;

        public bool ShowInToolbar => false;

        public bool RequiresDialog => true;

        public bool CanRun(IDeviceInfo device) => true;

        public Task ExecuteAsync(ITaskExecutionContext ctx, IDeviceInfo device, string? payloadJson, CancellationToken ct) => Task.CompletedTask;

        public Task<string?> QueryAsync(ITaskQueryContext ctx, IDeviceInfo device, string method, string? payloadJson, CancellationToken ct) =>
            query(ctx, device, method, payloadJson, ct);
    }
}
