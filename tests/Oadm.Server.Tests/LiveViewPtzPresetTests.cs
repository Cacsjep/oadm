using System.Text.Json.Nodes;

using Grpc.Core;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Oadm.Core.Auth;
using Oadm.Core.Devices;
using Oadm.Core.LiveView;
using Oadm.Core.Vapix;
using Oadm.Sdk.Devices;
using Oadm.Server.Tests.Mcp;
using Oadm.Server.Tests.Support;
using Oadm.Server.Tools;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests;

/// <summary>Saving the PTZ position to a preset: <c>MovePtz</c> with <c>SAVE_PRESET</c> (audited) and the MCP preview and run.</summary>
public sealed class LiveViewPtzPresetTests
{
    [Fact]
    public async Task ASavedPresetIsAuditedAndAPresetTheCameraDoesNotListIsRefused()
    {
        var ptz = new FakePtz();
        await using var host = await TestServerHost.StartAsync(configureServices: services => Use(services, ptz));
        var device = (await host.Get<DeviceRepository>().AddAsync(new Device { Serial = "B8A44F000001", Address = "10.0.0.22", Model = "Q6135-LE", Status = DeviceStatus.Ok }, CancellationToken.None)).Id;

        await host.LiveView.MovePtzAsync(new Proto.MovePtzRequest { DeviceId = device.ToString(), Camera = 1, Move = Proto.PtzMove.SavePreset, Preset = "Gate" });
        var saved = Assert.Single(ptz.Commands);
        Assert.Equal((PtzCommandKind.SavePreset, "Gate"), (saved.Kind, saved.Preset));

        var ex = await Assert.ThrowsAsync<RpcException>(async () => await host.LiveView.MovePtzAsync(
            new Proto.MovePtzRequest { DeviceId = device.ToString(), Camera = 1, Move = Proto.PtzMove.SavePreset, Preset = "Nope" }));
        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
        Assert.Equal(PtzCommand.NoSuchPreset, ex.Status.Detail);
        Assert.Single(ptz.Commands);

        var audit = await host.Get<AuditLog>().ListAsync(100, 0, CancellationToken.None);
        var entry = Assert.Single(audit.Entries, e => e.Action == AuditActions.PtzPresetSaved);
        Assert.Equal(("10.0.0.22 (B8A44F000001)", "Gate"), (entry.Target, entry.Detail));
    }

    [Fact]
    public async Task AnAgentSavesThePositionToAPresetTheCameraListsAfterThePreview()
    {
        var ptz = new FakePtz();
        await using var server = await McpTestServer.StartAsync(configureServices: services => Use(services, ptz));
        var device = await DeviceServiceTests.AddDeviceAsync(server.Host, "10.67.0.2", 9702);
        var key = await server.CreateKeyAsync();
        var id = device.Id.ToString();

        var unknown = await server.CallToolAsync(key, "oadm_devices_ptz_preview", new JsonObject { ["device_id"] = id, ["move"] = "save_preset", ["preset"] = "Nope" });
        Assert.True(unknown["isError"]!.GetValue<bool>());
        Assert.Contains(LiveViewTools.NoSuchPreset, unknown["content"]![0]!["text"]!.GetValue<string>(), StringComparison.Ordinal);

        var preview = await server.ToolAsync(key, "oadm_devices_ptz_preview", new JsonObject { ["device_id"] = id, ["move"] = "save_preset", ["preset"] = "gate" });
        Assert.Equal("Save the current position to the preset Gate", preview["change"]!.GetValue<string>());
        Assert.Empty(ptz.Commands); // the preview only reads

        var run = await server.ToolAsync(key, "oadm_devices_ptz_run", new JsonObject { ["handle"] = preview["handle"]!.GetValue<string>() });
        Assert.True(run["moved"]!.GetValue<bool>());
        var saved = Assert.Single(ptz.Commands);
        Assert.Equal((PtzCommandKind.SavePreset, "Gate"), (saved.Kind, saved.Preset)); // the camera's spelling
        Assert.Contains((await server.Host.Audit.ListAsync(new Proto.ListAuditRequest())).Entries,
            e => e.Action == AuditActions.PtzPresetSaved && e.Detail == "Gate" && e.UserName.StartsWith("MCP ", StringComparison.Ordinal));
    }

    private static void Use(IServiceCollection services, FakePtz ptz)
    {
        services.RemoveAll<IDevicePtz>();
        services.AddSingleton<IDevicePtz>(ptz);
    }

    /// <summary>A PTZ camera with the presets Home and Gate; a save to another name is refused like <see cref="DevicePtz"/> does.</summary>
    private sealed class FakePtz : IDevicePtz
    {
        private static readonly string[] Presets = ["Home", "Gate"];
        private readonly List<PtzCommand> _commands = [];

        public IReadOnlyList<PtzCommand> Commands
        {
            get
            {
                lock (_commands)
                {
                    return [.. _commands];
                }
            }
        }

        public Task<PtzState> GetAsync(Guid deviceId, int camera, CancellationToken ct) =>
            Task.FromResult(new PtzState { IsSupported = true, CanGoHome = true, CanMove = true, Presets = Presets });

        public Task SendAsync(Guid deviceId, int camera, PtzCommand command, CancellationToken ct)
        {
            if (command.Kind == PtzCommandKind.SavePreset && !Presets.Contains(command.Preset, StringComparer.Ordinal))
            {
                throw new ArgumentException(PtzCommand.NoSuchPreset);
            }

            lock (_commands)
            {
                _commands.Add(command);
            }

            return Task.CompletedTask;
        }
    }
}
