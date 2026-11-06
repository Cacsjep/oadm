using System.Text.Json;

using Grpc.Core;

using Oadm.Core.Auth;
using Oadm.Core.Plugins;
using Oadm.Sdk.Plugins;
using Oadm.Server.Tests.Support;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests;

/// <summary>
/// A page call (PluginService.Invoke) with a payload the plugin cannot read is refused with the plain text of
/// <see cref="PluginJson"/> (INVALID_ARGUMENT), never answered as a failure of the plugin (INTERNAL) with the reader's text.
/// </summary>
public sealed class PluginJsonRefusalTests
{
    private const string PluginId = "test.json";

    private static async Task<TestServerHost> StartAsync()
    {
        var host = await TestServerHost.StartAsync();
        host.Get<PluginRegistry>().RegisterCorePlugin(new JsonPlugin(), new PluginOrigin("tests", "1.0.0", null));
        await host.Get<RoleStore>().SyncCatalogAsync(PermissionCatalog.Build(host.Get<PluginRegistry>()), CancellationToken.None);
        await host.Get<CorePluginHost>().StartAllAsync(CancellationToken.None);
        return host;
    }

    private static Task<RpcException> RefusedAsync(TestServerHost host, string method, string payload) =>
        Assert.ThrowsAsync<RpcException>(async () => await host.Plugins.InvokeAsync(new Proto.InvokeRequest { PluginId = PluginId, Method = method, PayloadJson = payload }));

    [Theory]
    [InlineData("must", "{ \"name\": ", "The request cannot be read.")]
    [InlineData("must", "{ \"port\": \"hunter2\" }", "The request cannot be read.")]
    [InlineData("must", "", "The request is empty.")]
    [InlineData("must", "null", "The request is empty.")]
    [InlineData("may", "[1, 2]", "The request cannot be read.")]
    public async Task APayloadThePluginCannotReadIsARefusalWithAPlainText(string method, string payload, string expected)
    {
        await using var host = await StartAsync();

        var refused = await RefusedAsync(host, method, payload);

        Assert.Equal(StatusCode.InvalidArgument, refused.StatusCode);
        Assert.Equal(expected, refused.Status.Detail); // nothing of the payload, no parameter name, no reader text
    }

    [Fact]
    public async Task ACallWithoutAPayloadReadsAsAnEmptyOneWhereThePluginAllowsIt()
    {
        await using var host = await StartAsync();

        var reply = await host.Plugins.InvokeAsync(new Proto.InvokeRequest { PluginId = PluginId, Method = "may" });

        Assert.Equal("""{"port":0}""", reply.PayloadJson);
    }

    [Fact]
    public async Task AReaderErrorThePluginLetsThroughIsStillAFailureOfThePlugin()
    {
        // What a plugin without the helper gets: this is why every plugin parses through PluginJson.
        await using var host = await StartAsync();

        var failed = await RefusedAsync(host, "raw", "{");

        Assert.Equal(StatusCode.Internal, failed.StatusCode);
        Assert.StartsWith("Plugin test.json failed: ", failed.Status.Detail, StringComparison.Ordinal);
    }

    private sealed record Request(string? Name, int Port);

    /// <summary>"must" needs a payload, "may" reads none as an empty one, "raw" parses without the helper.</summary>
    private sealed class JsonPlugin : ICorePlugin
    {
        private static readonly JsonSerializerOptions Options = PluginJson.CreateOptions();

        public string Id => PluginId;

        public string DisplayName => "JSON test";

        public string? IconKey => null;

        public IReadOnlyList<ITaskPlugin> TaskPlugins => [];

        public Task StartAsync(ICorePluginContext ctx, CancellationToken ct) => Task.CompletedTask;

        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

        public UserRole RequiredRole(string method) => UserRole.Operator;

        public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) => Task.FromResult<string?>(method switch
        {
            "must" => PluginJson.Serialize(PluginJson.Parse<Request>(payloadJson, Options), Options),
            "may" => PluginJson.Serialize(PluginJson.ParseOrEmpty<Request>(payloadJson, Options), Options),
            _ => JsonSerializer.Serialize(JsonSerializer.Deserialize<Request>(payloadJson ?? "{}", Options), Options),
        });
    }
}
