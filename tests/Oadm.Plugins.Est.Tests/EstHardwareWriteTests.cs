using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

using Oadm.Core.Tests.Hardware;
using Oadm.Core.Vapix;
using Oadm.Plugins.Est.Device;
using Oadm.Plugins.Est.Serving;
using Oadm.Plugins.Est.Tasks;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Sites;
using Oadm.Sdk.Testing;
using Oadm.Sdk.Vapix;

using Xunit.Abstractions;

namespace Oadm.Plugins.Est.Tests;

/// <summary>
/// Opt-in write test of "Set up certificate enrollment" against 10.0.0.48 (dev-cameras.yaml), only with
/// <c>OADM_EST_HARDWARE_WRITE=1</c> and <c>OADM_EST_SERVER_ADDRESS</c> (this computer's address towards the camera): a
/// real EST server on TCP 9454 of this computer with a test CA; the real task sets up the HTTPS profile; the camera must
/// enroll with its device ID and serve HTTPS with the new certificate. Afterwards the profile is removed again and the
/// web server gets its previous certificate back.
/// </summary>
[Trait("Category", "HardwareWrite")]
public sealed class EstHardwareWriteTests(ITestOutputHelper output)
{
    public sealed class WriteFactAttribute : FactAttribute
    {
        public WriteFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("OADM_EST_HARDWARE_WRITE") != "1"
                || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OADM_EST_SERVER_ADDRESS"))
                || !DevCameras.All.Any(c => c.Address == "10.0.0.48"))
            {
                Skip = "Writes to 10.0.0.48: set OADM_EST_HARDWARE_WRITE=1 and OADM_EST_SERVER_ADDRESS.";
            }
        }
    }

    [WriteFact]
    public async Task The_camera_enrolls_with_its_device_id_and_serves_https_with_the_new_certificate()
    {
        var camera = DevCameras.All.First(c => c.Address == "10.0.0.48");
        var serverAddress = IPAddress.Parse(Environment.GetEnvironmentVariable("OADM_EST_SERVER_ADDRESS")!);
        var ct = CancellationToken.None;
        using var handler = new HttpClientHandler
        {
            Credentials = new NetworkCredential(camera.User, camera.Password),
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        };
        using var vapix = new VapixClient(new Uri($"https://{camera.Address}/"), handler, disposeHandler: false);

        var webBefore = await Pki.Device.WebServerTls.GetAsync(vapix, ct);
        var caBefore = (await CameraEstApi.ListCaCertificatesAsync(vapix, ct)).Select(c => c.Alias).ToHashSet(StringComparer.Ordinal);
        var certificatesBefore = (await CameraEstApi.ListCertificatesAsync(vapix, ct)).Select(c => c.Alias).ToHashSet(StringComparer.Ordinal);
        output.WriteLine($"Web server before: certificate {webBefore.CertificateAlias}");
        Assert.False(webBefore.CertificateAlias is { } inUse && EstTaskIds.IsOadm(inUse), "The camera still uses a test certificate: restore it first.");

        using var ca = new FakeAuthority();
        var devices = new Devices();
        var device = new FakeDevice { Serial = "B8A44F631339", Address = camera.Address, FirmwareVersion = "12.11.77", HttpsEnabled = true };
        devices.List.Add(device);
        // PKI's policies of a new server (Enrollment "All devices", Sign-in CA) and an HTTPS policy of 30 days for the profile.
        var policies = await TestPolicies.EstStoreAsync();
        var thirtyDays = await TestPolicies.AddAsync(policies, new Oadm.Plugins.Pki.Policies.PkiPolicy
        {
            Name = "HTTPS 30 days", Kind = Oadm.Plugins.Pki.Policies.PolicyKinds.HttpsCertificate, CaId = "oadm", ValidDays = 30, Key = Oadm.Plugins.Pki.Policies.PolicyKeys.Ecc,
        });
        var settings = new FakeSettings();
        await settings.SetAsync(EstStore.ConfigKey, EstJson.Serialize(new EstConfig { Profiles = [new EstProfile("https", ProfileUse.Https, 30) { PolicyId = thirtyDays.Id }] }), ct);
        await using var network = new RealNetwork();
        await using var est = new EstService(new EstHost(settings, devices, network, () => ca, () => true)
        {
            LocalAddresses = () => [serverAddress.ToString()],
            HostName = "oadm-test",
            Policies = () => policies,
        });
        await est.StartAsync(ct);
        Assert.Equal(RelayState.Running, est.Listener!.State);

        var task = new EstSetupTask(() => est);
        Assert.True(task.CanRun(device));
        try
        {
            var ctx = new TaskContext(vapix, new FakeDeviceNetwork { LocalAddress = serverAddress });
            try
            {
                await task.ExecuteAsync(ctx, device, "{}", ct);
            }
            finally
            {
                ctx.Flush();
                foreach (var line in ctx.Lines)
                {
                    output.WriteLine(line);
                }

                foreach (var enrollment in est.Store.Enrollments())
                {
                    output.WriteLine($"Enrollment {enrollment.Kind} {enrollment.Profile}: {(enrollment.Ok ? "ok" : "refused")} {enrollment.Reason}");
                }
            }

            var issued = Assert.Single(est.Store.Current());
            output.WriteLine($"Issued {issued.SerialNumber}, valid until {issued.NotAfterUtc:yyyy-MM-dd}, pinned {ctx.PinnedFingerprint}");
            var profile = Assert.Single(await CameraEstApi.ReadProfilesAsync(vapix, ct));
            Assert.Equal(("oadm_https", "ENROLLED"), (profile.Id, profile.Status));
            Assert.Equal(issued.Fingerprint, ctx.PinnedFingerprint);

            // The camera's web server switches to the certificate from the test CA a few seconds after ENROLLED.
            using var presented = await HandshakeAsync(camera.Address, ca.Certificate.Subject);
            output.WriteLine($"HTTPS now: {presented.Subject}, issued by {presented.Issuer}");
            Assert.Equal(issued.Fingerprint, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(presented.RawData)));
            Assert.Contains("axis-b8a44f631339.local", presented.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single().EnumerateDnsNames());

            // A second run writes nothing (after the web server's restart with the new certificate).
            await RetryAsync(() => CameraEstApi.DiscoverAsync(vapix, ct));
            var again = new TaskContext(vapix, new FakeDeviceNetwork { LocalAddress = serverAddress });
            await task.ExecuteAsync(again, device, "{}", ct);
            again.Flush();
            foreach (var line in again.Lines)
            {
                output.WriteLine("Again: " + line);
            }

            Assert.DoesNotContain(again.Lines, l => l.StartsWith(EstTaskIds.AddStep("https"), StringComparison.Ordinal));

            // A changed profile (another name of the camera: other SANs): the camera never stores a certificate over the
            // previous one of the same name (it reported ENROLLED and kept it after a change in place), so the profile is
            // removed and added under its other name; the new certificate takes the web server.
            var renamed = device with { HostName = "oadm-est-renamed" };
            var beforeRewrite = est.Store.Current().Single().SerialNumber;
            await RetryAsync(() => CameraEstApi.DiscoverAsync(vapix, ct));
            var rewrite = new TaskContext(vapix, new FakeDeviceNetwork { LocalAddress = serverAddress });
            try
            {
                await task.ExecuteAsync(rewrite, renamed, "{}", ct);
            }
            finally
            {
                rewrite.Flush();
                foreach (var line in rewrite.Lines)
                {
                    output.WriteLine("Rewrite: " + line);
                }

                foreach (var enrollment in est.Store.Enrollments())
                {
                    output.WriteLine($"Rewrite enrollment {enrollment.TimeUtc:HH:mm:ss} {enrollment.Kind} {enrollment.Profile}: {(enrollment.Ok ? "ok" : "refused")} {enrollment.Reason}");
                }

                foreach (var p in await RetryAsync(() => CameraEstApi.ReadProfilesAsync(vapix, ct)))
                {
                    output.WriteLine($"Rewrite profile {p.Id} {p.Status} {p.Certificate}");
                }

                foreach (var c in await RetryAsync(() => CameraEstApi.ListCertificatesAsync(vapix, ct)))
                {
                    output.WriteLine("Rewrite certificate " + c.Alias);
                }
            }

            Assert.Contains(rewrite.Lines, l => l.StartsWith(EstTaskIds.AddStep("https"), StringComparison.Ordinal));
            Assert.NotEqual(beforeRewrite, est.Store.Current().Single().SerialNumber);
            var rewritten = (await RetryAsync(() => CameraEstApi.ReadProfilesAsync(vapix, ct))).Single();
            Assert.Equal(EstTaskIds.AlternatePrefix + "https", rewritten.Id);
            Assert.Contains("DNS:oadm-est-renamed", rewritten.SubjectAlternativeNames);
            await PresentsAsync(camera.Address, est.Store.Current().Single().Fingerprint!);

            Assert.DoesNotContain(await RetryAsync(() => CameraEstApi.ListCertificatesAsync(vapix, ct)), c => c.Alias == "oadm_https");

            // The dialog's read-only query lists the new profile.
            var listed = EstJson.Deserialize<CameraProfileList>(await task.QueryAsync(new QueryContext(vapix), device, EstTaskIds.ListProfilesMethod, null, ct));
            output.WriteLine("Listed: " + string.Join(", ", listed.Profiles.Select(p => $"{p.Id} {p.Status} {string.Join("/", p.Services)} {p.Server}")));
            Assert.Contains(listed.Profiles, p => p.Id == rewritten.Id && p.Status == "ENROLLED");

            var remove = new TaskContext(vapix, new FakeDeviceNetwork());
            await task.ExecuteAsync(remove, device, EstJson.Serialize(new SetupPayload { Action = SetupAction.Remove, Profiles = [rewritten.Id] }), ct);
            Assert.Empty(await CameraEstApi.ReadProfilesAsync(vapix, ct));

            // Set up again after Remove (the reported case): the web server still holds the removed profile's certificate
            // ("Certificate is in use"), so the profile takes the other name, enrolls, and the older certificate is deleted
            // once the new one took the web server.
            Assert.False(await CameraEstApi.TryDeleteCertificateAsync(vapix, rewritten.Id, ct), "the web server holds the removed profile's certificate");
            var readd = new TaskContext(vapix, new FakeDeviceNetwork { LocalAddress = serverAddress });
            try
            {
                await task.ExecuteAsync(readd, device, "{}", ct);
            }
            finally
            {
                readd.Flush();
                foreach (var line in readd.Lines)
                {
                    output.WriteLine("Set up again: " + line);
                }
            }

            var readded = Assert.Single(await RetryAsync(() => CameraEstApi.ReadProfilesAsync(vapix, ct)));
            Assert.Equal((EstTaskIds.ProfilePrefix + "https", "ENROLLED"), (readded.Id, readded.Status));
            var newest = est.Store.Current().Single();
            Assert.Equal(newest.Fingerprint, readd.PinnedFingerprint);
            await PresentsAsync(camera.Address, newest.Fingerprint!);

            Assert.DoesNotContain(await RetryAsync(() => CameraEstApi.ListCertificatesAsync(vapix, ct)), c => c.Alias == rewritten.Id);
            await task.ExecuteAsync(new TaskContext(vapix, new FakeDeviceNetwork()), device, EstJson.Serialize(new SetupPayload { Action = SetupAction.Remove, Profiles = [readded.Id] }), ct);
        }
        finally
        {
            // Back to how it was: no OADM EST profile, the previous web server certificate, no test certificates.
            foreach (var left in (await RetryAsync(() => CameraEstApi.ReadProfilesAsync(vapix, ct))).Where(p => EstTaskIds.IsOadm(p.Id)))
            {
                await RetryAsync(() => CameraEstApi.RemoveProfileAsync(vapix, left.Id, ct));
            }

            await RetryAsync(() => Pki.Device.WebServerTls.SetAsync(vapix, webBefore, ct));
            await Task.Delay(TimeSpan.FromSeconds(10), ct); // the web server restarts with its certificate
            foreach (var certificate in (await RetryAsync(() => CameraEstApi.ListCertificatesAsync(vapix, ct))).Where(c => EstTaskIds.IsOadm(c.Alias) && !certificatesBefore.Contains(c.Alias)))
            {
                await RetryAsync(() => Pki.Device.CertApi.DeleteCertificateAsync(vapix, certificate.Alias, ct));
            }

            // Also the CA the camera installs by itself from the enrollment answer (named after its common name).
            foreach (var added in (await RetryAsync(() => CameraEstApi.ListCaCertificatesAsync(vapix, ct))).Where(c => !caBefore.Contains(c.Alias)))
            {
                await RetryAsync(() => Pki.Device.CertApi.DeleteCaCertificateAsync(vapix, added.Alias, ct));
            }

            output.WriteLine($"Restored: web server certificate {webBefore.CertificateAlias}, test certificates removed");
        }
    }

    /// <summary>A camera call, again while its web server restarts (up to 60 s).</summary>
    private static async Task<T> RetryAsync<T>(Func<Task<T>> call)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await call();
            }
            catch (HttpRequestException) when (attempt < 20)
            {
                await Task.Delay(TimeSpan.FromSeconds(3));
            }
        }
    }

    private static Task<bool> RetryAsync(Func<Task> call) => RetryAsync(async () =>
    {
        await call();
        return true;
    });

    /// <summary>The certificate the camera's web server presents once it is issued by <paramref name="issuer"/> (up to 60 s).</summary>
    /// <summary>Waits until the web server presents the certificate (it switches a few seconds after ENROLLED), up to 60 s.</summary>
    private static async Task PresentsAsync(string address, string fingerprint)
    {
        var last = "";
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(address, 443);
                await using var tls = new SslStream(tcp.GetStream(), false, (_, certificate, _, _) =>
                {
                    last = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(certificate!.GetRawCertData()));
                    return true;
                });
                await tls.AuthenticateAsClientAsync(address);
                if (string.Equals(last, fingerprint, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or AuthenticationException)
            {
                // the web server restarts with the new certificate
            }

            await Task.Delay(TimeSpan.FromSeconds(3));
        }

        Assert.Fail($"The web server presents {last}, not {fingerprint}.");
    }

    private static async Task<X509Certificate2> HandshakeAsync(string address, string issuer)
    {
        X509Certificate2? seen = null;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(address, 443);
                await using var tls = new SslStream(tcp.GetStream(), false, (_, certificate, _, _) =>
                {
                    seen = X509CertificateLoader.LoadCertificate(certificate!.GetRawCertData());
                    return true;
                });
                await tls.AuthenticateAsClientAsync(address);
                if (seen!.Issuer == issuer)
                {
                    return seen;
                }

                seen.Dispose();
                await Task.Delay(TimeSpan.FromSeconds(3));
            }
            catch (Exception ex) when (ex is IOException or SocketException or AuthenticationException)
            {
                await Task.Delay(TimeSpan.FromSeconds(3)); // the web server restarts with the new certificate
            }
        }

        throw new TimeoutException("The camera's web server did not answer.");
    }

    /// <summary>The task's context: the real camera client, the steps as lines, the pin it asks for.</summary>
    private sealed class TaskContext(IVapixClient vapix, IDeviceNetwork network) : ITaskExecutionContext
    {
        private readonly TaskStepList _steps = new();

        public Guid TaskId { get; } = Guid.NewGuid();

        public Microsoft.Extensions.Logging.ILogger Logger => Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        public ICorePlugin? Owner => null;

        public IUploadedFiles Files => throw new NotSupportedException("No uploads.");

        public IVapixClient Vapix => vapix;

        public IDeviceNetwork? Network => network;

        public string? PinnedFingerprint { get; private set; }

        public List<string> Lines { get; } = [];

        public void ReportProgress(int percent, string? message = null)
        {
        }

        public ITaskStep BeginStep(string name) => _steps.Begin(name);

        public void ReportWarning(string message) => Lines.Add("Warning: " + message);

        public void Log(TaskLogLevel level, string message) => Lines.Add($"{level}: {message}");

        public Task UpdateDeviceTlsAsync(string scheme, string? expectedFingerprintSha256, CancellationToken ct)
        {
            PinnedFingerprint = expectedFingerprintSha256;
            return Task.CompletedTask;
        }

        public void Flush() => Lines.AddRange(StepRun.Lines(_steps));
    }

    private sealed class QueryContext(IVapixClient vapix) : ITaskQueryContext
    {
        public IVapixClient Vapix => vapix;

        public Microsoft.Extensions.Logging.ILogger Logger => Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    internal sealed class Devices : IDeviceRepository
    {
        public List<IDeviceInfo> List { get; } = [];

        public Task<IReadOnlyList<IDeviceInfo>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<IDeviceInfo>>([.. List]);

        public Task<IDeviceInfo?> FindAsync(Guid id, CancellationToken ct) => Task.FromResult(List.Find(d => d.Id == id));
    }

    /// <summary>A real TCP listener on every address (the test computer's network).</summary>
    internal sealed class RealNetwork : IPluginNetwork, IAsyncDisposable
    {
        private readonly List<RealListener> _listeners = [];

        public IDeviceNetwork ForDevice(IDeviceInfo device) => new FakeDeviceNetwork();

        public IDeviceNetwork ForSite(Guid? siteId) => new FakeDeviceNetwork();

        public Task<IUdpRelay> ListenUdpAsync(int port, string reason, CancellationToken ct) => throw new NotSupportedException();

        public Task<ITcpRelay> ListenTcpAsync(int port, string reason, CancellationToken ct)
        {
            var listener = new RealListener(port);
            _listeners.Add(listener);
            return Task.FromResult<ITcpRelay>(listener);
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var listener in _listeners)
            {
                await listener.DisposeAsync();
            }
        }
    }

    private sealed class RealListener : ITcpRelay
    {
        private readonly TcpListener _listener;

        public RealListener(int port)
        {
            Port = port;
            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start();
        }

        public Guid? SiteId => null;

        public SiteProtocol Protocol => SiteProtocol.Tcp;

        public int Port { get; }

        public RelayState State => RelayState.Running;

        public string StateText => "EST server on this computer: running";

        public event EventHandler? StateChanged
        {
            add { }
            remove { }
        }

        public async ValueTask<IIncomingConnection> AcceptAsync(CancellationToken ct)
        {
            var client = await _listener.AcceptTcpClientAsync(ct);
            return new Connection(client);
        }

        public ValueTask DisposeAsync()
        {
            _listener.Stop();
            return ValueTask.CompletedTask;
        }

        private sealed class Connection(TcpClient client) : IIncomingConnection
        {
            public Stream Stream { get; } = client.GetStream();

            public IPEndPoint Remote { get; } = (IPEndPoint)client.Client.RemoteEndPoint!;

            public Guid? SiteId => null;

            public ValueTask DisposeAsync()
            {
                client.Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
