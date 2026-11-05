using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Sdk.Testing;
using Oadm.Sdk.Actions;

using Opc.Ua;

namespace Oadm.Plugins.OpcUa.Tests;

/// <summary>The session against the stack's own server on the loopback. Nothing here reaches another machine.</summary>
public sealed class OpcUaSessionTests
{
    private static OpcUaSession Session(OpcUaOptions options) => new(options, NullLogger.Instance, 200);

    private static OpcUaOptions Secured(OpcUaTestServer server, string security = OpcUaRecipient.SecuritySignAndEncrypt, bool allowUntrusted = true) => new()
    {
        Endpoint = server.Endpoint,
        Security = security,
        ClientCertificate = [TestCertificates.Client()],
        AllowUntrusted = allowUntrusted,
    };

    private static async Task<string> RefusedAsync(OpcUaSession session)
    {
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => session.ConnectAsync(CancellationToken.None));
        return refused.Message;
    }

    [Fact]
    public async Task OnlyTheAddressIsEnoughAndAWriteArrives()
    {
        await using var server = await OpcUaTestServer.StartAsync();
        await using var session = Session(new OpcUaOptions { Endpoint = server.Endpoint });

        await session.ConnectAsync(CancellationToken.None);
        await session.WriteAsync(server.Node("number"), new Variant(42), CancellationToken.None);
        await session.WriteAsync(server.Node("switch"), new Variant(true), CancellationToken.None);
        await session.WriteAsync(server.Node("text"), new Variant("Grüße"), CancellationToken.None);

        Assert.Equal(42, server.ValueOf("number"));
        Assert.Equal(true, server.ValueOf("switch"));
        Assert.Equal("Grüße", server.ValueOf("text"));
    }

    [Fact]
    public async Task ARefusalOfTheServerIsShownInPlainWordsAndKeepsTheSession()
    {
        await using var server = await OpcUaTestServer.StartAsync();
        await using var session = Session(new OpcUaOptions { Endpoint = server.Endpoint });
        await session.ConnectAsync(CancellationToken.None);

        var wrongType = await Assert.ThrowsAsync<InvalidOperationException>(() => session.WriteAsync(server.Node("number"), new Variant("text"), CancellationToken.None));
        var noNode = await Assert.ThrowsAsync<InvalidOperationException>(() => session.WriteAsync(new NodeId(99999, 2), new Variant(1), CancellationToken.None));
        var locked = await Assert.ThrowsAsync<InvalidOperationException>(() => session.WriteAsync(server.Node("locked"), new Variant(1), CancellationToken.None));

        Assert.Equal("The node has another data type. Choose its value type.", wrongType.Message);
        Assert.Equal("The server has no such node.", noNode.Message);
        Assert.Equal("The node cannot be written.", locked.Message);
        await session.WriteAsync(server.Node("number"), new Variant(7), CancellationToken.None);
        Assert.Equal(7, server.ValueOf("number"));
    }

    [Theory]
    [InlineData(OpcUaRecipient.SecuritySign, OpcUaRecipient.PolicyBest)]
    [InlineData(OpcUaRecipient.SecuritySignAndEncrypt, OpcUaRecipient.PolicyBest)]
    [InlineData(OpcUaRecipient.SecuritySignAndEncrypt, OpcUaRecipient.PolicyBasic256Sha256)]
    [InlineData(OpcUaRecipient.SecuritySign, OpcUaRecipient.PolicyAes128)]
    [InlineData(OpcUaRecipient.SecuritySignAndEncrypt, OpcUaRecipient.PolicyAes256)]
    public async Task SecurityWorksWithTheChosenCertificate(string security, string policy)
    {
        await using var server = await OpcUaTestServer.StartAsync();
        await using var session = Session(new OpcUaOptions
        {
            Endpoint = server.Endpoint,
            Security = security,
            Policy = policy,
            ClientCertificate = [TestCertificates.Client()],
            AllowUntrusted = true,
        });

        await session.ConnectAsync(CancellationToken.None);
        await session.WriteAsync(server.Node("number"), new Variant(5), CancellationToken.None);

        Assert.Equal(5, server.ValueOf("number"));
    }

    [Fact]
    public async Task SecurityWithoutACertificateSaysToChooseOne()
    {
        await using var session = Session(new OpcUaOptions { Endpoint = "opc.tcp://127.0.0.1:1", Security = OpcUaRecipient.SecuritySign });

        Assert.Equal(OpcUaTexts.NeedsClientCertificate, await RefusedAsync(session));
    }

    [Fact]
    public async Task ACertificateWithoutAUriNameIsRefusedBeforeAnythingIsSent()
    {
        await using var session = Session(new OpcUaOptions
        {
            Endpoint = "opc.tcp://127.0.0.1:1",
            Security = OpcUaRecipient.SecuritySign,
            ClientCertificate = [TestCertificates.Client(uri: null)],
        });

        Assert.Equal(OpcUaTexts.NoUriName, await RefusedAsync(session));
    }

    [Fact]
    public async Task AnUntrustedServerIsRefused()
    {
        await using var server = await OpcUaTestServer.StartAsync();
        await using var session = Session(Secured(server, allowUntrusted: false));

        Assert.Equal(OpcUaTexts.ServerCertificateNotTrusted, await RefusedAsync(session));
    }

    [Fact]
    public async Task TheImportedCertificateOfASelfSignedServerIsTrusted()
    {
        await using var server = await OpcUaTestServer.StartAsync();
        await using var session = Session(new OpcUaOptions
        {
            Endpoint = server.Endpoint,
            Security = OpcUaRecipient.SecuritySignAndEncrypt,
            ClientCertificate = [TestCertificates.Client()],
            TrustedCa = server.Certificates(),
        });

        await session.ConnectAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AnotherTrustedCertificateDoesNotTrustTheServer()
    {
        await using var server = await OpcUaTestServer.StartAsync();
        await using var session = Session(new OpcUaOptions
        {
            Endpoint = server.Endpoint,
            Security = OpcUaRecipient.SecuritySignAndEncrypt,
            ClientCertificate = [TestCertificates.Client()],
            TrustedCa = [TestCertificates.Client()],
        });

        Assert.Equal(OpcUaTexts.ServerCertificateNotTrusted, await RefusedAsync(session));
    }

    [Fact]
    public async Task AServerThatDoesNotTrustTheClientIsExplained()
    {
        await using var server = await OpcUaTestServer.StartAsync(acceptClients: false);
        await using var session = Session(Secured(server));

        Assert.Equal("The server does not trust the client certificate. Trust it on the server.", await RefusedAsync(session));
    }

    [Fact]
    public async Task APasswordWorksOnAnEncryptedChannel()
    {
        await using var server = await OpcUaTestServer.StartAsync(encryptPassword: false);
        await using var session = Session(new OpcUaOptions
        {
            Endpoint = server.Endpoint,
            Security = OpcUaRecipient.SecuritySignAndEncrypt,
            Login = OpcUaRecipient.LoginUser,
            User = OpcUaTestServer.User,
            Password = OpcUaTestServer.UserPassword,
            ClientCertificate = [TestCertificates.Client()],
            AllowUntrusted = true,
        });

        await session.ConnectAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ThePoliciesEncryptionProtectsAPasswordWithoutSecurity()
    {
        await using var server = await OpcUaTestServer.StartAsync();
        await using var session = Session(new OpcUaOptions
        {
            Endpoint = server.Endpoint,
            Login = OpcUaRecipient.LoginUser,
            User = OpcUaTestServer.User,
            Password = OpcUaTestServer.UserPassword,
        });

        await session.ConnectAsync(CancellationToken.None);
    }

    [Fact]
    public async Task TheStacksRefusalOfAnUnprotectedPasswordIsShown()
    {
        await using var server = await OpcUaTestServer.StartAsync(encryptPassword: false);
        await using var session = Session(new OpcUaOptions
        {
            Endpoint = server.Endpoint,
            Login = OpcUaRecipient.LoginUser,
            User = OpcUaTestServer.User,
            Password = OpcUaTestServer.UserPassword,
        });

        var reason = await RefusedAsync(session);

        Assert.Equal("This password would go out unprotected, and the server's login does not encrypt it. Choose Sign and encrypt.", reason);
    }

    [Fact]
    public async Task AWrongPasswordIsAPlainRefusal()
    {
        await using var server = await OpcUaTestServer.StartAsync();
        await using var session = Session(new OpcUaOptions
        {
            Endpoint = server.Endpoint,
            Login = OpcUaRecipient.LoginUser,
            User = OpcUaTestServer.User,
            Password = "wrong-password",
        });

        var reason = await RefusedAsync(session);

        Assert.Equal("The server refused the login. Check the user name and password.", reason);
        Assert.DoesNotContain("wrong-password", reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnonymousNotAllowedIsExplained()
    {
        await using var server = await OpcUaTestServer.StartAsync(anonymous: false);
        await using var session = Session(new OpcUaOptions { Endpoint = server.Endpoint });

        Assert.Equal("The server does not allow anonymous login. Choose another login.", await RefusedAsync(session));
    }

    [Fact]
    public async Task ACertificateLoginWorksWithoutSecurity()
    {
        await using var server = await OpcUaTestServer.StartAsync();
        await using var session = Session(new OpcUaOptions
        {
            Endpoint = server.Endpoint,
            Login = OpcUaRecipient.LoginCertificate,
            ClientCertificate = [TestCertificates.Client()],
        });

        await session.ConnectAsync(CancellationToken.None);
        await session.WriteAsync(server.Node("number"), new Variant(9), CancellationToken.None);

        Assert.Equal(9, server.ValueOf("number"));
    }

    [Fact]
    public async Task ACertificateLoginWorksOnAnEncryptedChannel()
    {
        await using var server = await OpcUaTestServer.StartAsync();
        await using var session = Session(new OpcUaOptions
        {
            Endpoint = server.Endpoint,
            Security = OpcUaRecipient.SecuritySignAndEncrypt,
            Login = OpcUaRecipient.LoginCertificate,
            ClientCertificate = [TestCertificates.Client()],
            AllowUntrusted = true,
        });

        await session.ConnectAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AClientCertificateWithItsIssuersGoesOutAsAChain()
    {
        var (issued, issuer) = TestCertificates.ClientOfCa();
        await using var server = await OpcUaTestServer.StartAsync();
        await using var session = Session(new OpcUaOptions
        {
            Endpoint = server.Endpoint,
            Security = OpcUaRecipient.SecuritySignAndEncrypt,
            ClientCertificate = [issued, issuer],
            AllowUntrusted = true,
        });

        await RefusedAsync(session);

        // The server has no revocation list of the CA. It got as far as asking for one because the CA arrived with the certificate.
        Assert.Contains(server.Warnings.Lines, l => l.Contains("BadCertificateRevocationUnknown", StringComparison.Ordinal));
        Assert.DoesNotContain(server.Warnings.Lines, l => l.Contains("BadCertificateChainIncomplete", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ThePoolsCertificatesStayUsableAfterTheSession()
    {
        var chosen = TestCertificates.Client();
        await using var server = await OpcUaTestServer.StartAsync();
        var session = Session(new OpcUaOptions
        {
            Endpoint = server.Endpoint,
            Security = OpcUaRecipient.SecuritySignAndEncrypt,
            ClientCertificate = [chosen],
            AllowUntrusted = true,
        });
        await session.ConnectAsync(CancellationToken.None);

        await session.DisposeAsync();

        Assert.True(chosen.HasPrivateKey);
        Assert.NotEmpty(chosen.Thumbprint);
        chosen.Dispose();
    }

    [Fact]
    public async Task AWriteBeforeTheConnectionIsRefused()
    {
        await using var session = Session(new OpcUaOptions { Endpoint = "opc.tcp://127.0.0.1:1" });

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => session.WriteAsync(new NodeId(1, 2), new Variant(1), CancellationToken.None));

        Assert.Equal(RecipientSessionTexts.NotConnected(null), refused.Message);
    }

    [Fact]
    public async Task NoServerMeansAPlainReason()
    {
        var port = OpcUaTestServer.FreePort();
        await using var session = Session(new OpcUaOptions { Endpoint = $"opc.tcp://127.0.0.1:{port}" });

        Assert.Equal($"opc.tcp://127.0.0.1:{port} cannot be reached.", await RefusedAsync(session));
    }

    [Fact]
    public async Task AStoppedServerDropsTheSession()
    {
        var server = await OpcUaTestServer.StartAsync();
        await using (server)
        {
            await using var session = Session(new OpcUaOptions { Endpoint = server.Endpoint });
            var dropped = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            session.Dropped += (_, e) => dropped.TrySetResult(e.Reason);
            await session.ConnectAsync(CancellationToken.None);

            await server.StopAsync();

            var reason = await dropped.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Contains(reason, new[] { OpcUaTexts.ConnectionEnded, OpcUaTexts.ServerStopped });
        }
    }

    [Fact]
    public async Task ClosingTheSessionIsNotADrop()
    {
        await using var server = await OpcUaTestServer.StartAsync();
        var session = Session(new OpcUaOptions { Endpoint = server.Endpoint });
        var drops = 0;
        session.Dropped += (_, _) => Interlocked.Increment(ref drops);
        await session.ConnectAsync(CancellationToken.None);

        await session.DisposeAsync();

        Assert.Equal(0, Volatile.Read(ref drops));
    }

    [Fact]
    public async Task ManyActionsWriteAtTheSameTime()
    {
        await using var server = await OpcUaTestServer.StartAsync();
        await using var session = Session(new OpcUaOptions { Endpoint = server.Endpoint });
        await session.ConnectAsync(CancellationToken.None);

        await Task.WhenAll(Enumerable.Range(0, 40).Select(i => session.WriteAsync(server.Node("number"), new Variant(i), CancellationToken.None)));

        await Eventually.TrueAsync(() => server.ValueOf("number") is int, "a value arrived");
    }

    [Fact]
    public void TheFactoryReadsTheRecipient()
    {
        using var settings = System.Text.Json.JsonDocument.Parse("""{"endpoint":"opc.tcp://10.0.0.9:4840","security":"sign","policy":"aes128","login":"user","user":"a","password":"b","allowUntrusted":true}""");
        var request = new RecipientSessionRequest(settings.RootElement.Clone(), new Dictionary<string, IReadOnlyList<System.Security.Cryptography.X509Certificates.X509Certificate2>>(), NullLogger.Instance);

        var options = OpcUaOptions.From(request);

        Assert.IsType<OpcUaSession>(new OpcUaSessionFactory().Create(request));
        Assert.Equal(("opc.tcp://10.0.0.9:4840", OpcUaRecipient.SecuritySign, OpcUaRecipient.LoginUser, true), (options.Endpoint, options.Security, options.Login, options.AllowUntrusted));
        Assert.Equal(SecurityPolicies.Aes128_Sha256_RsaOaep, options.PolicyUri);
        Assert.Equal(MessageSecurityMode.Sign, options.Mode);
    }
}
