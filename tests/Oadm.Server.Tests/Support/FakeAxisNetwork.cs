using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

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

    /// <summary>Requests that carried credentials the device rejected.</summary>
    public int RejectedLogins { get; private set; }

    /// <summary>Requests with a password in the URL query (must never happen).</summary>
    public int PasswordInUrl { get; private set; }

    public bool IsDown => Offline || Volatile.Read(ref _downRequests) > 0;

    /// <summary>After a restart the device refuses this many requests, then answers again.</summary>
    public int RestartDownRequests { get; set; } = 2;

    public HttpResponseMessage Handle(HttpRequestMessage request, string body, NetworkCredential? credentials)
    {
        if (Offline)
        {
            throw Refused();
        }

        if (Interlocked.Decrement(ref _downRequests) >= 0)
        {
            throw Refused();
        }

        Interlocked.Exchange(ref _downRequests, 0);
        var path = request.RequestUri!.AbsolutePath.TrimStart('/');
        var query = request.RequestUri.Query;
        if (Password is not null && query.Contains(Password, StringComparison.Ordinal))
        {
            PasswordInUrl++;
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
                    return Json(Serialize(new { apiVersion = "1.3", data = new { propertyList = new { ProdNbr = Model, Version = Firmware, Brand = "AXIS" } } }));
                }

                return authorized ? Json(BasicDeviceInfoJson()) : Unauthorized();

            case "axis-cgi/systemready.cgi":
                return Json(Serialize(new { apiVersion = "1.5", method = "systemready", data = new { systemready = "yes", needsetup = NeedSetup ? "yes" : "no", uptime = "100", bootid = "b1", passphrasepolicy = "none" } }));

            case "axis-cgi/pwdgrp.cgi":
                PwdgrpCalls++;
                if (!NeedSetup)
                {
                    return Text("Error: account root already exists");
                }

                var form = ParseForm(body);
                if (form.GetValueOrDefault("action") != "add" || !form.TryGetValue("pwd", out var pwd))
                {
                    return Text("Error: bad request");
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
                return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

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

    private HttpResponseMessage Unauthorized()
    {
        var response = new HttpResponseMessage(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ParseAdd($"Digest realm=\"AXIS_{Serial}\", nonce=\"abc\", algorithm=MD5, qop=\"auth\"");
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

/// <summary>A set of fake devices by address, plus the HTTP plumbing that routes requests to them.</summary>
internal sealed class FakeAxisNetwork
{
    private readonly ConcurrentDictionary<string, FakeAxisDevice> _devices = new(StringComparer.OrdinalIgnoreCase);

    public FakeAxisDevice Add(string address, string serial, string? password, string model = "M3106-L Mk II")
    {
        var device = new FakeAxisDevice { Address = address, Serial = serial, Password = password, Model = model };
        _devices[address] = device;
        return device;
    }

    public FakeAxisDevice this[string address] => _devices[address];

    public FakeAxisDevice? Find(string host) => _devices.GetValueOrDefault(host.Trim('[', ']'));

    /// <summary>Anonymous handler (probes).</summary>
    public HttpMessageHandler CreateHandler(NetworkCredential? credentials = null, CertificatePinning? pinning = null) => new Handler(this, credentials, pinning);

    /// <summary><see cref="IVapixConnector"/> that sends the connection's credentials to the fake device.</summary>
    public IVapixConnector CreateConnector() => new Connector(this);

    /// <summary>The anonymous probe used by AddDevices, wired to this network.</summary>
    public VapixProbe CreateProbe() => new(TimeSpan.FromSeconds(2), (_, _) => CreateHandler());

    private sealed class Handler(FakeAxisNetwork network, NetworkCredential? credentials, CertificatePinning? pinning) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uri = request.RequestUri!;
            var device = network.Find(uri.Host);
            if (device is null || (uri.Scheme == Uri.UriSchemeHttps && !device.ServesHttps))
            {
                throw FakeAxisDevice.Refused();
            }

            if (uri.Scheme == Uri.UriSchemeHttps && pinning is not null && device.Certificate is { } certificate && !device.Offline
                && !pinning.Validate(certificate, null, uri.Host))
            {
                throw new HttpRequestException("The SSL connection could not be established (certificate pin mismatch).");
            }

            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            lock (device)
            {
                var response = device.Handle(request, body, credentials);
                response.RequestMessage = request;
                return response;
            }
        }
    }

    private sealed class Connector(FakeAxisNetwork network) : IVapixConnector
    {
        public VapixClient Connect(VapixConnectionOptions options)
        {
            var pinning = options.Scheme == Uri.UriSchemeHttps ? new CertificatePinning(options.PinnedCertificateFingerprint) : null;
            return new(VapixClient.BuildBaseAddress(options.Scheme, options.Address), network.CreateHandler(options.Credentials, pinning), pinning, TimeSpan.FromSeconds(5));
        }
    }
}

internal static class FakeSerials
{
    public static string Make(int n) => "ACCC8E" + n.ToString("X6", CultureInfo.InvariantCulture);
}
