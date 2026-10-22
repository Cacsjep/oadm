using System.Security.Cryptography.X509Certificates;
using System.Text;

using Oadm.Plugins.Est.DeviceId;
using Oadm.Plugins.Est.Protocol;

namespace Oadm.Plugins.Est.Tests;

/// <summary>The EST bodies (PKCS#7 certs-only, PKCS#10 in base64), the small HTTP/1.1 and the Axis device ID check.</summary>
public sealed class ProtocolTests
{
    [Fact]
    public void Certs_only_keeps_the_order_and_any_pkcs7_reader_reads_it()
    {
        using var root = TestPki.Root("Root");
        using var intermediate = TestPki.Intermediate(root, "Intermediate");
        var der = EstMessages.CertsOnly([intermediate.RawData, root.RawData]);

        var read = EstMessages.ReadCertsOnly(der);
        Assert.Equal([intermediate.RawData, root.RawData], read);

        // The platform's PKCS#7 reader (what other EST clients use) reads it too.
#pragma warning disable SYSLIB0057 // The PKCS#7 import is the point of this check.
        var collection = new X509Certificate2Collection();
        collection.Import(der);
#pragma warning restore SYSLIB0057
        Assert.Equal(2, collection.Count);

        var base64 = Encoding.ASCII.GetString(EstMessages.Base64(der));
        Assert.All(base64.Split("\r\n", StringSplitOptions.RemoveEmptyEntries), line => Assert.True(line.Length <= 76));
    }

    [Fact]
    public void A_request_is_read_from_base64_with_line_breaks_or_pem()
    {
        var (csr, key) = TestPki.Request();
        using (key)
        {
            var wrapped = Convert.ToBase64String(csr, Base64FormattingOptions.InsertLineBreaks);
            Assert.Equal(csr, EstMessages.ReadRequest(Encoding.ASCII.GetBytes(wrapped + "\n")));
            var pem = "-----BEGIN CERTIFICATE REQUEST-----\n" + wrapped + "\n-----END CERTIFICATE REQUEST-----\n";
            Assert.Equal(csr, EstMessages.ReadRequest(Encoding.ASCII.GetBytes(pem)));
            Assert.Throws<FormatException>(() => EstMessages.ReadRequest("not base64!"u8.ToArray()));
            Assert.Throws<FormatException>(() => EstMessages.ReadRequest([]));
        }
    }

    [Fact]
    public async Task Http_reads_requests_one_after_another_with_length_chunks_and_continue()
    {
        var text = "POST /.well-known/est/https/simpleenroll HTTP/1.1\r\nHost: x\r\nExpect: 100-continue\r\nContent-Length: 4\r\n\r\nabcd"
            + "POST /a?x=1 HTTP/1.1\r\nTransfer-Encoding: chunked\r\n\r\n2\r\nab\r\n3;x=y\r\ncde\r\n0\r\n\r\n"
            + "GET /b HTTP/1.1\r\nConnection: close\r\n\r\n";
        using var stream = new DuplexStream(Encoding.ASCII.GetBytes(text));
        var reader = EstHttp.Reader(stream);

        var first = await reader.ReadAsync(CancellationToken.None);
        Assert.Equal(("POST", "/.well-known/est/https/simpleenroll", "abcd"), (first!.Method, first.Path, Encoding.ASCII.GetString(first.Body)));
        Assert.Equal("HTTP/1.1 100 Continue\r\n\r\n", Encoding.ASCII.GetString(stream.Written.ToArray()));
        var second = await reader.ReadAsync(CancellationToken.None);
        Assert.Equal(("/a", "abcde", false), (second!.Path, Encoding.ASCII.GetString(second.Body), second.CloseAfter));
        var third = await reader.ReadAsync(CancellationToken.None);
        Assert.Equal(("GET", "/b", true), (third!.Method, third.Path, third.CloseAfter));
        Assert.Null(await reader.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Http_refuses_what_it_cannot_read()
    {
        await Assert.ThrowsAsync<HttpFormatException>(() => Read("HELLO\r\n\r\n"));
        await Assert.ThrowsAsync<HttpFormatException>(() => Read("POST / HTTP/1.1\r\nContent-Length: 999999\r\n\r\n"));
        await Assert.ThrowsAsync<HttpFormatException>(() => Read("GET / HTTP/1.1\r\nX: " + new string('a', 20_000) + "\r\n\r\n"));
        await Assert.ThrowsAsync<HttpFormatException>(() => Read("POST / HTTP/1.1\r\nContent-Length: 10\r\n\r\nabc"));
    }

    [Fact]
    public async Task Http_writes_status_length_and_close()
    {
        using var stream = new DuplexStream([]);
        await EstHttp.WriteAsync(stream, HttpResponse.Text(404, "Not found.") with { Headers = [new("Retry-After", "600")] }, close: true, CancellationToken.None);

        var written = Encoding.ASCII.GetString(stream.Written.ToArray());
        Assert.StartsWith("HTTP/1.1 404 Not Found\r\n", written, StringComparison.Ordinal);
        Assert.Contains("Content-Length: 12\r\n", written, StringComparison.Ordinal);
        Assert.Contains("Retry-After: 600\r\n", written, StringComparison.Ordinal);
        Assert.EndsWith("Connection: close\r\n\r\nNot found.\r\n", written, StringComparison.Ordinal);
    }

    [Fact]
    public void A_device_id_names_the_camera_by_its_serial_only_under_the_axis_roots()
    {
        using var root = TestPki.Root("Device ID Root");
        using var intermediate = TestPki.Intermediate(root, "Device ID Intermediate");
        using var other = TestPki.Root("Other Root");
        using var otherIntermediate = TestPki.Intermediate(other, "Other Intermediate");
        using var device = TestPki.DeviceId(intermediate, "b8a44f631339");
        using var foreign = TestPki.DeviceId(otherIntermediate, "B8A44F631339");
        var ids = new AxisDeviceId([Public(root)]);

        Assert.Equal("B8A44F631339", ids.Verify(device, [Public(intermediate)], DateTimeOffset.UtcNow));
        Assert.Null(ids.Verify(device, [], DateTimeOffset.UtcNow)); // without the intermediate there is no chain
        Assert.Null(ids.Verify(foreign, [Public(otherIntermediate)], DateTimeOffset.UtcNow));
        Assert.Null(ids.Verify(device, [Public(intermediate)], DateTimeOffset.UtcNow.AddYears(40))); // expired

        // The intermediates shipped with the plugin also complete the chain.
        Assert.Equal("B8A44F631339", new AxisDeviceId([Public(root), Public(intermediate)]).Verify(device, [], DateTimeOffset.UtcNow));
    }

    [Fact]
    public void The_plugin_ships_both_axis_device_id_roots_and_their_intermediates()
    {
        var ids = new AxisDeviceId();

        Assert.Equal(["Axis device ID Root CA ECC", "Axis device ID Root CA RSA"], ids.Roots.Select(r => r.GetNameInfo(X509NameType.SimpleName, false)).Order(StringComparer.Ordinal));
        Assert.All(ids.Roots, r => Assert.True(r.NotAfter.Year >= 2060));
    }

    [Theory]
    [InlineData("device-id-ecc-p256-12.11.pem")]
    [InlineData("device-id-rsa-2048-12.11.pem")]
    public void A_real_camera_device_id_passes_with_the_shipped_axis_certificates(string file)
    {
        // The camera sends only its own certificate here: the shipped intermediates complete the chain.
        using var certificate = X509Certificate2.CreateFromPem(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", file)));

        Assert.Equal("B8A44F631339", new AxisDeviceId().Verify(certificate, [], DateTimeOffset.UtcNow));
        Assert.Equal("B8A44F631339", AxisDeviceId.SerialOf(certificate));
    }

    [Theory]
    [InlineData("B8A44F631339", "B8A44F631339")]
    [InlineData("axis-b8a44f631339-ecc", "B8A44F631339")]
    [InlineData("B8A44F6313", null)]
    [InlineData("B8A44F6313391", null)]
    [InlineData(null, null)]
    public void Serial_numbers_are_twelve_hex_digits(string? value, string? expected) => Assert.Equal(expected, AxisDeviceId.Normalize(value));

    private static X509Certificate2 Public(X509Certificate2 certificate) => X509CertificateLoader.LoadCertificate(certificate.RawData);

    private static async Task Read(string text)
    {
        using var stream = new DuplexStream(Encoding.ASCII.GetBytes(text));
        await EstHttp.Reader(stream).ReadAsync(CancellationToken.None);
    }

    /// <summary>Reads from a fixed input, records what is written.</summary>
    private sealed class DuplexStream(byte[] input) : Stream
    {
        private readonly MemoryStream _input = new(input);

        public MemoryStream Written { get; } = new();

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => _input.Read(buffer, offset, Math.Min(count, 7)); // small reads on purpose

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => Written.Write(buffer, offset, count);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _input.Dispose();
                Written.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
