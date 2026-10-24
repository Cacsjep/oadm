using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;

using Oadm.Core.LiveView.Rtsp;
using Oadm.Core.Tests.Hardware;
using Oadm.Core.Vapix;
using Oadm.Plugins.DeviceIds;
using Oadm.Plugins.Est.Device;
using Oadm.Plugins.Est.Serving;
using Oadm.Plugins.Est.Tasks;
using Oadm.Sdk.Testing;

using Xunit.Abstractions;

namespace Oadm.Plugins.Est.Tests;

/// <summary>
/// Opt-in probe on 10.0.0.48 (same switches as <see cref="EstHardwareWriteTests"/>): adds a temporary EST profile whose
/// server does not answer, prints what the camera's event stream says about it for 90 s, then removes the profile.
/// </summary>
[Trait("Category", "HardwareWrite")]
public sealed class EstEventProbeTests(ITestOutputHelper output)
{
    private const string ProbeId = EstTaskIds.ProfilePrefix + "probe";

    [EstHardwareWriteTests.WriteFact]
    public Task The_camera_reports_an_unreachable_est_server_in_its_event_stream() => ProbeAsync(refusingServer: false);

    [EstHardwareWriteTests.WriteFact]
    public Task The_camera_reports_a_refused_enrollment_in_its_event_stream() => ProbeAsync(refusingServer: true);

    private async Task ProbeAsync(bool refusingServer)
    {
        var camera = DevCameras.All.First(c => c.Address == "10.0.0.48");
        var serverAddress = Environment.GetEnvironmentVariable("OADM_EST_SERVER_ADDRESS")!;
        var ct = CancellationToken.None;
        using var handler = new HttpClientHandler
        {
            Credentials = new NetworkCredential(camera.User, camera.Password),
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        };
        using var vapix = new VapixClient(new Uri($"https://{camera.Address}/"), handler, disposeHandler: false);

        Assert.DoesNotContain(await CameraEstApi.ReadProfilesAsync(vapix, ct), p => p.Id.StartsWith(EstTaskIds.ProfilePrefix, StringComparison.Ordinal));
        var caAlias = (await CameraEstApi.ListCaCertificatesAsync(vapix, ct))[0].Alias;
        var deviceId = DeviceIdAlias(await CameraEstApi.ListCertificatesAsync(vapix, ct));
        var certificatesBefore = (await CameraEstApi.ListCertificatesAsync(vapix, ct)).Select(c => c.Alias).ToHashSet(StringComparer.Ordinal);
        output.WriteLine($"CA {caAlias}, device ID {deviceId}");

        // The refusing server: a real EST server that does not manage the camera (403), its CA trusted by the camera.
        using var ca = new FakeAuthority();
        await using var network = new EstHardwareWriteTests.RealNetwork();
        var settings = new FakeSettings();
        await settings.SetAsync(EstStore.ConfigKey, EstJson.Serialize(new EstConfig { Profiles = [new EstProfile("probe", ProfileUse.Mqtt, 30)] }), ct);
        var policies = await TestPolicies.EstStoreAsync(); // the camera signs in (Sign-in CA "Axis device ID") and is refused as not managed
        await using var est = new EstService(new EstHost(settings, new EstHardwareWriteTests.Devices(), network, () => ca, () => true)
        {
            LocalAddresses = () => [serverAddress],
            HostName = "oadm-test",
            Policies = () => policies,
        });
        var caBefore = (await CameraEstApi.ListCaCertificatesAsync(vapix, ct)).Select(c => c.Alias).ToHashSet(StringComparer.Ordinal);
        var server = serverAddress + ":9459/.well-known/est/probe"; // nothing listens there
        if (refusingServer)
        {
            await est.StartAsync(ct);
            await CameraEstApi.AddCaCertificateAsync(vapix, "OADM CA probe", ca.Certificate.ExportCertificatePem(), ct);
            caAlias = "OADM CA probe";
            server = serverAddress + ":9454" + EstPluginInfo.PathPrefix + "probe";
        }

        await using var events = await RtspMetadataSource.OpenAsync(
            new RtspMetadataOptions { Address = camera.Address, Credentials = new NetworkCredential(camera.User, camera.Password) }, ct);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(150));
        var seen = new List<string>();
        var documents = 0;
        var reading = Task.Run(async () =>
        {
            try
            {
                await foreach (var document in events.ReadAsync(stop.Token))
                {
                    Interlocked.Increment(ref documents);
                    if (document.Xml.Contains("Certificate", StringComparison.OrdinalIgnoreCase) || document.Xml.Contains("EST", StringComparison.Ordinal))
                    {
                        lock (seen)
                        {
                            seen.Add($"{document.ReceivedUtc:HH:mm:ss} {document.Xml}");
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        });

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), ct); // the stream's first documents (the current states)
            await CameraEstApi.AddProfileAsync(vapix, new JsonObject
            {
                ["id"] = ProbeId,
                ["server"] = server,
                ["caCertificates"] = new JsonArray(caAlias),
                ["certificate"] = deviceId,
                ["services"] = new JsonArray("MQTT"),
                ["subject"] = new JsonObject { ["cn"] = "oadm-probe" },
                ["subjectAlternativeNames"] = new JsonArray(),
            }, ct);
            output.WriteLine($"{DateTime.UtcNow:HH:mm:ss} profile added");
            while (!stop.IsCancellationRequested)
            {
                var status = (await CameraEstApi.ReadProfilesAsync(vapix, ct)).FirstOrDefault(p => p.Id == ProbeId)?.Status;
                output.WriteLine($"{DateTime.UtcNow:HH:mm:ss} status {status}");
                await Task.Delay(TimeSpan.FromSeconds(15), ct);
            }

            await reading;
        }
        finally
        {
            await CameraEstApi.RemoveProfileAsync(vapix, ProbeId, ct);
            foreach (var added in (await CameraEstApi.ListCertificatesAsync(vapix, ct)).Where(c => !certificatesBefore.Contains(c.Alias)))
            {
                output.WriteLine("Left by the probe, removed: " + added.Alias);
                await Pki.Device.CertApi.DeleteCertificateAsync(vapix, added.Alias, ct);
            }

            foreach (var added in (await CameraEstApi.ListCaCertificatesAsync(vapix, ct)).Where(c => !caBefore.Contains(c.Alias)))
            {
                await Pki.Device.CertApi.DeleteCaCertificateAsync(vapix, added.Alias, ct);
            }

            foreach (var enrollment in est.Store.Enrollments())
            {
                output.WriteLine($"Server: {enrollment.Kind} {enrollment.Profile}: {(enrollment.Ok ? "ok" : "refused")} {enrollment.Reason}");
            }

            output.WriteLine($"Profile removed; {documents} documents read");
        }

        lock (seen)
        {
            foreach (var line in seen)
            {
                output.WriteLine(line);
            }
        }
    }

    private static string DeviceIdAlias(IReadOnlyList<CameraCertificate> certificates)
    {
        var ids = new AxisDeviceId();
        foreach (var entry in certificates)
        {
            var collection = new X509Certificate2Collection();
            try
            {
                collection.ImportFromPem(entry.Pem);
                if (collection.Count > 0 && ids.Verify(collection[0], [.. collection.Skip(1)], DateTimeOffset.UtcNow) is not null)
                {
                    return entry.Alias;
                }
            }
            catch (CryptographicException)
            {
            }
            finally
            {
                foreach (var certificate in collection)
                {
                    certificate.Dispose();
                }
            }
        }

        throw new InvalidOperationException("No device ID certificate.");
    }
}
