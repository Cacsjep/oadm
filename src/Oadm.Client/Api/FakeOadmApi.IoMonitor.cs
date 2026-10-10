using System.Text.Json;
using System.Text.Json.Nodes;

using Grpc.Core;

using Oadm.Contracts.V1;

namespace Oadm.Client.Api;

/// <summary>
/// Fake backend of the "I/O Monitor" core plugin (plugins/Oadm.Plugins.IoMonitor) so <c>--fake</c> shows the page: the
/// AXIS A9188 (10.0.0.30) has the ports of an A9210 (RELAY, O1-O3, I1-I5, two configurable ports); after Start an input
/// and the relay change every few seconds, pushed like the server does (topic "ports", camelCase JSON).
/// </summary>
public sealed partial class FakeOadmApi
{
    public const string IoMonitorPluginId = "oadm.io-monitor";

    private readonly Dictionary<string, CancellationTokenSource> _ioStreams = [];

    private static readonly (string Port, string Name, bool Output, string Usage, bool Configurable)[] FakeIoPorts =
    [
        ("0", "RELAY", true, "Relay", false), ("1", "I1", false, "Door contact", false), ("2", "I2", false, "", false),
        ("3", "O1", true, "", false), ("4", "O2", true, "", false), ("5", "I3", false, "", false), ("6", "O3", true, "", false),
        ("7", "I/O 1", false, "", true), ("8", "I/O 2", false, "", true), ("9", "I4", false, "", false), ("10", "I5", false, "", false),
    ];

    private Task<string?> InvokeIoMonitorAsync(string method, string? payloadJson)
    {
        var payload = string.IsNullOrWhiteSpace(payloadJson) ? null : JsonNode.Parse(payloadJson);
        switch (method)
        {
            case "start":
                return Task.FromResult<string?>(FakeStartIo(payload?["deviceId"]?.GetValue<string>() ?? string.Empty));
            case "stop":
                FakeStopIo(payload?["streamId"]?.GetValue<string>() ?? string.Empty);
                return Task.FromResult<string?>(null);
            case "keepAlive":
                return Task.FromResult<string?>(null);
            default:
                throw new RpcException(new Status(StatusCode.InvalidArgument, $"Unknown method '{method}'."));
        }
    }

    private string FakeStartIo(string deviceId)
    {
        Device? device;
        lock (_gate)
        {
            ThrowIfOffline();
            device = _devices.Find(d => d.Id == deviceId)?.Clone();
        }

        if (device is null || !device.Apis.Any(a => a.Id == "io-port-management"))
        {
            return JsonSerializer.Serialize(new { error = device is null ? Oadm.Sdk.Devices.DeviceMessages.Removed : "The device has no inputs or outputs to show" }, FakeJson);
        }

        var streamId = Guid.NewGuid().ToString("N");
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        lock (_gate)
        {
            _ioStreams[streamId] = cts;
        }

        _ = Task.Run(() => FakeIoStreamAsync(streamId, cts.Token), CancellationToken.None);
        return JsonSerializer.Serialize(new { streamId }, FakeJson);
    }

    private void FakeStopIo(string streamId)
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            _ioStreams.Remove(streamId, out cts);
        }

        if (cts is not null)
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    private async Task FakeIoStreamAsync(string streamId, CancellationToken ct)
    {
        var active = new HashSet<string> { "2" };
        try
        {
            for (var step = 0; !ct.IsCancellationRequested; step++)
            {
                var ports = new JsonArray();
                foreach (var (port, name, output, usage, configurable) in FakeIoPorts)
                {
                    ports.Add(new JsonObject
                    {
                        ["port"] = port,
                        ["name"] = name,
                        ["isOutput"] = output,
                        ["state"] = active.Contains(port) ? "closed" : "open",
                        ["normalState"] = "open",
                        ["usage"] = usage,
                        ["configurable"] = configurable,
                    });
                }

                _pluginEvents.Publish(new PluginEvent
                {
                    PluginId = IoMonitorPluginId,
                    Topic = "ports",
                    PayloadJson = new JsonObject { ["streamId"] = streamId, ["ports"] = ports, ["readUtc"] = DateTimeOffset.UtcNow }.ToJsonString(FakeJson),
                });
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                foreach (var toggle in step % 2 == 0 ? new[] { "1", "0" } : ["1"])
                {
                    if (!active.Remove(toggle))
                    {
                        active.Add(toggle);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
