using System.Globalization;
using System.Text.Json.Nodes;

using Grpc.Core;

using Oadm.Contracts.V1;

namespace Oadm.Client.Api;

/// <summary>
/// Fake Dashboards plugin so <c>--fake</c> and the tests show the page: three sample device triggers with values
/// (Temperature, Gate open, Number plate), the dashboard "Building A", list / save / delete in memory, and values for the
/// watched widgets that change every second (pushed like the server). The JSON is the plugin's (camelCase, enums as text);
/// the rules are the plugin's own tests.
/// </summary>
public sealed partial class FakeOadmApi
{
    public const string DashboardsPluginId = "oadm.dashboards";

    private const string FakeTemperatureType = "oadm.device-triggers.fake-temperature";
    private const string FakeGateType = "oadm.device-triggers.fake-gate";
    private const string FakePlateType = "oadm.device-triggers.fake-plate";

    private static readonly string[] FakePlates = ["W-12345A", "B-77812", "L-4420K", "S-90A71", "G-1203B"];

    private JsonArray? _dashboards;
    private HashSet<(string Type, string? Device)> _dashboardKeys = [];
    private bool _dashboardValuesRunning;

    private Task<string?> InvokeDashboardsAsync(string method, string? payloadJson)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            _dashboards ??= SampleDashboards();
            switch (method)
            {
                case "list":
                    return Task.FromResult<string?>(new JsonObject { ["dashboards"] = _dashboards.DeepClone() }.ToJsonString());
                case "catalog":
                    return Task.FromResult<string?>(FakeDashboardCatalog().ToJsonString());
                case "save":
                    var request = JsonNode.Parse(payloadJson ?? "{}")!.AsObject();
                    var id = request["id"]?.GetValue<string>();
                    var existing = _dashboards.OfType<JsonObject>().FirstOrDefault(d => d["id"]!.GetValue<string>() == id);
                    var saved = new JsonObject
                    {
                        ["id"] = string.IsNullOrEmpty(id) ? Guid.NewGuid().ToString("N") : id,
                        ["name"] = request["name"]?.GetValue<string>(),
                        ["version"] = (existing?["version"]?.GetValue<long>() ?? 0) + 1,
                        ["widgets"] = request["widgets"]?.DeepClone() ?? new JsonArray(),
                    };
                    if (existing is not null)
                    {
                        _dashboards[_dashboards.IndexOf(existing)] = saved;
                    }
                    else
                    {
                        _dashboards.Add(saved);
                    }

                    return Task.FromResult<string?>(saved.ToJsonString());
                case "delete":
                    var target = JsonNode.Parse(payloadJson ?? "{}")?["id"]?.GetValue<string>();
                    if (_dashboards.OfType<JsonObject>().FirstOrDefault(d => d["id"]!.GetValue<string>() == target) is { } gone)
                    {
                        _dashboards.Remove(gone);
                    }

                    return Task.FromResult<string?>(null);
                case "watch":
                    var keys = JsonNode.Parse(payloadJson ?? "{}")?["keys"]?.AsArray() ?? [];
                    _dashboardKeys = [.. keys.OfType<JsonObject>().Select(k => (k["typeId"]!.GetValue<string>(), k["deviceId"]?.GetValue<string>()))];
                    if (!_dashboardValuesRunning)
                    {
                        _dashboardValuesRunning = true;
                        _ = PushDashboardValuesAsync(_cts.Token);
                    }

                    return Task.FromResult<string?>(FakeValues(_dashboardKeys).ToJsonString());
                case "unwatch":
                    _dashboardKeys = [];
                    return Task.FromResult<string?>(null);
                default:
                    throw new RpcException(new Status(StatusCode.InvalidArgument, $"Unknown method '{method}'."));
            }
        }
    }

    /// <summary>New values for the watched widgets every second, like device triggers sending events.</summary>
    private async Task PushDashboardValuesAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
                HashSet<(string, string?)> keys;
                lock (_gate)
                {
                    keys = [.. _dashboardKeys];
                }

                if (keys.Count > 0)
                {
                    _pluginEvents.Publish(new PluginEvent { PluginId = DashboardsPluginId, Topic = "values", PayloadJson = FakeValues(keys).ToJsonString(FakeJson) });
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>The values now: a temperature that drifts, a gate that opens every few seconds, the last plate seen.</summary>
    private static JsonObject FakeValues(IEnumerable<(string Type, string? Device)> keys)
    {
        var now = DateTimeOffset.UtcNow;
        var seconds = now.ToUnixTimeSeconds();
        var values = new JsonArray();
        foreach (var (type, device) in keys)
        {
            var salt = device is null ? 0 : Math.Abs(device.GetHashCode(StringComparison.Ordinal) % 7);
            var data = new JsonObject();
            var on = false;
            switch (type)
            {
                case FakeTemperatureType:
                    data["temperature"] = (22 + salt + (3 * Math.Sin((seconds + salt) / 6.0))).ToString("0.0", CultureInfo.InvariantCulture);
                    break;
                case FakeGateType:
                    on = (seconds / 4 + salt) % 2 == 0;
                    data["port"] = "1";
                    break;
                case FakePlateType:
                    data["plate"] = FakePlates[(int)(seconds / 5 + salt) % FakePlates.Length];
                    data["speed"] = (30 + ((seconds + salt) % 25)).ToString(CultureInfo.InvariantCulture);
                    break;
            }

            values.Add(new JsonObject { ["typeId"] = type, ["deviceId"] = device, ["on"] = on, ["data"] = data, ["time"] = now });
        }

        return new JsonObject { ["values"] = values };
    }

    private static JsonObject FakeDashboardCatalog()
    {
        static JsonObject Field(string name, string label, string kind) => new() { ["name"] = name, ["label"] = label, ["kind"] = kind };
        static JsonObject Event(string id, string name, bool stateful, params JsonObject[] fields) => new()
        {
            ["typeId"] = id,
            ["name"] = name,
            ["group"] = "Device triggers",
            ["sourceId"] = DeviceTriggersPluginId,
            ["sourceName"] = "Device triggers",
            ["stateful"] = stateful,
            ["deviceScope"] = true,
            ["available"] = true,
            ["fields"] = new JsonArray(fields),
            ["hasUnseenFields"] = false,
        };

        return new JsonObject
        {
            ["events"] = new JsonArray(
                Event(FakeGateType, "Gate open", true, Field("@active", "Active", "YesNo"), Field("port", "Port number", "Number")),
                Event(FakePlateType, "Number plate", false, Field("plate", "Plate", "Text"), Field("speed", "Speed", "Number")),
                Event(FakeTemperatureType, "Temperature", false, Field("temperature", "Temperature", "Number"))),
        };
    }

    /// <summary>"Building A": two temperatures, the gate, the last plate and a camera (called under the lock).</summary>
    private JsonArray SampleDashboards()
    {
        var camera = _devices.FirstOrDefault(d => d.Category is DeviceCategory.Camera);
        var others = _devices.Where(d => d.Category is not DeviceCategory.Camera).Take(2).ToList();
        var first = others.ElementAtOrDefault(0)?.Id ?? camera?.Id;
        var second = others.ElementAtOrDefault(1)?.Id ?? first;
        JsonObject Widget(string id, string kind, int x, int y, int w, int h, string name, string? type, string? field, string? device, JsonObject? extra = null)
        {
            var widget = new JsonObject
            {
                ["id"] = id,
                ["kind"] = kind,
                ["x"] = x,
                ["y"] = y,
                ["width"] = w,
                ["height"] = h,
                ["name"] = name,
                ["typeId"] = type,
                ["field"] = field,
                ["deviceId"] = device,
            };
            foreach (var (key, value) in extra ?? [])
            {
                widget[key] = value?.DeepClone();
            }

            return widget;
        }

        var widgets = new JsonArray(
            Widget("t1", "Number", 0, 0, 5, 5, "Server room", FakeTemperatureType, "temperature", first,
                new JsonObject { ["unit"] = "°C", ["decimals"] = 1, ["thresholds"] = new JsonArray(new JsonObject { ["value"] = 25, ["color"] = "Orange" }, new JsonObject { ["value"] = 28, ["color"] = "Red" }) }),
            Widget("t2", "Number", 5, 0, 5, 5, "Gate house", FakeTemperatureType, "temperature", second, new JsonObject { ["unit"] = "°C", ["decimals"] = 1 }),
            Widget("g", "OnOff", 0, 5, 5, 5, "Gate", FakeGateType, "@active", second,
                new JsonObject { ["onIcon"] = "lockOpen", ["onColor"] = "Amber", ["onText"] = "Open", ["offIcon"] = "lock", ["offColor"] = "Green", ["offText"] = "Closed" }),
            Widget("p", "Text", 5, 5, 5, 5, "Last plate", FakePlateType, "plate", first, new JsonObject { ["fontSize"] = 50 }));
        if (camera is not null)
        {
            widgets.Add(Widget("v", "Video", 10, 0, 10, 10, camera.Address, null, null, camera.Id));
        }

        return [new JsonObject { ["id"] = "building-a", ["name"] = "Building A", ["version"] = 1, ["widgets"] = widgets }];
    }
}
