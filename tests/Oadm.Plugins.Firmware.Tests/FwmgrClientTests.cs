using System.Net;
using System.Text;

using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Firmware.Tests;

public sealed class FwmgrClientTests
{
    [Fact]
    public void Parses_recorded_status_of_P3265V_on_12_11_77()
    {
        var status = FwmgrClient.ParseStatusResponse(Fixture.Read("fwmgr-status-p3265v-12.11.77.json"));

        Assert.Equal("1.10", status.ApiVersion);
        Assert.Equal("12.11.77", status.ActiveFirmwareVersion);
        Assert.Equal("7454651131", status.ActiveFirmwarePart);
        Assert.Equal("crash", status.ResetSource);
        Assert.Null(status.InactiveFirmwareVersion);
        Assert.Null(status.IsCommitted);
        Assert.False(status.HasUncommittedUpgrade);
    }

    [Fact]
    public void Parses_documented_status_with_pending_rollback()
    {
        var status = FwmgrClient.ParseStatusResponse(
            """{"apiVersion":"1.0","context":"abc","method":"status","data":{"activeFirmwareVersion":"7.50.3","inactiveFirmwareVersion":"7.40.1.2","isCommited":false,"timeToRollback":48}}""");

        Assert.Equal("7.40.1.2", status.InactiveFirmwareVersion);
        Assert.False(status.IsCommitted);
        Assert.Equal(48, status.TimeToRollback);
        Assert.True(status.HasUncommittedUpgrade);
    }

    [Fact]
    public void Parses_documented_committed_status()
    {
        var status = FwmgrClient.ParseStatusResponse(
            """{"apiVersion":"1.0","context":"abc","method":"status","data":{"activeFirmwareVersion":"7.50.3","activeFirmwarePart":"1234568","inactiveFirmwareVersion":"7.40.1.2","isCommited":true,"lastUpgradeAt":"2018-08-12T11:55:12+01:00"}}""");

        Assert.True(status.IsCommitted);
        Assert.Equal("2018-08-12T11:55:12+01:00", status.LastUpgradeAt);
        Assert.False(status.HasUncommittedUpgrade);
    }

    [Fact]
    public void Recorded_error_response_becomes_FwmgrException()
    {
        var ex = Assert.Throws<FwmgrException>(() => FwmgrClient.ParseStatusResponse(Fixture.Read("fwmgr-error-405-p3265v.json")));

        Assert.Equal(405, ex.Code);
        Assert.Equal("Unknown method in request.", ex.DeviceMessage);
    }

    [Theory]
    [InlineData(409, "older firmware version without a factory default")]
    [InlineData(410, "revoked")]
    [InlineData(415, "not a valid AXIS OS image")]
    [InlineData(421, "does not match this device")]
    [InlineData(422, "signature")]
    [InlineData(423, "busy")]
    public void Upgrade_errors_have_readable_messages_and_mean_nothing_was_installed(int code, string text)
    {
        var ex = new FwmgrException("upgrade", code, "x");

        Assert.Contains(text, ex.Message, StringComparison.Ordinal);
        Assert.True(ex.NothingInstalled);
    }

    [Fact]
    public void Status_and_commit_requests_use_api_version_1_0()
    {
        Assert.Equal("""{"apiVersion":"1.0","context":"oadm","method":"status"}""", FwmgrClient.BuildJson("status", null));
        Assert.Equal(
            """{"apiVersion":"1.0","context":"oadm","method":"upgrade","params":{"factoryDefaultMode":"soft","autoCommit":"started","autoRollback":"never"}}""",
            FwmgrClient.BuildJson("upgrade", new FwmgrUpgradeOptions(FactoryDefaultMode.Soft, "started", "never")));
    }

    [Fact]
    public async Task Upgrade_request_is_multipart_with_json_part_then_file_part()
    {
        var image = Fixture.Image();
        using var content = new FirmwareStreamContent(_ => Task.FromResult<Stream>(new MemoryStream(image)), image.Length);
        using var request = FwmgrClient.BuildUpgradeRequest(content, "C:\\fw\\P3265-V_12_11_77.bin", new FwmgrUpgradeOptions(FactoryDefaultMode.None, "never", "30"));

        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("axis-cgi/firmwaremanagement.cgi", request.RequestUri!.OriginalString);
        Assert.True(request.Headers.ExpectContinue);
        var contentType = request.Content!.Headers.ContentType!.ToString();
        Assert.StartsWith("multipart/form-data; boundary=", contentType, StringComparison.Ordinal);
        Assert.DoesNotContain("\"", contentType, StringComparison.Ordinal);

        var parts = await Multipart.ParseAsync(request.Content, CancellationToken.None);
        Assert.Equal(2, parts.Count);
        Assert.Equal("json", parts[0].Name);
        Assert.Equal("application/json", parts[0].ContentType);
        Assert.Contains("\"method\":\"upgrade\"", Encoding.UTF8.GetString(parts[0].Body), StringComparison.Ordinal);
        Assert.Equal("file", parts[1].Name);
        Assert.Equal("P3265-V_12_11_77.bin", parts[1].FileName);
        Assert.Equal("application/octet-stream", parts[1].ContentType);
        Assert.Contains("Content-Disposition: form-data; name=\"file\"; filename=\"P3265-V_12_11_77.bin\"", parts[1].RawHeaders, StringComparison.Ordinal);
        Assert.Equal(image, parts[1].Body);

        // Content-Length is known up front (no chunked transfer to the device).
        Assert.NotNull(request.Content.Headers.ContentLength);
    }

    [Fact]
    public async Task Upgrade_through_HttpClient_sends_the_body_and_reads_the_version()
    {
        var image = Fixture.Image(3 * 1024 * 1024);
        var handler = new CapturingHandler("""{"apiVersion":"1.10","context":"oadm","method":"upgrade","data":{"firmwareVersion":"12.11.77"}}""");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://10.0.0.48/") };
        var vapix = new HttpVapix(http);
        using var content = new FirmwareStreamContent(_ => Task.FromResult<Stream>(new MemoryStream(image)), image.Length);

        var version = await new FwmgrClient(vapix).UpgradeAsync(content, "P3265-V_12_11_77.bin", new FwmgrUpgradeOptions(FactoryDefaultMode.None, "never", "30"), TimeSpan.FromMinutes(20), CancellationToken.None);

        Assert.Equal("12.11.77", version);
        Assert.Equal(new Uri("https://10.0.0.48/axis-cgi/firmwaremanagement.cgi"), handler.Uri);
        Assert.Equal(image.Length, content.BytesSent);
        Assert.True(handler.BodyLength > image.Length);
        Assert.Equal(TimeSpan.FromMinutes(20), handler.RequestedTimeout);
    }

    [Fact]
    public async Task Upgrade_error_answer_throws_with_code()
    {
        var handler = new CapturingHandler("""{"apiVersion":"1.10","method":"upgrade","error":{"code":422,"message":"Upgrade failed due to image missing a mandatory digital signature."}}""");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://10.0.0.48/") };
        using var content = new FirmwareStreamContent(_ => Task.FromResult<Stream>(new MemoryStream(new byte[10])), 10);

        var ex = await Assert.ThrowsAsync<FwmgrException>(() =>
            new FwmgrClient(new HttpVapix(http)).UpgradeAsync(content, "a.bin", new FwmgrUpgradeOptions(FactoryDefaultMode.None, "never", "30"), TimeSpan.FromMinutes(1), CancellationToken.None));

        Assert.Equal(422, ex.Code);
        Assert.Equal("upgrade", ex.Method);
    }

    [Fact]
    public async Task Unauthorized_answer_is_reported_as_credentials_problem()
    {
        var handler = new CapturingHandler("", HttpStatusCode.Unauthorized);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://10.0.0.48/") };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new FwmgrClient(new HttpVapix(http)).GetStatusAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Streaming_never_holds_more_than_one_chunk_ahead()
    {
        const int size = 8 * 1024 * 1024;
        var source = new TrackingSource(size);
        var sink = new TrackingSink(source);
        var reports = new List<long>();
        using var content = new FirmwareStreamContent(_ => Task.FromResult<Stream>(source), size, reports.Add);

        await content.CopyToAsync(sink);

        Assert.Equal(size, sink.Written);
        Assert.True(source.MaxAhead <= FirmwareStreamContent.ChunkSize, $"read ahead {source.MaxAhead} bytes");
        Assert.Equal(size, reports[^1]);
        Assert.True(reports.Count > 50);
        Assert.True(source.Disposed);
    }

    [Fact]
    public async Task Content_is_reopened_for_a_second_send()
    {
        var opens = 0;
        var data = Fixture.Image();
        using var content = new FirmwareStreamContent(_ =>
        {
            opens++;
            return Task.FromResult<Stream>(new MemoryStream(data));
        }, data.Length);

        using var first = new MemoryStream();
        await content.CopyToAsync(first);
        using var second = new MemoryStream();
        await content.CopyToAsync(second);

        Assert.Equal(2, opens);
        Assert.Equal(data, second.ToArray());
    }

    [Fact]
    public async Task File_that_changed_size_fails_the_upload()
    {
        using var content = new FirmwareStreamContent(_ => Task.FromResult<Stream>(new MemoryStream(new byte[100])), 200);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => content.CopyToAsync(new MemoryStream()));
        Assert.IsType<IOException>(ex.InnerException);
    }

    private sealed class CapturingHandler(string json, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public Uri? Uri { get; private set; }
        public long BodyLength { get; private set; }
        public TimeSpan? RequestedTimeout { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri = request.RequestUri;
            if (request.Options.TryGetValue(VapixRequestOptions.Timeout, out var timeout))
            {
                RequestedTimeout = timeout;
            }

            if (request.Content is not null)
            {
                var counter = new CountingStream();
                await request.Content.CopyToAsync(counter, cancellationToken);
                BodyLength = counter.Length;
            }

            return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class HttpVapix(HttpClient http) : Oadm.Sdk.Vapix.IVapixClient
    {
        public Uri BaseAddress => http.BaseAddress!;
        public Task<Oadm.Sdk.Vapix.BasicDeviceInfo> GetBasicDeviceInfoAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<string, string>> ListParametersAsync(IEnumerable<string> groups, CancellationToken ct) => throw new NotSupportedException();
        public Task RestartAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<Oadm.Sdk.Vapix.DeviceApi>> GetApiListAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => http.SendAsync(request, ct);
    }

    private sealed class CountingStream : Stream
    {
        private long _length;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _length;
        public override long Position { get => _length; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => _length += count;
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _length += buffer.Length;
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Lazily generated source that tracks how far reads run ahead of the sink.</summary>
    private sealed class TrackingSource(long size) : Stream
    {
        public long ReadBytes { get; private set; }
        public long Written { get; set; }
        public long MaxAhead { get; private set; }
        public bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => size;
        public override long Position { get => ReadBytes; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = (int)Math.Min(count, size - ReadBytes);
            buffer.AsSpan(offset, n).Fill(0x5A);
            ReadBytes += n;
            MaxAhead = Math.Max(MaxAhead, ReadBytes - Written);
            return n;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var n = (int)Math.Min(buffer.Length, size - ReadBytes);
            buffer.Span[..n].Fill(0x5A);
            ReadBytes += n;
            MaxAhead = Math.Max(MaxAhead, ReadBytes - Written);
            return ValueTask.FromResult(n);
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class TrackingSink(TrackingSource source) : Stream
    {
        public long Written { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => Written;
        public override long Position { get => Written; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            Written += count;
            source.Written = Written;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Written += buffer.Length;
            source.Written = Written;
            return ValueTask.CompletedTask;
        }
    }
}
