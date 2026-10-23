using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Core.Events;
using Oadm.Sdk.Actions;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Notifications.Tests;

/// <summary>An action context that fills the placeholders exactly like the server (<see cref="Placeholders.Values"/>).</summary>
internal sealed class TestContext(IReadOnlyDictionary<string, string>? data = null, IDeviceInfo? device = null) : IActionContext
{
    private readonly Dictionary<string, string> _values = Placeholders.Values(
        "Gate rule",
        new ActionTrigger("t", "Person in the yard", device?.Id, device?.Address, device?.Model, true, new DateTimeOffset(2026, 10, 11, 12, 5, 9, TimeSpan.Zero), data ?? new Dictionary<string, string>()),
        device);

    public List<string> Lines { get; } = [];

    public IVapixClient? Vapix => null;

    public ILogger Logger => NullLogger.Instance;

    public ICorePlugin? Owner => null;

    public IPluginSettings Settings => throw new NotSupportedException();

    public void Log(string message) => Lines.Add(message);

    /// <summary>The allow-list of a rule of a device group; null = everything (a rule of all devices).</summary>
    public Func<string, int, bool>? Allowed { get; init; }

    /// <summary>Every address the action asked for.</summary>
    public List<(string Host, int Port)> Asked { get; } = [];

    public bool IsDestinationAllowed(string host, int port)
    {
        Asked.Add((host, port));
        return Allowed?.Invoke(host, port) ?? true;
    }

    public string Text(string? text) => Text(text, PlaceholderEscape.None);

    public string Text(string? text, PlaceholderEscape escape) => ActionPlaceholders.Fill(text, name => _values.GetValueOrDefault(name), escape);
}

internal static class Run
{
    public static ActionInvocation Invocation(JsonObject settings, ActionEdge edge = ActionEdge.TurnedOn) =>
        new(Guid.NewGuid(), "Gate rule", edge, JsonDocument.Parse(settings.ToJsonString()).RootElement.Clone(), null,
            new ActionTrigger("t", "Person in the yard", null, null, null, true, DateTimeOffset.UtcNow, new Dictionary<string, string>()));

    public static JsonElement Element(JsonObject settings) => JsonDocument.Parse(settings.ToJsonString()).RootElement.Clone();

    public static JsonArray Pairs(params (string Name, string Value)[] rows) =>
        [.. rows.Select(r => (JsonNode)new JsonObject { ["name"] = r.Name, ["value"] = r.Value })];
}

internal sealed class TestDevice : IDeviceInfo
{
    public Guid Id { get; } = Guid.NewGuid();
    public string Serial => "B8A44F631339";
    public string Address => "10.0.0.48";
    public string? HostName => "cam-gate";
    public string? Model => "AXIS P3265-V";
    public string? FirmwareVersion => "12.11.77";
    public DeviceStatus Status => DeviceStatus.Ok;
    public DeviceCategory Category => DeviceCategory.Camera;
    public bool HasVideo => true;
    public IReadOnlyList<DeviceApi> Apis => [];
    public IReadOnlyList<string> Tags => ["Building A", "Gate"];
    public bool? HttpsEnabled => true;
}

/// <summary>One HTTP request as the server read it.</summary>
internal sealed record ReceivedRequest(string Method, string Target, IReadOnlyDictionary<string, string> Headers, string Body);

/// <summary>
/// A tiny HTTP/1.1 server on 127.0.0.1 (plain or TLS with a self-signed certificate): records every request and answers
/// with <see cref="Status"/> / <see cref="ResponseBody"/>; with <see cref="DigestRealm"/> it first answers 401 with a Digest
/// challenge until a request carries "Authorization: Digest". One request per connection.
/// </summary>
internal sealed class MiniHttpServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly X509Certificate2? _certificate;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    public MiniHttpServer(bool tls = false)
    {
        if (tls)
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest("CN=127.0.0.1", key, HashAlgorithmName.SHA256);
            using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            _certificate = X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null);
        }

        _listener.Start();
        _loop = Task.Run(LoopAsync);
    }

    public string BaseUrl => (_certificate is null ? "http" : "https") + "://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port;

    public List<ReceivedRequest> Requests { get; } = [];

    public int Status { get; set; } = 200;

    public string ResponseBody { get; set; } = "ok";

    public string? DigestRealm { get; set; }

    /// <summary>Answers a path with a redirect (302) to the returned location; null = no redirect.</summary>
    public Func<string, string?>? Redirect { get; set; }

    private async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }

            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                Stream stream = client.GetStream();
                if (_certificate is not null)
                {
                    var ssl = new SslStream(stream);
                    await ssl.AuthenticateAsServerAsync(_certificate);
                    stream = ssl;
                }

                await using (stream)
                {
                    var head = await ReadHeadAsync(stream);
                    var lines = head.Split("\r\n");
                    var first = lines[0].Split(' ');
                    var headers = lines.Skip(1).Where(l => l.Contains(':', StringComparison.Ordinal))
                        .ToDictionary(l => l[..l.IndexOf(':', StringComparison.Ordinal)].Trim(), l => l[(l.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim(), StringComparer.OrdinalIgnoreCase);
                    var length = headers.TryGetValue("Content-Length", out var l) ? int.Parse(l, System.Globalization.CultureInfo.InvariantCulture) : 0;
                    var body = new byte[length];
                    await stream.ReadExactlyAsync(body);
                    lock (Requests)
                    {
                        Requests.Add(new ReceivedRequest(first[0], first[1], headers, Encoding.UTF8.GetString(body)));
                    }

                    if (Redirect?.Invoke(first[1]) is { } location)
                    {
                        const string Crlf = "\r\n";
                        await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 302 Found" + Crlf + "Location: " + location + Crlf + "Content-Length: 0" + Crlf + "Connection: close" + Crlf + Crlf));
                        await stream.FlushAsync();
                        return;
                    }

                    var challenge = DigestRealm is not null && !(headers.TryGetValue("Authorization", out var auth) && auth.StartsWith("Digest ", StringComparison.Ordinal));
                    var status = challenge ? 401 : Status;
                    var text = challenge ? "login" : ResponseBody;
                    var extra = challenge ? $"WWW-Authenticate: Digest realm=\"{DigestRealm}\", nonce=\"abc123\", qop=\"auth\", algorithm=MD5\r\n" : string.Empty;
                    var bytes = Encoding.UTF8.GetBytes(text);
                    var response = $"HTTP/1.1 {status} {(status == 200 ? "OK" : status == 401 ? "Unauthorized" : "Error")}\r\n{extra}Content-Type: text/plain\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(response));
                    await stream.WriteAsync(bytes);
                    await stream.FlushAsync();
                }
            }
            catch (Exception ex) when (ex is IOException or System.Security.Authentication.AuthenticationException or ObjectDisposedException)
            {
                // The client gave up (refused certificate, timeout).
            }
        }
    }

    private static async Task<string> ReadHeadAsync(Stream stream)
    {
        var buffer = new List<byte>();
        var one = new byte[1];
        while (await stream.ReadAsync(one) == 1)
        {
            buffer.Add(one[0]);
            if (buffer.Count >= 4 && buffer[^4] == '\r' && buffer[^3] == '\n' && buffer[^2] == '\r' && buffer[^1] == '\n')
            {
                break;
            }
        }

        return Encoding.ASCII.GetString([.. buffer]);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        try
        {
            await _loop;
        }
        catch (OperationCanceledException)
        {
        }

        _stop.Dispose();
        _certificate?.Dispose();
    }
}

/// <summary>A tiny SMTP server on 127.0.0.1 without encryption: records the envelope and the message of every mail.</summary>
internal sealed class MiniSmtpServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    public MiniSmtpServer()
    {
        _listener.Start();
        _loop = Task.Run(LoopAsync);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public List<(string From, List<string> To, string Data, string? Auth)> Mails { get; } = [];

    private async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }

            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII);
                await using var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
                await writer.WriteLineAsync("220 mini ESMTP");
                string from = string.Empty;
                string? auth = null;
                var to = new List<string>();
                while (await reader.ReadLineAsync() is { } line)
                {
                    var command = line.Length >= 4 ? line[..4].ToUpperInvariant() : line.ToUpperInvariant();
                    switch (command)
                    {
                        case "EHLO":
                            await writer.WriteLineAsync("250-mini");
                            await writer.WriteLineAsync("250-AUTH PLAIN");
                            await writer.WriteLineAsync("250 8BITMIME");
                            break;
                        case "HELO":
                            await writer.WriteLineAsync("250 mini");
                            break;
                        case "AUTH":
                            auth = Encoding.UTF8.GetString(Convert.FromBase64String(line.Split(' ')[^1])).Replace('\0', '|');
                            await writer.WriteLineAsync("235 ok");
                            break;
                        case "MAIL":
                            from = line[(line.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim().Split(' ')[0].Trim('<', '>');
                            await writer.WriteLineAsync("250 ok");
                            break;
                        case "RCPT":
                            to.Add(line[(line.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim().Split(' ')[0].Trim('<', '>'));
                            await writer.WriteLineAsync("250 ok");
                            break;
                        case "DATA":
                            await writer.WriteLineAsync("354 go");
                            var data = new StringBuilder();
                            while (await reader.ReadLineAsync() is { } d && d != ".")
                            {
                                data.AppendLine(d);
                            }

                            lock (Mails)
                            {
                                Mails.Add((from, [.. to], data.ToString(), auth));
                            }

                            await writer.WriteLineAsync("250 queued");
                            break;
                        case "QUIT":
                            await writer.WriteLineAsync("221 bye");
                            return;
                        default:
                            await writer.WriteLineAsync("250 ok");
                            break;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        try
        {
            await _loop;
        }
        catch (OperationCanceledException)
        {
        }

        _stop.Dispose();
    }
}
