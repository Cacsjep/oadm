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
    private static async Task<(WebApplication App, Uri Address, string DataDirectory)> StartTlsServerAsync(string? dataDirectory = null, Action<IServiceCollection>? more = null)
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
                more?.Invoke(services);
            },
        });
        await OadmServerHost.StartAsync(app);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        return (app, new Uri(address), dataDirectory);
    }

    private static async Task StopAsync(WebApplication app, string dataDirectory, bool deleteData = true)
    {
        var keyName = app.Services.GetRequiredService<ServerTlsCertificate>().KeyName;
        if (deleteData && OperatingSystem.IsWindows() && System.Security.Cryptography.CngKey.Exists(keyName))
        {
            // The test's named key in the user key store goes with its data folder.
            using var key = System.Security.Cryptography.CngKey.Open(keyName);
            key.Delete();
        }

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

    [Fact]
    public async Task OnWindowsEveryStartUsesTheSameNamedKeyAndLeavesNoNewKeyFile()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // Linux and macOS keep the key in memory (encrypted in server-tls.json): nothing is written to a key store.
        }

        var data = TestServerHost.NewDataDirectory();
        string? keyName = null;
        try
        {
            var (first, _, _) = await StartTlsServerAsync(data);
            string uniqueName;
            string fingerprint;
            try
            {
                var tls = first.Services.GetRequiredService<ServerTlsCertificate>();
                keyName = tls.KeyName;
                fingerprint = tls.Fingerprint;
                uniqueName = PersistedKeyFile(tls, keyName);
                Assert.DoesNotContain("encryptedKey\": \"M", await File.ReadAllTextAsync(Path.Combine(data, ServerTlsCertificate.FileName)), StringComparison.Ordinal);
            }
            finally
            {
                await StopAsync(first, data, deleteData: false);
            }

            // Second start: the same certificate with the same key file (no new key per start).
            var (second, _, _) = await StartTlsServerAsync(data);
            try
            {
                var tls = second.Services.GetRequiredService<ServerTlsCertificate>();
                Assert.Equal(fingerprint, tls.Fingerprint);
                Assert.Equal(uniqueName, PersistedKeyFile(tls, keyName));

                // Regenerating overwrites the named key: still one key of that name, a new certificate.
                await tls.LoadOrCreateAsync(regenerate: true, CancellationToken.None);
                Assert.NotEqual(fingerprint, tls.Fingerprint);
                PersistedKeyFile(tls, keyName);
            }
            finally
            {
                await StopAsync(second, data);
            }
        }
        finally
        {
            if (keyName is not null && System.Security.Cryptography.CngKey.Exists(keyName))
            {
                using var key = System.Security.Cryptography.CngKey.Open(keyName);
                key.Delete();
            }
        }
    }

    [Fact]
    public async Task OnWindowsAMovedDataFolderGetsANewCertificateAndSaysSoInTheLog()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // Linux and macOS: the key is in server-tls.json and moves with the folder.
        }

        var original = TestServerHost.NewDataDirectory();
        var moved = TestServerHost.NewDataDirectory();
        string? originalKey = null;
        try
        {
            var (first, _, _) = await StartTlsServerAsync(original);
            var fingerprint = first.Services.GetRequiredService<ServerTlsCertificate>().Fingerprint;
            originalKey = first.Services.GetRequiredService<ServerTlsCertificate>().KeyName;
            await StopAsync(first, original, deleteData: false);
            foreach (var file in Directory.GetFiles(original, "*", SearchOption.AllDirectories).Where(f => !f.Contains(Path.DirectorySeparatorChar + "logs" + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
            {
                var target = Path.Combine(moved, Path.GetRelativePath(original, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
            }

            var log = new RecordingLogger<ServerTlsCertificate>();
            var (second, _, _) = await StartTlsServerAsync(moved, s => s.AddSingleton<Microsoft.Extensions.Logging.ILogger<ServerTlsCertificate>>(log));
            Assert.NotEqual(fingerprint, second.Services.GetRequiredService<ServerTlsCertificate>().Fingerprint);
            await StopAsync(second, moved, deleteData: false);

            var warning = Assert.Single(log.Messages, m => m.Level == Microsoft.Extensions.Logging.LogLevel.Warning);
            Assert.Contains("belongs to another data folder", warning.Text, StringComparison.Ordinal);
            Assert.Contains("The server certificate changed", warning.Text, StringComparison.Ordinal);
        }
        finally
        {
            foreach (var name in new[] { originalKey, originalKey is null ? null : KeyNameOf(moved) })
            {
                if (name is not null && System.Security.Cryptography.CngKey.Exists(name))
                {
                    using var key = System.Security.Cryptography.CngKey.Open(name);
                    key.Delete();
                }
            }

            SqliteConnection.ClearAllPools();
            foreach (var folder in new[] { original, moved })
            {
                try
                {
                    Directory.Delete(folder, recursive: true);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }

    /// <summary>Records what one class logs (a closed <c>ILogger&lt;T&gt;</c> registration wins over the host's).</summary>
    private sealed class RecordingLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        public System.Collections.Concurrent.ConcurrentQueue<(Microsoft.Extensions.Logging.LogLevel Level, string Text)> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Enqueue((logLevel, formatter(state, exception)));
    }

    /// <summary>The Windows key name of a data folder (as <see cref="ServerTlsCertificate.KeyName"/> makes it).</summary>
    private static string KeyNameOf(string dataDirectory) =>
        "OADM Server TLS " + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(dataDirectory).ToUpperInvariant())))[..16];

    /// <summary>The certificate's private key is the persisted named CNG key; returns its key file name.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string PersistedKeyFile(ServerTlsCertificate tls, string keyName)
    {
        using var key = System.Security.Cryptography.X509Certificates.ECDsaCertificateExtensions.GetECDsaPrivateKey(tls.Certificate);
        var cng = Assert.IsType<System.Security.Cryptography.ECDsaCng>(key);
        Assert.False(cng.Key.IsEphemeral);
        Assert.Equal(keyName, cng.Key.KeyName);
        return cng.Key.UniqueName!;
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
