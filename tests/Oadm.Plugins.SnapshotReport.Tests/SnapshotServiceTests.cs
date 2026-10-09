using System.Net;
using System.Net.Sockets;
using System.Text;

using Oadm.Sdk.Devices;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.SnapshotReport.Tests;

public sealed class SnapshotServiceTests
{
    private static readonly VideoResolution[] Hd = [new(1920, 1080), new(1280, 720), new(640, 360)];

    [Fact]
    public void Single_source_is_one_tile_named_like_the_device()
    {
        var device = new FakeDevice();
        var tile = Assert.Single(SnapshotService.Expand(device, [new VideoSource(1, "Camera", 0, Hd)]));
        Assert.Equal("10.0.0.48", tile.Title);
        Assert.Null(tile.SourceLabel);
        Assert.Equal(1, tile.Camera);
        Assert.Equal(1, tile.SourceCount);
        Assert.Equal("AXIS P3265-V", tile.Device.Model);
    }

    [Fact]
    public void Multisensor_sources_are_labeled_sensor_n_and_named_views_keep_their_name()
    {
        var device = new FakeDevice { Address = "10.0.0.32", Model = "AXIS P3727-PLE" };
        var tiles = SnapshotService.Expand(device,
        [
            new VideoSource(1, "Camera 1", 0, Hd),
            new VideoSource(2, "Camera 2", 1, Hd),
            new VideoSource(3, "", 2, Hd),
            new VideoSource(4, "Camera 4", 3, Hd),
            new VideoSource(5, "Quad view", 0, Hd),
        ]);
        Assert.Equal(
            ["10.0.0.32 - Sensor 1", "10.0.0.32 - Sensor 2", "10.0.0.32 - Sensor 3", "10.0.0.32 - Sensor 4", "10.0.0.32 - Quad view"],
            tiles.Select(t => t.Title).ToArray());
        Assert.Equal([1, 2, 3, 4, 5], tiles.Select(t => t.Camera).ToArray());
        Assert.All(tiles, t => Assert.Equal(5, t.SourceCount));
    }

    [Fact]
    public void View_areas_use_the_view_area_names_and_encoder_inputs_are_channels()
    {
        var camera = SnapshotService.Expand(new FakeDevice(), [new VideoSource(1, "View Area 1", 0, Hd), new VideoSource(2, "View Area 2", 0, Hd)]);
        Assert.Equal(["View Area 1", "View Area 2"], camera.Select(t => t.SourceLabel!).ToArray());

        var encoder = SnapshotService.Expand(new FakeDevice { Category = DeviceCategory.Encoder }, [new VideoSource(1, "Camera 1", 0, Hd), new VideoSource(2, "Camera 2", 1, Hd)]);
        Assert.Equal(["Channel 1", "Channel 2"], encoder.Select(t => t.SourceLabel!).ToArray());
    }

    [Fact]
    public async Task List_sources_skips_non_video_devices_and_orders_by_address()
    {
        var camera = new FakeDevice { Address = "10.0.0.48" };
        var multi = new FakeDevice { Address = "10.0.0.9", Serial = "ACCC8E77E3A1", Model = "AXIS P3727-PLE" };
        var speaker = new FakeDevice { Address = "10.0.0.2", Category = DeviceCategory.Speaker };
        var door = new FakeDevice { Address = "10.0.0.3", Category = DeviceCategory.DoorController };
        var factory = new FakeVapixFactory();
        factory.Add(camera.Id);
        factory.Add(multi.Id).Sources = [new VideoSource(1, "Camera 1", 0, Hd), new VideoSource(2, "Camera 2", 1, Hd)];
        using var service = new SnapshotService(new FakeRepository(camera, multi, speaker, door), factory);

        var result = await service.ListSourcesAsync(new ListSourcesRequest(), CancellationToken.None);

        Assert.Equal(["10.0.0.9 - Sensor 1", "10.0.0.9 - Sensor 2", "10.0.0.48"], result.Tiles.Select(t => t.Title).ToArray());
        Assert.DoesNotContain(result.Tiles, t => t.Device.DeviceId == speaker.Id || t.Device.DeviceId == door.Id);

        var selected = await service.ListSourcesAsync(new ListSourcesRequest { DeviceIds = [camera.Id, speaker.Id] }, CancellationToken.None);
        Assert.Equal(camera.Id, Assert.Single(selected.Tiles).Device.DeviceId);
    }

    [Fact]
    public async Task List_sources_gives_one_error_tile_per_failing_device()
    {
        var locked = new FakeDevice { Address = "10.0.0.23", Status = DeviceStatus.CredentialsRequired };
        var rejecting = new FakeDevice { Address = "10.0.0.24" };
        var factory = new FakeVapixFactory();
        factory.Add(locked.Id);
        factory.Add(rejecting.Id).SourcesError = new InvalidOperationException("param.cgi: HTTP 401");
        using var service = new SnapshotService(new FakeRepository(locked, rejecting), factory);

        var tiles = (await service.ListSourcesAsync(new ListSourcesRequest(), CancellationToken.None)).Tiles;

        Assert.Equal("Credentials required - the device rejects the stored credentials", tiles[0].Error);
        Assert.Equal("Unauthorized - HTTP 401 (check the credentials)", tiles[1].Error);
        Assert.Empty(factory.Cameras[locked.Id].Requests);
    }

    [Fact]
    public void Request_uses_image_cgi_with_camera_and_resolution_and_its_own_timeout()
    {
        Assert.Equal("axis-cgi/jpg/image.cgi?camera=2&resolution=1280x720", SnapshotRequests.BuildUri(2, new VideoResolution(1280, 720)));
        using var request = SnapshotRequests.Build(1, new VideoResolution(640, 360), TimeSpan.FromSeconds(10));
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.False(request.RequestUri!.IsAbsoluteUri);
        Assert.True(request.Options.TryGetValue(VapixRequestOptions.Timeout, out var timeout));
        Assert.Equal(TimeSpan.FromSeconds(10), timeout);
    }

    [Theory]
    [InlineData(1280, 720, "1280x720")]
    [InlineData(1920, 1080, "1920x1080")]
    [InlineData(1000, 1000, "640x360")]
    [InlineData(320, 180, "640x360")]
    public void Resolution_is_the_largest_of_the_source_that_fits(int maxWidth, int maxHeight, string expected)
    {
        var source = new VideoSource(1, "Camera", 0, Hd);
        Assert.Equal(expected, SnapshotRequests.ChooseResolution(source, maxWidth, maxHeight).ToString());
        Assert.Equal("800x450", SnapshotRequests.ChooseResolution(new VideoSource(1, "Camera", 0, []), 800, 450).ToString());
    }

    [Fact]
    public async Task Snapshot_returns_the_jpeg_with_its_size_and_capture_time()
    {
        var device = new FakeDevice();
        var factory = new FakeVapixFactory();
        var camera = factory.Add(device.Id);
        using var service = new SnapshotService(new FakeRepository(device), factory);

        var snapshot = await service.TakeAsync(device.Id, 1, 1280, 720, CancellationToken.None);

        Assert.True(snapshot.IsOk, snapshot.Error);
        Assert.Equal((1280, 720), (snapshot.Width, snapshot.Height));
        Assert.NotNull(snapshot.CapturedUtc);
        Assert.Equal("GET axis-cgi/jpg/image.cgi?camera=1&resolution=1280x720", Assert.Single(camera.Requests));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "text/html", "<html><title>401 Unauthorized</title></html>", "Unauthorized - HTTP 401 (check the credentials)")]
    [InlineData(HttpStatusCode.Forbidden, null, "", "Forbidden - HTTP 403 (administrator rights are required)")]
    // Recorded from 10.0.0.48 (AXIS OS 12.11) for camera=9.
    [InlineData(HttpStatusCode.BadRequest, "text/html", "<HTML><HEAD><TITLE>400 Bad Request, The request had bad syntax or was inherently impossible to be satisfied.</TITLE></HEAD>\n<BODY><H1>400 Bad Request</H1></BODY></HTML>", "Bad Request - HTTP 400")]
    [InlineData(HttpStatusCode.OK, "text/plain", "Error: Camera is disabled\r\n", "Error: Camera is disabled")]
    [InlineData(HttpStatusCode.ServiceUnavailable, null, null, "Service Unavailable - HTTP 503")]
    public async Task Http_failures_become_readable_errors(HttpStatusCode status, string? contentType, string? body, string expected)
    {
        var device = new FakeDevice();
        var factory = new FakeVapixFactory();
        factory.Add(device.Id).Handler = (_, _) =>
        {
            var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body ?? string.Empty)) };
            if (contentType is not null)
            {
                response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
            }

            return Task.FromResult(response);
        };
        using var service = new SnapshotService(new FakeRepository(device), factory);

        var snapshot = await service.TakeAsync(device.Id, 1, 1280, 720, CancellationToken.None);

        Assert.False(snapshot.IsOk);
        Assert.Equal(expected, snapshot.Error);
    }

    [Fact]
    public async Task Slow_device_times_out_with_a_readable_error()
    {
        var device = new FakeDevice();
        var factory = new FakeVapixFactory();
        factory.Add(device.Id).Delay = TimeSpan.FromSeconds(30);
        using var service = new SnapshotService(new FakeRepository(device), factory, timeout: TimeSpan.FromMilliseconds(200));

        var snapshot = await service.TakeAsync(device.Id, 1, 1280, 720, CancellationToken.None);

        Assert.Equal("Timeout after 0.2 s", snapshot.Error);
        Assert.Equal("Timeout after 10 s", SnapshotRequests.ExceptionError(new TaskCanceledException("x", new TimeoutException()), TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void Exceptions_map_to_short_texts()
    {
        var timeout = TimeSpan.FromSeconds(10);
        Assert.StartsWith("Unreachable - ", SnapshotRequests.ExceptionError(new HttpRequestException("x", new SocketException((int)SocketError.ConnectionRefused)), timeout), StringComparison.Ordinal);
        Assert.Equal("Unauthorized - HTTP 401 (check the credentials)", SnapshotRequests.ExceptionError(new InvalidOperationException("basicdeviceinfo.cgi: HTTP 401"), timeout));
        Assert.Equal("The device was removed from OADM.", SnapshotRequests.ExceptionError(new KeyNotFoundException("x"), timeout));
        Assert.Equal("Unreachable - Name not resolved", SnapshotRequests.ExceptionError(new HttpRequestException("Name not resolved."), timeout));
    }

    [Fact]
    public async Task Status_and_category_are_checked_before_any_request()
    {
        var certificate = new FakeDevice { Status = DeviceStatus.CertificateChanged };
        var factoryDefault = new FakeDevice { Status = DeviceStatus.PasswordNotSet };
        var speaker = new FakeDevice { Category = DeviceCategory.Speaker };
        var factory = new FakeVapixFactory();
        foreach (var d in new[] { certificate, factoryDefault, speaker })
        {
            factory.Add(d.Id);
        }

        using var service = new SnapshotService(new FakeRepository(certificate, factoryDefault, speaker), factory);

        Assert.Equal("Certificate changed. Remove the device and add it again to trust the new certificate.", (await service.TakeAsync(certificate.Id, 1, 640, 360, CancellationToken.None)).Error);
        Assert.Equal("Password not set - the device is in factory default", (await service.TakeAsync(factoryDefault.Id, 1, 640, 360, CancellationToken.None)).Error);
        Assert.Equal("The device has no video", (await service.TakeAsync(speaker.Id, 1, 640, 360, CancellationToken.None)).Error);
        Assert.Equal("The device was removed from OADM.", (await service.TakeAsync(Guid.NewGuid(), 1, 640, 360, CancellationToken.None)).Error);
        Assert.Equal("The device has no video source 7", (await new SnapshotService(new FakeRepository(new FakeDevice { Id = certificate.Id }), factory).TakeAsync(certificate.Id, 7, 640, 360, CancellationToken.None)).Error);
        Assert.All(factory.Cameras.Values, c => Assert.Empty(c.Requests));
    }

    [Fact]
    [Trait("Category", "Timing")] // the peak is reached only while 80 ms requests overlap
    public async Task At_most_four_snapshots_run_at_the_same_time()
    {
        var device = new FakeDevice();
        var factory = new FakeVapixFactory();
        var camera = factory.Add(device.Id);
        camera.Delay = TimeSpan.FromMilliseconds(80);
        using var service = new SnapshotService(new FakeRepository(device), factory);

        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => service.TakeAsync(device.Id, 1, 640, 360, CancellationToken.None)));

        Assert.All(results, r => Assert.True(r.IsOk));
        Assert.Equal(4, camera.MaxConcurrent);
    }

    [Fact]
    public void Jpeg_size_is_read_from_the_sof_marker()
    {
        var jpeg = Oadm.Client.Api.FakeOadmApi.TestPicture(320, 240, "x", "y", DateTimeOffset.UtcNow, 3);
        Assert.True(SnapshotRequests.TryGetJpegSize(jpeg, out var w, out var h));
        Assert.Equal((320, 240), (w, h));
        Assert.False(SnapshotRequests.TryGetJpegSize(Encoding.ASCII.GetBytes("Error: no"), out _, out _));
    }
}
