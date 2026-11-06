using Oadm.Core.Plugins;
using Oadm.Plugins.VapixCommander;
using Oadm.Server.Tests.Support;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests;

/// <summary>The VAPIX Commander core plugin inside the real server: page calls through PluginService, hidden rollout task.</summary>
public sealed class VapixCommanderServerTests
{
    /// <summary>Two read-only sample commands (param.cgi list Brand, basicdeviceinfo) in a temp library folder.</summary>
    internal static string WriteSampleLibrary()
    {
        var directory = Path.Combine(Path.GetTempPath(), "oadm-commander-library-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "Common.json"), """
            {
              "formatVersion": 1,
              "category": "Common",
              "commands": [
                {
                  "id": "common.brand.read", "version": 1, "name": "Read brand parameters", "category": "Common",
                  "requires": [ { "api": "param-cgi", "minVersion": "1.0" } ], "writes": false, "fields": [],
                  "request": { "method": "GET", "path": "/axis-cgi/param.cgi", "query": { "action": "list", "group": "Brand" } },
                  "response": { "kind": "param-cgi", "extract": [ { "label": "Product", "param": "Brand.ProdNbr" } ] }
                },
                {
                  "id": "common.basicdeviceinfo.read", "version": 1, "name": "Read basic device information", "category": "Common",
                  "requires": [ { "api": "basic-device-info", "minVersion": "1.0" } ], "writes": false, "fields": [],
                  "request": { "method": "POST", "path": "/axis-cgi/basicdeviceinfo.cgi", "bodyType": "json",
                               "body": { "apiVersion": "1.0", "method": "getAllProperties" } },
                  "response": { "kind": "json-rpc", "extract": [ { "label": "Model", "path": "data.propertyList.ProdNbr" }, { "label": "AXIS OS", "path": "data.propertyList.Version" } ] }
                }
              ]
            }
            """);
        return directory;
    }

    internal static async Task<VapixCommanderPlugin> RegisterAsync(TestServerHost host, string libraryDirectory)
    {
        var plugin = new VapixCommanderPlugin(libraryDirectory);
        Assert.True(host.Get<PluginRegistry>().RegisterCorePlugin(plugin, new PluginOrigin(VapixCommanderPlugin.PluginId, "0.1.0", null)));
        await host.Get<CorePluginHost>().StartAllAsync(CancellationToken.None);
        return plugin;
    }

    internal static async Task<T> InvokeAsync<T>(TestServerHost host, string method, object? request)
        where T : class, new()
    {
        var reply = await host.Plugins.InvokeAsync(new Proto.InvokeRequest
        {
            PluginId = VapixCommanderPlugin.PluginId,
            Method = method,
            PayloadJson = request is null ? string.Empty : CommandJson.Write(request),
        });
        return CommandJson.Read<T>(reply.PayloadJson);
    }

    [Fact]
    public async Task PageCallsReachThePluginAndTheRolloutTaskIsHiddenFromMenus()
    {
        await using var host = await TestServerHost.StartAsync();
        var library = WriteSampleLibrary();
        try
        {
            await RegisterAsync(host, library);

            var pages = await host.Plugins.ListCorePluginsAsync(new Proto.Empty());
            var page = Assert.Single(pages.Plugins);
            Assert.Equal("VAPIX Commander", page.DisplayName);
            Assert.Equal("command", page.IconKey);

            var commands = await InvokeAsync<CommandListReply>(host, CommanderMethods.ListLibrary, null);
            Assert.Equal(2, commands.Commands.Count);

            var saved = await InvokeAsync<SaveCommandReply>(host, CommanderMethods.Save, new SaveCommandRequest { Command = commands.Commands[0].Command });
            Assert.Null(saved.Error);
            Assert.Single((await InvokeAsync<CommandListReply>(host, CommanderMethods.ListSaved, null)).Commands);

            var menus = await host.Tasks.ListTaskPluginsAsync(new Proto.ListTaskPluginsRequest());
            Assert.DoesNotContain(menus.Plugins, p => p.Id == RolloutTaskPlugin.PluginId);
        }
        finally
        {
            Directory.Delete(library, recursive: true);
        }
    }

    /// <summary>
    /// The rollout task is hidden and has no permission a role could hold: the page's "Run on many devices" is the check
    /// (docs/spec/security.md, Page tasks), and TaskService.Run never starts it.
    /// </summary>
    [Fact]
    public async Task AnOperatorWithTheRolloutRightStartsARolloutAndRunNeverStartsTheHiddenTask()
    {
        await using var host = await TestServerHost.StartAsync();
        var library = WriteSampleLibrary();
        try
        {
            await RegisterAsync(host, library);
            await host.Get<Core.Auth.RoleStore>().SyncCatalogAsync(Core.Auth.PermissionCatalog.Build(host.Get<PluginRegistry>()), CancellationToken.None);
            var device = (await DeviceServiceTests.AddDeviceAsync(host, "10.9.0.1", 1)).Id;
            var op = await host.InvokerForUserAsync("tech1", Sdk.Plugins.UserRole.Operator);

            var reply = await new Proto.PluginService.PluginServiceClient(op).InvokeAsync(new Proto.InvokeRequest
            {
                PluginId = VapixCommanderPlugin.PluginId,
                Method = CommanderMethods.Rollout,
                PayloadJson = CommandJson.Write(new RolloutRequest
                {
                    DeviceIds = [device],
                    Commands = [new RolloutCommand { Command = new CommandRef { Source = CommandSources.Library, Id = "common.brand.read" } }],
                }),
            });

            var started = CommandJson.Read<RolloutReply>(reply.PayloadJson);
            Assert.Null(started.Error);
            var taskId = Assert.Single(started.TaskIds);
            var tasks = await host.Tasks.ListAsync(new Proto.ListTasksRequest());
            Assert.Contains(tasks.Tasks, t => t.Id == taskId.ToString() && t.PluginId == RolloutTaskPlugin.PluginId);

            var direct = await Assert.ThrowsAsync<Grpc.Core.RpcException>(async () => await host.Tasks.RunAsync(
                new Proto.RunTaskRequest { PluginId = RolloutTaskPlugin.PluginId, PayloadJson = "{}", DeviceIds = { device.ToString() } }));
            Assert.Equal((Grpc.Core.StatusCode.PermissionDenied, "This task starts only from its page."), (direct.StatusCode, direct.Status.Detail));
        }
        finally
        {
            Directory.Delete(library, recursive: true);
        }
    }
}
