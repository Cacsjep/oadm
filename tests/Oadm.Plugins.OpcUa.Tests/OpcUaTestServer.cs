using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Opc.Ua;
using Opc.Ua.Identity;
using Opc.Ua.Server;
using Opc.Ua.Server.Fluent;
using Opc.Ua.Server.Hosting;

namespace Oadm.Plugins.OpcUa.Tests;

/// <summary>The stack's own server on 127.0.0.1, with a free port and its certificates in a temp folder of its own.</summary>
internal sealed class OpcUaTestServer : IAsyncDisposable
{
    public const string ApplicationUri = "urn:localhost:OadmTestServer";
    public const string User = "operator";
    public const string UserPassword = "secret-Password-1";

    private const string NamespaceUri = "urn:oadm:test";

    private readonly IHost _host;
    private readonly string _folder;
    private readonly Dictionary<string, BaseVariableState> _nodes = new(StringComparer.Ordinal);
    private readonly IServerInternal _internals;

    private OpcUaTestServer(IHost host, string folder, int port, Dictionary<string, BaseVariableState> nodes, ServerWarnings warnings, IServerInternal internals)
    {
        _internals = internals;
        _host = host;
        _folder = folder;
        Port = port;
        _nodes = nodes;
        Warnings = warnings;
    }

    public int Port { get; }

    /// <summary>What the server logged as wrong.</summary>
    public ServerWarnings Warnings { get; }

    public string Endpoint => $"opc.tcp://127.0.0.1:{Port}/Test";

    /// <summary>The sessions the server holds.</summary>
    public int OpenSessions => _internals.SessionManager.GetSessions().Count;

    public static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>A running server.</summary>
    /// <param name="acceptClients">False: only certificates in its trust list pass.</param>
    /// <param name="anonymous">Whether anonymous login is allowed.</param>
    /// <param name="encryptPassword">False: the login policy does not encrypt a password.</param>
    /// <param name="firstPort">The port of the first try; default a free one.</param>
    public static async Task<OpcUaTestServer> StartAsync(bool acceptClients = true, bool anonymous = true, bool encryptPassword = true, int? firstPort = null)
    {
        // Another test can take a free port before the server binds it.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await TryStartAsync(acceptClients, anonymous, encryptPassword, attempt == 1 && firstPort is { } port ? port : FreePort());
            }
            catch (ServerNotStartedException) when (attempt < 10)
            {
                // The next round takes another port.
            }
        }
    }

    private static async Task<OpcUaTestServer> TryStartAsync(bool acceptClients, bool anonymous, bool encryptPassword, int port)
    {
        var folder = Path.Combine(Path.GetTempPath(), "oadm-opcua-test-" + Guid.NewGuid().ToString("N"));
        var nodes = new Dictionary<string, BaseVariableState>(StringComparer.Ordinal);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IServerInternal? internals = null;
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true, ContentRootPath = Path.GetTempPath() });
        builder.Logging.ClearProviders();
        var warnings = new ServerWarnings();
        builder.Logging.AddProvider(warnings);

        builder.Services
            .AddOpcUa()
            .AddServer(o =>
            {
                o.ApplicationName = "OadmTestServer";
                o.ApplicationUri = ApplicationUri;
                o.ProductUri = "urn:oadm:test-server";
                o.SubjectName = "CN=OadmTestServer, DC=localhost";
                o.PkiRoot = Path.Combine(folder, "pki");
                o.AutoAcceptUntrustedCertificates = acceptClients;
                o.IncludeUnsecurePolicyNone = true;
                o.IncludeSignAndEncryptPolicies = true;
                o.IncludeEccPolicies = false;
                o.EndpointUrls.Add($"opc.tcp://127.0.0.1:{port}/Test");
                if (anonymous)
                {
                    o.UserTokenPolicies.Add(new OpcUaUserTokenPolicy { TokenType = UserTokenType.Anonymous });
                }

                o.UserTokenPolicies.Add(new OpcUaUserTokenPolicy { TokenType = UserTokenType.Certificate });
                if (encryptPassword)
                {
                    o.UserTokenPolicies.Add(new OpcUaUserTokenPolicy { TokenType = UserTokenType.UserName });
                }

                o.ConfigureBuilder = server =>
                {
                    server.AddSignPolicies();
                    if (!encryptPassword)
                    {
                        server.AddUserTokenPolicy(new UserTokenPolicy { TokenType = UserTokenType.UserName, SecurityPolicyUri = SecurityPolicies.None });
                    }
                };
            })
            .AddDefaultIdentityAuthenticators(o =>
            {
                o.EnableAnonymous = anonymous;
                o.EnableUserNamePassword = false;
                o.EnableX509 = true;
                o.EnableJwt = false;
            })
            .AddIdentityAuthenticator(new PasswordCheck())
            .AddNodeManager(NamespaceUri, manager =>
            {
                nodes["number"] = manager.AddVariable<int>("Number").Writable().Node;
                nodes["switch"] = manager.AddVariable<bool>("Switch").Writable().Node;
                nodes["text"] = manager.AddVariable<string>("Text").Writable().Node;
                nodes["locked"] = manager.AddVariable<int>("Locked").Node;
            })
            .AddStartupTask((_, context, _) =>
            {
                internals = (IServerInternal)context;
                ready.TrySetResult();
                return default;
            });

        var host = builder.Build();
        try
        {
            // The hosted service starts the server in the background; when that fails, the host stops.
            using var failed = host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping
                .Register(() => ready.TrySetException(new ServerNotStartedException()));
            await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(60));
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(60));
            return new OpcUaTestServer(host, folder, port, nodes, warnings, internals!);
        }
        catch (ServerNotStartedException)
        {
            await new OpcUaTestServer(host, folder, port, nodes, warnings, internals!).DisposeAsync();
            throw;
        }
    }

    /// <summary>The node id of a variable, as a user enters it.</summary>
    public NodeId Node(string name) => _nodes[name].NodeId;

    public string NodeText(string name) => Node(name).ToString();

    public object? ValueOf(string name) => _nodes[name].Value.AsBoxedObject();

    /// <summary>Every write of the node hangs until <paramref name="release"/> is set; the result ends when the first one arrived.</summary>
    public Task HoldWrites(string name, ManualResetEventSlim release)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _nodes[name].OnSimpleWriteValue = (ISystemContext _, NodeState _, ref Variant _) =>
        {
            started.TrySetResult();
            release.Wait(TimeSpan.FromSeconds(60));
            return ServiceResult.Good;
        };
        return started.Task;
    }

    /// <summary>The server's certificates, as an administrator exports them: one per kind of key.</summary>
    public X509Certificate2[] Certificates() =>
        [.. Directory.GetFiles(Path.Combine(_folder, "pki"), "*.der", SearchOption.AllDirectories).Select(X509CertificateLoader.LoadCertificateFromFile)];

    /// <summary>Stops the server; the host stays disposed.</summary>
    public Task StopAsync() => _host.StopAsync();

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _host.StopAsync().WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (Exception ex) when (ex is ObjectDisposedException or TimeoutException)
        {
            // Stopped by the test, or it hangs: the process ends anyway.
        }

        _host.Dispose();
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file the stack still holds; it is in the temp folder.
        }
    }
}

/// <summary>A server that could not start, on a port another test took in between.</summary>
internal sealed class ServerNotStartedException : Exception
{
}

/// <summary>The server's log, warnings and worse.</summary>
internal sealed class ServerWarnings : ILoggerProvider, ILogger
{
    private readonly ConcurrentQueue<string> _lines = new();

    public IEnumerable<string> Lines => _lines;

    public ILogger CreateLogger(string categoryName) => this;

    public void Dispose()
    {
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        _lines.Enqueue(formatter(state, exception) + " " + exception?.Message);
}

/// <summary>One user with one password.</summary>
internal sealed class PasswordCheck : IUserTokenAuthenticator
{
    public UserTokenType TokenType => UserTokenType.UserName;

    public string? IssuedTokenProfileUri => null;

    public ValueTask<AuthenticationResult> AuthenticateAsync(AuthenticationContext context, CancellationToken ct = default)
    {
        if (context.TokenHandler is not UserNameIdentityTokenHandler login)
        {
            return new(AuthenticationResult.NotHandled);
        }

        var right = login.UserName == OpcUaTestServer.User
            && login.DecryptedPassword is { } password
            && System.Text.Encoding.UTF8.GetString(password) == OpcUaTestServer.UserPassword;
        return new(right ? AuthenticationResult.Accept(new UserIdentity(login)) : AuthenticationResult.Reject(new ServiceResult(StatusCodes.BadUserAccessDenied)));
    }
}

/// <summary>Certificates as PKI hands them out: with the key and a URI name.</summary>
internal static class TestCertificates
{
    public const string ClientUri = "urn:oadm:test-client";

    public static X509Certificate2 Client(string? uri = ClientUri)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=OADM test client", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        if (uri is not null)
        {
            names.AddUri(new Uri(uri));
        }

        names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation | X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.DataEncipherment,
            true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1"), new Oid("1.3.6.1.5.5.7.3.2")],
            false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    }

    /// <summary>A client certificate with its key, and the CA that signed it.</summary>
    public static (X509Certificate2 Issued, X509Certificate2 Issuer) ClientOfCa()
    {
        using var caKey = RSA.Create(2048);
        var caRequest = new CertificateRequest("CN=OADM test CA", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        caRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(caRequest.PublicKey, false));
        using var ca = caRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(60));

        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=OADM test client", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddUri(new Uri(ClientUri));
        names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation | X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.DataEncipherment,
            true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1"), new Oid("1.3.6.1.5.5.7.3.2")], false));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(ca, true, false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        using var issued = request.Create(ca, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30), RandomNumberGenerator.GetBytes(8));
        return (issued.CopyWithPrivateKey(key), X509CertificateLoader.LoadCertificate(ca.RawData));
    }
}
