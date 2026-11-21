using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Oadm.Sdk.Testing;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Overlays.Tests;

internal static class Fixtures
{
    public static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    /// <summary>The "data" of a recorded answer.</summary>
    public static JsonNode? Data(string name) => JsonNode.Parse(Read(name))!["data"];

    /// <summary>API list recorded from AXIS P3265-V, AXIS OS 12.11 (lists dynamicoverlay 1.8).</summary>
    public static IReadOnlyList<DeviceApi> P3265ApiList() => ParseApiList(Read("apilist-p3265v-12.11.json"));

    public static IReadOnlyList<DeviceApi> ParseApiList(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return [.. doc.RootElement.GetProperty("data").GetProperty("apiList").EnumerateArray()
            .Select(a => new DeviceApi(a.GetProperty("id").GetString()!, a.GetProperty("version").GetString()!))];
    }

    /// <summary>A device with the recorded API list.</summary>
    public static FakeDevice Camera(string address = "10.0.0.48") => new() { Address = address, Model = "AXIS P3265-V", Apis = P3265ApiList() };
}

/// <summary>
/// A camera with the overlay API: keeps its overlays, answers getOverlayCapabilities, list, addText and remove like the
/// recorded AXIS OS 12.11 (lowest free identity, error 300 when the view area is full, 302 for an unknown identity, 103 for
/// a remove with more than the identity) and records every method it got.
/// </summary>
internal sealed class OverlayCamera : FakeCamera, IVapixClient
{
    private readonly List<JsonObject> _texts = [];
    private readonly List<JsonObject> _images = [];

    public OverlayCamera()
    {
        Apis = Fixtures.P3265ApiList();
        Capabilities = (JsonObject)Fixtures.Data("getOverlayCapabilities-p3265v-12.11.json")!;
    }

    /// <summary>
    /// The video sources the camera lists (GetVideoSourcesAsync); null: a host that cannot list them (NotSupportedException).
    /// </summary>
    public IReadOnlyList<VideoSource>? VideoSources { get; set; }

    /// <summary>
    /// The sources of the AXIS P4708-PLVE (AXIS OS 13.0.114, 2026-11-21): Camera 1 (sensor 0), Camera 2 (sensor 1) and the
    /// Group View of both (Source "1,0", which the host's parser reads as sensor 0).
    /// </summary>
    public static IReadOnlyList<VideoSource> P4708Sources() =>
        [new VideoSource(1, "Camera 1", 0, []), new VideoSource(2, "Camera 2", 1, []), new VideoSource(3, "Group View", 0, [])];

    public Task<IReadOnlyList<VideoSource>> GetVideoSourcesAsync(CancellationToken ct) =>
        VideoSources is { } sources ? Task.FromResult(sources) : throw new NotSupportedException("This client cannot list video sources.");

    /// <summary>The getOverlayCapabilities data (recorded P3265-V by default: 8 slots per view area).</summary>
    public JsonObject Capabilities { get; set; }

    /// <summary>The methods in order ("list", "addText", "remove").</summary>
    public List<string> Methods { get; } = [];

    /// <summary>The params of every addText.</summary>
    public List<JsonObject> Added { get; } = [];

    /// <summary>Answers the next addText with this error code (once).</summary>
    public int? FailNextAdd { get; set; }

    public int WriteCount => Methods.Count(m => m is "addText" or "remove");

    public IReadOnlyList<JsonObject> Texts => _texts;

    /// <summary>Puts an overlay on the camera as if the user had made it.</summary>
    public int Put(int camera, string text, JsonNode? position = null, string textColor = "white", string bg = "black", string ol = "transparent", int fontSize = 48)
    {
        var identity = NextIdentity();
        _texts.Add(new JsonObject
        {
            ["camera"] = camera,
            ["identity"] = identity,
            ["text"] = text,
            ["position"] = position ?? "topLeft",
            ["textColor"] = textColor,
            ["textBGColor"] = bg,
            ["textOLColor"] = ol,
            ["fontSize"] = fontSize,
            ["reference"] = "channel",
            ["visible"] = true,
        });
        return identity;
    }

    public void PutImage(int camera) => _images.Add(new JsonObject { ["camera"] = camera, ["identity"] = NextIdentity(), ["overlayPath"] = "/etc/overlays/axis(128x44).ovl" });

    /// <summary>Gives every overlay a new identity (as after a restart).</summary>
    public void Renumber()
    {
        foreach (var t in _texts)
        {
            t["identity"] = t["identity"]!.GetValue<int>() + 100;
        }
    }

    public override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var path = request.RequestUri!.OriginalString;
        if (path != DynamicOverlayApi.Path)
        {
            return await base.SendAsync(request, ct);
        }

        var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject();
        var method = body["method"]!.GetValue<string>();
        Methods.Add(method);
        var p = body["params"] as JsonObject;
        if (p is null)
        {
            return Error(method, 102, "Mandatory parameter(s) missing");
        }

        switch (method)
        {
            case "getOverlayCapabilities":
                return Data(method, Capabilities.DeepClone());
            case "list":
                return Data(method, new JsonObject
                {
                    ["imageFiles"] = new JsonArray("/etc/overlays/axis(128x44).ovl"),
                    ["imageOverlays"] = new JsonArray([.. _images.Select(i => i.DeepClone())]),
                    ["textOverlays"] = new JsonArray([.. _texts.Select(t => t.DeepClone())]),
                });
            case "addText":
                Added.Add((JsonObject)p.DeepClone());
                if (FailNextAdd is { } code)
                {
                    FailNextAdd = null;
                    return Error(method, code, "Invalid value");
                }

                var camera = p["camera"]!.GetValue<int>();
                var slots = Capabilities["numAvailableSlots"]!.GetValue<int>();
                if (_texts.Count(t => t["camera"]!.GetValue<int>() == camera) + _images.Count(i => i["camera"]!.GetValue<int>() == camera) >= slots)
                {
                    return Error(method, 300, "Unable to create overlay (limit reached)");
                }

                var identity = Put(camera, p["text"]!.GetValue<string>(), p["position"]?.DeepClone(),
                    p["textColor"]?.GetValue<string>() ?? "black", p["textBGColor"]?.GetValue<string>() ?? "transparent",
                    p["textOLColor"]?.GetValue<string>() ?? "transparent", p["fontSize"]?.GetValue<int>() ?? 48);
                return Data(method, new JsonObject { ["camera"] = camera, ["identity"] = identity });
            case "remove":
                if (p.Count != 1)
                {
                    return Error(method, 103, "Unknown parameter supplied");
                }

                var id = p["identity"]!.GetValue<int>();
                var removed = _texts.RemoveAll(t => t["identity"]!.GetValue<int>() == id) + _images.RemoveAll(i => i["identity"]!.GetValue<int>() == id);
                return removed == 0 ? Error(method, 302, "Invalid value for parameter identity") : Data(method, new JsonObject());
            default:
                return Error(method, 203, "Invalid method");
        }
    }

    private int NextIdentity()
    {
        var used = _texts.Concat(_images).Select(o => o["identity"]!.GetValue<int>()).ToHashSet();
        var n = 1;
        while (used.Contains(n))
        {
            n++;
        }

        return n;
    }

    private static HttpResponseMessage Data(string method, JsonNode data) => Json(new JsonObject { ["apiVersion"] = "1.10", ["context"] = "oadm", ["method"] = method, ["data"] = data });

    private static HttpResponseMessage Error(string method, int code, string message) =>
        Json(new JsonObject { ["apiVersion"] = "1.10", ["context"] = "oadm", ["method"] = method, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } });

    private static HttpResponseMessage Json(JsonNode node) =>
        new(HttpStatusCode.OK) { Content = new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json") };
}

/// <summary>A query context over a camera.</summary>
internal sealed class QueryContext(IVapixClient vapix) : Oadm.Sdk.Plugins.ITaskQueryContext
{
    public IVapixClient Vapix { get; } = vapix;

    public Microsoft.Extensions.Logging.ILogger Logger => Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
}
