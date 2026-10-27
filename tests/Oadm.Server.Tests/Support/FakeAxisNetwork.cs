using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

using Oadm.Core.Discovery;
using Oadm.Core.Sites;
using Oadm.Core.Vapix;

namespace Oadm.Server.Tests.Support;

/// <summary>A simulated Axis device answering the VAPIX endpoints OADM uses.</summary>
internal sealed class FakeAxisDevice
{
    private int _downRequests;

    public required string Address { get; init; }

    public required string Serial { get; init; }

    public string Model { get; init; } = "M3106-L Mk II";

    public string Firmware { get; set; } = "12.6.85";

    public string User { get; set; } = "root";

    public string? Password { get; set; }

    /// <summary>Factory default: no admin user yet (systemready needsetup = yes).</summary>
    public bool NeedSetup => Password is null;

    /// <summary>Serves HTTPS (443). The fake has no TLS; with <see cref="Certificate"/> the pinning check is simulated.</summary>
    public bool ServesHttps { get; set; }

    /// <summary>
    /// The certificate the device "presents" on HTTPS: the connection's <see cref="CertificatePinning"/> validates it
    /// like a real handshake (a pin mismatch fails the request). Null: no certificate check.
    /// </summary>
    public System.Security.Cryptography.X509Certificates.X509Certificate2? Certificate { get; set; }

    /// <summary>When true, every connection is refused.</summary>
    public bool Offline { get; set; }

    public int RestartCalls { get; private set; }

    /// <summary>Answer of apidiscovery.cgi getApiList (id, version).</summary>
    public List<(string Id, string Version)> Apis { get; } = [("user-management", "1.2"), ("network-settings", "1.37")];

    public int PwdgrpCalls { get; private set; }

    /// <summary>systemready passphrasepolicy ("none", "length", "complex").</summary>
    public string PassphrasePolicy { get; set; } = "none";

    /// <summary>pwdgrp.cgi refuses shorter first passwords (the device's passphrase policy).</summary>
    public int MinimumPasswordLength { get; set; }

    /// <summary>Every pwdgrp.cgi request: URL scheme, HTTP method and whether the URL carried a query.</summary>
    public List<(string Scheme, string Method, bool QueryInUrl)> PwdgrpRequests { get; } = [];

    /// <summary>Requests that carried credentials the device rejected.</summary>
    public int RejectedLogins { get; private set; }

    /// <summary>
    /// What the device answers to the anonymous <c>getAllUnrestrictedProperties</c> (the add page's Axis check).
    /// Default: a valid Axis answer with its serial number.
    /// </summary>
    public FakeAxisAnswer AnonymousAnswer { get; set; } = FakeAxisAnswer.Axis;

    /// <summary>Serves HTTP (80). False: plain HTTP connections are refused.</summary>
    public bool ServesHttp { get; set; } = true;

    /// <summary>
    /// Accepts only Basic login on HTTP (Network.HTTP.AuthenticationPolicy=basic): a login over HTTP counts only when the
    /// client may send Basic there, and its 401 challenges only Basic (<c>WWW-Authenticate: Basic realm="AXIS_..."</c>, as
    /// an AXIS OS device set to Basic answers).
    /// </summary>
    public bool BasicOnlyOverHttp { get; set; }

    private bool _basicChallenge;

    /// <summary>Every request that carried credentials: URL scheme and user name, in order.</summary>
    public List<(string Scheme, string User)> CredentialRequests { get; } = [];

    /// <summary>Requests with a password in the URL query (must never happen).</summary>
    public int PasswordInUrl { get; private set; }

    public bool IsDown => Offline || Volatile.Read(ref _downRequests) > 0;

    /// <summary>After a restart the device refuses this many requests, then answers again.</summary>
    public int RestartDownRequests { get; set; } = 2;

    /// <summary>Every request that reached the device (also refused ones): the canary tests check that nothing did.</summary>
    public int Requests => Volatile.Read(ref _requests);

    private int _requests;

    public HttpResponseMessage Handle(HttpRequestMessage request, string body, NetworkCredential? credentials)
    {
        Interlocked.Increment(ref _requests);
        if (Offline)
        {
            throw Refused();
        }

        if (Interlocked.Decrement(ref _downRequests) >= 0)
        {
            throw Refused();
        }

        Interlocked.Exchange(ref _downRequests, 0);
        _basicChallenge = BasicOnlyOverHttp && request.RequestUri!.Scheme == Uri.UriSchemeHttp;
        var path = request.RequestUri!.AbsolutePath.TrimStart('/');
        var query = request.RequestUri.Query;
        if (Password is not null && query.Contains(Password, StringComparison.Ordinal))
        {
            PasswordInUrl++;
        }

        if (credentials is not null)
        {
            CredentialRequests.Add((request.RequestUri.Scheme, credentials.UserName));
        }

        var authorized = !NeedSetup && credentials is not null
            && credentials.UserName == User && credentials.Password == Password;
        if (credentials is not null && !authorized && !NeedSetup)
        {
            RejectedLogins++;
        }

        switch (path)
        {
            case "axis-cgi/basicdeviceinfo.cgi":
                if (body.Contains("getAllUnrestrictedProperties", StringComparison.Ordinal))
                {
                    return AnonymousAnswer switch
                    {
                        FakeAxisAnswer.Axis => Json(Serialize(new { apiVersion = "1.3", data = new { propertyList = new { SerialNumber = Serial, ProdNbr = Model, Version = Firmware, Brand = "AXIS" } } })),
                        FakeAxisAnswer.OtherSerial => Json(Serialize(new { apiVersion = "1.3", data = new { propertyList = new { SerialNumber = "ACCC8EFFFFFF", ProdNbr = Model, Brand = "AXIS" } } })),
                        FakeAxisAnswer.NoSerial => Json(Serialize(new { apiVersion = "1.3", data = new { propertyList = new { ProdNbr = Model, Brand = "AXIS" } } })),
                        FakeAxisAnswer.NoModel => Json(Serialize(new { apiVersion = "1.3", data = new { propertyList = new { SerialNumber = Serial, Brand = "AXIS" } } })),
                        FakeAxisAnswer.Unauthorized => Unauthorized(),
                        FakeAxisAnswer.UnauthorizedOtherRealm => Unauthorized("AXIS_ACCC8EFFFFFF"),
                        FakeAxisAnswer.UnauthorizedNoRealm => Unauthorized("device"),
                        _ => new HttpResponseMessage(HttpStatusCode.NotFound),
                    };
                }

                return authorized ? Json(BasicDeviceInfoJson()) : Unauthorized();

            case "axis-cgi/systemready.cgi":
                return Json(Serialize(new { apiVersion = "1.5", method = "systemready", data = new { systemready = "yes", needsetup = NeedSetup ? "yes" : "no", uptime = "100", bootid = "b1", passphrasepolicy = PassphrasePolicy } }));

            case "axis-cgi/pwdgrp.cgi":
                PwdgrpCalls++;
                PwdgrpRequests.Add((request.RequestUri.Scheme, request.Method.Method, query.Length > 0));
                if (!NeedSetup)
                {
                    return Text("Error: account root already exists");
                }

                var form = ParseForm(body);
                if (form.GetValueOrDefault("action") != "add" || !form.TryGetValue("pwd", out var pwd))
                {
                    return Text("Error: bad request");
                }

                if (pwd.Length < MinimumPasswordLength)
                {
                    return Text("Error: the password does not meet the passphrase policy");
                }

                User = form.GetValueOrDefault("user") ?? "root";
                Password = pwd;
                return Text("Created account root.");

            case "axis-cgi/param.cgi":
                if (!authorized)
                {
                    return Unauthorized();
                }

                return Text(
                    "Network.BootProto=dhcp\n"
                    + $"Network.UPnP.FriendlyName=AXIS {Model} - {Serial}\n"
                    + "Network.Interface.I0.dot1x.Enabled=no\n"
                    + "root.HTTPS.Enabled=yes\n");

            case "axis-cgi/apidiscovery.cgi":
                return authorized
                    ? Json(Serialize(new { apiVersion = "1.1", method = "getApiList", data = new { apiList = Apis.Select(a => new { id = a.Id, version = a.Version, name = a.Id, status = "official" }) } }))
                    : Unauthorized();

            case "axis-cgi/restart.cgi":
                if (!authorized)
                {
                    return Unauthorized();
                }

                RestartCalls++;
                Volatile.Write(ref _downRequests, RestartDownRequests);
                return Text("OK");

            default:
                return WebPage?.Invoke(request, body, authorized) ?? new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    /// <summary>Pages of the web interface (Web View relay tests): request, body, whether the login was right.</summary>
    public Func<HttpRequestMessage, string, bool, HttpResponseMessage?>? WebPage { get; set; }

    private string BasicDeviceInfoJson() => Serialize(new
    {
        apiVersion = "1.3",
        data = new
        {
            propertyList = new
            {
                SerialNumber = Serial,
                ProdNbr = Model,
                ProdShortName = "AXIS " + Model,
                ProdFullName = "AXIS " + Model + " Camera",
                Version = Firmware,
                HardwareID = "1",
                Architecture = "aarch64",
            },
        },
    });

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value);

    private HttpResponseMessage Unauthorized(string? realm = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ParseAdd(_basicChallenge
            ? $"Basic realm=\"{realm ?? "AXIS_" + Serial}\""
            : $"Digest realm=\"{realm ?? "AXIS_" + Serial}\", nonce=\"abc\", algorithm=MD5, qop=\"auth\"");
        return response;
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Text(string text) =>
        new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "text/plain") };

    private static Dictionary<string, string> ParseForm(string body) =>
        body.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .ToDictionary(p => Uri.UnescapeDataString(p[0].Replace('+', ' ')), p => p.Length > 1 ? Uri.UnescapeDataString(p[1].Replace('+', ' ')) : string.Empty);

    internal static HttpRequestException Refused() =>
        new(HttpRequestError.ConnectionError, "Connection refused", new SocketException((int)SocketError.ConnectionRefused));
}

/// <summary>
/// A set of fake devices by address, plus the HTTP plumbing that routes requests to them. Devices at sites live in a
/// network of their own per site (<see cref="AtSite"/>): two sites may both have 192.168.0.20 (docs/spec/sites.md,
/// Discovery and moves at sites); connections, probes and DNS names go to the network of their route.
/// </summary>
internal sealed class FakeAxisNetwork
{
    private readonly ConcurrentDictionary<string, FakeAxisDevice> _devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, FakeAxisNetwork> _sites = new();

    /// <summary>The devices reached through a site (created on first use).</summary>
    public FakeAxisNetwork AtSite(Guid siteId) => _sites.GetOrAdd(siteId, _ => new FakeAxisNetwork());

    /// <summary>The network of a route: this one for the server's own network, else the site's.</summary>
    public FakeAxisNetwork For(DeviceRoute route) => route.SiteId is { } site ? AtSite(site) : this;

    /// <summary>A site problem for a request (e.g. a port the site does not allow); null = none.</summary>
    public Func<Uri, SiteException?>? SiteProblem { get; set; }

    public FakeAxisDevice Add(string address, string serial, string? password, string model = "M3106-L Mk II")
    {
        var device = new FakeAxisDevice { Address = address, Serial = serial, Password = password, Model = model };
        _devices[address] = device;
        return device;
    }

    public FakeAxisDevice this[string address] => _devices[address];

    public FakeAxisDevice? Find(string host) => _devices.GetValueOrDefault(host.Trim('[', ']'));

    /// <summary>Anonymous handler (probes).</summary>
    public HttpMessageHandler CreateHandler(NetworkCredential? credentials = null, CertificatePinning? pinning = null, bool allowBasicOverHttp = false) =>
        new Handler(this, credentials, pinning, allowBasicOverHttp);

    /// <summary>DNS names of addresses (IP -> FQDN) for the discovery's DNS lookup; no real DNS in tests.</summary>
    public ConcurrentDictionary<string, string> DnsNames { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The discovery's DNS lookup over <see cref="DnsNames"/>.</summary>
    public IDnsNameLookup CreateDnsLookup() => new DnsLookup(this);

    private sealed class DnsLookup(FakeAxisNetwork network) : IDnsNameLookup
    {
        public Task<string?> LookupAsync(IPAddress address, CancellationToken ct) =>
            Task.FromResult(network.DnsNames.TryGetValue(address.ToString(), out var name) ? name : null);

        public Task<string?> LookupAsync(IPAddress address, DeviceRoute route, CancellationToken ct) =>
            Task.FromResult(network.For(route).DnsNames.TryGetValue(address.ToString(), out var name) ? name : null);
    }

    /// <summary><see cref="IVapixConnector"/> that sends the connection's credentials to the fake device.</summary>
    public IVapixConnector CreateConnector() => new Connector(this);

    /// <summary>Delay before every device answer (parallelism tests). Default none.</summary>
    public TimeSpan Latency { get; set; }

    /// <summary>Awaited before every answer to a request with a login (gates: hold a login while the test acts).</summary>
    public Func<NetworkCredential, CancellationToken, Task>? BeforeLoginAnswer { get; set; }

    /// <summary>Every connection opened through <see cref="CreateConnector"/>, in order.</summary>
    public ConcurrentQueue<VapixConnectionOptions> Connections { get; } = new();

    /// <summary>The anonymous probe used by AddDevices, wired to this network.</summary>
    /// <remarks>Generous timeouts: nothing in the fake network hangs, but a busy test machine answers slowly.</remarks>
    public VapixProbe CreateProbe() => new(AnswerTimeout, (_, _, route) => For(route).CreateHandler());

    /// <summary>Timeout of the probes and clients of the fake network: far above what a busy machine needs.</summary>
    public static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(30);

    private sealed class Handler(FakeAxisNetwork network, NetworkCredential? credentials, CertificatePinning? pinning, bool allowBasicOverHttp) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uri = request.RequestUri!;

            // A handler for every route (the discovery probe) reads the route from the request.
            var routed = request.Options.TryGetValue(DeviceRouter.RouteOption, out var route) ? network.For(route) : network;
            if (routed.SiteProblem?.Invoke(uri) is { } problem)
            {
                throw new HttpRequestException(problem.Message, problem);
            }

            var device = routed.Find(uri.Host);
            if (device is null || (uri.Scheme == Uri.UriSchemeHttps && !device.ServesHttps) || (uri.Scheme == Uri.UriSchemeHttp && !device.ServesHttp))
            {
                throw FakeAxisDevice.Refused();
            }

            if (uri.Scheme == Uri.UriSchemeHttps && pinning is not null && device.Certificate is { } certificate && !device.Offline
                && !pinning.Validate(certificate, null, uri.Host))
            {
                throw new HttpRequestException("The SSL connection could not be established (certificate pin mismatch).");
            }

            if (network.Latency > TimeSpan.Zero)
            {
                await Task.Delay(network.Latency, cancellationToken).ConfigureAwait(false);
            }

            if (credentials is not null && network.BeforeLoginAnswer is { } gate)
            {
                await gate(credentials, cancellationToken).ConfigureAwait(false);
            }

            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            lock (device)
            {
                // A device with Basic only on HTTP never sees a login the client may not send as Basic there.
                var sent = device.BasicOnlyOverHttp && uri.Scheme == Uri.UriSchemeHttp && !allowBasicOverHttp ? null : credentials;
                var response = device.Handle(request, body, sent);
                response.RequestMessage = request;
                return response;
            }
        }
    }

    private sealed class Connector(FakeAxisNetwork network) : IVapixConnector
    {
        public VapixClient Connect(VapixConnectionOptions options)
        {
            network.Connections.Enqueue(options);
            var pinning = options.Scheme == Uri.UriSchemeHttps ? new CertificatePinning(options.PinnedCertificateFingerprint) : null;
            return new(VapixClient.BuildBaseAddress(options.Scheme, options.Address),
                network.For(options.Route).CreateHandler(options.Credentials, pinning, options.AllowBasicOverHttp), pinning, AnswerTimeout)
            {
                Route = options.Route,
                AllowsBasicOverHttp = options.AllowBasicOverHttp,
            };
        }
    }
}

/// <summary>Answers of a fake device to the anonymous Axis check.</summary>
internal enum FakeAxisAnswer
{
    /// <summary>Valid: serial number and product number.</summary>
    Axis,

    /// <summary>Not found (HTTP 404): an imitation that only copies the 401 realm.</summary>
    NotFound,

    /// <summary>A property list without a serial number.</summary>
    NoSerial,

    /// <summary>A property list without a product number.</summary>
    NoModel,

    /// <summary>Another serial number than the discovered one.</summary>
    OtherSerial,

    /// <summary>HTTP 401 for the anonymous request with the device's realm (AXIS OS 8.x).</summary>
    Unauthorized,

    /// <summary>HTTP 401 for the anonymous request, realm with another serial number.</summary>
    UnauthorizedOtherRealm,

    /// <summary>HTTP 401 for the anonymous request without an AXIS_ realm.</summary>
    UnauthorizedNoRealm,
}

internal static class FakeSerials
{
    public static string Make(int n) => "ACCC8E" + n.ToString("X6", CultureInfo.InvariantCulture);
}
