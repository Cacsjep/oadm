using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Oadm.Sdk.Actions;

namespace Oadm.Plugins.Notifications.Tests;

public sealed class SendTcpActionTests
{
    private static JsonObject Settings(int port, string message, string format = "text") => new()
    {
        ["host"] = "127.0.0.1",
        ["port"] = port,
        ["format"] = format,
        ["message"] = message,
        ["lineEnd"] = "none",
        ["tls"] = false,
        ["waitAnswer"] = false,
        ["timeout"] = 30, // loopback answers at once; a busy machine may still take seconds
    };

    [Fact]
    public async Task Text_gets_the_event_values_and_the_line_end()
    {
        await using var server = new MiniTcpServer();
        var settings = Settings(server.Port, "ALARM {device} {objectId}");
        settings["lineEnd"] = "crlf";
        var context = new TestContext(new Dictionary<string, string> { ["objectId"] = "7" }, new TestDevice());

        await new SendTcpAction().ExecuteAsync(context, Run.Invocation(settings), CancellationToken.None);

        const string Expected = "ALARM 10.0.0.48 (AXIS P3265-V) 7\r\n";
        Assert.Equal(Expected, Encoding.UTF8.GetString(await server.ReceivedAsync()));
        Assert.Equal([$"Sent {Expected.Length} bytes to 127.0.0.1:{server.Port}"], context.Lines);
    }

    [Fact]
    public async Task Json_values_are_escaped_and_hex_is_sent_as_bytes()
    {
        await using var json = new MiniTcpServer();
        var context = new TestContext(new Dictionary<string, string> { ["objectId"] = "a \"quoted\" \\ value" });
        await new SendTcpAction().ExecuteAsync(context, Run.Invocation(Settings(json.Port, "{\"id\": \"{objectId}\"}", "json")), CancellationToken.None);
        using var parsed = JsonDocument.Parse(await json.ReceivedAsync());
        Assert.Equal("a \"quoted\" \\ value", parsed.RootElement.GetProperty("id").GetString());

        await using var hex = new MiniTcpServer();
        await new SendTcpAction().ExecuteAsync(context, Run.Invocation(Settings(hex.Port, "02 41 0x42\n03", "hex")), CancellationToken.None);
        Assert.Equal(new byte[] { 0x02, 0x41, 0x42, 0x03 }, await hex.ReceivedAsync());
    }

    [Fact]
    public async Task An_expected_answer_must_come()
    {
        await using var server = new MiniTcpServer { Answer = "ACK 42\r\n" };
        var settings = Settings(server.Port, "PING");
        settings["waitAnswer"] = true;
        settings["expect"] = "ACK";
        var context = new TestContext();

        await new SendTcpAction().ExecuteAsync(context, Run.Invocation(settings), CancellationToken.None);
        Assert.Equal(["Sent 4 bytes to 127.0.0.1:" + server.Port + ", answer: ACK 42"], context.Lines);

        server.Answer = "NAK";
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => new SendTcpAction().ExecuteAsync(new TestContext(), Run.Invocation(settings), CancellationToken.None));
        Assert.Equal("The answer does not contain \"ACK\": NAK", ex.Message);

        server.Answer = string.Empty; // closes without a word
        ex = await Assert.ThrowsAsync<InvalidOperationException>(() => new SendTcpAction().ExecuteAsync(new TestContext(), Run.Invocation(settings), CancellationToken.None));
        Assert.Equal("The connection was closed without an answer.", ex.Message);
    }

    [Fact]
    public async Task Tls_needs_a_trusted_certificate_unless_allowed()
    {
        await using var server = new MiniTcpServer(tls: true);
        var settings = Settings(server.Port, "hello");
        settings["tls"] = true;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => new SendTcpAction().ExecuteAsync(new TestContext(), Run.Invocation(settings), CancellationToken.None));
        Assert.StartsWith("The certificate of 127.0.0.1:" + server.Port + " is not trusted", ex.Message, StringComparison.Ordinal);

        settings["allowUntrusted"] = true;
        await new SendTcpAction().ExecuteAsync(new TestContext(), Run.Invocation(settings), CancellationToken.None);

        // With TLS 1.3 the server may finish its handshake before the client refuses the certificate: the refused
        // connection then arrives as an empty one first.
        byte[] received;
        do
        {
            received = await server.ReceivedAsync();
        }
        while (received.Length == 0);

        Assert.Equal("hello", Encoding.UTF8.GetString(received));
    }

    [Fact]
    public async Task A_closed_port_fails_with_the_target()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => new SendTcpAction().ExecuteAsync(new TestContext(), Run.Invocation(Settings(port, "x")), CancellationToken.None));
        Assert.StartsWith("127.0.0.1:" + port + " cannot be reached", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_are_checked_before_saving()
    {
        var action = new SendTcpAction();
        var bad = Settings(1, "02 4", "hex");
        bad["host"] = "not a host!";
        var errors = action.Validate(Run.Element(bad), statefulRule: true);
        Assert.Equal("Enter a host name or IP address.", errors["host"]);
        Assert.Equal("Enter hex bytes, for example 02 41 42 03.", errors["message"]);

        Assert.Equal("The message is not valid JSON.", action.Validate(Run.Element(Settings(1, "{\"a\": ", "json")), true)["message"]);
        Assert.Empty(action.Validate(Run.Element(Settings(1, "{\"count\": {total}}", "json")), true)); // a placeholder may be a number
        Assert.Equal("TCP to 127.0.0.1:1", action.Describe(Run.Element(Settings(1, "x"))));
        Assert.Null(SendTcpAction.Hex("0g"));
        Assert.Equal(new byte[] { 0xAB, 0x01 }, SendTcpAction.Hex("ab01"));
    }

    [Fact]
    public void It_runs_on_the_off_edge_only_when_asked()
    {
        var action = new SendTcpAction();
        var settings = Settings(1, "x");
        Assert.False(action.RunsOn(ActionEdge.TurnedOff, Run.Element(settings)));
        settings["onOff"] = true;
        Assert.True(action.RunsOn(ActionEdge.TurnedOff, Run.Element(settings)));
        Assert.True(action.RunsOn(ActionEdge.TurnedOn, Run.Element(settings)));
    }
}

/// <summary>Accepts connections on 127.0.0.1, keeps what each one sent, and optionally answers.</summary>
internal sealed class MiniTcpServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly X509Certificate2? _certificate;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private readonly System.Threading.Channels.Channel<byte[]> _received = System.Threading.Channels.Channel.CreateUnbounded<byte[]>();

    public MiniTcpServer(bool tls = false)
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

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>Sent back after the first data; empty closes the connection at once; null reads until the client closes.</summary>
    public string? Answer { get; set; }

    /// <summary>What the next connection sent, once it closed or sent its data.</summary>
    public async Task<byte[]> ReceivedAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        return await _received.Reader.ReadAsync(timeout.Token);
    }

    private async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                Stream stream = client.GetStream();
                if (_certificate is not null)
                {
                    var tls = new SslStream(stream);
                    await tls.AuthenticateAsServerAsync(_certificate);
                    stream = tls;
                }

                await using (stream)
                {
                    var buffer = new byte[8192];
                    using var data = new MemoryStream();
                    var read = await stream.ReadAsync(buffer, _stop.Token);
                    data.Write(buffer, 0, read);
                    var answer = Answer;
                    if (answer is null)
                    {
                        // Without an answer: read until the client closes, so the whole message is kept.
                        while ((read = await stream.ReadAsync(buffer, _stop.Token)) > 0)
                        {
                            data.Write(buffer, 0, read);
                        }
                    }
                    else if (answer.Length > 0)
                    {
                        await stream.WriteAsync(Encoding.UTF8.GetBytes(answer), _stop.Token);
                        await stream.FlushAsync(_stop.Token);
                    }

                    _received.Writer.TryWrite(data.ToArray());
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or System.Security.Authentication.AuthenticationException)
            {
                // a refused TLS handshake or a test that ended
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
        _certificate?.Dispose();
    }
}
