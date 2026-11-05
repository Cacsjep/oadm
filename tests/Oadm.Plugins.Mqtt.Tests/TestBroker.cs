using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging.Abstractions;

using MQTTnet;
using MQTTnet.Server;

using Oadm.Sdk.Actions;

namespace Oadm.Plugins.Mqtt.Tests;

/// <summary>MQTTnet's own broker on the loopback, on a free port; it records what it gets.</summary>
internal sealed class TestBroker : IAsyncDisposable
{
    private readonly X509Certificate2? _certificate;

    private TestBroker(MqttServer server, int port, X509Certificate2? certificate)
    {
        Server = server;
        Port = port;
        _certificate = certificate;
        server.InterceptingPublishAsync += e =>
        {
            Messages.Enqueue(e.ApplicationMessage);
            return Task.CompletedTask;
        };
    }

    public MqttServer Server { get; }

    public int Port { get; }

    public ConcurrentQueue<MqttApplicationMessage> Messages { get; } = new();

    public static Task<TestBroker> StartAsync(Action<MqttServer>? prepare = null) =>
        StartAsync(
            port => new MqttServerOptionsBuilder()
                .WithDefaultEndpoint()
                .WithDefaultEndpointBoundIPAddress(IPAddress.Loopback)
                .WithDefaultEndpointBoundIPV6Address(IPAddress.None)
                .WithDefaultEndpointPort(port),
            prepare,
            null);

    public static Task<TestBroker> StartTlsAsync(X509Certificate2 certificate, bool askClientCertificate = false, Action<MqttServer>? prepare = null)
    {
        var withKey = X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12), null);
        return StartAsync(
            port =>
            {
                var options = new MqttServerOptionsBuilder()
                    .WithoutDefaultEndpoint()
                    .WithEncryptedEndpoint()
                    .WithEncryptedEndpointBoundIPAddress(IPAddress.Loopback)
                    .WithEncryptedEndpointBoundIPV6Address(IPAddress.None)
                    .WithEncryptedEndpointPort(port)
                    .WithEncryptionCertificate(withKey);
                return askClientCertificate ? options.WithClientCertificate((_, sent, _, _) => sent is not null) : options;
            },
            prepare,
            withKey);
    }

    /// <summary>A port nothing listens on.</summary>
    public static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public async ValueTask DisposeAsync()
    {
        await Server.StopAsync(new MqttServerStopOptions());
        Server.Dispose();
        _certificate?.Dispose();
    }

    private static async Task<TestBroker> StartAsync(Func<int, MqttServerOptionsBuilder> options, Action<MqttServer>? prepare, X509Certificate2? certificate)
    {
        // Another test can take a free port before the broker binds it.
        for (var attempt = 1; ; attempt++)
        {
            var port = FreePort();
            var server = new MqttServerFactory().CreateMqttServer(options(port).Build());
            prepare?.Invoke(server);
            try
            {
                await server.StartAsync();
                return new TestBroker(server, port, certificate);
            }
            catch (SocketException) when (attempt < 10)
            {
                server.Dispose();
            }
        }
    }
}

/// <summary>Sessions as the pool makes them.</summary>
internal static class Sessions
{
    /// <summary>The request of a recipient on 127.0.0.1.</summary>
    public static RecipientSessionRequest Request(int port, object? extra = null, IReadOnlyDictionary<string, IReadOnlyList<X509Certificate2>>? certificates = null)
    {
        var settings = JsonNode.Parse(JsonSerializer.Serialize(extra ?? new { }))!.AsObject();
        settings["host"] = "127.0.0.1";
        settings["port"] = port;
        settings.TryAdd("clientId", "test-" + Guid.NewGuid().ToString("N"));
        return new RecipientSessionRequest(JsonSerializer.SerializeToElement(settings), certificates ?? new Dictionary<string, IReadOnlyList<X509Certificate2>>(), NullLogger.Instance);
    }

    public static CancellationToken Patient => new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;
}
