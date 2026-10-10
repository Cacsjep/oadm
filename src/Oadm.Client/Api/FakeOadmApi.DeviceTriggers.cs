using System.Globalization;
using System.Text.Json.Nodes;

using Grpc.Core;

using Oadm.Contracts.V1;

namespace Oadm.Client.Api;

/// <summary>
/// Fake backend of the "Device triggers" core plugin (plugins/Oadm.Plugins.DeviceTriggers) so <c>--fake</c> shows the
/// page and the editor: three sample triggers with live-looking states, event lists per device kind (cameras: virtual
/// inputs, digital input, tampering, storage, Object Analytics scenarios; the A9188: virtual inputs and eight inputs), the
/// devices with a bad status refused like the server does. JSON as the plugin's contract (camelCase, enums as text).
/// </summary>
public sealed partial class FakeOadmApi
{
    public const string DeviceTriggersPluginId = "oadm.device-triggers";

    private const string FakeVirtualInput = "tns1:Device/tnsaxis:IO/VirtualInput";
    private const string FakeDigitalInput = "tns1:Device/tnsaxis:IO/Port";
    private const string FakeTampering = "tns1:VideoSource/tnsaxis:Tampering";
    private const string FakeScenario1 = "tnsaxis:CameraApplicationPlatform/ObjectAnalytics/Device1Scenario1";
    private const string FakeStorage = "tnsaxis:Storage/Disruption";

    private readonly List<JsonObject> _fakeTriggers = [];
    private readonly Dictionary<string, JsonObject> _fakeTriggerReads = [];

    private Task<string?> InvokeDeviceTriggersAsync(string method, string? payloadJson)
    {
        var payload = string.IsNullOrWhiteSpace(payloadJson) ? new JsonObject() : JsonNode.Parse(payloadJson)!.AsObject();
        lock (_gate)
        {
            ThrowIfOffline();
            EnsureFakeTriggers();
            switch (method)
            {
                case "list":
                    return Done(new JsonObject { ["triggers"] = new JsonArray([.. _fakeTriggers.OrderBy(t => (string)t["name"]!, StringComparer.CurrentCultureIgnoreCase).Select(FakeTriggerSummary)]) });
                case "get":
                {
                    var trigger = FakeTrigger(payload["id"]?.GetValue<string>());
                    return Done(new JsonObject
                    {
                        ["trigger"] = trigger?.DeepClone(),
                        ["devices"] = trigger is null ? new JsonArray() : new JsonArray([.. FakeTriggerDevices(trigger)]),
                    });
                }

                case "startRead":
                {
                    var ids = payload["deviceIds"]?.AsArray().Select(i => i!.GetValue<string>()).ToList() ?? [];
                    var job = FakeReadEvents(ids);
                    _fakeTriggerReads[(string)job["jobId"]!] = job;
                    return Done(job);
                }

                case "readStatus":
                    return _fakeTriggerReads.TryGetValue(payload["jobId"]?.GetValue<string>() ?? string.Empty, out var read)
                        ? Done(read)
                        : throw new RpcException(new Status(StatusCode.InvalidArgument, "The read of the event lists ended. Read the events again."));
                case "save":
                    return Done(FakeSaveTrigger(payload["trigger"]!.AsObject()));
                case "delete":
                    _fakeTriggers.RemoveAll(t => (string)t["id"]! == payload["id"]?.GetValue<string>());
                    return Task.FromResult<string?>(null);
                case "setEnabled":
                    if (FakeTrigger(payload["id"]?.GetValue<string>()) is { } toggled)
                    {
                        toggled["enabled"] = payload["enabled"]?.GetValue<bool>() ?? false;
                    }

                    return Task.FromResult<string?>(null);
                case "duplicate":
                {
                    if (FakeTrigger(payload["id"]?.GetValue<string>()) is not { } source)
                    {
                        return Done(new JsonObject { ["error"] = "The trigger no longer exists." });
                    }

                    var copy = source.DeepClone().AsObject();
                    copy["id"] = Guid.NewGuid().ToString();
                    copy["name"] = (string)source["name"]! + " (copy)";
                    copy["enabled"] = false;
                    _fakeTriggers.Add(copy);
                    return Done(new JsonObject { ["trigger"] = copy.DeepClone() });
                }

                default:
                    throw new RpcException(new Status(StatusCode.InvalidArgument, $"Unknown method '{method}'."));
            }
        }

        static Task<string?> Done(JsonNode node) => Task.FromResult<string?>(node.ToJsonString(FakeJson));
    }

    private JsonObject? FakeTrigger(string? id) => _fakeTriggers.Find(t => (string)t["id"]! == id);

    /// <summary>Three sample triggers on the sample devices. Call under the lock.</summary>
    private void EnsureFakeTriggers()
    {
        if (_fakeTriggers.Count > 0)
        {
            return;
        }

        string Id(string address) => _devices.Find(d => d.Address == address)?.Id ?? Guid.Empty.ToString();
        _fakeTriggers.Add(FakeTriggerJson("Person in the yard", FakeScenario1, "AXIS Object Analytics › Scenario 1", true, "active", [], string.Empty, ["active"], [Id("10.0.0.48"), Id("10.0.0.31"), Id("10.0.0.22")]));
        _fakeTriggers.Add(FakeTriggerJson("Gate contact", FakeDigitalInput, "Device › Input ports › Digital input port", true, "state", [("port", "0")], "Port: Port 1 (0)", ["port", "state"], [Id("10.0.0.30")]));
        _fakeTriggers.Add(FakeTriggerJson("Camera tampering", FakeTampering, "Video source › Camera tampering", false, null, [], string.Empty, ["channel", "tampering"], [Id("10.0.0.48"), Id("10.0.0.21")], enabled: false));
    }

    private static JsonObject FakeTriggerJson(string name, string topic, string eventName, bool stateful, string? stateItem, (string Name, string Value)[] filters, string filterText, string[] values, string[] devices, bool enabled = true) => new()
    {
        ["id"] = Guid.NewGuid().ToString(),
        ["name"] = name,
        ["enabled"] = enabled,
        ["topic"] = topic,
        ["eventName"] = eventName,
        ["stateful"] = stateful,
        ["stateItem"] = stateItem,
        ["filters"] = new JsonArray([.. filters.Select(f => new JsonObject { ["name"] = f.Name, ["value"] = f.Value })]),
        ["filterText"] = filterText,
        ["values"] = new JsonArray([.. values.Select(v => JsonValue.Create(v))]),
        ["deviceIds"] = new JsonArray([.. devices.Select(d => JsonValue.Create(d))]),
    };

    /// <summary>Per device: connection and state like the server would report them (the A9188 is unreachable in fake mode).</summary>
    private IEnumerable<JsonObject> FakeTriggerDevices(JsonObject trigger)
    {
        var enabled = trigger["enabled"]!.GetValue<bool>();
        var stateful = trigger["stateful"]!.GetValue<bool>();
        var first = true;
        foreach (var id in trigger["deviceIds"]!.AsArray().Select(d => d!.GetValue<string>()))
        {
            var device = _devices.Find(d => d.Id == id);
            string connection;
            string? problem = null;
            if (!enabled)
            {
                connection = "off";
            }
            else if (device is null || FakeTriggerRefusal(device) is { } refusal)
            {
                connection = "lost";
                problem = device is null ? Oadm.Sdk.Devices.DeviceMessages.Removed : FakeTriggerRefusal(device);
            }
            else
            {
                connection = "connected";
            }

            var on = enabled && stateful && first && connection == "connected";
            var last = DateTimeOffset.UtcNow.AddMinutes(first ? -3 : -42);
            yield return new JsonObject
            {
                ["deviceId"] = id,
                ["connection"] = connection,
                ["problem"] = problem,
                ["isOn"] = on,
                ["lastEventUtc"] = connection == "connected" ? last.ToString("O", CultureInfo.InvariantCulture) : null,
                ["lastEventText"] = connection != "connected" ? null : stateful ? on ? "On · active 1" : "Off" : "Happened · channel 1, tampering 1",
            };
            first = false;
        }
    }

    private static string? FakeTriggerRefusal(Device device) => device.Status switch
    {
        DeviceStatus.Unreachable => Oadm.Sdk.Devices.DeviceMessages.Unreachable("No route to host"),
        DeviceStatus.CredentialsRequired => Oadm.Sdk.Devices.DeviceMessages.CredentialsRequired,
        DeviceStatus.PasswordNotSet => Oadm.Sdk.Devices.DeviceMessages.PasswordNotSet,
        DeviceStatus.CertificateChanged => Oadm.Sdk.Devices.DeviceMessages.CertificateChanged,
        _ => null,
    };

    private JsonObject FakeTriggerSummary(JsonObject trigger)
    {
        var devices = FakeTriggerDevices(trigger).ToList();
        var last = devices.Select(d => d["lastEventUtc"]?.GetValue<string>()).Where(t => t is not null).Max();
        return new JsonObject
        {
            ["id"] = trigger["id"]!.DeepClone(),
            ["name"] = trigger["name"]!.DeepClone(),
            ["enabled"] = trigger["enabled"]!.DeepClone(),
            ["eventName"] = trigger["eventName"]!.DeepClone(),
            ["filterText"] = trigger["filterText"]!.DeepClone(),
            ["stateful"] = trigger["stateful"]!.DeepClone(),
            ["devices"] = devices.Count,
            ["on"] = devices.Count(d => d["isOn"]!.GetValue<bool>()),
            ["lost"] = devices.Count(d => (string)d["connection"]! == "lost"),
            ["waiting"] = 0,
            ["lastEventUtc"] = last,
        };
    }

    /// <summary>A finished read: per device kind a typical event list, refused statuses as errors.</summary>
    private JsonObject FakeReadEvents(List<string> ids)
    {
        var events = new List<JsonObject>
        {
            FakeEvent(FakeVirtualInput, "Device › Input ports › Virtual input", true, "active", [("port", "Port number", Enumerable.Range(1, 64).Select(i => (i.ToString(CultureInfo.InvariantCulture), (string?)null)).ToArray())], ["active"]),
            FakeEvent(FakeDigitalInput, "Device › Input ports › Digital input port", true, "state", [("port", "Port", Enumerable.Range(0, 8).Select(i => (i.ToString(CultureInfo.InvariantCulture), (string?)$"Port {i + 1}")).ToArray())], ["state"]),
            FakeEvent(FakeTampering, "Video source › Camera tampering", false, null, [("channel", "Channel", [("1", null)])], ["tampering"]),
            FakeEvent(FakeStorage, "Storage › Storage disruption", true, "disruption", [("disk_id", "Disk", [("SD_DISK", null), ("NetworkShare", null)])], ["disruption"]),
            FakeEvent(FakeScenario1, "AXIS Object Analytics › Scenario 1", true, "active", [], ["active"]),
        };
        var devices = new JsonArray();
        foreach (var id in ids)
        {
            var device = _devices.Find(d => d.Id == id);
            var refusal = device is null ? Oadm.Sdk.Devices.DeviceMessages.Removed : device.Status == DeviceStatus.Unreachable ? null : FakeTriggerRefusal(device);
            if (device is not null && device.Status == DeviceStatus.Unreachable)
            {
                refusal = Oadm.Sdk.Devices.DeviceMessages.Unreachable("No route to host");
            }

            int[] offered = device?.Model.Contains("A9188", StringComparison.Ordinal) == true ? [0, 1] : device?.Category == DeviceCategory.Camera ? [0, 1, 2, 3, 4] : [0, 1, 3];
            devices.Add(new JsonObject
            {
                ["deviceId"] = id,
                ["error"] = refusal,
                ["events"] = refusal is null ? new JsonArray([.. offered.Select(i => JsonValue.Create(i))]) : new JsonArray(),
            });
        }

        return new JsonObject
        {
            ["jobId"] = Guid.NewGuid().ToString("N"),
            ["total"] = ids.Count,
            ["done"] = ids.Count,
            ["finished"] = true,
            ["events"] = new JsonArray([.. events]),
            ["devices"] = devices,
        };
    }

    private static JsonObject FakeEvent(string topic, string name, bool stateful, string? stateItem, (string Name, string Label, (string Value, string? Name)[] Values)[] sources, string[] data) => new()
    {
        ["topic"] = topic,
        ["name"] = name,
        ["stateful"] = stateful,
        ["stateItem"] = stateItem,
        ["sources"] = new JsonArray([.. sources.Select(s => new JsonObject
        {
            ["name"] = s.Name,
            ["displayName"] = s.Label,
            ["values"] = new JsonArray([.. s.Values.Select(v => new JsonObject { ["value"] = v.Value, ["displayName"] = v.Name })]),
        })]),
        ["data"] = new JsonArray([.. data.Select(d => new JsonObject { ["name"] = d, ["displayName"] = null, ["values"] = new JsonArray() })]),
    };

    private JsonObject FakeSaveTrigger(JsonObject trigger)
    {
        var id = trigger["id"]?.GetValue<string>();
        var name = trigger["name"]?.GetValue<string>()?.Trim() ?? string.Empty;
        var errors = new JsonObject();
        if (name.Length == 0)
        {
            errors["Name"] = "Enter a name.";
        }
        else if (_fakeTriggers.Any(t => (string)t["id"]! != id && string.Equals((string)t["name"]!, name, StringComparison.OrdinalIgnoreCase)))
        {
            errors["Name"] = "Another trigger has this name.";
        }

        if (string.IsNullOrWhiteSpace(trigger["topic"]?.GetValue<string>()))
        {
            errors["Event"] = "Choose an event.";
        }

        if (trigger["deviceIds"] is not JsonArray { Count: > 0 })
        {
            errors["Devices"] = "Choose at least one device.";
        }

        if (errors.Count > 0)
        {
            return new JsonObject { ["errors"] = errors };
        }

        var saved = trigger.DeepClone().AsObject();
        saved["name"] = name;
        if (string.IsNullOrEmpty(id) || id == Guid.Empty.ToString())
        {
            saved["id"] = Guid.NewGuid().ToString();
            _fakeTriggers.Add(saved);
        }
        else
        {
            var i = _fakeTriggers.FindIndex(t => (string)t["id"]! == id);
            if (i < 0)
            {
                _fakeTriggers.Add(saved);
            }
            else
            {
                _fakeTriggers[i] = saved;
            }
        }

        return new JsonObject { ["trigger"] = saved.DeepClone() };
    }
}
