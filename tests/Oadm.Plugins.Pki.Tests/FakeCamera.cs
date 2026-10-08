using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Plugins.Pki.Device;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Pki.Tests;

/// <summary>The recorded read-only answers of 10.0.0.48 (AXIS P3265-V, AXIS OS 12.11.77), linked from tests/Oadm.Plugins.Pki.Tests/Fixtures.</summary>
internal static class PkiFixture
{
    public static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    public const string Discover = "config-discover.json";
    public const string Certificates = "rest-cert-v1-certificates.json";
    public const string CaCertificates = "rest-cert-v1-ca_certificates.json";
    public const string WebServer = "soap-GetWebServerTlsConfiguration.xml";
    public const string NetworkInfo = "network_settings-getNetworkInfo.json";
    public const string Fault = "soap-acertificates-GetCertificates-fault.xml";
    public const string Http = "paramcgi-Network.HTTP.txt";
    public const string DateTimeInfo = "time-service-getDateTimeInfo-P3265-V-12.11.json";

    /// <summary>What 10.0.0.48 lists in getApiList (the relevant part).</summary>
    public static readonly IReadOnlyList<DeviceApi> Apis =
    [
        new("network-settings", "1.37"), new("param-cgi", "1.0"), new("time-service", "1.1"), new("basic-device-info", "1.3"), new("api-discovery", "1.1"),
    ];

    /// <summary>param.cgi list text into the dictionary shape of ListParametersAsync (no "root.").</summary>
    public static Dictionary<string, string> Parameters(string name) =>
        Read(name).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.Split('=', 2))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0].StartsWith("root.", StringComparison.Ordinal) ? p[0][5..] : p[0], p => p[1]);
}

internal sealed record PkiFakeDevice(Guid Id, string Address = "10.0.0.48", string Serial = "B8A44F631339", string Firmware = "12.11.77") : IDeviceInfo
{
    public string? HostName => null;

    public string? Model => "P3265-V";

    public string? FirmwareVersion => Firmware;

    public DeviceStatus Status => DeviceStatus.Ok;

    public DeviceCategory Category => DeviceCategory.Camera;

    public bool HasVideo => true;

    public IReadOnlyList<DeviceApi> Apis { get; init; } = PkiFixture.Apis;
}

/// <summary>One request the fake received.</summary>
internal sealed record CameraRequest(string Method, string Path, string Body)
{
    /// <summary>Changes the device (REST POST/PATCH/DELETE, SOAP Set, setWired8021XConfiguration).</summary>
    public bool IsWrite =>
        (Path.StartsWith(CertApi.BasePath, StringComparison.Ordinal) && Method != "GET")
        || Body.Contains("SetWebServerTlsConfiguration", StringComparison.Ordinal)
        || Body.Contains("setWired8021XConfiguration", StringComparison.Ordinal);
}

/// <summary>
/// A stateful fake AXIS camera: REST cert v1 (keys generated in memory, real CSRs, PATCH checks the key), the SOAP web
/// server TLS calls, network_settings.cgi 802.1X, time.cgi and param.cgi, starting from the recorded 10.0.0.48 answers.
/// Never touches the network.
/// </summary>
internal sealed class FakeCamera : IVapixClient
{
    private readonly Dictionary<string, (string Pem, RSA? Key)> _certificates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _cas = new(StringComparer.Ordinal);
    private readonly JsonObject _networkInfo;

    public FakeCamera()
    {
        foreach (var item in JsonNode.Parse(PkiFixture.Read(PkiFixture.Certificates))!["data"]!.AsArray())
        {
            _certificates[item!["alias"]!.GetValue<string>()] = (item["certificate"]!.GetValue<string>(), null);
        }

        foreach (var item in JsonNode.Parse(PkiFixture.Read(PkiFixture.CaCertificates))!["data"]!.AsArray())
        {
            _cas[item!["alias"]!.GetValue<string>()] = item["certificate"]!.GetValue<string>();
        }

        var web = WebServerTls.Parse(PkiFixture.Read(PkiFixture.WebServer));
        Policy = web.Policy!;
        Ciphers = web.Ciphers;
        WebServerAlias = web.CertificateAlias!;
        _networkInfo = JsonNode.Parse(PkiFixture.Read(PkiFixture.NetworkInfo))!.AsObject();
        Parameters = PkiFixture.Parameters(PkiFixture.Http);
    }

    public Uri BaseAddress { get; init; } = new("https://10.0.0.48/");

    public string Serial { get; init; } = "B8A44F631339";

    public IReadOnlyList<DeviceApi> ApiList { get; set; } = PkiFixture.Apis;

    /// <summary>False: config/discover does not list cert (AXIS OS before 11.11).</summary>
    public bool HasCertApi { get; set; } = true;

    /// <summary>False: the SOAP web server service answers with a fault.</summary>
    public bool HasWebServerService { get; set; } = true;

    /// <summary>Device clock minus real clock.</summary>
    public TimeSpan ClockOffset { get; set; }

    /// <summary>A REST call whose path contains this text answers with error code 5.</summary>
    public string? FailPath { get; set; }

    public string Policy { get; set; }

    public IReadOnlyList<string> Ciphers { get; }

    public string WebServerAlias { get; set; }

    public Dictionary<string, string> Parameters { get; }

    public List<CameraRequest> Requests { get; } = [];

    public IReadOnlyList<CameraRequest> Writes => [.. Requests.Where(r => r.IsWrite)];

    public IReadOnlyCollection<string> CertificateAliases => _certificates.Keys;

    public IReadOnlyCollection<string> CaAliases => _cas.Keys;

    public string CertificatePem(string alias) => _certificates[alias].Pem;

    public JsonObject Dot1x => _networkInfo["data"]!["devices"]![0]!["wired"]!["8021X"]!.AsObject();

    public JsonObject EapTlsParams => Dot1x["configurations"]!.AsArray().Single(c => c!["mode"]!.GetValue<string>() == NetworkInfoApi.EapTlsMode)!["params"]!.AsObject();

    /// <summary>SHA-256 of the certificate the web server presents on 443 (null when HTTPS is off).</summary>
    public string? ServedFingerprint =>
        ConnectionPolicy.IsHttpOnly(Policy) ? null
        : _certificates.TryGetValue(WebServerAlias, out var c) ? Convert.ToHexString(SHA256.HashData(X509Certificate2.CreateFromPem(c.Pem).RawData)) : null;

    public void AddCertificate(string alias, string pem) => _certificates[alias] = (pem, null);

    public void AddCaCertificate(string alias, string pem) => _cas[alias] = pem;

    public string CaCertificatePem(string alias) => _cas[alias];

    public Task<IReadOnlyList<DeviceApi>> GetApiListAsync(CancellationToken ct) => Task.FromResult(ApiList);

    public Task<BasicDeviceInfo> GetBasicDeviceInfoAsync(CancellationToken ct) =>
        Task.FromResult(new BasicDeviceInfo(Serial, "P3265-V", null, null, "12.11.77", null, null));

    public Task<IReadOnlyDictionary<string, string>> ListParametersAsync(IEnumerable<string> groups, CancellationToken ct)
    {
        var prefixes = groups.ToList();
        IReadOnlyDictionary<string, string> result = Parameters
            .Where(p => prefixes.Any(g => p.Key == g || p.Key.StartsWith(g + ".", StringComparison.OrdinalIgnoreCase)))
            .ToDictionary(p => p.Key, p => p.Value);
        return Task.FromResult(result);
    }

    public Task RestartAsync(CancellationToken ct) => throw new NotSupportedException("The PKI tasks never restart a device.");

    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
        var path = request.RequestUri!.OriginalString.TrimStart('/');
        lock (Requests)
        {
            Requests.Add(new CameraRequest(request.Method.Method, path, body));
        }

        var (status, text, type) = Handle(request.Method.Method, path, body);
        var response = new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, type) };
        response.Headers.Date = DateTimeOffset.UtcNow + ClockOffset;
        return response;
    }

    private (HttpStatusCode, string, string) Handle(string method, string path, string body)
    {
        if (path == CertApi.DiscoverPath)
        {
            var discover = JsonNode.Parse(PkiFixture.Read(PkiFixture.Discover))!;
            if (!HasCertApi)
            {
                discover["apis"]!.AsObject().Remove("cert");
            }

            return Json(discover.ToJsonString());
        }

        if (path.StartsWith(CertApi.BasePath, StringComparison.Ordinal))
        {
            return Rest(method, Uri.UnescapeDataString(path[CertApi.BasePath.Length..]), body);
        }

        if (path == WebServerTls.ServicesPath)
        {
            return Soap(body);
        }

        if (path == NetworkInfoApi.Path)
        {
            return NetworkSettings(body);
        }

        if (path == DeviceClock.TimePath)
        {
            var info = JsonNode.Parse(PkiFixture.Read(PkiFixture.DateTimeInfo))!;
            info["data"]!["dateTime"] = (DateTimeOffset.UtcNow + ClockOffset).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
            return Json(info.ToJsonString());
        }

        return (HttpStatusCode.NotFound, "Not found", "text/plain");
    }

    private (HttpStatusCode, string, string) Rest(string method, string path, string body)
    {
        if (FailPath is not null && path.Contains(FailPath, StringComparison.Ordinal))
        {
            return Error(HttpStatusCode.BadRequest, 5, "Validation error: Failed to generate key pair: This key algorithm is not supported on TPM");
        }

        var data = string.IsNullOrEmpty(body) ? null : JsonNode.Parse(body)!["data"];
        switch (method, path)
        {
            case ("GET", "/certificates"):
                return Success(new JsonArray([.. _certificates.Select(c => (JsonNode)new JsonObject { ["alias"] = c.Key, ["certificate"] = c.Value.Pem, ["keystore"] = "SE0" })]));
            case ("GET", "/ca_certificates"):
                return Success(new JsonArray([.. _cas.Select(c => (JsonNode)new JsonObject { ["alias"] = c.Key, ["certificate"] = c.Value })]));
            case ("POST", "/create_certificate"):
            {
                var alias = data!["alias"]!.GetValue<string>();
                if (_certificates.ContainsKey(alias))
                {
                    return Error(HttpStatusCode.BadRequest, 2, "Alias already exists");
                }

                Assert.Equal("RSA-2048", data["key_type"]!.GetValue<string>());
                var key = RSA.Create(2048);
                var request = new CertificateRequest(data["subject"]!.GetValue<string>(), key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                using var self = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddYears(1));
                _certificates[alias] = (self.ExportCertificatePem() + "\n", key);
                return Success(null);
            }

            case ("POST", "/install_from_pkcs12"):
            {
                var alias = data!["alias"]!.GetValue<string>();
                var collection = X509CertificateLoader.LoadPkcs12Collection(Convert.FromBase64String(data["pkcs12"]!.GetValue<string>()), data["passphrase"]!.GetValue<string>());
                var leaf = collection.Single(c => c.HasPrivateKey);
                _certificates[alias] = (leaf.ExportCertificatePem() + "\n", leaf.GetRSAPrivateKey());
                return Success(null);
            }

            case ("POST", "/ca_certificates"):
            {
                var alias = data!["alias"]!.GetValue<string>();
                if (_cas.ContainsKey(alias))
                {
                    return Error(HttpStatusCode.BadRequest, 2, "Alias already exists");
                }

                _cas[alias] = data["certificate"]!.GetValue<string>();
                return Success(null);
            }
        }

        if (path.StartsWith("/certificates/", StringComparison.Ordinal))
        {
            var rest = path["/certificates/".Length..];
            var csr = rest.EndsWith("/get_csr", StringComparison.Ordinal);
            var alias = csr ? rest[..^"/get_csr".Length] : rest;
            if (!_certificates.TryGetValue(alias, out var entry))
            {
                return Error(HttpStatusCode.NotFound, 1, "Resource not found: " + alias);
            }

            if (csr && method == "POST")
            {
                var request = new CertificateRequest(data!["subject"]!.GetValue<string>(), entry.Key!, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                return Success(JsonValue.Create(request.CreateSigningRequestPem()));
            }

            if (method == "PATCH")
            {
                var pem = data!["certificate"]!.GetValue<string>();
                using var certificate = X509Certificate2.CreateFromPem(pem);
                using var publicKey = certificate.GetRSAPublicKey()!;
                if (entry.Key is null || !publicKey.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(entry.Key.ExportSubjectPublicKeyInfo()))
                {
                    return Error(HttpStatusCode.BadRequest, 5, "The certificate does not match the key");
                }

                _certificates[alias] = (pem, entry.Key);
                return Success(null);
            }

            if (method == "DELETE")
            {
                _certificates.Remove(alias);
                return Success(null);
            }
        }

        if (path.StartsWith("/ca_certificates/", StringComparison.Ordinal) && method == "DELETE")
        {
            return _cas.Remove(path["/ca_certificates/".Length..]) ? Success(null) : Error(HttpStatusCode.NotFound, 1, "Resource not found");
        }

        return Error(HttpStatusCode.NotFound, 1, "Resource not found: " + path);
    }

    private (HttpStatusCode, string, string) Soap(string body)
    {
        if (!HasWebServerService)
        {
            return (HttpStatusCode.InternalServerError, PkiFixture.Read(PkiFixture.Fault), "application/soap+xml");
        }

        if (body.Contains("GetWebServerTlsConfiguration", StringComparison.Ordinal))
        {
            var set = $"<aweb:CertificateSet><acert:Certificates><acert:Id>{WebServerAlias}</acert:Id></acert:Certificates><acert:CACertificates></acert:CACertificates><acert:TrustedCertificates></acert:TrustedCertificates></aweb:CertificateSet>";
            var ciphers = string.Concat(Ciphers.Select(c => $"<acert:Cipher>{c}</acert:Cipher>"));
            return (HttpStatusCode.OK, Envelope($"<aweb:GetWebServerTlsConfigurationResponse><aweb:Configuration name=\"WebServer\"><aweb:Tls>true</aweb:Tls><aweb:ConnectionPolicies><aweb:Admin>{Policy}</aweb:Admin></aweb:ConnectionPolicies><aweb:Ciphers>{ciphers}</aweb:Ciphers>{set}</aweb:Configuration></aweb:GetWebServerTlsConfigurationResponse>"), "application/soap+xml");
        }

        if (body.Contains("SetWebServerTlsConfiguration", StringComparison.Ordinal))
        {
            var xml = XDocument.Parse(body);
            XNamespace web = WebServerTls.WebNs;
            XNamespace cert = WebServerTls.CertNs;

            // Like AXIS OS 12.11 (gSOAP): the configuration elements must be in the web server namespace.
            var configuration = xml.Descendants(web + "SetWebServerTlsConfiguration").Single().Elements().SingleOrDefault();
            if (configuration?.Name != web + "Configuration" || configuration.Element(web + "Tls") is null)
            {
                return (HttpStatusCode.InternalServerError, Envelope("<SOAP-ENV:Fault><SOAP-ENV:Code><SOAP-ENV:Value>SOAP-ENV:Sender</SOAP-ENV:Value><SOAP-ENV:Subcode><SOAP-ENV:Value>ter:TagMismatch</SOAP-ENV:Value></SOAP-ENV:Subcode></SOAP-ENV:Code><SOAP-ENV:Reason><SOAP-ENV:Text xml:lang=\"en\">Tag mismatch</SOAP-ENV:Text></SOAP-ENV:Reason></SOAP-ENV:Fault>"), "application/soap+xml");
            }

            var alias = xml.Descendants(cert + "Certificates").Single().Elements(cert + "Id").Single().Value;
            Assert.True(_certificates.ContainsKey(alias), "The web server certificate must exist: " + alias);
            Assert.Equal(Ciphers, xml.Descendants(cert + "Cipher").Select(c => c.Value).ToList()); // ciphers sent back as read
            WebServerAlias = alias;
            Policy = xml.Descendants(web + "Admin").Single().Value;
            return (HttpStatusCode.OK, Envelope("<aweb:SetWebServerTlsConfigurationResponse/>"), "application/soap+xml");
        }

        return (HttpStatusCode.InternalServerError, PkiFixture.Read(PkiFixture.Fault), "application/soap+xml");
    }

    private (HttpStatusCode, string, string) NetworkSettings(string body)
    {
        var request = JsonNode.Parse(body)!;
        var method = request["method"]!.GetValue<string>();
        if (method == "getNetworkInfo")
        {
            return Json(_networkInfo.ToJsonString());
        }

        if (method == "setWired8021XConfiguration")
        {
            var p = request["params"]!.AsObject();
            Assert.Equal("eth0", p["deviceName"]!.GetValue<string>());
            Dot1x["enabled"] = p["enabled"]!.GetValue<bool>();
            if (p["mode"] is { } mode)
            {
                Dot1x["mode"] = mode.GetValue<string>();
            }

            foreach (var key in new[] { "identity", "eapolVersion", "certClient", "certsCA" })
            {
                if (p[key] is { } value)
                {
                    EapTlsParams[key] = value.DeepClone();
                }
            }

            return Json(new JsonObject { ["apiVersion"] = request["apiVersion"]!.GetValue<string>(), ["method"] = method, ["data"] = new JsonObject() }.ToJsonString());
        }

        return Json("{\"error\":{\"code\":4002,\"message\":\"Method not supported\"}}");
    }

    private static string Envelope(string body) =>
        $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><SOAP-ENV:Envelope xmlns:SOAP-ENV=\"{WebServerTls.SoapNs}\" xmlns:aweb=\"{WebServerTls.WebNs}\" xmlns:acert=\"{WebServerTls.CertNs}\"><SOAP-ENV:Body>{body}</SOAP-ENV:Body></SOAP-ENV:Envelope>";

    private static (HttpStatusCode, string, string) Json(string text) => (HttpStatusCode.OK, text, "application/json");

    private static (HttpStatusCode, string, string) Success(JsonNode? data)
    {
        var reply = new JsonObject { ["status"] = "success" };
        if (data is not null)
        {
            reply["data"] = data;
        }

        return Json(reply.ToJsonString());
    }

    private static (HttpStatusCode, string, string) Error(HttpStatusCode status, int code, string message) =>
        (status, new JsonObject { ["status"] = "error", ["error"] = new JsonObject { ["code"] = code, ["message"] = message } }.ToJsonString(), "application/json");
}

/// <summary>In-memory uploads.</summary>
internal sealed class MemoryFiles : IUploadedFiles
{
    public Dictionary<string, (string Name, byte[] Data)> Files { get; } = [];

    public Task<UploadedFile?> FindAsync(string fileId, CancellationToken ct) =>
        Task.FromResult(Files.TryGetValue(fileId, out var f) ? new UploadedFile(fileId, f.Name, f.Data.Length, "00") : null);

    public Task<Stream> OpenReadAsync(string fileId, CancellationToken ct) => Task.FromResult<Stream>(new MemoryStream(Files[fileId].Data));
}

/// <summary>
/// Task context like the server's: steps via <see cref="TaskStepList"/>, warnings, log, and UpdateDeviceTlsAsync against the
/// fake (the "handshake" sees <see cref="FakeCamera.ServedFingerprint"/>).
/// </summary>
internal sealed class PkiTaskContext : ITaskExecutionContext, ITaskQueryContext
{
    public PkiTaskContext(FakeCamera camera)
    {
        Camera = camera;
        Steps = new TaskStepList(onWarning: Warnings.Add);
    }

    public FakeCamera Camera { get; }

    public TaskStepList Steps { get; }

    public Guid TaskId { get; } = Guid.NewGuid();

    public IVapixClient Vapix => Camera;

    public ILogger Logger { get; } = NullLogger.Instance;

    public ICorePlugin? Owner { get; init; }

    public MemoryFiles MemoryFiles { get; } = new();

    public IUploadedFiles Files => MemoryFiles;

    public List<string> Warnings { get; } = [];

    public List<(TaskLogLevel Level, string Message)> LogEntries { get; } = [];

    /// <summary>The connection OADM uses after the task ("https" or "http") and the pinned fingerprint.</summary>
    public string Scheme { get; private set; } = "https";

    public string? PinnedFingerprint { get; private set; }

    public int TlsUpdates { get; private set; }

    public void PlanSteps(params string[] names) => Steps.Plan(names);

    public ITaskStep BeginStep(string name) => Steps.Begin(name);

    public void ReportProgress(int percent, string? message = null)
    {
    }

    public void ReportWarning(string message) => Warnings.Add(message);

    public void Log(TaskLogLevel level, string message) => LogEntries.Add((level, message));

    public Task UpdateDeviceTlsAsync(string scheme, string? expectedFingerprintSha256, CancellationToken ct)
    {
        TlsUpdates++;
        if (scheme == "https")
        {
            var served = Camera.ServedFingerprint ?? throw new DeviceIdentityException("The device does not answer over HTTPS.");
            if (expectedFingerprintSha256 is not null && !string.Equals(served, expectedFingerprintSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new DeviceIdentityException("The device does not present the expected certificate.");
            }

            PinnedFingerprint = served;
        }

        Scheme = scheme;
        return Task.CompletedTask;
    }
}
