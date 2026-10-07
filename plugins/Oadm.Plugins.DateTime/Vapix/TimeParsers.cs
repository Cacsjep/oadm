using System.Globalization;
using System.Text.Json;

using Oadm.Plugins.DateAndTime.Model;

namespace Oadm.Plugins.DateAndTime.Vapix;

/// <summary>Parses time.cgi, ntp.cgi and param.cgi Time answers (recorded on AXIS P3265-V, AXIS OS 12.11).</summary>
public static class TimeParsers
{
    /// <summary>param.cgi groups of the legacy fallback.</summary>
    public static readonly IReadOnlyList<string> TimeGroups = ["Time"];

    /// <summary>time-service <c>getDateTimeInfo</c> data into <paramref name="into"/>.</summary>
    public static CurrentTimeSettings ParseDateTimeInfo(string json, CurrentTimeSettings into)
    {
        ArgumentNullException.ThrowIfNull(into);
        using var doc = JsonDocument.Parse(json);
        TimeClient.ThrowIfError(doc.RootElement, "getDateTimeInfo");
        var data = Data(doc.RootElement);
        return into with
        {
            DeviceUtc = Date(data, "dateTime"),
            DeviceLocal = Date(data, "localDateTime"),
            TimeZone = String(data, "timeZone") is { Length: > 0 } tz ? tz : null,
            PosixTimeZone = String(data, "posixTimeZone"),
            DstEnabled = Bool(data, "dstEnabled"),
            MaxYear = Int(data, "maxYearSupported"),
        };
    }

    /// <summary>ntp <c>getNTPInfo</c> data.client into <paramref name="into"/>.</summary>
    public static CurrentTimeSettings ParseNtpInfo(string json, CurrentTimeSettings into)
    {
        ArgumentNullException.ThrowIfNull(into);
        using var doc = JsonDocument.Parse(json);
        TimeClient.ThrowIfError(doc.RootElement, "getNTPInfo");
        var data = Data(doc.RootElement);
        var client = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("client", out var c) ? c : data;
        return into with
        {
            NtpEnabled = Bool(client, "enabled"),
            NtpSource = String(client, "serversSource") is { } s
                ? string.Equals(s, "dhcp", StringComparison.OrdinalIgnoreCase) ? Model.NtpSource.Dhcp : Model.NtpSource.Static
                : null,
            NtpServers = Strings(client, "staticServers"),
            AdvertisedServers = Strings(client, "advertisedServers"),
            NtsEnabled = Bool(client, "NTSEnabled"),
            NtsServers = Strings(client, "staticNTSKEServers"),
            Synced = Bool(client, "synced"),
            NtpOffsetMilliseconds = client.ValueKind == JsonValueKind.Object && client.TryGetProperty("timeOffset", out var o) && o.TryGetDouble(out var od) ? od : null,
            MaxStaticServers = Int(client, "maxSupportedStaticServers"),
        };
    }

    /// <summary>
    /// param.cgi <c>Time</c> group (keys without "root."): <c>SyncSource</c> (NTP, None), <c>ObtainFromDHCP</c> (NTP server
    /// from DHCP), <c>NTP.Server</c>, <c>NTP.VolatileServer</c> (DHCP), <c>POSIXTimeZone</c>, <c>DST.Enabled</c>,
    /// <c>TimeZone</c> (IANA, newer firmware), <c>ServerDate</c> / <c>ServerTime</c> (local time, older firmware).
    /// </summary>
    public static CurrentTimeSettings ParseParameters(IReadOnlyDictionary<string, string> parameters, CurrentTimeSettings into)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(into);
        string? Get(string key) => parameters.TryGetValue(key, out var v) ? v : null;
        var sync = Get("Time.SyncSource");
        var dhcp = Get("Time.ObtainFromDHCP");
        var server = Get("Time.NTP.Server");
        var volatileServer = Get("Time.NTP.VolatileServer");
        System.DateTime? local = null;
        if (Get("Time.ServerDate") is { } d && Get("Time.ServerTime") is { } t
            && System.DateTime.TryParseExact($"{d} {t}", ["yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            local = parsed;
        }

        return into with
        {
            // The Time API (when present) is the better source for the time zone: keep what it reported.
            TimeZone = into.TimeZone ?? (Get("Time.TimeZone") is { Length: > 0 } tz ? tz : null),
            PosixTimeZone = into.PosixTimeZone ?? Get("Time.POSIXTimeZone"),
            DstEnabled = into.DstEnabled ?? (Get("Time.DST.Enabled") is { } dst ? IsYes(dst) : null),
            NtpEnabled = sync is null ? into.NtpEnabled : string.Equals(sync, "NTP", StringComparison.OrdinalIgnoreCase),
            NtpSource = dhcp is null ? into.NtpSource : IsYes(dhcp) ? Model.NtpSource.Dhcp : Model.NtpSource.Static,
            NtpServers = server is null ? into.NtpServers : string.IsNullOrWhiteSpace(server) || server == "0.0.0.0" ? [] : [server],
            AdvertisedServers = volatileServer is null ? into.AdvertisedServers : string.IsNullOrWhiteSpace(volatileServer) || volatileServer == "0.0.0.0" ? [] : [volatileServer],
            DeviceLocal = local is { } l ? new DateTimeOffset(l, TimeSpan.Zero) : into.DeviceLocal,
        };
    }

    private static bool IsYes(string value) =>
        value.Equals("yes", StringComparison.OrdinalIgnoreCase) || value.Equals("true", StringComparison.OrdinalIgnoreCase);

    private static JsonElement Data(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data) ? data : default;

    private static string? String(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static bool? Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind is JsonValueKind.True or JsonValueKind.False ? p.GetBoolean() : null;

    private static int? Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.TryGetInt32(out var i) ? i : null;

    private static DateTimeOffset? Date(JsonElement e, string name) =>
        String(e, name) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    private static string[] Strings(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Array
            ? [.. p.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).Where(x => x.Length > 0)]
            : [];
}
