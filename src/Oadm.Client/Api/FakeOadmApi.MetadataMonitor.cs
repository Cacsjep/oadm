using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Grpc.Core;

using Oadm.Contracts.V1;

namespace Oadm.Client.Api;

/// <summary>
/// Fake backend of the "Metadata Monitor" core plugin (plugins/Oadm.Plugins.MetadataMonitor) so <c>--fake</c> shows the
/// page: Start opens a generated event stream (the Initialized burst of a camera: 64 virtual inputs, digital input,
/// storage, temperature, then a change every second or two), pushed through <see cref="WatchCorePluginAsync"/> like
/// the server does. The JSON matches the plugin's contract (camelCase); the plugin tests check that.
/// </summary>
public sealed partial class FakeOadmApi
{
    public const string MetadataMonitorPluginId = "oadm.metadata-monitor";

    private readonly Broadcast<PluginEvent> _pluginEvents = new();
    private readonly Dictionary<string, CancellationTokenSource> _metadataStreams = [];

    /// <summary>Pause between generated changes (tests shorten it).</summary>
    public TimeSpan MetadataChangeInterval { get; set; } = TimeSpan.FromMilliseconds(1500);

    /// <summary>Live events of the simulated core plugins (the Metadata Monitor and the Hardening scan publish).</summary>
    public async IAsyncEnumerable<PluginEvent> WatchCorePluginAsync(string pluginId, [EnumeratorCancellation] CancellationToken ct)
    {
        if (!string.Equals(pluginId, MetadataMonitorPluginId, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(pluginId, HardeningScanPluginId, StringComparison.OrdinalIgnoreCase))
        {
            yield break;
        }

        var channel = _pluginEvents.Subscribe();
        try
        {
            while (await channel.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (channel.Reader.TryRead(out var item))
                {
                    if (string.Equals(item.PluginId, pluginId, StringComparison.OrdinalIgnoreCase))
                    {
                        yield return item;
                    }
                }
            }
        }
        finally
        {
            _pluginEvents.Unsubscribe(channel);
        }
    }

    private Task<string?> InvokeMetadataMonitorAsync(string method, string? payloadJson)
    {
        JsonNode? payload = string.IsNullOrWhiteSpace(payloadJson) ? null : JsonNode.Parse(payloadJson);
        switch (method)
        {
            case "start":
                return Task.FromResult<string?>(FakeStartMetadata(payload?["deviceId"]?.GetValue<string>() ?? string.Empty));
            case "stop":
                FakeStopMetadata(payload?["streamId"]?.GetValue<string>() ?? string.Empty);
                return Task.FromResult<string?>(null);
            case "keepAlive":
                return Task.FromResult<string?>(null);
            default:
                throw new RpcException(new Status(StatusCode.InvalidArgument, $"Unknown method '{method}'."));
        }
    }

    private string FakeStartMetadata(string deviceId)
    {
        Device? device;
        lock (_gate)
        {
            ThrowIfOffline();
            device = _devices.Find(d => d.Id == deviceId)?.Clone();
        }

        string? error = device?.Status switch
        {
            null => "The device is no longer managed by OADM",
            DeviceStatus.CredentialsRequired => "Credentials required - the device rejects the stored credentials",
            DeviceStatus.PasswordNotSet => "Password not set - the device is in factory default",
            DeviceStatus.CertificateChanged => "Certificate changed - accept the new certificate first",
            DeviceStatus.Unreachable => $"Unreachable - RTSP port 554 on {device.Address} is not reachable",
            _ => null,
        };
        if (error is not null)
        {
            return JsonSerializer.Serialize(new { error }, FakeJson);
        }

        var streamId = Guid.NewGuid().ToString("N");
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        lock (_gate)
        {
            _metadataStreams[streamId] = cts;
        }

        _ = Task.Run(() => FakeMetadataStreamAsync(streamId, device!.Id, cts.Token), CancellationToken.None);
        return JsonSerializer.Serialize(new { streamId }, FakeJson);
    }

    private void FakeStopMetadata(string streamId)
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            _metadataStreams.Remove(streamId, out cts);
        }

        if (cts is not null)
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    private async Task FakeMetadataStreamAsync(string streamId, string deviceId, CancellationToken ct)
    {
        long seq = 0;
        void State(string state, string? text = null) => PublishMetadata("state", new JsonObject
        {
            ["streamId"] = streamId,
            ["deviceId"] = deviceId,
            ["state"] = state,
            ["text"] = text,
            ["messages"] = seq,
            ["lost"] = 0,
        });

        void Batch(IEnumerable<(string Topic, string Operation, (string Name, string Value)[] Source, (string Name, string Value)[] Data)> items)
        {
            var messages = new JsonArray();
            foreach (var (topic, operation, source, data) in items)
            {
                messages.Add(FakeMetadataMessage(++seq, topic, operation, source, data));
            }

            PublishMetadata("messages", new JsonObject { ["streamId"] = streamId, ["messages"] = messages, ["dropped"] = 0 });
            State("Live");
        }

        try
        {
            State("Connecting");
            await Task.Delay(400, ct).ConfigureAwait(false);
            State("Live");
            await Task.Delay(300, ct).ConfigureAwait(false);

            const string Init = "Initialized";
            var burst = new List<(string, string, (string, string)[], (string, string)[])>();
            burst.Add(("tns1:Device/tnsaxis:Status/SystemReady", Init, [], [("ready", "1")]));
            burst.Add(("tns1:Device/Trigger/DigitalInput", Init, [("InputToken", "0")], [("LogicalState", "0")]));
            burst.Add(("tns1:Device/tnsaxis:Status/Temperature/Inside", Init, [], [("sensor_level", "1")]));
            burst.Add(("tns1:Device/tnsaxis:Status/Temperature/Above", Init, [], [("sensor_level", "0")]));
            burst.Add(("tns1:Device/tnsaxis:Status/Temperature/Below", Init, [], [("sensor_level", "0")]));
            foreach (var disk in new[] { "SD_DISK", "NetworkShare" })
            {
                burst.Add(("tnsaxis:Storage/Alert", Init, [("disk_id", disk)], [("overall_health", "-3"), ("alert", "0"), ("temperature", "-3"), ("wear", "-3")]));
                burst.Add(("tnsaxis:Storage/Disruption", Init, [("disk_id", disk)], [("disruption", disk == "NetworkShare" ? "1" : "0")]));
            }

            for (var port = 1; port <= 64; port++)
            {
                burst.Add(("tns1:Device/tnsaxis:IO/VirtualInput", Init, [("port", port.ToString(CultureInfo.InvariantCulture))], [("active", "0")]));
            }

            Batch(burst);

            var virtualInputs = new bool[65];
            var round = 0;
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(MetadataChangeInterval, ct).ConfigureAwait(false);
                round++;
                var port = 1 + ((round * 7) % 8);
                virtualInputs[port] = !virtualInputs[port];
                var change = new List<(string, string, (string, string)[], (string, string)[])>
                {
                    ("tns1:Device/tnsaxis:IO/VirtualInput", "Changed", [("port", port.ToString(CultureInfo.InvariantCulture))], [("active", virtualInputs[port] ? "1" : "0")]),
                };
                if (round % 5 == 0)
                {
                    change.Add(("tns1:Device/tnsaxis:Status/Temperature/Above", "Changed", [], [("sensor_level", round % 10 == 0 ? "0" : "1")]));
                }

                if (round % 7 == 0)
                {
                    change.Add(("tnsaxis:Storage/Disruption", "Changed", [("disk_id", "SD_DISK")], [("disruption", round % 14 == 0 ? "0" : "1")]));
                }

                Batch(change);
            }
        }
        catch (OperationCanceledException)
        {
            State("Stopped");
        }
    }

    private void PublishMetadata(string topic, JsonObject payload) =>
        _pluginEvents.Publish(new PluginEvent { PluginId = MetadataMonitorPluginId, Topic = topic, PayloadJson = payload.ToJsonString(FakeJson) });

    /// <summary>One message like the plugin's parser makes it from a device notification.</summary>
    private static JsonObject FakeMetadataMessage(long seq, string topic, string operation, (string Name, string Value)[] source, (string Name, string Value)[] data)
    {
        var now = DateTime.UtcNow;
        static string Items((string Name, string Value)[] items) =>
            string.Concat(items.Select(i => $"<tt:SimpleItem Name=\"{i.Name}\" Value=\"{i.Value}\"/>"));
        var xml = "<wsnt:NotificationMessage xmlns:tns1=\"http://www.onvif.org/ver10/topics\" xmlns:tnsaxis=\"http://www.axis.com/2009/event/topics\" "
            + "xmlns:wsnt=\"http://docs.oasis-open.org/wsn/b-2\" xmlns:tt=\"http://www.onvif.org/ver10/schema\">"
            + $"<wsnt:Topic Dialect=\"http://docs.oasis-open.org/wsn/t-1/TopicExpression/Simple\">{topic}</wsnt:Topic>"
            + $"<wsnt:Message><tt:Message UtcTime=\"{now:yyyy-MM-ddTHH:mm:ss.ffffffZ}\" PropertyOperation=\"{operation}\">"
            + $"<tt:Source>{Items(source)}</tt:Source><tt:Key></tt:Key><tt:Data>{Items(data)}</tt:Data></tt:Message></wsnt:Message></wsnt:NotificationMessage>";
        var info = new StringBuilder(operation == "Initialized" ? "[INIT]" : "[" + operation.ToUpperInvariant() + "]");
        foreach (var (name, value) in source.Concat(data))
        {
            info.Append(' ').Append(name).Append(" = ").Append(value).Append(';');
        }

        var tree = string.Join('/', topic.Split('/').Select(p => p.StartsWith("tns1:", StringComparison.Ordinal) ? p[5..] : p.StartsWith("tnsaxis:", StringComparison.Ordinal) ? p[8..] : p));
        return new JsonObject
        {
            ["seq"] = seq,
            ["utcTime"] = now,
            ["category"] = "Event",
            ["topic"] = tree,
            ["captureUtc"] = now,
            ["operation"] = operation,
            ["info"] = info.ToString(),
            ["xml"] = xml,
        };
    }
}
