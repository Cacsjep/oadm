using System.Net;
using System.Net.Sockets;

using Oadm.Sdk.Devices;

namespace Oadm.Plugins.SystemReport.Tests;

/// <summary>One device's server report: modes, fallbacks, refusals, errors, streaming to disk and limits.</summary>
public sealed class DownloaderTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly FakeVapixFactory _vapix = new();

    public DownloaderTests() => Directory.CreateDirectory(_folder.Path);

    public void Dispose() => _folder.Dispose();

    private string File1 => Path.Combine(_folder.Path, "1.zip");

    [Fact]
    public async Task A_camera_sends_the_report_with_its_picture()
    {
        var device = new FakeDevice();
        var camera = _vapix.Add(device.Id);

        var result = await new ServerReportDownloader(_vapix).DownloadAsync(device, File1, CancellationToken.None);

        Assert.True(result.IsOk, result.Error);
        Assert.Equal(ServerReportRequests.ModeZipWithImage, result.Mode);
        Assert.Equal(["GET axis-cgi/serverreport.cgi?mode=zip_with_image"], camera.Requests);
        Assert.Equal(new FileInfo(File1).Length, result.Bytes);
        using var zip = System.IO.Compression.ZipFile.OpenRead(File1);
        Assert.Contains(zip.Entries, e => e.Name == "serverreport_image.jpg");
    }

    [Fact]
    public async Task A_device_without_video_sends_the_plain_report()
    {
        var device = new FakeDevice { Category = DeviceCategory.Speaker, Model = "AXIS C1310-E" };
        var camera = _vapix.Add(device.Id);

        var result = await new ServerReportDownloader(_vapix).DownloadAsync(device, File1, CancellationToken.None);

        Assert.True(result.IsOk, result.Error);
        Assert.Equal(ServerReportRequests.ModeZip, result.Mode);
        Assert.Equal(["GET axis-cgi/serverreport.cgi?mode=zip"], camera.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.OK)] // 200 with an error text instead of a ZIP
    public async Task Without_the_picture_mode_the_plain_report_is_taken(HttpStatusCode status)
    {
        var device = new FakeDevice();
        var camera = _vapix.Add(device.Id);
        camera.Handler = (request, _) => Task.FromResult(request.RequestUri!.OriginalString.EndsWith("zip_with_image", StringComparison.Ordinal)
            ? FakeCamera.Text(status, "# Error: mode not supported")
            : FakeCamera.Zip(FakeCamera.Report(withImage: false)));

        var result = await new ServerReportDownloader(_vapix).DownloadAsync(device, File1, CancellationToken.None);

        Assert.True(result.IsOk, result.Error);
        Assert.Equal(ServerReportRequests.ModeZip, result.Mode);
        Assert.Equal(2, camera.Requests.Count);
        Assert.True(File.Exists(File1));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "Unauthorized - HTTP 401")]
    [InlineData(HttpStatusCode.Forbidden, "Forbidden - HTTP 403 (the server report needs an administrator account)")]
    public async Task Rejected_credentials_are_not_retried(HttpStatusCode status, string expected)
    {
        var device = new FakeDevice();
        var camera = _vapix.Add(device.Id);
        camera.Handler = (_, _) => Task.FromResult(FakeCamera.Text(status, "<html><title>401 Unauthorized</title></html>", "text/html"));

        var result = await new ServerReportDownloader(_vapix).DownloadAsync(device, File1, CancellationToken.None);

        Assert.Equal(expected, result.Error);
        Assert.Single(camera.Requests);
        Assert.False(File.Exists(File1));
    }

    [Fact]
    public async Task Both_modes_failing_report_the_last_error_and_leave_no_file()
    {
        var device = new FakeDevice();
        var camera = _vapix.Add(device.Id);
        camera.Handler = (_, _) => Task.FromResult(FakeCamera.Text(HttpStatusCode.OK, "<html><head><title>Server busy</title></head></html>", "text/html"));

        var result = await new ServerReportDownloader(_vapix).DownloadAsync(device, File1, CancellationToken.None);

        Assert.Equal("The device did not send a ZIP file: Server busy", result.Error);
        Assert.Equal(2, camera.Requests.Count);
        Assert.False(File.Exists(File1));
    }

    [Theory]
    [InlineData(DeviceStatus.CredentialsRequired, "Credentials required - the device rejects the stored credentials")]
    [InlineData(DeviceStatus.PasswordNotSet, "Password not set - the device is in factory default")]
    [InlineData(DeviceStatus.CertificateChanged, "Certificate changed - accept the new certificate first")]
    public async Task Refused_statuses_send_nothing(DeviceStatus status, string expected)
    {
        var device = new FakeDevice { Status = status };
        var camera = _vapix.Add(device.Id);

        var result = await new ServerReportDownloader(_vapix).DownloadAsync(device, File1, CancellationToken.None);

        Assert.Equal(expected, result.Error);
        Assert.Empty(camera.Requests);
    }

    [Fact]
    public async Task A_device_that_does_not_answer_in_time_times_out()
    {
        var device = new FakeDevice();
        var camera = _vapix.Add(device.Id);
        camera.Delay = TimeSpan.FromSeconds(30);

        var result = await new ServerReportDownloader(_vapix, TimeSpan.FromMilliseconds(200)).DownloadAsync(device, File1, CancellationToken.None);

        Assert.Equal("Timeout after 0.2 s", result.Error);
        Assert.False(File.Exists(File1));
    }

    [Fact]
    public async Task An_unreachable_device_says_so()
    {
        var device = new FakeDevice();
        var camera = _vapix.Add(device.Id);
        camera.Handler = (_, _) => throw new HttpRequestException("Connection refused", new SocketException((int)SocketError.ConnectionRefused));

        var result = await new ServerReportDownloader(_vapix).DownloadAsync(device, File1, CancellationToken.None);

        Assert.StartsWith("Unreachable - ", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_report_larger_than_16_megabytes_is_streamed_to_disk()
    {
        var device = new FakeDevice();
        var camera = _vapix.Add(device.Id);
        camera.Padding = 20 * 1024 * 1024;

        var result = await new ServerReportDownloader(_vapix).DownloadAsync(device, File1, CancellationToken.None);

        Assert.True(result.IsOk, result.Error);
        Assert.True(result.Bytes > 20L * 1024 * 1024);
        Assert.Equal(result.Bytes, new FileInfo(File1).Length);
    }

    [Fact]
    public async Task A_report_over_the_limit_is_dropped()
    {
        var device = new FakeDevice();
        var camera = _vapix.Add(device.Id);
        camera.Padding = 2 * 1024 * 1024;

        var result = await new ServerReportDownloader(_vapix, maxBytes: 1024 * 1024).DownloadAsync(device, File1, CancellationToken.None);

        Assert.Equal("The report is larger than 1 MB and was not saved.", result.Error);
        Assert.False(File.Exists(File1));
    }

    [Fact]
    public void Error_texts_and_zip_detection()
    {
        Assert.True(ServerReportRequests.IsZip("PK\u0003\u0004"u8));
        Assert.True(ServerReportRequests.IsZip("PK\u0005\u0006"u8));
        Assert.False(ServerReportRequests.IsZip("<htm"u8));
        Assert.False(ServerReportRequests.IsZip("PK"u8));
        Assert.Equal("No server report API (serverreport.cgi) - HTTP 404", ServerReportRequests.HttpError(HttpStatusCode.NotFound, null, null));
        Assert.Equal("Error: busy - HTTP 503", ServerReportRequests.HttpError(HttpStatusCode.ServiceUnavailable, "text/plain", "Error: busy\nmore"));
        Assert.Equal("Request failed - HTTP 500", ServerReportRequests.HttpError(HttpStatusCode.InternalServerError, null, ""));
        Assert.Equal("The device is no longer managed", ServerReportRequests.ExceptionError(new KeyNotFoundException(), TimeSpan.FromSeconds(1)));
        Assert.Equal("Unauthorized - HTTP 401", ServerReportRequests.ExceptionError(new InvalidOperationException("serverreport.cgi: HTTP 401"), TimeSpan.FromSeconds(1)));
        Assert.Equal("axis-cgi/serverreport.cgi?mode=zip_with_image", ServerReportRequests.BuildUri(ServerReportRequests.ModeZipWithImage));
    }
}
