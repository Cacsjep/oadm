using System.Diagnostics;
using System.Globalization;
using Oadm.Plugins.Radius.Api;
using Oadm.Plugins.Radius.Protocol.Radius;
using Oadm.Plugins.Radius.Tests.Support;
using Org.BouncyCastle.Tls;
using Xunit.Abstractions;

namespace Oadm.Plugins.Radius.Tests;

/// <summary>
/// Scale through the plugin (docs/spec/radius-server.md "Limits and load"): 1,000 cameras each run a full EAP-TLS
/// conversation through the UDP listener and the 64 workers, checked against 30,000 managed devices, with the log at 50,000
/// entries kept. Decision 2026-10-24: 1,000 instead of all 30,000 (that took 8 min at 16 ms per camera, half of it the test
/// camera's own TLS); the time per camera does not grow with the count. Budget about 10x the measured time.
/// </summary>
[Trait("Category", "Perf")]
public sealed class PluginPerfTests(ITestOutputHelper output)
{
    [Fact]
    public async Task AThousandCamerasAuthenticateThroughThePluginAmongThirtyThousandDevices()
    {
        const int Devices = 30_000;
        const int Count = 1_000;
        await using var harness = await PluginHarness.CreateAsync(start: false);
        var serials = Enumerable.Range(0, Devices).Select(i => "ACCC8E" + i.ToString("X6", CultureInfo.InvariantCulture)).ToList();
        foreach (var serial in serials)
        {
            harness.AddDevice(serial);
        }

        await harness.StartAsync(workers: 64);
        var settings = harness.Service.GetState(true).Settings;
        await harness.CallAsync<RadiusState>(RadiusMethods.SaveSettings, settings with { LogKept = RadiusLimits.MaxLogKept });
        await harness.AddSwitchAsync("Core switch", "10.0.0.2");
        var certificates = serials.Take(Count).Select(s => harness.Ca.Camera(s)).ToList();
        var accepted = 0;
        var watch = Stopwatch.StartNew();

        using var gate = new SemaphoreSlim(64);
        var conversations = Enumerable.Range(0, Count).Select(async i =>
        {
            await gate.WaitAsync();
            try
            {
                var serial = serials[i];
                var radiusSwitch = new PluginSwitch(harness, harness.Listener, new System.Net.IPEndPoint(System.Net.IPAddress.Parse("10.0.0.2"), 1024 + (i % 60_000)),
                    System.Text.Encoding.ASCII.GetBytes(PluginHarness.Secret))
                {
                    CallingStationId = string.Join('-', Enumerable.Range(0, 6).Select(b => serial.Substring(b * 2, 2))),
                    NasPortId = "GigabitEthernet1/0/" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), // one camera per port
                };
                var camera = new TestSupplicant(new SupplicantOptions
                {
                    Certificate = certificates[i],
                    TrustedCa = harness.Ca.CaCertificate,
                    Versions = i % 2 == 0 ? [ProtocolVersion.TLSv13] : [ProtocolVersion.TLSv12],
                });
                var conversation = await radiusSwitch.RunAsync(camera);
                if (conversation.Final == RadiusCode.AccessAccept && camera.Msk!.AsSpan().SequenceEqual(conversation.MskFromAccept))
                {
                    Interlocked.Increment(ref accepted);
                }
            }
            finally
            {
                gate.Release();
            }
        });
        await Task.WhenAll(conversations);
        var handshakes = watch.Elapsed;

        watch.Restart();
        harness.Tick();
        var drained = watch.Elapsed;
        watch.Restart();
        var page = await harness.CallAsync<LogPage>(RadiusMethods.ListLog, new LogQuery("ACCC8E0001"));
        var search = watch.Elapsed;

        output.WriteLine($"{Count} authentications among {Devices} devices through the plugin (64 at a time): {handshakes.TotalMilliseconds:F0} ms, {handshakes.TotalMilliseconds / Count:F2} ms each");
        output.WriteLine($"Sessions tab and log of {Count} ended sessions: {drained.TotalMilliseconds:F0} ms; search over the log: {search.TotalMilliseconds:F0} ms");
        Assert.Equal(Count, accepted);
        Assert.Equal(Count, harness.Service.Log.Count);
        Assert.Equal(256, page.Total); // ACCC8E000100 .. ACCC8E0001FF
        Assert.True(handshakes < TimeSpan.FromMinutes(3), $"{handshakes.TotalMilliseconds:F0} ms");
        Assert.True(drained < TimeSpan.FromSeconds(10), $"{drained.TotalMilliseconds:F0} ms");
        Assert.True(search < TimeSpan.FromSeconds(2), $"{search.TotalMilliseconds:F0} ms");
    }
}
