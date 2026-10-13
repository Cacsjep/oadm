using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Xml;
using System.Xml.Linq;

using Oadm.Sdk.Devices;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Acap;

/// <summary>An Application API call returned an error. <see cref="Code"/> is the VAPIX error code when the device sent one.</summary>
public sealed class AcapDeviceException : Exception
{
    public AcapDeviceException()
    {
    }

    public AcapDeviceException(string message)
        : base(message)
    {
    }

    public AcapDeviceException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public AcapDeviceException(int? code, string message)
        : base(message)
    {
        Code = code;
    }

    /// <summary>The VAPIX error code ("Error: 5"), null for an answer without one.</summary>
    public int? Code { get; }

    /// <summary>The HTTP status when the request itself failed (500, 404, ...); null for a VAPIX error.</summary>
    public int? HttpStatus { get; init; }

    /// <summary>Code 15: the device timed out and does not know the outcome (the caller verifies it).</summary>
    public bool OutcomeUnknown => Code == 15;
}

public enum AcapControlAction
{
    Start = 0,
    Stop = 1,
    Remove = 2,
}

/// <summary>
/// VAPIX Application API (<c>/axis-cgi/applications/*.cgi</c>) on top of <see cref="IVapixClient.SendAsync"/>.
/// Verified read-only on AXIS P3265-V, AXIS OS 12.11.77 (list.cgi, config.cgi get, info.cgi).
/// </summary>
public sealed class ApplicationApiClient(IVapixClient vapix)
{
    public const string ListPath = "/axis-cgi/applications/list.cgi";
    public const string UploadPath = "/axis-cgi/applications/upload.cgi";

    /// <summary>Per-request timeout of the upload (sending the package and the installation).</summary>
    public static readonly TimeSpan UploadTimeout = TimeSpan.FromMinutes(10);

    /// <summary>Per-request timeout of control.cgi (start, stop, remove): seen at about 60 s for a remove.</summary>
    public static readonly TimeSpan ControlTimeout = TimeSpan.FromMinutes(2);
    public const string ControlPath = "/axis-cgi/applications/control.cgi";
    public const string ConfigPath = "/axis-cgi/applications/config.cgi";
    public const string LicensePath = "/axis-cgi/applications/license.cgi";
    public const string EmbeddedDevelopmentVersionParameter = "Properties.EmbeddedDevelopment.Version";

    private readonly IVapixClient _vapix = vapix ?? throw new ArgumentNullException(nameof(vapix));

    /// <summary>Installed applications (read-only).</summary>
    public async Task<IReadOnlyList<InstalledApplication>> ListAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, ListPath);
        var text = await SendForTextAsync(request, ct).ConfigureAwait(false);
        return ParseList(text);
    }

    /// <summary>Architecture, firmware, embedded development version and AllowUnsigned (read-only, best effort for the optional parts).</summary>
    public async Task<AcapDeviceFacts> GetDeviceFactsAsync(CancellationToken ct)
    {
        var info = await _vapix.GetBasicDeviceInfoAsync(ct).ConfigureAwait(false);
        return new AcapDeviceFacts
        {
            Architecture = info.Architecture,
            FirmwareVersion = info.Version,
            EmbeddedDevelopmentVersion = await TryGetEmbeddedDevelopmentVersionAsync(ct).ConfigureAwait(false),
            AllowUnsigned = await TryGetAllowUnsignedAsync(ct).ConfigureAwait(false),
        };
    }

    /// <summary>The device's embedded development (ACAP SDK) version parameter; null when not available.</summary>
    public async Task<string?> TryGetEmbeddedDevelopmentVersionAsync(CancellationToken ct)
    {
        try
        {
            var parameters = await _vapix.ListParametersAsync([EmbeddedDevelopmentVersionParameter], ct).ConfigureAwait(false);
            return parameters.TryGetValue(EmbeddedDevelopmentVersionParameter, out var embdev) ? embdev : null;
        }
#pragma warning disable CA1031 // Optional value: only legacy package.conf packages need it.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            return null;
        }
    }

    /// <summary><c>config.cgi?action=get&amp;name=AllowUnsigned</c> (AXIS OS 11.2+); null when not available.</summary>
    public async Task<bool?> TryGetAllowUnsignedAsync(CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, ConfigPath + "?action=get&name=AllowUnsigned");
            var text = await SendForTextAsync(request, ct).ConfigureAwait(false);
            return ParseConfigBool(text, "AllowUnsigned");
        }
        catch (AcapDeviceException)
        {
            return null;
        }
    }

    /// <summary>
    /// Uploads and installs an .eap with <c>upload.cgi</c> (multipart field "file"). The device answers
    /// after the installation; "OK" means installed. <paramref name="progress"/> gets the bytes sent.
    /// </summary>
    public async Task UploadAsync(Stream package, string fileName, IProgress<long>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(package);
        using var content = new MultipartFormDataContent();
        var file = new StreamContent(new ProgressReadStream(package, progress), 81920);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(file, "file", fileName);
        using var request = new HttpRequestMessage(HttpMethod.Post, UploadPath) { Content = content };
        // upload.cgi answers only after the installation, which can take minutes for large packages.
        request.Options.Set(VapixRequestOptions.Timeout, UploadTimeout);
        var text = await SendForTextAsync(request, ct).ConfigureAwait(false);
        EnsureOk(text, UploadErrors);
    }

    /// <summary><c>control.cgi?action=start|stop|remove&amp;package=name</c>.</summary>
    public async Task ControlAsync(AcapControlAction action, string package, CancellationToken ct)
    {
        if (!EapReader.IsValidAppName(package))
        {
            throw new ArgumentException("Invalid application name.", nameof(package));
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, BuildControlUri(action, package));
        // Removing (and stopping) a large application can take about a minute before the device answers.
        request.Options.Set(VapixRequestOptions.Timeout, ControlTimeout);
        var text = await SendForTextAsync(request, ct).ConfigureAwait(false);
        EnsureOk(text, ControlErrors);
    }

    /// <summary>
    /// Installs a license key file: <c>license.cgi?action=uploadlicensekey&amp;package=name</c>, multipart field "fileData"
    /// (as the device's own web page sends it, seen in its script on AXIS OS 12.11). The device checks the key and answers
    /// "OK" or "Error: ...".
    /// </summary>
    public async Task UploadLicenseKeyAsync(string package, string keyXml, CancellationToken ct)
    {
        if (!EapReader.IsValidAppName(package))
        {
            throw new ArgumentException("Invalid application name.", nameof(package));
        }

        ArgumentException.ThrowIfNullOrEmpty(keyXml);
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(keyXml));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/xml");
        content.Add(file, "fileData", "license.xml");
        using var request = new HttpRequestMessage(HttpMethod.Post, BuildLicenseUri(package)) { Content = content };
        var text = await SendForTextAsync(request, ct).ConfigureAwait(false);
        EnsureLicenseAccepted(text);
    }

    /// <summary>
    /// license.cgi answers "OK" or "Error: &lt;code&gt;" (VAPIX "Manage license keys"); the device's own page treats every answer
    /// without "Error" as accepted, the same here. A refusal is translated with <see cref="LicenseErrors"/>.
    /// </summary>
    public static void EnsureLicenseAccepted(string reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        var text = reply.Trim();
        if (text.StartsWith("Error:", StringComparison.OrdinalIgnoreCase))
        {
            EnsureOk(text, LicenseErrors);
        }

        if (text.Contains("Error", StringComparison.OrdinalIgnoreCase))
        {
            throw new AcapDeviceException(null, "The device refused the license key: " + FirstLine(text));
        }
    }

    /// <summary>License.cgi error codes (VAPIX Application API, "Manage license keys").</summary>
    public static readonly IReadOnlyDictionary<int, string> LicenseErrors = new Dictionary<int, string>
    {
        [21] = "The license key file is not valid.",
        [22] = "The device could not save the license key. The device's system log says why.",
        [23] = "The device could not remove the license key.",
        [24] = "The application is not installed.",
        [25] = "The license key is for another application.",
        [26] = "The license key does not fit this version of the application.",
        [30] = "The license key is for another device.",
        [31] = "The license key has expired.",
    };

    public static string BuildLicenseUri(string package) =>
        $"{LicensePath}?action=uploadlicensekey&package={Uri.EscapeDataString(package)}";

    public static string BuildControlUri(AcapControlAction action, string package) =>
        $"{ControlPath}?action={action.ToString().ToLowerInvariant()}&package={Uri.EscapeDataString(package)}";

    /// <summary>Parses the list.cgi XML reply.</summary>
    public static IReadOnlyList<InstalledApplication> ParseList(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);
        XElement root;
        try
        {
            root = DeviceXml.ParseElement(xml.Trim());
        }
        catch (XmlException ex)
        {
            throw new AcapDeviceException("The application list from the device could not be read.", ex);
        }

        if (!string.Equals((string?)root.Attribute("result"), "ok", StringComparison.OrdinalIgnoreCase))
        {
            var error = root.Element("error");
            throw new AcapDeviceException(ParseInt((string?)error?.Attribute("type")),
                "The device could not list its applications: " + ((string?)error?.Attribute("message") ?? root.ToString(SaveOptions.DisableFormatting)));
        }

        return root.Elements("application")
            .Select(a => new InstalledApplication
            {
                Name = (string?)a.Attribute("Name") ?? string.Empty,
                NiceName = (string?)a.Attribute("NiceName"),
                Vendor = (string?)a.Attribute("Vendor"),
                Version = (string?)a.Attribute("Version"),
                Status = (string?)a.Attribute("Status"),
                License = (string?)a.Attribute("License"),
                LicenseExpirationDate = (string?)a.Attribute("LicenseExpirationDate"),
                ApplicationId = (string?)a.Attribute("ApplicationID"),
                Bundled = string.Equals((string?)a.Attribute("Bundled"), "Yes", StringComparison.OrdinalIgnoreCase),
                SignatureStatus = (string?)a.Attribute("SignatureStatus"),
                CompatibleOsVersions = a.Element("CompatibleOsVersions")?.Elements("VersionRange")
                    .Select(r => new OsVersionRange((string?)r.Element("Min"), (string?)r.Element("Max")))
                    .ToList() ?? [],
            })
            .Where(a => a.Name.Length > 0)
            .OrderBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Parses <c>&lt;reply result="ok"&gt;&lt;param name="AllowUnsigned" value="true"/&gt;&lt;/reply&gt;</c>.</summary>
    public static bool? ParseConfigBool(string xml, string name)
    {
        try
        {
            var root = DeviceXml.ParseElement(xml.Trim());
            if (!string.Equals((string?)root.Attribute("result"), "ok", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var value = root.Elements("param").FirstOrDefault(p => string.Equals((string?)p.Attribute("name"), name, StringComparison.OrdinalIgnoreCase))?.Attribute("value")?.Value;
            return bool.TryParse(value, out var b) ? b : null;
        }
        catch (XmlException)
        {
            return null;
        }
    }

    /// <summary>
    /// Upload.cgi error codes, every one the VAPIX Application API lists ("Upload application"), in plain words with what to
    /// do. The device's system log has the details where the device itself points there (5, 10, 15).
    /// </summary>
    public static readonly IReadOnlyDictionary<int, string> UploadErrors = new Dictionary<int, string>
    {
        [1] = "The file is not an application package (.eap).",
        [2] = "The package signature is missing or invalid; the device accepts only signed applications.",
        [3] = "The package is too large or the device's storage is full.",
        [5] = "The package is not compatible with this device. The device's system log says why.",
        [10] = "The device could not install the package. The device's system log says why.",
        [12] = "Another application is being uploaded to the device. Try again when it is done.",
        [13] = "The package needs a user or group that the device does not allow.",
        [14] = "The application is already installed with other upper and lower case letters in its name. Remove it first.",
        [15] = "The device timed out and the result is unknown. Check the installed applications and the device's system log.",
        [27] = "Upgrade not allowed: the vendor of the package differs from the installed application.",
        [29] = "The package's manifest.json or package.conf is not valid.",
        [51] = "Developer mode: the serial number in the package is not this device's.",
        [52] = "Developer mode: the vendor changed, so the upgrade is not allowed.",
        [53] = "Developer mode: the vendor of the package is not the developer mode vendor.",
        [54] = "Developer mode does not allow the contents of this package's manifest.",
    };

    /// <summary>Control.cgi error codes (VAPIX Application API, "Control application").</summary>
    public static readonly IReadOnlyDictionary<int, string> ControlErrors = new Dictionary<int, string>
    {
        [1] = "The installed application package is not valid. Install it again.",
        [4] = "The application is not installed.",
        [6] = "The application is already running.",
        [7] = "The application is not running.",
        [8] = "The application stopped after an error. Stop it, then start it again.",
        [9] = "Too many applications are running. Stop one first.",
        [10] = "The device could not do this. The device's system log says why.",
        [15] = "The device timed out and the result is unknown.",
    };

    /// <summary>Throws <see cref="AcapDeviceException"/> unless the reply is "OK".</summary>
    public static void EnsureOk(string reply, IReadOnlyDictionary<int, string> errors)
    {
        ArgumentNullException.ThrowIfNull(reply);
        ArgumentNullException.ThrowIfNull(errors);
        var text = reply.Trim();
        if (text.StartsWith("OK", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (text.StartsWith("Error:", StringComparison.OrdinalIgnoreCase))
        {
            var code = ParseInt(text["Error:".Length..].Trim().Split([' ', '\n', '\r'], 2)[0]);
            var message = code is { } c && errors.TryGetValue(c, out var m) ? m
                : code is null ? "The device refused it: " + FirstLine(text)
                : "The device refused it.";
            throw new AcapDeviceException(code, code is null ? message : $"{message} (error {code})");
        }

        throw new AcapDeviceException("Unexpected answer from the device: " + FirstLine(text));
    }

    private async Task<string> SendForTextAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await _vapix.SendAsync(request, ct).ConfigureAwait(false);
        var path = request.RequestUri?.OriginalString.Split('?')[0] ?? string.Empty;
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new UnauthorizedAccessException(DeviceMessages.CredentialsRequired);
        }

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new UnauthorizedAccessException(AdministratorRequired);
        }

        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            throw new AcapDeviceException(null, DeviceMessages.ForHttpStatus(status) ?? string.Create(CultureInfo.InvariantCulture, $"HTTP {status}"))
            {
                HttpStatus = status,
            };
        }

        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    /// <summary>HTTP 403 on an application request: the stored account is not an administrator.</summary>
    public const string AdministratorRequired = "The stored account is not an administrator on the device. Log in with an administrator account.";

    private static int? ParseInt(string? s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    private static string FirstLine(string text)
    {
        var line = text.Split('\n', 2)[0].Trim();
        return line.Length > 200 ? line[..200] : line;
    }
}

/// <summary>Read-only wrapper that reports how many bytes have been read (upload progress).</summary>
internal sealed class ProgressReadStream(Stream inner, IProgress<long>? progress) : Stream
{
    private long _read;

    public override bool CanRead => true;

    public override bool CanSeek => inner.CanSeek;

    public override bool CanWrite => false;

    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set
        {
            inner.Position = value;
            _read = value;
        }
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Count(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override long Seek(long offset, SeekOrigin origin)
    {
        _read = inner.Seek(offset, origin);
        return _read;
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private int Count(int n)
    {
        if (n > 0)
        {
            _read += n;
            progress?.Report(_read);
        }

        return n;
    }
}
