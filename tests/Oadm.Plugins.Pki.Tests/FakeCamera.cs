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

    public static bool Exists(string name) => File.Exists(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

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

    /// <summary>False: a user limited to a device group added it (no host names in its certificates).</summary>
    public bool AddedByAllDevices { get; init; } = true;

    public bool? ConnectsOverHttps { get; init; } = true;
}

/// <summary>One request the fake received.</summary>
internal sealed record CameraRequest(string Method, string Path, string Body)
{
    private static readonly string[] SoapWrites =
    [
        "SetWebServerTlsConfiguration", "CreateCertificate2", "GetPkcs10Request2", "LoadCertificates", "LoadCACertificates", "LoadPkcs12",
        "DeleteCertificates", "SetCertSet", "SetDot1XConfiguration",
    ];

    /// <summary>
    /// Changes the device (REST POST/PATCH/DELETE, SOAP Set / Load / Create / Delete, setWired8021XConfiguration, param.cgi
    /// update). The SOAP certificate request counts as a write like REST get_csr (a POST that the device must allow).
    /// </summary>
    public bool IsWrite =>
        (Path.StartsWith(CertApi.BasePath, StringComparison.Ordinal) && Method != "GET")
        || SoapWrites.Any(w => Body.Contains("<acertificates:" + w, StringComparison.Ordinal) || Body.Contains("<tds:" + w, StringComparison.Ordinal) || Body.Contains("<aweb:" + w, StringComparison.Ordinal))
        || Body.Contains("setWired8021XConfiguration", StringComparison.Ordinal)
        || Body.StartsWith("action=update", StringComparison.Ordinal);
}

/// <summary>
/// A stateful fake AXIS camera: REST cert v1 (keys generated in memory, real CSRs, PATCH checks the key), the SOAP web
/// server TLS calls, network_settings.cgi 802.1X, time.cgi and param.cgi, starting from the recorded 10.0.0.48 answers.
/// Never touches the network.
/// </summary>
/// <summary>What <see cref="FakeCamera"/> does with a certificate sent with its chain.</summary>
public enum ChainAnswer
{
    /// <summary>Takes it (REST keeps the PEM as sent, SOAP the certificate itself).</summary>
    Takes,

    /// <summary>Refuses it (REST an error answer, SOAP a fault); the certificate alone is taken.</summary>
    Refuses,

    /// <summary>SOAP takes it but keeps the whole DER sequence as the entry.</summary>
    KeepsAsSent,
}

internal sealed class FakeCamera : IVapixClient
{
    private readonly Dictionary<string, (string Pem, AsymmetricAlgorithm? Key)> _certificates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _cas = new(StringComparer.Ordinal);
    private JsonObject _networkInfo;

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

    /// <summary>The server setting "Allow Basic login over HTTP" (default off).</summary>
    public bool AllowsBasicOverHttp { get; set; }

    /// <summary>
    /// Like AXIS OS 9.80 after a connection policy change: the web server restarts. After each SetWebServerTlsConfiguration
    /// the camera answers <see cref="AnswersBeforeRestart"/> more requests, then refuses the next <see cref="RestartRefusals"/>
    /// (every request, the HTTPS handshake of the task context too), then answers again. 0 (default): no restart.
    /// </summary>
    public int RestartRefusals { get; set; }

    public int AnswersBeforeRestart { get; set; } = 1;

    /// <summary>Requests refused while the web server restarted.</summary>
    public int Refused { get; private set; }

    private int _untilRestart = -1;
    private int _refusalsLeft;

    /// <summary>Counts one request against a restart in progress; throws like a refused connection while it restarts.</summary>
    public void Connect()
    {
        lock (Requests)
        {
            if (_untilRestart > 0)
            {
                _untilRestart--;
                return;
            }

            if (_untilRestart == 0 && _refusalsLeft > 0)
            {
                _refusalsLeft--;
                Refused++;
                if (_refusalsLeft == 0)
                {
                    _untilRestart = -1;
                }

                throw new HttpRequestException("No connection could be made because the target machine actively refused it.", new System.Net.Sockets.SocketException(10061));
            }
        }
    }

    private void WebServerChanged()
    {
        if (RestartRefusals > 0)
        {
            _untilRestart = AnswersBeforeRestart;
            _refusalsLeft = RestartRefusals;
        }
    }

    public string Serial { get; init; } = "B8A44F631339";

    public IReadOnlyList<DeviceApi> ApiList { get; set; } = PkiFixture.Apis;

    /// <summary>False: config/discover does not list cert (AXIS OS before 11.11).</summary>
    public bool HasCertApi { get; set; } = true;

    /// <summary>"released" (default) or "beta" (AXIS OS 11.9: the REST API is not used).</summary>
    public string CertApiState { get; set; } = "released";

    /// <summary>False: the REST certificate API answers 404 (AXIS OS before 11.11).</summary>
    public bool HasRestCertificates { get; set; } = true;

    /// <summary>True: <c>config/discover</c> itself answers 404 (AXIS OS 9.80 and older).</summary>
    public bool DiscoverMissing { get; set; }

    /// <summary>The AXIS OS version of basicdeviceinfo.cgi and <c>Properties.Firmware.Version</c>.</summary>
    public string Firmware { get; set; } = "12.11.77";

    /// <summary>False: basicdeviceinfo.cgi does not answer (the version comes from the parameter).</summary>
    public bool HasBasicDeviceInfo { get; set; } = true;

    /// <summary>False: network_settings.cgi has no getNetworkInfo (AXIS OS 8.40: "Method does not exist").</summary>
    public bool HasNetworkSettings { get; set; } = true;

    /// <summary>
    /// False (network settings before 1.38): getNetworkInfo names no client or CA certificate and setWired8021XConfiguration
    /// answers success to them without binding them (research of 2026-10-25 on 9.80, 11.9 and 11.11).
    /// </summary>
    public bool NetworkSettingsBindsCertificates { get; set; } = true;

    /// <summary>True (AXIS OS before 7.10): the web server reports and requires the Operator and Viewer policies.</summary>
    public bool WebServerRoles { get; set; }

    public string OperatorPolicy { get; set; } = ConnectionPolicy.HttpAndHttps;

    public string ViewerPolicy { get; set; } = ConnectionPolicy.HttpAndHttps;

    /// <summary>False: the web server reports TLS off (an untouched AXIS OS 6.53).</summary>
    public bool TlsOn { get; set; } = true;

    /// <summary>True: LoadPkcs12 refuses an ECC key (AXIS OS 6.53: "Action failed").</summary>
    public bool RefusesEccPkcs12 { get; set; }

    /// <summary>A SOAP operation with this name answers with a fault and changes nothing.</summary>
    public string? FailSoap { get; set; }

    /// <summary>What the camera does with a certificate sent with its chain (REST PATCH, SOAP LoadCertificates).</summary>
    public ChainAnswer Chains { get; set; } = ChainAnswer.Takes;

    /// <summary>The SOAP certificate set <c>EAPTLS_WIRED</c> (the 802.1X certificates where network settings do not bind them).</summary>
    public CertSet WiredSet { get; set; } = CertSet.Empty;

    /// <summary>The ids of every SOAP CreateCertificate2, in order.</summary>
    public List<string> SoapKeysCreated { get; } = [];

    /// <summary>
    /// A camera like the recorded older AXIS OS (tests/Oadm.Plugins.Pki.Tests/Fixtures/Legacy/&lt;version&gt;): "6.53", "8.40",
    /// "9.80", "10.12", "11.9", "11.11". The CA certificates, the web server and the network settings start from the recorded
    /// answers of that version where there are any; 802.1X starts off.
    /// </summary>
    public static FakeCamera Legacy(string version)
    {
        var camera = new FakeCamera { Firmware = LegacyFirmware(version) };
        camera.MakeLegacy(version);
        return camera;
    }

    private static string LegacyFirmware(string version) => version switch
    {
        "6.53" => "6.53.4",
        "8.40" => "8.40.104",
        "9.80" => "9.80.3",
        "10.12" => "10.12.236",
        "11.9" => "11.9.65",
        "11.11" => "11.11.232",
        _ => throw new ArgumentException("No recorded answers of " + version, nameof(version)),
    };

    private void MakeLegacy(string version)
    {
        var folder = "Legacy/" + version + "/";
        var major = int.Parse(version.Split('.')[0], CultureInfo.InvariantCulture);
        var rest = version == "11.11";
        HasCertApi = rest || version == "11.9";
        CertApiState = rest ? "released" : "beta";
        HasRestCertificates = rest;
        DiscoverMissing = major < 10;
        HasBasicDeviceInfo = version != "6.53";
        HasNetworkSettings = major >= 9;
        NetworkSettingsBindsCertificates = false;
        WebServerRoles = version == "6.53";
        RefusesEccPkcs12 = version == "6.53";
        KeyTypes = rest ? null : KeyTypes; // 11.11: the keystore lists no key types
        ApiList = version switch
        {
            "6.53" or "8.40" => [new("param-cgi", "1.0", "Legacy Parameter Handling", "from parameters")],
            "9.80" => [new("network-settings", "1.6"), new("param-cgi", "1.0")],
            "10.12" => [new("network-settings", "1.15"), new("param-cgi", "1.0"), new("time-service", "1.0")],
            "11.9" => [new("network-settings", "1.29"), new("param-cgi", "1.0"), new("time-service", "1.0")],
            _ => [new("network-settings", "1.33"), new("param-cgi", "1.0"), new("time-service", "1.0")],
        };

        // Certificates: only the recorded web server answer's state; the device certificates of the fixture are other cameras'.
        if (!rest)
        {
            _certificates.Clear();
            _cas.Clear();
            if (PkiFixture.Exists(folder + "GetCACertificates.xml"))
            {
                foreach (var ca in CertSoapApi.ParseCertificates(PkiFixture.Read(folder + "GetCACertificates.xml"), "CACertificate"))
                {
                    _cas[ca.Alias] = ca.Pem;
                }
            }
        }

        if (PkiFixture.Exists(folder + "GetWebServerTlsConfiguration.xml"))
        {
            var web = Oadm.Plugins.Pki.Device.WebServerTls.Parse(PkiFixture.Read(folder + "GetWebServerTlsConfiguration.xml"));
            Policy = web.Policy!;
            OperatorPolicy = web.OperatorPolicy ?? Policy;
            ViewerPolicy = web.ViewerPolicy ?? Policy;
            Ciphers = web.Ciphers;
            TlsOn = web.Tls;
        }

        // The served certificate: the device's own self-signed one.
        using (var key = RSA.Create(2048))
        {
            var request = new CertificateRequest("CN=axis-accc8e000009", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var self = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
            WebServerAlias = "Default (self-signed)";
            _certificates[WebServerAlias] = (self.ExportCertificatePem() + "\n", null);
        }

        if (PkiFixture.Exists(folder + "network_settings-getNetworkInfo.json"))
        {
            _networkInfo = JsonNode.Parse(PkiFixture.Read(folder + "network_settings-getNetworkInfo.json"))!.AsObject();
            Dot1x["enabled"] = false;
        }

        foreach (var (name, value) in PkiFixture.Parameters("paramcgi-Network.Interface.I0.dot1x.txt"))
        {
            Parameters[name] = value;
        }

        Parameters[Dot1xSettings.EapolParameter] = "1";
        Parameters["Properties.Firmware.Version"] = Firmware;
    }

    /// <summary>False: the SOAP web server service answers with a fault.</summary>
    public bool HasWebServerService { get; set; } = true;

    /// <summary>Device clock minus real clock.</summary>
    public TimeSpan ClockOffset { get; set; }

    /// <summary>A REST call whose path contains this text answers with error code 5.</summary>
    public string? FailPath { get; set; }

    /// <summary>SetWebServerTlsConfiguration answers with a SOAP fault and changes nothing.</summary>
    public bool FailWebServerSwitch { get; set; }

    /// <summary>SetWebServerTlsConfiguration applies the settings, then answers with a SOAP fault (the answer got lost).</summary>
    public bool FailWebServerSwitchAnswer { get; set; }

    /// <summary>Every SOAP request gets this answer (status and body) instead of the service's.</summary>
    public (HttpStatusCode Status, string Body)? SoapAnswer { get; set; }

    /// <summary>setWired8021XConfiguration answers with an error after it applied the settings (the answer got lost).</summary>
    public bool FailDot1xAnswer { get; set; }

    /// <summary>setWired8021XConfiguration keeps the client certificate it had (the read-back names another one).</summary>
    public bool KeepDot1xClient { get; set; }

    public string Policy { get; set; }

    public IReadOnlyList<string> Ciphers { get; private set; }

    public string WebServerAlias { get; set; }

    public Dictionary<string, string> Parameters { get; }

    public List<CameraRequest> Requests { get; } = [];

    /// <summary>The key types the keystore offers (GET cert/v1); null: the device does not say (404).</summary>
    public IReadOnlyList<string>? KeyTypes { get; set; } = ["RSA-2048", "RSA-4096", "EC-P256", "EC-P384", "EC-P521"];

    /// <summary>The key types of every create_certificate, in order.</summary>
    public List<string> KeyTypesCreated { get; } = [];

    public IReadOnlyList<CameraRequest> Writes => [.. Requests.Where(r => r.IsWrite)];

    public IReadOnlyCollection<string> CertificateAliases => _certificates.Keys;

    public IReadOnlyCollection<string> CaAliases => _cas.Keys;

    public string CertificatePem(string alias) => _certificates[alias].Pem;

    /// <summary>The <c>getNetworkInfo</c> answer (host name, addresses); a test changes it to what the camera reports.</summary>
    public JsonObject NetworkInfo => _networkInfo;

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

    public Task<BasicDeviceInfo> GetBasicDeviceInfoAsync(CancellationToken ct) => HasBasicDeviceInfo
        ? Task.FromResult(new BasicDeviceInfo(Serial, "P3265-V", null, null, Firmware, null, null))
        : Task.FromException<BasicDeviceInfo>(new HttpRequestException("Not found", null, HttpStatusCode.NotFound));

    public Task<IReadOnlyDictionary<string, string>> ListParametersAsync(IEnumerable<string> groups, CancellationToken ct)
    {
        Connect();
        var prefixes = groups.ToList();
        IReadOnlyDictionary<string, string> result = Parameters
            .Where(p => prefixes.Any(g => p.Key == g || p.Key.StartsWith(g + ".", StringComparison.OrdinalIgnoreCase)))
            .ToDictionary(p => p.Key, p => p.Value);
        return Task.FromResult(result);
    }

    public Task RestartAsync(CancellationToken ct) => throw new NotSupportedException("The PKI tasks never restart a device.");

    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Connect();
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
            if (DiscoverMissing)
            {
                return (HttpStatusCode.NotFound, "<html><body>Not Found</body></html>", "text/html");
            }

            var discover = JsonNode.Parse(PkiFixture.Read(PkiFixture.Discover))!;
            if (!HasCertApi)
            {
                discover["apis"]!.AsObject().Remove("cert");
            }
            else
            {
                discover["apis"]!["cert"]!["v1"]!["state"] = CertApiState;
            }

            return Json(discover.ToJsonString());
        }

        if (path.StartsWith(CertApi.BasePath, StringComparison.Ordinal))
        {
            return HasRestCertificates
                ? Rest(method, Uri.UnescapeDataString(path[CertApi.BasePath.Length..]), body)
                : (HttpStatusCode.NotFound, "<html><body>Not Found</body></html>", "text/html");
        }

        if (path == Dot1xSettings.ParamPath && method == "POST" && body.StartsWith("action=update", StringComparison.Ordinal))
        {
            foreach (var pair in body.Split('&').Skip(1).Select(p => p.Split('=', 2)))
            {
                Parameters[Uri.UnescapeDataString(pair[0])] = Uri.UnescapeDataString(pair[1]);
            }

            return (HttpStatusCode.OK, "OK", "text/plain");
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
            case ("GET", ""):
                return KeyTypes is null
                    ? Error(HttpStatusCode.NotFound, 1, "Resource not found")
                    : Success(new JsonObject
                    {
                        ["keystores"] = new JsonArray(new JsonObject { ["id"] = "SE0", ["key_types"] = new JsonArray([.. KeyTypes.Select(t => (JsonNode)JsonValue.Create(t)!)]) }),
                        ["settings"] = new JsonObject { ["keystore"] = "SE0" },
                    });
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

                var keyType = data["key_type"]!.GetValue<string>();
                KeyTypesCreated.Add(keyType);
                Assert.Contains(keyType, KeyTypes ?? ["RSA-2048"]);
                AsymmetricAlgorithm key = keyType == "EC-P256" ? ECDsa.Create(ECCurve.NamedCurves.nistP256) : RSA.Create(2048);
                var request = Request(data["subject"]!.GetValue<string>(), key);
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
                var request = Request(data!["subject"]!.GetValue<string>(), entry.Key!);
                return Success(JsonValue.Create(request.CreateSigningRequestPem()));
            }

            if (method == "PATCH")
            {
                var pem = data!["certificate"]!.GetValue<string>();
                if (Chains == ChainAnswer.Refuses && pem.Split("BEGIN CERTIFICATE").Length > 2)
                {
                    return Error(HttpStatusCode.BadRequest, 5, "Validation error: Invalid certificate");
                }

                using var certificate = X509Certificate2.CreateFromPem(pem);
                if (entry.Key is null || !certificate.PublicKey.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(entry.Key.ExportSubjectPublicKeyInfo()))
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
        if (SoapAnswer is { } answer)
        {
            return (answer.Status, answer.Body, "application/soap+xml");
        }

        var operation = XDocument.Parse(body).Descendants(XName.Get("Body", WebServerTls.SoapNs)).Single().Elements().First();
        if (FailSoap is not null && operation.Name.LocalName == FailSoap)
        {
            return Fault(HttpStatusCode.InternalServerError, "SOAP-ENV:Receiver", "ter:Action", "Action failed");
        }

        if (operation.Name.NamespaceName is DeviceSoap.DeviceNs or DeviceSoap.CertificatesNs)
        {
            return Certificates(operation);
        }

        if (!HasWebServerService)
        {
            return (HttpStatusCode.InternalServerError, PkiFixture.Read(PkiFixture.Fault), "application/soap+xml");
        }

        if (body.Contains("GetWebServerTlsConfiguration", StringComparison.Ordinal))
        {
            var set = $"<aweb:CertificateSet><acert:Certificates><acert:Id>{WebServerAlias}</acert:Id></acert:Certificates><acert:CACertificates></acert:CACertificates><acert:TrustedCertificates></acert:TrustedCertificates></aweb:CertificateSet>";
            var ciphers = string.Concat(Ciphers.Select(c => $"<acert:Cipher>{c}</acert:Cipher>"));
            var roles = WebServerRoles ? $"<aweb:Operator>{OperatorPolicy}</aweb:Operator><aweb:Viewer>{ViewerPolicy}</aweb:Viewer>" : string.Empty;
            return (HttpStatusCode.OK, Envelope($"<aweb:GetWebServerTlsConfigurationResponse><aweb:Configuration name=\"WebServer\"><aweb:Tls>{(TlsOn ? "true" : "false")}</aweb:Tls><aweb:ConnectionPolicies><aweb:Admin>{Policy}</aweb:Admin>{roles}</aweb:ConnectionPolicies><aweb:Ciphers>{ciphers}</aweb:Ciphers>{set}</aweb:Configuration></aweb:GetWebServerTlsConfigurationResponse>"), "application/soap+xml");
        }

        if (body.Contains("SetWebServerTlsConfiguration", StringComparison.Ordinal))
        {
            if (FailWebServerSwitch)
            {
                return (HttpStatusCode.InternalServerError, PkiFixture.Read(PkiFixture.Fault), "application/soap+xml");
            }

            var xml = XDocument.Parse(body);
            XNamespace web = WebServerTls.WebNs;
            XNamespace cert = WebServerTls.CertNs;

            // Like AXIS OS 12.11 (gSOAP): the configuration elements must be in the web server namespace.
            var configuration = xml.Descendants(web + "SetWebServerTlsConfiguration").Single().Elements().SingleOrDefault();
            if (configuration?.Name != web + "Configuration" || configuration.Element(web + "Tls") is null)
            {
                return (HttpStatusCode.InternalServerError, Envelope("<SOAP-ENV:Fault><SOAP-ENV:Code><SOAP-ENV:Value>SOAP-ENV:Sender</SOAP-ENV:Value><SOAP-ENV:Subcode><SOAP-ENV:Value>ter:TagMismatch</SOAP-ENV:Value></SOAP-ENV:Subcode></SOAP-ENV:Code><SOAP-ENV:Reason><SOAP-ENV:Text xml:lang=\"en\">Tag mismatch</SOAP-ENV:Text></SOAP-ENV:Reason></SOAP-ENV:Fault>"), "application/soap+xml");
            }

            // Like AXIS OS 6.53: Admin alone is an occurrence violation.
            if (WebServerRoles && (xml.Descendants(web + "Operator").SingleOrDefault() is null || xml.Descendants(web + "Viewer").SingleOrDefault() is null))
            {
                return Fault(HttpStatusCode.BadRequest, "SOAP-ENV:Sender", "ter:InvalidArgs", "Invalid argument");
            }

            var alias = xml.Descendants(cert + "Certificates").Single().Elements(cert + "Id").Single().Value;
            Assert.True(_certificates.ContainsKey(alias), "The web server certificate must exist: " + alias);
            Assert.Equal(Ciphers, xml.Descendants(cert + "Cipher").Select(c => c.Value).ToList()); // ciphers sent back as read
            WebServerAlias = alias;
            Policy = xml.Descendants(web + "Admin").Single().Value;
            TlsOn = xml.Descendants(web + "Tls").Single().Value == "true";
            if (WebServerRoles)
            {
                OperatorPolicy = xml.Descendants(web + "Operator").Single().Value;
                ViewerPolicy = xml.Descendants(web + "Viewer").Single().Value;
            }

            WebServerChanged();
            return FailWebServerSwitchAnswer
                ? (HttpStatusCode.InternalServerError, PkiFixture.Read(PkiFixture.Fault), "application/soap+xml")
                : (HttpStatusCode.OK, Envelope("<aweb:SetWebServerTlsConfigurationResponse/>"), "application/soap+xml");
        }

        return (HttpStatusCode.InternalServerError, PkiFixture.Read(PkiFixture.Fault), "application/soap+xml");
    }

    /// <summary>
    /// The SOAP certificate services like the recorded AXIS OS 6.53 to 11.11 answers: lists as base64 DER, RSA 2048 keys made
    /// in memory with real requests, a signed certificate loaded under a new id finds its key, ids are unique, certificates in
    /// use cannot be deleted, the certificate set EAPTLS_WIRED.
    /// </summary>
    private (HttpStatusCode, string, string) Certificates(XElement operation)
    {
        XNamespace tds = DeviceSoap.DeviceNs;
        XNamespace tt = DeviceSoap.SchemaNs;
        XNamespace ac = DeviceSoap.CertificatesNs;
        XNamespace cert = DeviceSoap.CertNs;
        static string Der(string pem) => pem.Length == 0 ? string.Empty : Convert.ToBase64String(CertSoapApi.Der(pem));
        static string Ids(string name, IReadOnlyList<string> ids) => $"<acert:{name}>{string.Concat(ids.Select(i => $"<acert:Id>{i}</acert:Id>"))}</acert:{name}>";
        var invalidId = Fault(HttpStatusCode.BadRequest, "SOAP-ENV:Sender", "ter:InvalidArgVal", "Invalid ID");
        switch (operation.Name.LocalName)
        {
            case "GetCertificates":
                return SoapOk($"<tds:GetCertificatesResponse>{string.Concat(_certificates.Select(c => $"<tds:NvtCertificate><tt:CertificateID>{c.Key}</tt:CertificateID><tt:Certificate><tt:Data>{Der(c.Value.Pem)}</tt:Data></tt:Certificate></tds:NvtCertificate>"))}</tds:GetCertificatesResponse>");
            case "GetCACertificates":
                return SoapOk($"<tds:GetCACertificatesResponse>{string.Concat(_cas.Select(c => $"<tds:CACertificate><tt:CertificateID>{c.Key}</tt:CertificateID><tt:Certificate><tt:Data>{Der(c.Value)}</tt:Data></tt:Certificate></tds:CACertificate>"))}</tds:GetCACertificatesResponse>");
            case "CreateCertificate2":
            {
                var id = operation.Element(ac + "Id")!.Value;
                if (_certificates.ContainsKey(id))
                {
                    return invalidId;
                }

                Assert.Null(operation.Element(ac + "KeyType")); // ter:TagMismatch on the recorded firmware
                var key = RSA.Create(2048);
                var request = Request("CN=" + operation.Element(ac + "Subject")!.Element(cert + "CN")!.Value, key);
                using var self = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddYears(1));
                _certificates[id] = (self.ExportCertificatePem() + "\n", key);
                SoapKeysCreated.Add(id);
                return SoapOk($"<acertificates:CreateCertificate2Response><acertificates:Id>{id}</acertificates:Id><acertificates:Certificate>{Convert.ToBase64String(self.RawData)}</acertificates:Certificate></acertificates:CreateCertificate2Response>");
            }

            case "GetPkcs10Request2":
            {
                var id = operation.Element(ac + "Id")!.Value;
                if (!_certificates.TryGetValue(id, out var entry) || entry.Key is null)
                {
                    return invalidId;
                }

                var request = Request("CN=" + operation.Element(ac + "Subject")!.Element(cert + "CN")!.Value, entry.Key);
                return SoapOk($"<acertificates:GetPkcs10Request2Response><acertificates:Pkcs10Request>{Convert.ToBase64String(request.CreateSigningRequest())}</acertificates:Pkcs10Request></acertificates:GetPkcs10Request2Response>");
            }

            case "LoadCertificates":
            {
                var item = operation.Element(tds + "NVTCertificate")!;
                var id = item.Element(tt + "CertificateID")!.Value;
                var bytes = Convert.FromBase64String(item.Element(tt + "Certificate")!.Element(tt + "Data")!.Value);
                System.Formats.Asn1.AsnDecoder.ReadEncodedValue(bytes, System.Formats.Asn1.AsnEncodingRules.DER, out _, out _, out var first);
                var chain = first < bytes.Length;
                if (chain && Chains == ChainAnswer.Refuses)
                {
                    return Fault(HttpStatusCode.BadRequest, "SOAP-ENV:Sender", "ter:InvalidArgVal", "Invalid certificate");
                }

                using var loaded = X509CertificateLoader.LoadCertificate(bytes.AsSpan(0, first));
                var key = _certificates.Values.Select(v => v.Key).FirstOrDefault(k => k is not null && loaded.PublicKey.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(k.ExportSubjectPublicKeyInfo()));
                if (_certificates.ContainsKey(id) || key is null)
                {
                    return Fault(HttpStatusCode.BadRequest, "SOAP-ENV:Sender", "ter:InvalidArgVal", "Invalid ID"); // also the id of the key itself (10.12)
                }

                // KeepsAsSent: the whole sequence stays one entry (what an unknown AXIS OS might do); else the certificate itself.
                _certificates[id] = (chain && Chains == ChainAnswer.KeepsAsSent ? new string(PemEncoding.Write("CERTIFICATE", bytes)) + "\n" : loaded.ExportCertificatePem() + "\n", key);
                return SoapOk("<tds:LoadCertificatesResponse></tds:LoadCertificatesResponse>");
            }

            case "LoadCACertificates":
            {
                var item = operation.Element(tds + "CACertificate")!;
                var id = item.Element(tt + "CertificateID")!.Value;
                if (_cas.ContainsKey(id))
                {
                    return Fault(HttpStatusCode.BadRequest, "SOAP-ENV:Sender", "ter:InvalidArgVal", "Invalid CA certificate ID");
                }

                using var loaded = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(item.Element(tt + "Certificate")!.Element(tt + "Data")!.Value));
                _cas[id] = loaded.ExportCertificatePem() + "\n";
                return SoapOk("<tds:LoadCACertificatesResponse></tds:LoadCACertificatesResponse>");
            }

            case "LoadPkcs12":
            {
                var p = operation.Element(ac + "Pkcs12")!;
                var id = p.Element(cert + "Id")!.Value;
                var collection = X509CertificateLoader.LoadPkcs12Collection(Convert.FromBase64String(p.Element(cert + "Certificate")!.Value), p.Element(cert + "Password")!.Value);
                var leaf = collection.Single(c => c.HasPrivateKey);
                AsymmetricAlgorithm? key = leaf.GetRSAPrivateKey();
                if (key is null && RefusesEccPkcs12)
                {
                    return Fault(HttpStatusCode.InternalServerError, "SOAP-ENV:Receiver", "ter:Action", "Action failed");
                }

                _certificates[id] = (leaf.ExportCertificatePem() + "\n", key ?? leaf.GetECDsaPrivateKey());
                return SoapOk($"<acertificates:LoadPkcs12Response><acertificates:Id>{id}</acertificates:Id><acertificates:Certificate xsi:nil=\"true\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\"/></acertificates:LoadPkcs12Response>");
            }

            case "DeleteCertificates":
            {
                var id = operation.Element(tds + "CertificateID")!.Value;
                var inUse = (!ConnectionPolicy.IsHttpOnly(Policy) && TlsOn && WebServerAlias == id) || WiredSet.Certificates.Contains(id) || WiredSet.CaCertificates.Contains(id);
                if (inUse)
                {
                    return Fault(HttpStatusCode.BadRequest, "SOAP-ENV:Sender", "ter:OperationProhibited", "Certificate in use");
                }

                return _certificates.Remove(id) || _cas.Remove(id) ? SoapOk("<tds:DeleteCertificatesResponse></tds:DeleteCertificatesResponse>") : invalidId;
            }

            case "GetCertSet":
                Assert.Equal(CertSoapApi.WiredDot1xSet, operation.Element(ac + "CertSetName")!.Value);
                return SoapOk($"<acertificates:GetCertSetResponse><acertificates:CertSet>{Ids("Certificates", WiredSet.Certificates)}{Ids("CACertificates", WiredSet.CaCertificates)}{Ids("TrustedCertificates", WiredSet.TrustedCertificates)}</acertificates:CertSet></acertificates:GetCertSetResponse>");
            case "SetCertSet":
            {
                Assert.Equal(CertSoapApi.WiredDot1xSet, operation.Element(ac + "CertSetName")!.Value);
                var set = CertSoapApi.ParseCertSet(new XElement(ac + "Body", operation));
                Assert.All(set.Certificates, id => Assert.True(_certificates.ContainsKey(id), "The client certificate must exist: " + id));
                Assert.All(set.CaCertificates, id => Assert.True(_cas.ContainsKey(id), "The CA certificate must exist: " + id));
                WiredSet = set;
                return SoapOk("<acertificates:SetCertSetResponse></acertificates:SetCertSetResponse>");
            }
        }

        return Fault(HttpStatusCode.InternalServerError, "SOAP-ENV:Receiver", "ter:ActionNotSupported", "Optional action not implemented");
    }

    private static (HttpStatusCode, string, string) SoapOk(string body) => (HttpStatusCode.OK, Envelope(body), "application/soap+xml");

    private static (HttpStatusCode, string, string) Fault(HttpStatusCode status, string code, string subcode, string reason) =>
        (status, Envelope($"<SOAP-ENV:Fault><SOAP-ENV:Code><SOAP-ENV:Value>{code}</SOAP-ENV:Value><SOAP-ENV:Subcode><SOAP-ENV:Value>{subcode}</SOAP-ENV:Value></SOAP-ENV:Subcode></SOAP-ENV:Code><SOAP-ENV:Reason><SOAP-ENV:Text xml:lang=\"en\">{reason}</SOAP-ENV:Text></SOAP-ENV:Reason></SOAP-ENV:Fault>"), "application/soap+xml");

    private (HttpStatusCode, string, string) NetworkSettings(string body)
    {
        var request = JsonNode.Parse(body)!;
        var method = request["method"]!.GetValue<string>();
        if (!HasNetworkSettings)
        {
            return Json(new JsonObject { ["apiVersion"] = "1.0", ["method"] = method, ["error"] = new JsonObject { ["code"] = 400, ["message"] = "Method does not exist" } }.ToJsonString());
        }

        if (method == "getNetworkInfo")
        {
            if (!NetworkSettingsBindsCertificates)
            {
                var copy = _networkInfo.DeepClone();
                foreach (var configuration in copy["data"]!["devices"]![0]!["wired"]!["8021X"]!["configurations"]!.AsArray())
                {
                    configuration!["params"]!.AsObject().Remove("certClient");
                    configuration["params"]!.AsObject().Remove("certsCA");
                }

                return Json(copy.ToJsonString());
            }

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
                if (p[key] is { } value && !(KeepDot1xClient && key == "certClient") && (NetworkSettingsBindsCertificates || key is "identity" or "eapolVersion"))
                {
                    EapTlsParams[key] = value.DeepClone();
                }
            }

            if (FailDot1xAnswer)
            {
                return Json("{\"error\":{\"code\":1100,\"message\":\"Internal error\"}}");
            }

            return Json(new JsonObject { ["apiVersion"] = request["apiVersion"]!.GetValue<string>(), ["method"] = method, ["data"] = new JsonObject() }.ToJsonString());
        }

        return Json("{\"error\":{\"code\":4002,\"message\":\"Method not supported\"}}");
    }

    private static string Envelope(string body) => DeviceSoap.Envelope(body);

    private static (HttpStatusCode, string, string) Json(string text) => (HttpStatusCode.OK, text, "application/json");

    private static CertificateRequest Request(string subject, AsymmetricAlgorithm key) => key is ECDsa ec
        ? new CertificateRequest(subject, ec, HashAlgorithmName.SHA256)
        : new CertificateRequest(subject, (RSA)key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

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
        try
        {
            Camera.Connect(); // the server connects with the new scheme and reads the serial number
        }
        catch (HttpRequestException ex)
        {
            throw new DeviceIdentityException($"The device does not answer over {scheme.ToUpperInvariant()}: {ex.Message}");
        }

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
