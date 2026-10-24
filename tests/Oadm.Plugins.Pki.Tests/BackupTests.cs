using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Oadm.Core;
using Oadm.Core.Backup;
using Oadm.Core.Persistence;
using Oadm.Core.Plugins;
using Oadm.Core.Security;
using Oadm.Plugins.Pki.Ca;
using Oadm.Sdk.Certificates;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Pki.Tests;

/// <summary>
/// The list of issued certificates and the revocation list are part of the encrypted backup (plugin records in the server
/// database, docs/spec/settings-backup.md): a restore on another server brings them back with the CA.
/// </summary>
public sealed class BackupTests
{
    private const string Password = "correct horse";

    /// <summary>A migrated server database in a data folder of its own, like the server wires it.</summary>
    private sealed class Server : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;

        private Server(OadmPaths paths, ServiceProvider provider)
        {
            Paths = paths;
            _provider = provider;
        }

        public OadmPaths Paths { get; }

        public IPluginSettings Settings => _provider.GetRequiredService<IPluginSettingsProvider>().GetSettings(PkiPluginInfo.PluginId);

        public IPluginRecords Records => new DbPluginRecords(_provider.GetRequiredService<IDbContextFactory<OadmDbContext>>(), PkiPluginInfo.PluginId);

        public ISecretProtector Secrets => new PluginSecretProtector(_provider.GetRequiredService<CredentialProtector>());

        public static async Task<Server> OpenAsync(string folder)
        {
            var paths = new OadmPaths(folder);
            var provider = new ServiceCollection().AddOadmPersistence(paths).BuildServiceProvider();
            await provider.GetRequiredService<DatabaseInitializer>().InitializeAsync(CancellationToken.None);
            return new Server(paths, provider);
        }

        /// <summary>The PKI plugin on this server's database.</summary>
        public async Task<PkiPlugin> StartPkiAsync()
        {
            var plugin = new PkiPlugin(PkiHarness.Options(new FakeTrustStore()));
            await plugin.StartAsync(new TestCoreContext(Settings, Secrets, null, null) { OtherRecords = Records }, CancellationToken.None);
            await plugin.Service!.Background;
            return plugin;
        }

        public async ValueTask DisposeAsync()
        {
            await _provider.DisposeAsync();
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task A_restore_on_another_server_brings_back_the_issued_certificates_and_the_revocation_list()
    {
        var root = Path.Combine(Path.GetTempPath(), "oadm-tests", Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "a");
        var target = Path.Combine(root, "b");
        try
        {
            // Server A: two certificates of one device, one revoked.
            var file = new MemoryStream();
            string revoked, valid, caId, created;
            byte[] der;
            await using (var a = await Server.OpenAsync(source))
            {
                await using (var pki = await a.StartPkiAsync())
                {
                    IDeviceCertificateAuthority authority = new PkiCertificateAuthority(pki.Service!);
                    var device = Guid.NewGuid();
                    var first = await RevocationTests.SignAsync(authority, device);
                    revoked = first.SerialNumber;
                    der = first.Certificate;
                    valid = (await RevocationTests.SignAsync(authority, device, use: DeviceCertificateUse.Client)).SerialNumber;
                    await authority.RevokeAsync([revoked], RevocationReason.DeviceReplaced, "admin", CancellationToken.None);
                    caId = pki.Service!.Ca!.Id;

                    // A certificate created here: its key is encrypted with the master key, in the plugin records.
                    var policy = pki.Service.Policies.FirstOf(Policies.PolicyKinds.HttpsCertificate)!.Id;
                    var reply = PkiJson.Deserialize<Certificates.CertificateActionReply>(await pki.InvokeAsync(Certificates.CertificateMethods.Create,
                        PkiJson.Serialize(new Certificates.CreateCertificateRequest(DeviceCertificatePurposes.Https, policy, "nvr.example.com", "nvr.example.com")), CancellationToken.None));
                    created = reply.Id!;
                } // stopping writes what waits

                await new BackupArchive(a.Paths) { Iterations = 1000 }.CreateAsync(file, Password,
                    new BackupHeader { Kind = "manual", OadmVersion = "4.2.0", ServerName = "site-a" }, [new BackupPlugin(PkiPluginInfo.PluginId, "0.1.0")], CancellationToken.None);
            }

            // Server B: its own database and master key; the restore replaces both.
            await (await Server.OpenAsync(target)).DisposeAsync();
            var archive = new BackupArchive(new OadmPaths(target));
            file.Position = 0;
            await archive.StageAsync(file, Password, "site-a.oadmbackup", "admin@PC", "10.0.0.5", CancellationToken.None);
            var outcome = archive.ApplyPending((_, _) => { });
            Assert.Null(outcome?.Error);

            await using var b = await Server.OpenAsync(target);
            await using var restored = await b.StartPkiAsync();
            IDeviceCertificateAuthority again = new PkiCertificateAuthority(restored.Service!);
            Assert.Equal(caId, restored.Service!.Ca!.Id);
            Assert.True(again.IsRevoked(revoked));
            Assert.False(again.IsRevoked(valid));
            Assert.Equal(("admin", RevocationReason.DeviceReplaced), (again.FindRevocation(revoked)!.UserName, again.FindRevocation(revoked)!.Reason));
            var page = await again.ListIssuedAsync(new IssuedCertificateQuery(), CancellationToken.None);
            Assert.Equal(2, page.Total);
            Assert.Equal(der, await again.GetIssuedCertificateAsync(revoked, CancellationToken.None));

            // The created certificate and its key came along: the restored master key reads it.
            await using var download = await restored.OpenDownloadAsync(Certificates.CertificateMethods.Download,
                PkiJson.Serialize(new Certificates.DownloadCertificateRequest(created, Certificates.CertificateFormats.Pkcs12, Password)), CancellationToken.None);
            using var buffer = new MemoryStream();
            await download.Content.CopyToAsync(buffer);
            using var withKey = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12(buffer.ToArray(), Password,
                OperatingSystem.IsMacOS() ? System.Security.Cryptography.X509Certificates.X509KeyStorageFlags.DefaultKeySet : System.Security.Cryptography.X509Certificates.X509KeyStorageFlags.EphemeralKeySet);
            Assert.True(withKey.HasPrivateKey);
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch (IOException)
            {
                // a lingering handle must not fail the test
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
