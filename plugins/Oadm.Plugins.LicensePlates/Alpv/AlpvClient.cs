using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

using Oadm.Plugins.Acap;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.LicensePlates.Alpv;

/// <summary>An area as ALPV's <c>api.cgi?api=getconfig</c> returns it: the parsed area and its JSON, kept whole for the write.</summary>
internal sealed record AreaJson(AlpvArea Area, JsonObject Json);

/// <summary>
/// The calls OADM makes to AXIS License Plate Verifier on one camera (docs/spec/license-plate-center.md "ALPV on the camera",
/// checked on ALPV 3.0.13 and 3.1.7): the application list, the configuration (secrets dropped while parsed), the areas, the
/// event switches, and the writes of the Set up task and the recommended image settings. ALPV has no API list of its own:
/// the version of the app is the API version (rule 5).
/// </summary>
internal sealed class AlpvClient(IVapixClient vapix)
{
    public const string ConfigPath = "/local/fflprapp/config.cgi";
    public const string CameraConfigPath = "/local/fflprapp/config.cgi?config=camera";
    public const string AreasPath = "/local/fflprapp/api.cgi?api=getconfig";
    public const string AreasWritePath = "/local/fflprapp/config_json.cgi";
    public const string EventsPath = "/local/fflprapp/vapix_events.cgi";

    /// <summary>Sets the image settings ALPV recommends; it <b>writes</b> although it is a GET.</summary>
    public const string OptimalImagePath = "/local/fflprapp/api.cgi?api=camera&sensor=setoptimalparams";

    /// <summary>The event switches OADM needs.</summary>
    public static readonly IReadOnlyList<string> NeededEvents = ["New", "Update", "Lost"];

    private readonly IVapixClient _vapix = vapix ?? throw new ArgumentNullException(nameof(vapix));

    /// <summary>ALPV in the application list; null when it is not installed.</summary>
    public async Task<InstalledApplication?> AppAsync(CancellationToken ct)
    {
        var apps = await new ApplicationApiClient(_vapix).ListAsync(ct).ConfigureAwait(false);
        return Find(apps);
    }

    /// <summary>ALPV in <paramref name="apps"/>; null when it is not installed.</summary>
    public static InstalledApplication? Find(IEnumerable<InstalledApplication> apps) =>
        apps.FirstOrDefault(a => string.Equals(a.Name, LicensePlatesInfo.AppName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// What keeps the app from taking part, in the order the page shows it: not installed, another major version than 3, no
    /// valid license, not running; null when it may.
    /// </summary>
    public static CameraStatus? Problem(InstalledApplication? app)
    {
        if (app is null)
        {
            return CameraStatus.AlpvNotInstalled;
        }

        if (MajorOf(app.Version) != 3)
        {
            return CameraStatus.AlpvNeedsVersion3;
        }

        if (!string.Equals(app.License, "Valid", StringComparison.OrdinalIgnoreCase))
        {
            return CameraStatus.AlpvNoLicense;
        }

        return string.Equals(app.Status, "Running", StringComparison.OrdinalIgnoreCase) ? null : CameraStatus.AlpvStopped;
    }

    /// <summary>The major version of "3.1.7"; null when it cannot be read.</summary>
    public static int? MajorOf(string? version)
    {
        var text = version?.Trim() ?? string.Empty;
        var dot = text.IndexOf('.', StringComparison.Ordinal);
        return int.TryParse(dot < 0 ? text : text[..dot], NumberStyles.Integer, CultureInfo.InvariantCulture, out var major) ? major : null;
    }

    /// <summary>
    /// The check before the first write of a task (rule 5): the application list read fresh, ALPV 3, licensed, running;
    /// anything else fails with <see cref="DeviceNotCompatibleException"/> ("Nothing was changed").
    /// </summary>
    public async Task<InstalledApplication> RequireAsync(CancellationToken ct)
    {
        var app = await AppAsync(ct).ConfigureAwait(false);
        if (Problem(app) is { } problem)
        {
            throw new DeviceNotCompatibleException(LicensePlateTexts.Status(problem) + ". " + LicensePlateTexts.NothingChanged);
        }

        return app!;
    }

    public async Task<AlpvConfig> ReadConfigAsync(CancellationToken ct) =>
        AlpvConfig.Parse(await GetTextAsync(ConfigPath, ct).ConfigureAwait(false));

    /// <summary>Both areas (area 2 always exists; ALPV uses it when active).</summary>
    public async Task<IReadOnlyList<AreaJson>> ReadAreasAsync(CancellationToken ct) =>
        ParseAreas(await GetTextAsync(AreasPath, ct).ConfigureAwait(false));

    public static IReadOnlyList<AreaJson> ParseAreas(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new AlpvException("The areas of ALPV could not be read.", ex);
        }

        if (root?["roi"] is not JsonArray list)
        {
            throw new AlpvException("The areas of ALPV could not be read.");
        }

        var areas = new List<AreaJson>();
        foreach (var item in list.OfType<JsonObject>())
        {
            var corners = new List<AreaPoint>();
            foreach (var point in item["coordinates"] as JsonArray ?? [])
            {
                if (point is JsonArray { Count: >= 2 } xy && Number(xy[0]) is { } x && Number(xy[1]) is { } y)
                {
                    corners.Add(new AreaPoint(x, y));
                }
            }

            var area = new AlpvArea
            {
                Id = Number(item["id"]) ?? areas.Count + 1,
                Active = item["active"] is JsonValue a && a.TryGetValue<bool>(out var active) && active,
                Angle = Number(item["angle"]) ?? 0,
                Corners = corners,
            };
            areas.Add(new AreaJson(area, (JsonObject)item.DeepClone()));
        }

        return areas;
    }

    private static int? Number(JsonNode? node) => node is JsonValue v
        ? v.TryGetValue<int>(out var i) ? i : v.TryGetValue<double>(out var d) ? (int)Math.Round(d) : v.TryGetValue<string>(out var s) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : null
        : null;

    /// <summary>
    /// The body of <c>POST config_json.cgi</c>: both areas always (as ALPV's page does), each the read JSON with the new
    /// active, angle and corners; its other keys stay as read.
    /// </summary>
    public static string AreasBody(IReadOnlyList<AreaJson> read, IReadOnlyList<AlpvArea> areas)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(areas);
        var list = new JsonArray();
        foreach (var current in read)
        {
            var json = (JsonObject)current.Json.DeepClone();
            var wanted = areas.FirstOrDefault(a => a.Id == current.Area.Id);
            if (wanted is null)
            {
                json["active"] = current.Area.Id == 1; // area 1 is always used; area 2 not given: turned off, its corners kept
            }
            else
            {
                json["active"] = wanted.Id == 1 || wanted.Active;
                json["angle"] = wanted.Angle;
                json["coordinates"] = new JsonArray([.. wanted.Corners.Select(c => (JsonNode)new JsonArray(c.X, c.Y))]);
            }

            list.Add(json);
        }

        return new JsonObject { ["api"] = "setconfig", ["config"] = "roi", ["roi"] = list }.ToJsonString();
    }

    public async Task WriteAreasAsync(string body, CancellationToken ct) =>
        await PostAsync(AreasWritePath, body, "application/json", ct).ConfigureAwait(false);

    /// <summary>The event switches by name ("New": true).</summary>
    public async Task<IReadOnlyDictionary<string, bool>> ReadEventsAsync(CancellationToken ct) =>
        ParseEvents(await GetTextAsync(EventsPath, ct).ConfigureAwait(false));

    public static IReadOnlyDictionary<string, bool> ParseEvents(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            var events = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (var item in JsonNode.Parse(json)?["events_list"] as JsonArray ?? [])
            {
                foreach (var (name, value) in item as JsonObject ?? [])
                {
                    events[name] = value is JsonValue v && (v.TryGetValue<bool>(out var b) ? b : v.TryGetValue<string>(out var s) && string.Equals(s, "true", StringComparison.OrdinalIgnoreCase));
                }
            }

            return events;
        }
        catch (JsonException ex)
        {
            throw new AlpvException("The event switches of ALPV could not be read.", ex);
        }
    }

    /// <summary>New, Update and Lost of <paramref name="events"/> (a missing switch counts as off).</summary>
    public static EventSwitches SwitchesOf(IReadOnlyDictionary<string, bool> events) =>
        new(events.GetValueOrDefault("New"), events.GetValueOrDefault("Update"), events.GetValueOrDefault("Lost"));

    /// <summary>
    /// The body of <c>POST vapix_events.cgi</c> that turns on the needed switches that are off: only those keys (ALPV changes
    /// only what it gets); null when all are on. Never turns anything off.
    /// </summary>
    public static string? EventsBody(IReadOnlyDictionary<string, bool> events)
    {
        var off = NeededEvents.Where(e => !events.GetValueOrDefault(e)).ToList();
        return off.Count == 0 ? null : new JsonArray([.. off.Select(e => (JsonNode)new JsonObject { [e] = "true" })]).ToJsonString();
    }

    public async Task WriteEventsAsync(string body, CancellationToken ct) =>
        await PostAsync(EventsPath, body, "application/json", ct).ConfigureAwait(false);

    /// <summary>
    /// The body of <c>POST config.cgi</c>: only the changed elements, at the paths the camera's configuration returned
    /// ("plate.threshold" is <c>&lt;plate&gt;&lt;threshold&gt;</c>); null when nothing changed.
    /// </summary>
    public static string? ConfigBody(IReadOnlyDictionary<string, string> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (changes.Count == 0)
        {
            return null;
        }

        var root = new XElement("configuration");
        foreach (var (path, value) in changes.OrderBy(c => c.Key, StringComparer.Ordinal))
        {
            var parts = path.Split('.');
            var parent = root;
            foreach (var part in parts[..^1])
            {
                var next = parent.Element(part);
                if (next is null)
                {
                    next = new XElement(part);
                    parent.Add(next);
                }

                parent = next;
            }

            parent.Add(new XElement(parts[^1], value));
        }

        return new XDocument(new XDeclaration("1.0", "utf-8", null), root).Declaration + root.ToString(SaveOptions.DisableFormatting);
    }

    public async Task WriteConfigAsync(string body, CancellationToken ct) =>
        await PostAsync(ConfigPath, body, "text/xml", ct).ConfigureAwait(false);

    /// <summary>ALPV sets the image settings it recommends (a write, although it is a GET).</summary>
    public async Task SetOptimalImageAsync(CancellationToken ct) =>
        _ = await GetTextAsync(OptimalImagePath, ct).ConfigureAwait(false);

    private async Task<string> GetTextAsync(string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        return await SendAsync(request, ct).ConfigureAwait(false);
    }

    private async Task PostAsync(string path, string body, string mediaType, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body, Encoding.UTF8, mediaType) };
        _ = await SendAsync(request, ct).ConfigureAwait(false);
    }

    private async Task<string> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await _vapix.SendAsync(request, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized)
        {
            throw new UnauthorizedAccessException(DeviceMessages.Unauthorized);
        }

        if (response.StatusCode is HttpStatusCode.Forbidden)
        {
            throw new UnauthorizedAccessException(DeviceMessages.Forbidden);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new AlpvException("ALPV answered: " + (DeviceMessages.ForHttpStatus((int)response.StatusCode) ?? ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture)));
        }

        return text;
    }
}
