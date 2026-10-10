using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Core.Vapix;
using Oadm.Sdk.Actions;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Outputs.Tests;

/// <summary>
/// Opt-in write test: switches one output of a real device with the real action and VAPIX client, through the port
/// management API and through port.cgi, and leaves it inactive. Runs only with <c>OADM_OUTPUT_WRITE_URL</c>,
/// <c>OADM_OUTPUT_WRITE_USER</c>, <c>OADM_OUTPUT_WRITE_PASSWORD</c> (optional <c>OADM_OUTPUT_WRITE_PORT</c>, default 1) set,
/// and only when the developer asks for it (CLAUDE.md "device safety" 6). Passed on an AXIS A9210 (AXIS OS 12.10.73.2,
/// output 1 = RELAY), loan device, 2026-10-10.
/// </summary>
[Trait("Category", "HardwareWrite")]
public sealed class OutputHardwareWriteTests
{
    private static readonly string? Url = Environment.GetEnvironmentVariable("OADM_OUTPUT_WRITE_URL");

    public sealed class WriteFactAttribute : FactAttribute
    {
        public WriteFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Url))
            {
                Skip = "OADM_OUTPUT_WRITE_URL is not set.";
            }
        }
    }

    [WriteFact]
    public async Task Every_mode_switches_the_output_and_it_ends_inactive()
    {
        using var handler = new HttpClientHandler
        {
            Credentials = new NetworkCredential(Environment.GetEnvironmentVariable("OADM_OUTPUT_WRITE_USER"), Environment.GetEnvironmentVariable("OADM_OUTPUT_WRITE_PASSWORD")),
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        };
        using var vapix = new VapixClient(new Uri(Url!.TrimEnd('/') + "/"), handler, disposeHandler: false);
        var port = int.TryParse(Environment.GetEnvironmentVariable("OADM_OUTPUT_WRITE_PORT"), out var n) ? n : 1;
        var apis = await vapix.GetApiListAsync(CancellationToken.None);
        Assert.True(apis.Supports(PortManagementApi.ApiId, "1.0"), "The device has no I/O port management API.");

        foreach (var device in new[] { new Device(apis), new Device([]) }) // port management API, then port.cgi
        {
            await RunAsync(vapix, device, port, "active", ActionEdge.Fired);
            Assert.True(await IsActiveAsync(vapix, port));
            await RunAsync(vapix, device, port, "inactive", ActionEdge.Fired);
            Assert.False(await IsActiveAsync(vapix, port));
            await RunAsync(vapix, device, port, "toggle", ActionEdge.Fired);
            Assert.True(await IsActiveAsync(vapix, port));
            await RunAsync(vapix, device, port, "toggle", ActionEdge.Fired);
            Assert.False(await IsActiveAsync(vapix, port));
            await RunAsync(vapix, device, port, "follow", ActionEdge.TurnedOn);
            Assert.True(await IsActiveAsync(vapix, port));
            await RunAsync(vapix, device, port, "follow", ActionEdge.TurnedOff);
            Assert.False(await IsActiveAsync(vapix, port));

            var pulse = RunAsync(vapix, device, port, "pulse", ActionEdge.Fired, seconds: 2);
            await Task.Delay(700);
            Assert.True(await IsActiveAsync(vapix, port));
            await pulse;
            await Task.Delay(2_000);
            Assert.False(await IsActiveAsync(vapix, port));
        }
    }

    private static async Task<List<string>> RunAsync(VapixClient vapix, Device device, int port, string mode, ActionEdge edge, int seconds = 5)
    {
        var settings = new JsonObject { ["port"] = port, ["mode"] = mode, ["seconds"] = seconds };
        using var doc = JsonDocument.Parse(settings.ToJsonString());
        var context = new Context(vapix);
        var trigger = new ActionTrigger("t", "Test", device.Id, device.Address, device.Model, true, DateTimeOffset.UtcNow, new Dictionary<string, string>());
        await new SetOutputAction().ExecuteAsync(context, new ActionInvocation(Guid.NewGuid(), "Hardware test", edge, doc.RootElement.Clone(), device, trigger), CancellationToken.None);
        return context.Lines;
    }

    /// <summary>Read through the port management API: active = state differs from the normal state.</summary>
    private static async Task<bool> IsActiveAsync(VapixClient vapix, int port)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, PortManagementApi.Path)
        {
            Content = new StringContent("""{"apiVersion":"1.0","method":"getPorts"}""", Encoding.UTF8, "application/json"),
        };
        using var response = await vapix.SendAsync(request, CancellationToken.None);
        var item = JsonNode.Parse(await response.Content.ReadAsStringAsync())!["data"]!["items"]!.AsArray()
            .First(p => p!["port"]!.ToString() == (port - 1).ToString(System.Globalization.CultureInfo.InvariantCulture))!;
        return item["state"]!.ToString() != item["normalState"]!.ToString();
    }

    private sealed class Device(IReadOnlyList<DeviceApi> apis) : IDeviceInfo
    {
        public Guid Id { get; } = Guid.NewGuid();
        public string Serial => string.Empty;
        public string Address => Url ?? string.Empty;
        public string? HostName => null;
        public string? Model => null;
        public string? FirmwareVersion => null;
        public DeviceStatus Status => DeviceStatus.Ok;
        public DeviceCategory Category => DeviceCategory.IoModule;
        public bool HasVideo => false;
        public IReadOnlyList<DeviceApi> Apis { get; } = apis;
    }

    private sealed class Context(IVapixClient vapix) : IActionContext
    {
        public List<string> Lines { get; } = [];
        public IVapixClient? Vapix { get; } = vapix;
        public ILogger Logger => NullLogger.Instance;
        public ICorePlugin? Owner => null;
        public IPluginSettings Settings => throw new NotSupportedException();
        public void Log(string message) => Lines.Add(message);
        public string Text(string? text) => text ?? string.Empty;
    }
}
