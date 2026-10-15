using System.Net;
using System.Runtime.CompilerServices;

using Grpc.Net.Client;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Oadm.Core.Devices;
using Oadm.Core.Discovery.Mdns;
using Oadm.Core.Plugins;
using Oadm.Core.Security;
using Oadm.Core.Tests.Hardware;
using Oadm.Core.Vapix;
using Oadm.Plugins.ActionRules.Device;
using Oadm.Plugins.ActionRules.Rules;
using Oadm.Sdk.Devices;
using Oadm.Server.Hosting;

using Xunit.Abstractions;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Plugins.ActionRules.Tests;

/// <summary>
/// End to end on every camera of dev-cameras.yaml through the real server in-process (temp data folder, the plugin
/// registered as a core plugin, gRPC PluginService.Invoke, the real task engine and device logins): one own rule per camera
/// ("OADM e2e ..."), read, found as one group across AXIS OS 10 and 12, changed to "while the rule is active" for all at once,
/// undone, deleted. Runs only with <c>OADM_ACTION_RULES_WRITE=1</c> and when the developer asks for it; other rules of the
/// cameras are never touched and the own rules are removed at the end.
/// </summary>
[Trait("Category", "HardwareWrite")]
[Collection(DevCameraGroup.Name)]
public sealed class ServerHardwareWriteTests(ITestOutputHelper output)
{
    private const string Prefix = "OADM e2e ";
    private const string Once = "com.axis.action.fixed.notification.tcp";
    private const string WhileActive = "com.axis.action.unlimited.notification.tcp";

    [RulesHardwareWriteTests.WriteFact]
    public async Task One_group_changed_undone_and_deleted_on_every_camera_through_the_server()
    {
        var clients = DevCameras.All.Select(c => (Camera: c, Vapix: Client(c))).ToList();
        var dataDirectory = Path.Combine(Path.GetTempPath(), "oadm-action-rules-e2e", Guid.NewGuid().ToString("N"));
        var app = OadmServerHost.Build([], new OadmServerHostOptions
        {
            DataDirectory = dataDirectory,
            PluginRoots = [],
            LogToConsole = false,
            ConfigureBuilder = b => b.WebHost.UseTestServer(),
            ConfigureServices = services =>
            {
                services.RemoveAll<IMdnsBrowser>();
                services.AddSingleton<IMdnsBrowser, SilentMdnsBrowser>();
            },
        });
        app.Services.GetRequiredService<PluginRegistry>().RegisterCorePlugin(new ActionRulesPlugin(), PluginOrigin.FromAssembly(typeof(ActionRulesPlugin).Assembly));
        await OadmServerHost.StartAsync(app);
        var stamp = Guid.NewGuid().ToString("N")[..6];
        try
        {
            foreach (var (camera, vapix) in clients)
            {
                var set = await SoapActionEngine.ReadAsync(vapix, CancellationToken.None);
                await new RuleWriter(vapix, set, inPlace: false).AddAsync(new DeviceRule(string.Empty, Prefix + stamp, false, PageTests.VirtualInput, [], null, new RuleAction(Once,
                    [new("host", "192.0.2.10"), new("port", "9999"), new("qos", "0"), new("message", "e2e " + camera.Address)])), CancellationToken.None);
            }

            var server = app.GetTestServer();
            using var channel = GrpcChannel.ForAddress(server.BaseAddress, new GrpcChannelOptions { HttpHandler = server.CreateHandler(), MaxReceiveMessageSize = 32 * 1024 * 1024 });
            var token = await Oadm.Server.Auth.InProcessAccess.CreateTokenAsync(app.Services);
            var invoker = Grpc.Core.Interceptors.ChannelExtensions.Intercept(channel, new Oadm.Contracts.Security.AuthHeaderInterceptor(() => token));
            var plugins = new Proto.PluginService.PluginServiceClient(invoker);
            async Task<string> Invoke(string method, object? payload = null) =>
                (await plugins.InvokeAsync(new Proto.InvokeRequest { PluginId = ActionRulesInfo.PluginId, Method = method, PayloadJson = payload is null ? string.Empty : ActionRulesJson.Serialize(payload) })).PayloadJson;

            foreach (var (camera, vapix) in clients)
            {
                var info = await vapix.GetBasicDeviceInfoAsync(CancellationToken.None);
                var device = await app.Services.GetRequiredService<DeviceRepository>().AddAsync(new Oadm.Core.Devices.Device
                {
                    Serial = info.SerialNumber,
                    Address = camera.Address,
                    Status = DeviceStatus.Ok,
                    Category = DeviceCategory.Camera,
                    Scheme = camera.EffectiveScheme == "http" ? DeviceScheme.Http : DeviceScheme.Https,
                }, CancellationToken.None);
                await app.Services.GetRequiredService<CredentialStore>().SetAsync(device.Id, camera.User, camera.Password, CancellationToken.None);
                var refreshed = await app.Services.GetRequiredService<DevicePollingService>().RefreshAsync(device.Id, CancellationToken.None);
                output.WriteLine($"{camera}: {refreshed?.Model} {refreshed?.FirmwareVersion} {refreshed?.Status}");
            }

            var state = ActionRulesJson.Deserialize<RulesState>(await Invoke(ActionRulesMethods.Read));
            for (var i = 0; i < 300 && state.Reading; i++)
            {
                await Task.Delay(200);
                state = ActionRulesJson.Deserialize<RulesState>(await Invoke(ActionRulesMethods.GetState));
            }

            Assert.All(state.Rows, r => Assert.Equal(RowStates.Ok, r.State));
            var group = RuleGrouping.Build(state.Rows, RuleGroupBy.WhatItDoes).Single(g => g.Members.Any(m => m.Rule.Name == Prefix + stamp));
            Assert.Equal(clients.Count, group.DeviceCount); // the same rule on AXIS OS 10 and 12 is one group
            output.WriteLine($"group: {group.TriggerText} -> {group.ActionText}, {group.DeviceCount} devices, {group.ValuesText}");
            var targets = group.Members.Where(m => m.Rule.Name == Prefix + stamp)
                .Select(m => new DeviceTargets(m.Device.DeviceId, [new RuleRef(m.Rule.Id, m.Rule.Version)])).ToList();

            var edit = ActionRulesJson.Deserialize<RunStarted>(await Invoke(ActionRulesMethods.Edit, new RunRequest(targets, new RulePatch
            {
                Template = WhileActive,
                TriggerKind = TriggerKinds.Condition,
                Parameters = new Dictionary<string, string> { ["period"] = "5" },
            })));
            Assert.Equal(clients.Count, edit.TaskIds.Count);
            await UntilAsync(clients, r => r.Action.Template == WhileActive && r.StartEvent is null && r.Action.Get("message")!.StartsWith("e2e ", StringComparison.Ordinal));
            output.WriteLine("changed on every camera");

            var runs = ActionRulesJson.Deserialize<List<UndoRun>>(await Invoke(ActionRulesMethods.UndoRuns));
            Assert.Equal(edit.RunId, runs[0].RunId);
            await Invoke(ActionRulesMethods.Undo, new UndoRequest(edit.RunId));
            await UntilAsync(clients, r => r.Action.Template == Once && r.StartEvent is not null);
            output.WriteLine("undone on every camera");

            state = ActionRulesJson.Deserialize<RulesState>(await Invoke(ActionRulesMethods.Read));
            for (var i = 0; i < 300 && state.Reading; i++)
            {
                await Task.Delay(200);
                state = ActionRulesJson.Deserialize<RulesState>(await Invoke(ActionRulesMethods.GetState));
            }

            targets = [.. state.Rows.SelectMany(r => r.Rules.Where(x => x.Name == Prefix + stamp).Select(x => new DeviceTargets(r.DeviceId, [new RuleRef(x.Id, x.Version)])))];
            Assert.Equal(clients.Count, targets.Count);
            await Invoke(ActionRulesMethods.Delete, new RunRequest(targets, null));
            await UntilGoneAsync(clients);
            output.WriteLine("deleted on every camera");
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
            foreach (var (_, vapix) in clients)
            {
                var set = await SoapActionEngine.ReadAsync(vapix, CancellationToken.None);
                foreach (var rule in set.Rules.Where(r => r.Name.StartsWith(Prefix, StringComparison.Ordinal)))
                {
                    await new RuleWriter(vapix, set, inPlace: false).DeleteAsync(rule, CancellationToken.None);
                }

                vapix.Dispose();
            }
        }
    }

    private static VapixClient Client(DevCamera camera) => new(
        new Uri($"{camera.EffectiveScheme}://{camera.Address}/"),
        new HttpClientHandler
        {
            Credentials = new NetworkCredential(camera.User, camera.Password),
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        });

    private static async Task UntilAsync(IEnumerable<(DevCamera Camera, VapixClient Vapix)> clients, Func<DeviceRule, bool> done)
    {
        foreach (var (camera, vapix) in clients)
        {
            var until = DateTime.UtcNow.AddSeconds(90);
            while (true)
            {
                var rule = (await SoapActionEngine.ReadRulesAsync(vapix, CancellationToken.None)).SingleOrDefault(r => r.Name.StartsWith(Prefix, StringComparison.Ordinal));
                if (rule is not null && done(rule))
                {
                    break;
                }

                Assert.True(DateTime.UtcNow < until, $"{camera}: the rule did not change in time.");
                await Task.Delay(500);
            }
        }
    }

    private static async Task UntilGoneAsync(IEnumerable<(DevCamera Camera, VapixClient Vapix)> clients)
    {
        foreach (var (camera, vapix) in clients)
        {
            var until = DateTime.UtcNow.AddSeconds(90);
            while ((await SoapActionEngine.ReadRulesAsync(vapix, CancellationToken.None)).Any(r => r.Name.StartsWith(Prefix, StringComparison.Ordinal)))
            {
                Assert.True(DateTime.UtcNow < until, $"{camera}: the rule was not deleted in time.");
                await Task.Delay(500);
            }
        }
    }

    private sealed class SilentMdnsBrowser : IMdnsBrowser
    {
        public async IAsyncEnumerable<MdnsServiceInstance> BrowseAsync(MdnsBrowseOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }

            yield break;
        }
    }
}
