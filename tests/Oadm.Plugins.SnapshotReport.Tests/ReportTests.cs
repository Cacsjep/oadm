using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Client.Api;
using Oadm.Plugins.SnapshotReport.Report;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Tasks;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.SnapshotReport.Tests;

public sealed class ReportTests
{
    private static readonly DateTime Now = new(2026, 10, 7, 9, 30, 0, DateTimeKind.Utc);

    [Fact]
    public void Report_has_a_cover_and_two_snapshots_per_page_with_embedded_jpegs()
    {
        var p3265 = Facts("10.0.0.48", "AXIS P3265-V", "12.11.77", "Ok", Now.AddDays(300), "SelfSigned");
        var p3727 = Facts("10.0.0.32", "AXIS P3727-PLE", "11.8.64", "Ok", Now.AddDays(12), "Trusted");
        var offline = Facts("10.0.0.30", "AXIS M3106-L Mk II", "12.11.77", "Unreachable", Now.AddDays(-3), "Expired");
        var jpeg169 = FakeOadmApi.TestPicture(1920, 1080, "10.0.0.48", "P3265", Now, 1);
        var jpeg43 = FakeOadmApi.TestPicture(1440, 1080, "10.0.0.32", "P3727", Now, 2);
        var entries = new List<ReportEntry>
        {
            new(Tile(p3265, 1, null), Ok(jpeg169, 1920, 1080)),
            new(Tile(p3727, 1, "Sensor 1"), Ok(jpeg43, 1440, 1080)),
            new(Tile(p3727, 2, "Sensor 2"), Ok(jpeg43, 1440, 1080)),
            new(Tile(offline, 1, null), new CapturedSnapshot(null, 0, 0, null, "Timeout after 10 s")),
        };

        var report = ReportDocument.Render(new ReportData("Headquarters Vienna", "Jane Doe", new DateOnly(2026, 10, 7), "0.3.0", Now, entries));
        var pdf = PdfInspector.Open(report.Pdf);
        if (Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR") is { Length: > 0 } outDir)
        {
            Directory.CreateDirectory(outDir);
            File.WriteAllBytes(Path.Combine(outDir, "snapshot-report-sample.pdf"), report.Pdf);
        }

        Assert.Equal(3, pdf.PageCount);
        Assert.Equal(3, report.Pages);
        var cover = pdf.PageText(0);
        Assert.Contains("Maintenance report", cover, StringComparison.Ordinal);
        Assert.Contains("Headquarters Vienna", cover, StringComparison.Ordinal);
        Assert.Contains("Jane Doe", cover, StringComparison.Ordinal);
        Assert.Contains("2026-10-07", cover, StringComparison.Ordinal);
        Assert.Contains("OADM 0.3.0", cover, StringComparison.Ordinal);
        Assert.Contains("Cameras in report", cover, StringComparison.Ordinal);
        Assert.Contains("12.11.77", cover, StringComparison.Ordinal);
        Assert.Contains("11.8.64", cover, StringComparison.Ordinal);
        Assert.Contains("3 of 4 (1 failed)", cover, StringComparison.Ordinal);
        Assert.Contains("Expired 3 days ago", cover, StringComparison.Ordinal);
        Assert.Contains("12 days", cover, StringComparison.Ordinal);
        Assert.DoesNotContain("10.0.0.48", cover, StringComparison.Ordinal); // valid for 300 days: not listed
        Assert.Contains("Page 1 of 3", cover, StringComparison.Ordinal);
        Assert.Contains("Headquarters Vienna · 2026-10-07 · Maintenance report", cover, StringComparison.Ordinal);

        var page2 = pdf.PageText(1);
        Assert.Contains("10.0.0.48 · AXIS P3265-V", page2, StringComparison.Ordinal);
        Assert.Contains("10.0.0.32 - Sensor 1", page2, StringComparison.Ordinal);
        Assert.Contains("B8:A4:4F:63:13:39", page2, StringComparison.Ordinal);
        Assert.Contains("Self-signed · valid until", page2, StringComparison.Ordinal);
        var page3 = pdf.PageText(2);
        Assert.Contains("No snapshot: Timeout after 10 s", page3, StringComparison.Ordinal);
        Assert.Contains("Unreachable", page3, StringComparison.Ordinal);
        Assert.Contains("Expired on 2026-10-04 (3 days ago)", page3, StringComparison.Ordinal);

        // The JPEGs are embedded as they are (DCTDecode, byte for byte), the same picture only once.
        var images = pdf.Images();
        Assert.All(images, i => Assert.Equal("/DCTDecode", i.Filter));
        Assert.Contains(images, i => i.Data.AsSpan().SequenceEqual(jpeg169));
        Assert.Contains(images, i => i.Data.AsSpan().SequenceEqual(jpeg43));
        Assert.Equal(3, images.Count(i => i.Page > 0));
        Assert.True(report.Pdf.Length < jpeg169.Length + jpeg43.Length + 300_000, $"{report.Pdf.Length} bytes");
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 2)]
    [InlineData(7, 5)]
    public void Pages_are_cover_plus_one_per_two_snapshots(int entries, int pages)
    {
        var device = Facts("10.0.0.48", "AXIS P3265-V", "12.11.77", "Ok", null, null);
        var jpeg = FakeOadmApi.TestPicture(640, 360, "x", "y", Now, 1);
        var data = new ReportData("Site", "Tech", new DateOnly(2026, 1, 2), "1.0", Now,
            [.. Enumerable.Range(1, entries).Select(i => new ReportEntry(Tile(device, i, $"View Area {i}"), Ok(jpeg, 640, 360)))]);
        Assert.Equal(pages, PdfInspector.Open(ReportDocument.Render(data).Pdf).PageCount);
    }

    [Fact]
    public void Facts_texts_match_the_device_grid_wording()
    {
        var device = Facts("10.0.0.48", "M", "1", "CredentialsRequired", Now.AddDays(-3), "Expired");
        Assert.Equal("Expired on 2026-10-04 (3 days ago)", FactsText.Certificate(device, Now));
        Assert.Equal("Credentials required", FactsText.Status(device.Status));
        device.CertNotAfterUtc = Now.AddDays(245.5);
        device.CertTrust = "SelfSigned";
        Assert.Equal("Self-signed · valid until 2027-06-09 (245 days)", FactsText.Certificate(device, Now));
        device.CertNotAfterUtc = null;
        device.CertTrust = null;
        Assert.Equal("Not available", FactsText.Certificate(device, Now));
        Assert.Equal("B8:A4:4F:63:13:39", FactsText.Mac("B8A44F631339"));
        device.HostName = "cam1.example.com";
        Assert.Equal("10.0.0.48 (cam1.example.com)", FactsText.Address(device));
        Assert.True(FactsText.IsCertificateDue(Now.AddDays(30), Now));
        Assert.False(FactsText.IsCertificateDue(Now.AddDays(31), Now));
    }

    [Fact]
    public void Picture_fits_the_box_with_its_aspect_ratio()
    {
        var (w169, h169) = ReportDocument.Fit(1920, 1080);
        Assert.Equal(15.11, w169.Centimeter, 2);
        Assert.Equal(8.5, h169.Centimeter, 2);
        var (wWide, hWide) = ReportDocument.Fit(3840, 1080);
        Assert.Equal(17, wWide.Centimeter, 2);
        Assert.True(hWide.Centimeter < 8.5);
    }

    [Fact]
    public void Summary_counts_devices_sources_status_firmware_and_due_certificates()
    {
        var a = Facts("10.0.0.1", "M", "12.11.77", "Ok", Now.AddDays(31), "Trusted");
        var b = Facts("10.0.0.2", "M", "12.11.77", "Unreachable", Now.AddDays(30), "Trusted");
        var c = Facts("10.0.0.3", "M", "11.8.64", "Ok", null, null);
        var ok = Ok([0xFF, 0xD8, 0xFF, 0xD9], 1, 1);
        var summary = ReportSummary.From(new ReportData("s", "t", default, "v", Now,
            [new(Tile(a, 1, null), ok), new(Tile(b, 1, "Sensor 1"), ok), new(Tile(b, 2, "Sensor 2"), new CapturedSnapshot(null, 0, 0, null, "x")), new(Tile(c, 1, null), ok)]));
        Assert.Equal((3, 4, 2, 1, 3, 1), (summary.Devices, summary.Sources, summary.Online, summary.Offline, summary.Snapshots, summary.FailedSnapshots));
        Assert.Equal([("12.11.77", 2), ("11.8.64", 1)], summary.Firmware);
        Assert.Equal("10.0.0.2", Assert.Single(summary.CertificatesDue).Address);
    }

    [Fact]
    public async Task Plugin_builds_the_report_in_the_background_and_hands_it_out_in_chunks()
    {
        var camera = new FakeDevice { CertNotAfterUtc = DateTime.UtcNow.AddDays(10), CertTrustName = "SelfSigned" };
        var multi = new FakeDevice { Address = "10.0.0.32", Serial = "ACCC8E77E3A1", Model = "AXIS P3727-PLE", FirmwareVersion = "11.8.64" };
        var factory = new FakeVapixFactory();
        factory.Add(camera.Id);
        factory.Add(multi.Id).Sources = [new VideoSource(1, "Camera 1", 0, [new(2592, 1944), new(1440, 1080), new(960, 720)]), new VideoSource(2, "Camera 2", 1, [new(2592, 1944), new(1440, 1080), new(960, 720)])];
        using var plugin = new SnapshotReportPlugin();
        await plugin.StartAsync(new FakeCoreContext(new FakeRepository(camera, multi), factory), CancellationToken.None);

        var tiles = SnapshotReportJson.Deserialize<ListSourcesResult>(await plugin.InvokeAsync(SnapshotReportMethods.ListSources, "{}", CancellationToken.None)).Tiles;
        Assert.Equal(3, tiles.Count);

        var snapshot = SnapshotReportJson.Deserialize<SnapshotResult>(await plugin.InvokeAsync(SnapshotReportMethods.Snapshot,
            SnapshotReportJson.Serialize(new SnapshotRequest { DeviceId = multi.Id, Camera = 2 }), CancellationToken.None));
        Assert.Null(snapshot.Error);
        Assert.Equal((960, 720), (snapshot.Width, snapshot.Height));
        Assert.Equal("GET axis-cgi/jpg/image.cgi?camera=2&resolution=960x720", factory.Cameras[multi.Id].Requests[^1]);

        var request = new ReportRequest { Site = "Plant 7", Technician = "Max", Date = new DateOnly(2026, 10, 7), Items = [.. tiles.Select(t => new ReportItem { DeviceId = t.Device.DeviceId, Camera = t.Camera })] };
        var status = SnapshotReportJson.Deserialize<ReportJobStatus>(await plugin.InvokeAsync(SnapshotReportMethods.GenerateReport, SnapshotReportJson.Serialize(request), CancellationToken.None));
        Assert.Equal(3, status.Total);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (status.State == ReportJobStates.Running && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
            status = SnapshotReportJson.Deserialize<ReportJobStatus>(await plugin.InvokeAsync(SnapshotReportMethods.ReportStatus, SnapshotReportJson.Serialize(new ReportJobRequest { JobId = status.JobId }), CancellationToken.None));
        }

        Assert.Equal(ReportJobStates.Done, status.State);
        Assert.Equal((3, 0, 3), (status.Done, status.Failed, status.Pages));

        using var pdf = new MemoryStream();
        long offset = 0;
        ReportChunk chunk;
        do
        {
            chunk = SnapshotReportJson.Deserialize<ReportChunk>(await plugin.InvokeAsync(SnapshotReportMethods.ReadReport,
                SnapshotReportJson.Serialize(new ReadReportRequest { JobId = status.JobId, Offset = offset }), CancellationToken.None));
            var bytes = Convert.FromBase64String(chunk.DataBase64);
            pdf.Write(bytes);
            offset += bytes.Length;
        }
        while (!chunk.Eof);

        Assert.Equal(status.Size, pdf.Length);
        var inspector = PdfInspector.Open(pdf.ToArray());
        Assert.Equal(3, inspector.PageCount);
        Assert.Contains("Plant 7", inspector.PageText(0), StringComparison.Ordinal);
        Assert.Equal(3, inspector.Images().Count);
        Assert.All(factory.Cameras.Values.SelectMany(c => c.Requests), r => Assert.StartsWith("GET axis-cgi/jpg/image.cgi?", r, StringComparison.Ordinal));

        await plugin.InvokeAsync(SnapshotReportMethods.DeleteReport, SnapshotReportJson.Serialize(new ReportJobRequest { JobId = status.JobId }), CancellationToken.None);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => plugin.InvokeAsync(SnapshotReportMethods.ReportStatus, SnapshotReportJson.Serialize(new ReportJobRequest { JobId = status.JobId }), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => plugin.InvokeAsync(SnapshotReportMethods.GenerateReport, "{}", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => plugin.InvokeAsync("nope", null, CancellationToken.None));
    }

    private static DeviceFacts Facts(string address, string model, string firmware, string status, DateTime? certNotAfter, string? trust) => new()
    {
        DeviceId = Guid.NewGuid(),
        Address = address,
        Model = model,
        Serial = "B8A44F631339",
        Firmware = firmware,
        Status = status,
        CertNotAfterUtc = certNotAfter,
        CertTrust = trust,
    };

    private static SnapshotTile Tile(DeviceFacts device, int camera, string? label) => new()
    {
        Device = device,
        Camera = camera,
        SourceLabel = label,
        SourceCount = label is null ? 1 : 2,
        Title = label is null ? device.Address : $"{device.Address} - {label}",
    };

    private static CapturedSnapshot Ok(byte[] jpeg, int width, int height) => new(jpeg, width, height, Now, null);

    private sealed class FakeCoreContext(IDeviceRepository devices, IVapixClientFactory vapix) : ICorePluginContext
    {
        public IDeviceRepository Devices { get; } = devices;
        public IVapixClientFactory Vapix { get; } = vapix;
        public ITaskRunner Tasks => throw new NotSupportedException();
        public IPluginSettings Settings => throw new NotSupportedException();
        public ILogger Logger { get; } = NullLogger.Instance;
    }
}
