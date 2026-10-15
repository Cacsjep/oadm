using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;

using Oadm.Core.Vapix;

using Xunit.Abstractions;

namespace Oadm.Server.Tests.Perf;

/// <summary>
/// CPU of the status poll's new connection per poll (the cameras close idle connections after 5-10 s): many fake cameras
/// on loopback addresses 127.1.x.y behind one local Kestrel with TLS, polled with the real VAPIX client (certificate pinning
/// included) at a fixed rate. Server and client run in this process, so the CPU is both ends together. Opt-in with
/// <c>OADM_LIMITS_DEVICES</c>. Only loopback traffic; the server side closes idle connections (no client ports in TIME_WAIT).
/// </summary>
[Trait("Category", "Perf")]
public sealed class TlsLimitsTests(ITestOutputHelper output)
{
    private const string Info = """{"apiVersion":"1.3","data":{"propertyList":{"SerialNumber":"ACCC8E000001","ProdNbr":"P3265-V","ProdType":"Dome Camera","Version":"12.11.77","Soc":"Axis Artpec-8"}}}""";

    [Fact]
    public async Task NewTlsConnectionPerPollAtTheRateOf30000Devices()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OADM_LIMITS_DEVICES")))
        {
            return;
        }

        foreach (var keyKind in new[] { "RSA 2048", "ECDSA P-256" })
        {
            using var certificate = CreateCertificate(keyKind);
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(k =>
            {
                k.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(5);
                k.Listen(IPAddress.Any, 0, l =>
                {
                    l.Protocols = HttpProtocols.Http1;
                    l.UseHttps(certificate);
                });
            });
            await using var app = builder.Build();
            app.MapPost("/axis-cgi/basicdeviceinfo.cgi", () => Results.Text(Info, "application/json"));
            await app.StartAsync();
            var port = new Uri(app.Urls.First()).Port;

            // 556 polls per second = 30,000 devices every 54 s. Two rounds over the same devices: the first with full
            // handshakes, the second after the server closed the connections (the client may resume the TLS session).
            const int rate = 556;
            const int devices = 8_000;
            var clients = Enumerable.Range(0, devices).Select(i => VapixClient.Create(new VapixConnectionOptions
            {
                Address = $"127.1.{i / 250}.{i % 250 + 1}:{port}",
                Scheme = Uri.UriSchemeHttps,
                Credentials = new NetworkCredential("root", "pass"),
                Timeout = TimeSpan.FromSeconds(3),
            })).ToList();
            output.WriteLine($"== {keyKind}, {devices:N0} fake cameras, {rate} polls per second");
            await RoundAsync("round 1 (full handshakes)", clients, rate);
            await Task.Delay(TimeSpan.FromSeconds(7)); // the server closes every idle connection
            await RoundAsync("round 2 (new connections, sessions may resume)", clients, rate);
            foreach (var client in clients)
            {
                client.Dispose();
            }

            await app.StopAsync();
        }
    }

    private async Task RoundAsync(string what, List<VapixClient> clients, int rate)
    {
        using var process = Process.GetCurrentProcess();
        var cpu = process.TotalProcessorTime;
        var failures = 0;
        var watch = Stopwatch.StartNew();
        var interval = TimeSpan.FromSeconds(1.0 / rate);
        var running = new List<Task>(clients.Count);
        for (var i = 0; i < clients.Count; i++)
        {
            var due = interval * i - watch.Elapsed;
            if (due > TimeSpan.FromMilliseconds(5))
            {
                await Task.Delay(due);
            }

            var client = clients[i];
            running.Add(Task.Run(async () =>
            {
                try
                {
                    await client.GetBasicDeviceInfoAsync(CancellationToken.None);
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref failures);
                }
            }));
        }

        await Task.WhenAll(running);
        watch.Stop();
        process.Refresh();
        var used = process.TotalProcessorTime - cpu;
        output.WriteLine($"  {what}: {clients.Count / watch.Elapsed.TotalSeconds:N0} polls/s, {failures} failed, CPU {used.TotalMilliseconds / clients.Count:N2} ms per poll = {used.TotalSeconds / watch.Elapsed.TotalSeconds:N2} cores ({used.TotalSeconds / watch.Elapsed.TotalSeconds / Environment.ProcessorCount * 100:N0} % of {Environment.ProcessorCount}), working set {process.WorkingSet64 / 1024 / 1024:N0} MB");
    }

    private static X509Certificate2 CreateCertificate(string kind)
    {
        CertificateRequest request;
        AsymmetricAlgorithm key;
        if (kind.StartsWith("RSA", StringComparison.Ordinal))
        {
            var rsa = RSA.Create(2048);
            key = rsa;
            request = new CertificateRequest("CN=fake camera", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        else
        {
            var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            key = ecdsa;
            request = new CertificateRequest("CN=fake camera", ecdsa, HashAlgorithmName.SHA256);
        }

        using (key)
        {
            using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
            // Windows SChannel needs a key it can use from a PFX.
            return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null);
        }
    }
}
