using Grpc.Core;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Oadm.Contracts.Security;
using Oadm.Core.Auth;
using Oadm.Core.Discovery.Mdns;
using Oadm.Server.Auth;
using Oadm.Server.Hosting;
using Oadm.Server.Tests.Support;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests;

/// <summary>The real Kestrel endpoint over TLS with the server's own certificate and the client's trust on first use.</summary>
public sealed class TlsPinningTests
{
    private static async Task<(WebApplication App, Uri Address, string DataDirectory)> StartTlsServerAsync(string? dataDirectory = null)
    {
        dataDirectory ??= TestServerHost.NewDataDirectory();
        var app = OadmServerHost.Build([], new OadmServerHostOptions
        {
            DataDirectory = dataDirectory,
            ListenUrl = "https://127.0.0.1:0",
            PluginRoots = [],
            LogToConsole = false,
            ConfigureServices = services =>
            {
                services.RemoveAll<PasswordHasher>();
                services.AddSingleton(new PasswordHasher(1_000));
                services.RemoveAll<IMdnsBrowser>();
                services.AddSingleton<IMdnsBrowser, SilentBrowser>();
            },
        });
        await OadmServerHost.StartAsync(app);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        return (app, new Uri(address), dataDirectory);
    }

    private static async Task StopAsync(WebApplication app, string dataDirectory, bool deleteData = true)
    {
        await OadmServerHost.StopAsync(app);
        await app.DisposeAsync();
        SqliteConnection.ClearAllPools();
        if (deleteData)
        {
            try
            {
                Directory.Delete(dataDirectory, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    [Fact]
    public async Task FirstConnectionAsksForTheFingerprintThenTheTrustedServerAnswersAndAChangedOneIsRefused()
    {
        var (app, address, data) = await StartTlsServerAsync();
        var pins = new InMemoryPinStore();
        var pinning = new ServerCertificatePinning(pins);
        string serverFingerprint = app.Services.GetRequiredService<ServerTlsCertificate>().Fingerprint;
        try
        {
            Assert.True(File.Exists(Path.Combine(data, ServerTlsCertificate.FileName)));
            Assert.DoesNotContain("PRIVATE KEY", await File.ReadAllTextAsync(Path.Combine(data, ServerTlsCertificate.FileName)), StringComparison.Ordinal);

            // 1. Unknown certificate: the handshake is refused and the fingerprint is offered for confirmation.
            var (channel, invoker) = OadmChannel.Create(address, pinning, () => null);
            using (channel)
            {
                await Assert.ThrowsAsync<RpcException>(async () => await new Proto.AuthService.AuthServiceClient(invoker).StatusAsync(new Proto.Empty()));
            }

            Assert.Equal(PinCheck.Unknown, pinning.LastCheck(address));
            Assert.Equal(serverFingerprint, pinning.PresentedFingerprint(address));

            // 2. The user confirmed it: the pinned server answers over TLS.
            pinning.Trust(address, pinning.PresentedFingerprint(address)!);
            (channel, invoker) = OadmChannel.Create(address, pinning, () => null);
            using (channel)
            {
                var status = await new Proto.AuthService.AuthServiceClient(invoker).StatusAsync(new Proto.Empty());
                Assert.True(status.NeedsFirstAdmin);
                Assert.False(status.SetupCodeRequired); // 127.0.0.1 is the server computer
            }

            Assert.Equal(PinCheck.Trusted, pinning.LastCheck(address));
        }
        finally
        {
            await StopAsync(app, data, deleteData: false);
        }

        // 3. The same data folder keeps the certificate across restarts.
        var (again, againAddress, _) = await StartTlsServerAsync(data);
        try
        {
            Assert.Equal(serverFingerprint, again.Services.GetRequiredService<ServerTlsCertificate>().Fingerprint);
        }
        finally
        {
            await StopAsync(again, data);
        }

        // 4. Another server (other certificate) under a pinned address is refused until Forget server.
        var (other, otherAddress, otherData) = await StartTlsServerAsync();
        try
        {
            pinning.Trust(otherAddress, serverFingerprint); // pretend this address was pinned to the first server
            var (channel, invoker) = OadmChannel.Create(otherAddress, pinning, () => null);
            using (channel)
            {
                await Assert.ThrowsAsync<RpcException>(async () => await new Proto.AuthService.AuthServiceClient(invoker).StatusAsync(new Proto.Empty()));
            }

            Assert.Equal(PinCheck.Changed, pinning.LastCheck(otherAddress));
            Assert.NotEqual(serverFingerprint, pinning.PresentedFingerprint(otherAddress));

            pinning.Forget(otherAddress);
            Assert.Null(pinning.PinnedFingerprint(otherAddress));
        }
        finally
        {
            await StopAsync(other, otherData);
        }

        Assert.NotEqual(0, address.Port);
        Assert.NotNull(againAddress);
    }

    [Fact]
    public async Task RegenerateCreatesANewCertificate()
    {
        var data = TestServerHost.NewDataDirectory();
        var (app, _, _) = await StartTlsServerAsync(data);
        try
        {
            var tls = app.Services.GetRequiredService<ServerTlsCertificate>();
            var first = tls.Fingerprint;
            await tls.LoadOrCreateAsync(regenerate: true, CancellationToken.None);
            Assert.NotEqual(first, tls.Fingerprint);
            Assert.Contains(tls.Certificate.Extensions.OfType<System.Security.Cryptography.X509Certificates.X509SubjectAlternativeNameExtension>().Single().EnumerateIPAddresses(),
                ip => System.Net.IPAddress.IsLoopback(ip));
            Assert.True(tls.Certificate.NotAfter > DateTime.Now.AddYears(19));
            Assert.True(tls.Certificate.HasPrivateKey);
        }
        finally
        {
            await StopAsync(app, data);
        }
    }

    private sealed class SilentBrowser : IMdnsBrowser
    {
        public async IAsyncEnumerable<MdnsServiceInstance> BrowseAsync(
            MdnsBrowseOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }

            yield break;
        }
    }
}
