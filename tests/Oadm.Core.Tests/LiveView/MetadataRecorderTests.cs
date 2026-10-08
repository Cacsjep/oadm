using System.Diagnostics;
using System.Net;

using Oadm.Core.LiveView.Rtsp;
using Oadm.Core.Tests.Hardware;

namespace Oadm.Core.Tests.LiveView;

/// <summary>
/// Read-only RTSP event stream tests against the first dev camera (only DESCRIBE / SETUP / PLAY / TEARDOWN of
/// <c>video=0&amp;audio=0&amp;event=on</c>; nothing is changed on the device). <see cref="RecordEventFixture"/> rewrites
/// <c>Fixtures/LiveView/events.sdp</c> + <c>events.rtp</c> when OADM_RECORD_RTP_DIR points at that folder.
/// </summary>
[Trait("Category", "Hardware")]
public sealed class MetadataRecorderTests
{
    private static DevCamera Camera => DevCameras.All.FirstOrDefault(c => c.Address == "10.0.0.48") ?? DevCameras.All[0];

    [HardwareFact]
    public async Task RecordEventFixture()
    {
        var target = Environment.GetEnvironmentVariable("OADM_RECORD_RTP_DIR");
        if (string.IsNullOrEmpty(target))
        {
            return; // only records on request
        }

        var options = Options();
        await using var client = await RtspClient.ConnectAsync(Camera.Address, RtspClient.DefaultPort, options.Credentials, TimeSpan.FromSeconds(5), CancellationToken.None);
        var url = RtspMetadataSource.BuildUrl(options);
        var describe = await client.SendAsync("DESCRIBE", url, [new("Accept", "application/sdp")], CancellationToken.None);
        Assert.Equal(200, describe.StatusCode);
        var track = SdpMetadataTrack.Parse(describe.Body)!;
        var baseUrl = describe.Header("Content-Base") is { } cb ? new Uri(cb) : url;
        Assert.Equal(200, (await client.SendAsync("SETUP", track.ResolveControl(baseUrl), [new("Transport", "RTP/AVP/TCP;unicast;interleaved=0-1")], CancellationToken.None)).StatusCode);
        Assert.Equal(200, (await client.SendAsync("PLAY", baseUrl, [new("Range", "npt=0.000-")], CancellationToken.None)).StatusCode);

        // The Initialized burst right after PLAY: every packet for 4 seconds.
        var packets = new List<byte[]>();
        var watch = Stopwatch.StartNew();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        try
        {
            while (true)
            {
                var packet = await client.ReadInterleavedAsync(cts.Token);
                if (packet is null)
                {
                    break;
                }

                if (packet.Value.Channel == 0)
                {
                    packets.Add(packet.Value.Data);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }

        await client.SendWithoutResponseAsync("TEARDOWN", baseUrl, CancellationToken.None);
        Assert.NotEmpty(packets);
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(target, "events.sdp"), describe.Body.Replace(Camera.Address, "camera.invalid", StringComparison.Ordinal));
        await File.WriteAllBytesAsync(Path.Combine(target, "events.rtp"), RtpFixtures.Encode(packets));
        Console.WriteLine($"{packets.Count} RTP packets in {watch.Elapsed.TotalSeconds:F1} s");
    }

    [HardwareFact]
    public async Task EventStreamDeliversInitializedDocuments()
    {
        await using var source = await RtspMetadataSource.OpenAsync(Options(), CancellationToken.None);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var documents = new List<string>();
        await foreach (var document in source.ReadAsync(cts.Token))
        {
            documents.Add(document.Xml);
            if (documents.Any(d => d.Contains("PropertyOperation=\"Initialized\"", StringComparison.Ordinal)))
            {
                break;
            }
        }

        Assert.Contains(documents, d => d.Contains("NotificationMessage", StringComparison.Ordinal));
        Assert.Equal(0, source.LostDocuments);
    }

    private static RtspMetadataOptions Options() => new()
    {
        Address = Camera.Address,
        Credentials = new NetworkCredential(Camera.User, Camera.Password),
    };
}
