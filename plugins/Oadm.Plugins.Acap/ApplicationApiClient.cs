using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Xml;
using System.Xml.Linq;

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

    public int? Code { get; }
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
    public const string ControlPath = "/axis-cgi/applications/control.cgi";
    public const string ConfigPath = "/axis-cgi/applications/config.cgi";
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
        string? embdev = null;
        try
        {
            var parameters = await _vapix.ListParametersAsync([EmbeddedDevelopmentVersionParameter], ct).ConfigureAwait(false);
            parameters.TryGetValue(EmbeddedDevelopmentVersionParameter, out embdev);
        }
#pragma warning disable CA1031 // Optional value: only legacy package.conf packages need it.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            embdev = null;
        }

        return new AcapDeviceFacts
        {
            Architecture = info.Architecture,
            FirmwareVersion = info.Version,
            EmbeddedDevelopmentVersion = embdev,
            AllowUnsigned = await TryGetAllowUnsignedAsync(ct).ConfigureAwait(false),
        };
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
        var text = await SendForTextAsync(request, ct).ConfigureAwait(false);
        EnsureOk(text, ControlErrors);
    }

    public static string BuildControlUri(AcapControlAction action, string package) =>
        $"{ControlPath}?action={action.ToString().ToLowerInvariant()}&package={Uri.EscapeDataString(package)}";

    /// <summary>Parses the list.cgi XML reply.</summary>
    public static IReadOnlyList<InstalledApplication> ParseList(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);
        XElement root;
        try
        {
            root = XElement.Parse(xml.Trim());
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
            var root = XElement.Parse(xml.Trim());
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

    /// <summary>Upload.cgi error codes (VAPIX Application API).</summary>
    public static readonly IReadOnlyDictionary<int, string> UploadErrors = new Dictionary<int, string>
    {
        [1] = "The file is not a valid application package.",
        [2] = "The package signature is missing or invalid; the device accepts only signed applications.",
        [3] = "The package is too large or the device has not enough free space.",
        [5] = "The package is not compatible with this device.",
        [10] = "Unspecified error on the device.",
        [12] = "Uploading applications is currently not possible on the device.",
        [13] = "Installation failed (invalid user or group in the package).",
        [14] = "The application already exists on the device.",
        [15] = "The operation timed out on the device.",
        [27] = "Upgrade not allowed: the vendor of the installed application differs.",
        [29] = "The package has an invalid manifest.json or package.conf.",
    };

    /// <summary>Control.cgi error codes (VAPIX Application API).</summary>
    public static readonly IReadOnlyDictionary<int, string> ControlErrors = new Dictionary<int, string>
    {
        [1] = "Invalid application package.",
        [4] = "The application is not installed.",
        [6] = "The application is already running.",
        [7] = "The application is not running.",
        [8] = "The application is in a failure state.",
        [9] = "Too many applications are running.",
        [10] = "Unspecified error on the device.",
        [15] = "The operation timed out on the device.",
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
            var message = code is { } c && errors.TryGetValue(c, out var m) ? m : "The device reported " + FirstLine(text);
            throw new AcapDeviceException(code, code is null ? message : $"{message} (error {code})");
        }

        throw new AcapDeviceException("Unexpected answer from the device: " + FirstLine(text));
    }

    private async Task<string> SendForTextAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await _vapix.SendAsync(request, ct).ConfigureAwait(false);
        var path = request.RequestUri?.OriginalString.Split('?')[0] ?? string.Empty;
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new UnauthorizedAccessException($"{path}: HTTP {(int)response.StatusCode}. The stored account needs administrator rights.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new AcapDeviceException((int)response.StatusCode, $"{path}: HTTP {(int)response.StatusCode}");
        }

        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

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
