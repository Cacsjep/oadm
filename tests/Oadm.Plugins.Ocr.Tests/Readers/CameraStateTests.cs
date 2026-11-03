using Oadm.Plugins.Ocr.Api;
using Oadm.Plugins.Ocr.Model;
using Oadm.Plugins.Ocr.Readers;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Ocr.Tests.Readers;

/// <summary>
/// The cameras behind the readers: a camera OADM does not contact (certificate changed, credentials required, site offline)
/// gets no stream until it is Ok again; at a site at most 4 readers watch (each stream holds a large transfer of the node).
/// </summary>
public sealed class CameraStateTests
{
    private static void SetStatus(OcrHarness ocr, FakeDevice device, DeviceStatus status)
    {
        var index = ocr.Context.DeviceList.FindIndex(d => d.Id == device.Id);
        ocr.Context.DeviceList[index] = device with { Status = status };
    }

    [Fact]
    public async Task ARefusingCameraGetsNoStreamUntilItIsOk()
    {
        await using var ocr = await OcrHarness.StartAsync();
        SetStatus(ocr, ocr.Inside, DeviceStatus.CertificateChanged);

        var reader = await ocr.SaveAsync(ocr.NewReader("Gate", ReaderMode.Watching));

        Assert.Empty(ocr.Plugin.Runtime!.Watching);
        Assert.Equal(DeviceMessages.CertificateChanged, Assert.Single((await ocr.ListAsync()).Readers).Problem);
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => ocr.CallAsync(OcrMethods.ReadNow, new ReaderRequest { ReaderId = reader.Id }));
        Assert.Equal(DeviceMessages.CertificateChanged, refused.Message);
        Assert.Equal((0, 0), (ocr.Frames.Opened, ocr.Frames.Singles));

        // The next check after the camera is Ok again opens its stream and tells the pages.
        SetStatus(ocr, ocr.Inside, DeviceStatus.Ok);
        var lists = ocr.Context.PublishedEvents.Count(e => e.Topic == OcrMethods.ReadersTopic);
        ocr.Time.Advance(ReaderRuntime.RecheckInterval);
        await Eventually.TrueAsync(() => ocr.Frames.Open == 1, "the reader watches");
        await Eventually.TrueAsync(() => ocr.Context.PublishedEvents.Count(e => e.Topic == OcrMethods.ReadersTopic) > lists, "the pages heard of it");
        Assert.Null(Assert.Single((await ocr.ListAsync()).Readers).Problem);
    }

    [Fact]
    public async Task AtASiteAtMost4ReadersWatch()
    {
        await using var ocr = await OcrHarness.StartAsync();
        var site = Guid.NewGuid();
        var cameras = Enumerable.Range(1, 5).Select(i => ocr.Context.Add(new FakeDevice { Address = $"10.1.0.{i}", SiteId = site, SiteName = "Harbour" })).ToList();
        for (var i = 0; i < 4; i++)
        {
            await ocr.SaveAsync(ocr.NewReader($"Crane {i}", ReaderMode.Watching, device: cameras[i].Id));
        }

        var refused = await Assert.ThrowsAsync<ArgumentException>(() => ocr.SaveAsync(ocr.NewReader("Crane 5", ReaderMode.Watching, device: cameras[4].Id)));
        Assert.Equal("At most 4 readers can watch at once at the site Harbour. Set another reader there to On demand first.", refused.Message);

        // On demand at the site, and watching on the server's own network, stay possible.
        await ocr.SaveAsync(ocr.NewReader("Crane 5", device: cameras[4].Id));
        await ocr.SaveAsync(ocr.NewReader("Gate", ReaderMode.Watching));
        await Eventually.TrueAsync(() => ocr.Frames.Open == 5, "4 readers at the site and 1 at the server watch");
    }
}
