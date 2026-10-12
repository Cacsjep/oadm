using System.Text.Json.Nodes;

using Grpc.Core;

using Oadm.Contracts.V1;

namespace Oadm.Client.Api;

/// <summary>
/// Fake Live View plugin so <c>--fake</c> and the tests show saved views: one sample view "Entrance" with four cameras,
/// and list / save / delete in memory with the plugin's JSON (camelCase). The layout rules are the plugin's own tests.
/// </summary>
public sealed partial class FakeOadmApi
{
    public const string LiveViewPluginId = "oadm.live-view";

    private JsonArray? _liveViews;

    private Task<string?> InvokeLiveViewAsync(string method, string? payloadJson)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            _liveViews ??= SampleViews();
            switch (method)
            {
                case "list":
                    return Task.FromResult<string?>(new JsonObject { ["views"] = _liveViews.DeepClone() }.ToJsonString());
                case "save":
                    var request = JsonNode.Parse(payloadJson ?? "{}")!.AsObject();
                    var id = request["id"]?.GetValue<string>();
                    var existing = _liveViews.OfType<JsonObject>().FirstOrDefault(v => v["id"]!.GetValue<string>() == id);
                    var saved = new JsonObject
                    {
                        ["id"] = string.IsNullOrEmpty(id) ? Guid.NewGuid().ToString("N") : id,
                        ["name"] = request["name"]?.GetValue<string>(),
                        ["version"] = (existing?["version"]?.GetValue<long>() ?? 0) + 1,
                        ["tiles"] = request["tiles"]?.DeepClone() ?? new JsonArray(),
                    };
                    if (existing is not null)
                    {
                        _liveViews[_liveViews.IndexOf(existing)] = saved;
                    }
                    else
                    {
                        _liveViews.Add(saved);
                    }

                    return Task.FromResult<string?>(saved.ToJsonString());
                case "delete":
                    var target = JsonNode.Parse(payloadJson ?? "{}")?["id"]?.GetValue<string>();
                    if (_liveViews.OfType<JsonObject>().FirstOrDefault(v => v["id"]!.GetValue<string>() == target) is { } gone)
                    {
                        _liveViews.Remove(gone);
                    }

                    return Task.FromResult<string?>(null);
                default:
                    throw new RpcException(new Status(StatusCode.InvalidArgument, $"Unknown method '{method}'."));
            }
        }
    }

    /// <summary>"Entrance": the first four video devices, 2 x 2 (called under the lock).</summary>
    private JsonArray SampleViews()
    {
        var cameras = _devices.Where(d => d.Category is DeviceCategory.Camera or DeviceCategory.Encoder or DeviceCategory.Intercom).Take(4).ToList();
        var tiles = new JsonArray();
        for (var i = 0; i < cameras.Count; i++)
        {
            tiles.Add(new JsonObject
            {
                ["deviceId"] = cameras[i].Id,
                ["camera"] = 0,
                ["x"] = i % 2 * 10,
                ["y"] = i / 2 * 10,
                ["width"] = 10,
                ["height"] = 10,
            });
        }

        return [new JsonObject { ["id"] = "entrance", ["name"] = "Entrance", ["version"] = 1, ["tiles"] = tiles }];
    }
}
