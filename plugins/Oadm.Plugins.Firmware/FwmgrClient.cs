using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Firmware;

/// <summary>Raw fwmgr status (<c>firmwaremanagement.cgi</c> method <c>status</c>).</summary>
public sealed record FwmgrStatus(
    string? ApiVersion,
    string? ActiveFirmwareVersion,
    string? ActiveFirmwarePart,
    string? InactiveFirmwareVersion,
    bool? IsCommitted,
    string? PendingCommit,
    int? TimeToRollback,
    string? LastUpgradeAt,
    string? ResetSource)
{
    /// <summary>An earlier upgrade waits for commit (auto rollback pending). A new upgrade would fail with 412.</summary>
    public bool HasUncommittedUpgrade => IsCommitted == false || PendingCommit is not null || TimeToRollback is not null;
}

/// <summary>Parameters of the fwmgr <c>upgrade</c> method.</summary>
public sealed record FwmgrUpgradeOptions(FactoryDefaultMode FactoryDefaultMode, string AutoCommit, string AutoRollback);

/// <summary>fwmgr answered with an error object.</summary>
public sealed class FwmgrException : Exception
{
    public FwmgrException(string method, int code, string? deviceMessage)
        : base(Describe(method, code, deviceMessage))
    {
        Method = method;
        Code = code;
        DeviceMessage = deviceMessage;
    }

    public FwmgrException()
    {
        Method = string.Empty;
    }

    public FwmgrException(string message)
        : base(message)
    {
        Method = string.Empty;
    }

    public FwmgrException(string message, Exception innerException)
        : base(message, innerException)
    {
        Method = string.Empty;
    }

    public string Method { get; }

    public int Code { get; }

    public string? DeviceMessage { get; }

    /// <summary>The upgrade was refused before installation: the device still runs its old firmware.</summary>
    public bool NothingInstalled => Method == "upgrade" && Code is 400 or 409 or 410 or 412 or 415 or 417 or 421 or 422 or 423 or 424;

    private static string Describe(string method, int code, string? deviceMessage)
    {
        var text = code switch
        {
            409 when method == "upgrade" => "The device refuses to install an older firmware version without a factory default.",
            410 => "This firmware version has been revoked by Axis and cannot be installed.",
            412 when method == "upgrade" => "A previous upgrade has not been committed yet. Commit or roll it back first.",
            412 => "The active firmware is not committed.",
            415 => "The file is not a valid AXIS OS image.",
            421 => "The firmware image does not match this device (wrong product).",
            422 => "The firmware image is missing the mandatory Axis signature.",
            423 => "The device is busy with another firmware operation.",
            424 => "The firmware is signed with an unknown custom certificate.",
            404 when method == "rollback" => "There is no previous firmware to roll back to.",
            417 => "The device does not support this firmware management API version.",
            _ => "Firmware management request failed.",
        };
        var detail = string.IsNullOrWhiteSpace(deviceMessage) ? string.Empty : $" Device: \"{deviceMessage}\"";
        return string.Create(CultureInfo.InvariantCulture, $"{text} (fwmgr {method} error {code}.){detail}");
    }
}

/// <summary>
/// fwmgr JSON API (<c>/axis-cgi/firmwaremanagement.cgi</c>, API id <c>fwmgr</c>, major version 1)
/// over the pre-authenticated <see cref="IVapixClient"/>. Requests use apiVersion 1.0, which every
/// 1.x device accepts; status, upgrade and commit exist since 1.0.
/// </summary>
public sealed class FwmgrClient(IVapixClient vapix)
{
    public const string ApiId = "fwmgr";
    public const string MinApiVersion = "1.0";
    public const string RequestApiVersion = "1.0";
    public const string Path = "axis-cgi/firmwaremanagement.cgi";
    public const string Context = "oadm";

    private const string Boundary = "oadm-fw-7c1b0e4d5a2f";

    public async Task<FwmgrStatus> GetStatusAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Path)
        {
            Content = new StringContent(BuildJson("status", null), Encoding.UTF8, "application/json"),
        };
        var data = await SendAsync(request, "status", ct).ConfigureAwait(false);
        return ParseStatus(data);
    }

    /// <summary>Commits the running firmware; returns the committed version.</summary>
    public async Task<string?> CommitAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Path)
        {
            Content = new StringContent(BuildJson("commit", null), Encoding.UTF8, "application/json"),
        };
        var data = await SendAsync(request, "commit", ct).ConfigureAwait(false);
        return GetString(data, "firmwareVersion");
    }

    /// <summary>
    /// Uploads the image (multipart: part "json" then part "file") and returns the version the
    /// device reports for the new image. The device reboots afterwards.
    /// </summary>
    public async Task<string?> UpgradeAsync(HttpContent firmware, string fileName, FwmgrUpgradeOptions options, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(firmware);
        ArgumentNullException.ThrowIfNull(options);
        using var request = BuildUpgradeRequest(firmware, fileName, options);
        request.Options.Set(VapixRequestOptions.Timeout, timeout);
        var data = await SendAsync(request, "upgrade", ct).ConfigureAwait(false);
        return GetString(data, "firmwareVersion");
    }

    /// <summary>Builds the multipart upgrade request. The firmware content is not read here.</summary>
    public static HttpRequestMessage BuildUpgradeRequest(HttpContent firmware, string fileName, FwmgrUpgradeOptions options)
    {
        ArgumentNullException.ThrowIfNull(firmware);
        ArgumentNullException.ThrowIfNull(options);
        var json = new StringContent(BuildJson("upgrade", options), Encoding.UTF8);
        json.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        json.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data") { Name = "\"json\"" };

        firmware.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        firmware.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
        {
            Name = "\"file\"",
            FileName = "\"" + SafeFileName(fileName) + "\"",
        };

        var multipart = new MultipartContent("form-data", Boundary) { json, firmware };
        // Embedded HTTP servers expect an unquoted boundary parameter.
        multipart.Headers.ContentType = MediaTypeHeaderValue.Parse("multipart/form-data; boundary=" + Boundary);

        var request = new HttpRequestMessage(HttpMethod.Post, Path) { Content = multipart };
        // With Digest auth the device answers 401 before the body is sent instead of after 200 MB.
        request.Headers.ExpectContinue = true;
        return request;
    }

    public static string BuildJson(string method, FwmgrUpgradeOptions? upgrade)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("apiVersion", RequestApiVersion);
            writer.WriteString("context", Context);
            writer.WriteString("method", method);
            if (upgrade is not null)
            {
                writer.WriteStartObject("params");
                writer.WriteString("factoryDefaultMode", upgrade.FactoryDefaultMode.ToString().ToLowerInvariant());
                writer.WriteString("autoCommit", upgrade.AutoCommit);
                writer.WriteString("autoRollback", upgrade.AutoRollback);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>Parses the <c>data</c> object of a status response (also used for recorded fixtures).</summary>
    public static FwmgrStatus ParseStatusResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        ThrowOnError(doc.RootElement, "status");
        var data = doc.RootElement.TryGetProperty("data", out var d) ? d.Clone() : default;
        var status = ParseStatus(data);
        return status with { ApiVersion = GetString(doc.RootElement, "apiVersion") };
    }

    private static FwmgrStatus ParseStatus(JsonElement data) => new(
        null,
        GetString(data, "activeFirmwareVersion"),
        GetString(data, "activeFirmwarePart"),
        GetString(data, "inactiveFirmwareVersion"),
        // The API spells it "isCommited"; accept the correct spelling too.
        GetBool(data, "isCommited") ?? GetBool(data, "isCommitted"),
        GetString(data, "pendingCommit"),
        GetInt(data, "timeToRollback"),
        GetString(data, "lastUpgradeAt"),
        GetString(data, "resetSource"));

    private async Task<JsonElement> SendAsync(HttpRequestMessage request, string method, CancellationToken ct)
    {
        using var response = await vapix.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new UnauthorizedAccessException("The device rejected the stored credentials (firmware management needs an administrator account).");
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            response.EnsureSuccessStatusCode();
            throw new FwmgrException($"Unexpected answer from firmwaremanagement.cgi ({method}).", ex);
        }

        using (doc)
        {
            ThrowOnError(doc.RootElement, method);
            response.EnsureSuccessStatusCode();
            return doc.RootElement.TryGetProperty("data", out var data) ? data.Clone() : default;
        }
    }

    private static void ThrowOnError(JsonElement root, string method)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            throw new FwmgrException(method, GetInt(error, "code") ?? 0, GetString(error, "message"));
        }
    }

    private static string SafeFileName(string? name)
    {
        var file = System.IO.Path.GetFileName(name ?? string.Empty);
        var safe = new string([.. file.Where(c => c is >= ' ' and < (char)127 and not '"' and not '\\')]);
        return safe.Length == 0 ? "firmware.bin" : safe;
    }

    private static string? GetString(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number => v.GetRawText(),
                _ => null,
            }
            : null;

    private static bool? GetBool(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var v))
        {
            return null;
        }

        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(v.GetString(), out var b) => b,
            _ => null,
        };
    }

    private static int? GetInt(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var v))
        {
            return null;
        }

        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n))
        {
            return n;
        }

        return v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : null;
    }
}
