using Grpc.Core;

using Oadm.Core.Auth;
using Oadm.Core.Plugins;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;
using Oadm.Server.Plugins;
using Oadm.Server.Tests.Support;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests;

/// <summary>
/// The audit entry of an audited page call (PluginService.Invoke) is written however the call ends: a call that failed
/// or that the client gave up may have changed something already.
/// </summary>
public sealed class PluginCallAuditTests
{
    private const string PluginId = "test.outcomes";

    private static async Task<(TestServerHost Host, OutcomePlugin Plugin)> StartAsync()
    {
        var plugin = new OutcomePlugin();
        var host = await TestServerHost.StartAsync();
        host.Get<PluginRegistry>().RegisterCorePlugin(plugin, new PluginOrigin("tests", "1.0.0", null));
        await host.Get<RoleStore>().SyncCatalogAsync(PermissionCatalog.Build(host.Get<PluginRegistry>()), CancellationToken.None);
        await host.Get<CorePluginHost>().StartAllAsync(CancellationToken.None);
        return (host, plugin);
    }

    private static async Task<List<Proto.AuditEntry>> CallsAsync(TestServerHost host) =>
        [.. (await host.Audit.ListAsync(new Proto.ListAuditRequest())).Entries.Where(e => e.Action == AuditActions.PluginCall).Reverse()];

    [Fact]
    public async Task ACallThatFailsIsAuditedAsFailedAndOneThatEndsWellKeepsItsWords()
    {
        var (host, _) = await StartAsync();
        await using (host)
        {
            var failed = await Assert.ThrowsAsync<RpcException>(async () => await host.Plugins.InvokeAsync(new Proto.InvokeRequest { PluginId = PluginId, Method = "break" }));
            Assert.Equal(StatusCode.FailedPrecondition, failed.StatusCode);
            await host.Plugins.InvokeAsync(new Proto.InvokeRequest { PluginId = PluginId, Method = "save" });
            await host.Plugins.InvokeAsync(new Proto.InvokeRequest { PluginId = PluginId, Method = "read" }); // not audited

            var calls = await CallsAsync(host);
            Assert.Equal(["Changed the gate" + PluginGrpcService.FailedSuffix, "save"], calls.Select(e => e.Detail));
            Assert.All(calls, e => Assert.Equal(("admin", "Outcome test"), (e.UserName, e.Target)));
        }
    }

    [Fact]
    public async Task ACallTheClientGivesUpIsAuditedAsCancelled()
    {
        var (host, plugin) = await StartAsync();
        await using (host)
        {
            using var cts = new CancellationTokenSource();
            var call = host.Plugins.InvokeAsync(new Proto.InvokeRequest { PluginId = PluginId, Method = "wait" }, cancellationToken: cts.Token);
            await plugin.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(30)); // the plugin is at work
            await cts.CancelAsync();
            await Assert.ThrowsAsync<RpcException>(async () => await call);

            await Eventually.TrueAsync(async () => (await CallsAsync(host)).Count == 1, "the cancelled call is audited");
            var entry = Assert.Single(await CallsAsync(host));
            Assert.Equal(("admin", "Outcome test", "wait" + PluginGrpcService.CancelledSuffix), (entry.UserName, entry.Target, entry.Detail));
        }
    }

    /// <summary>"save" works, "break" throws after its work began, "wait" works until the call is cancelled; "read" is not audited.</summary>
    private sealed class OutcomePlugin : ICorePlugin
    {
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Id => PluginId;

        public string DisplayName => "Outcome test";

        public string? IconKey => null;

        public IReadOnlyList<ITaskPlugin> TaskPlugins => [];

        public Task StartAsync(ICorePluginContext ctx, CancellationToken ct) => Task.CompletedTask;

        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

        public UserRole RequiredRole(string method) => method == "read" ? UserRole.Operator : UserRole.Admin;

        public string? AuditDetail(string method, string? payloadJson) => method == "break" ? "Changed the gate" : null;

        public async Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct)
        {
            switch (method)
            {
                case "break":
                    throw new InvalidOperationException("The camera did not answer.");
                case "wait":
                    Waiting.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                    return null;
                default:
                    return method;
            }
        }
    }
}
