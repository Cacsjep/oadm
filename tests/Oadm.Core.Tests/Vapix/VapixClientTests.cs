using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Oadm.Core.Vapix;

namespace Oadm.Core.Tests.Vapix;

public class VapixClientTests
{
    private static readonly Uri HttpsBase = new("https://10.0.0.48/");

    private const string NeedSetupYes = """{"apiVersion":"1.5","method":"systemready","data":{"systemready":"yes","needsetup":"yes"}}""";

    private static VapixClient Client(FakeHttpMessageHandler handler, Uri? baseAddress = null, CertificatePinning? pinning = null)
    {
        return new VapixClient(baseAddress ?? HttpsBase, handler, pinning);
    }

    [Fact]
    public async Task GetBasicDeviceInfoPostsGetAllProperties()
    {
        var handler = new FakeHttpMessageHandler((_, _) => Fixtures.Json(Fixtures.Read("basicdeviceinfo-getAllProperties.json")));
        using var client = Client(handler);

        var info = await client.GetBasicDeviceInfoAsync(CancellationToken.None);

        Assert.Equal("B8A44F631339", info.SerialNumber);
        var (method, uri, body) = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, method);
        Assert.Equal("https://10.0.0.48/axis-cgi/basicdeviceinfo.cgi", uri.ToString());
        Assert.Contains("\"getAllProperties\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetApiListToleratesTheNonStandardUtf8Charset()
    {
        // AXIS OS 12.11 answers apidiscovery with "application/json; charset=utf8".
        var handler = new FakeHttpMessageHandler((_, _) =>
        {
            var content = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(Fixtures.Read("apidiscovery-getApiList.json")));
            content.Headers.TryAddWithoutValidation("Content-Type", "application/json; charset=utf8");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        using var client = Client(handler);

        var apis = await client.GetApiListAsync(CancellationToken.None);

        Assert.Equal(72, apis.Count);
        var (method, uri, body) = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, method);
        Assert.Equal("https://10.0.0.48/axis-cgi/apidiscovery.cgi", uri.ToString());
        Assert.Contains("\"getApiList\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListParametersBuildsGroupList()
    {
        var handler = new FakeHttpMessageHandler((_, _) => Fixtures.Text(Fixtures.Read("param-list-networkinfo.txt")));
        using var client = Client(handler);

        var p = await client.ListParametersAsync(["Network.BootProto", "HTTPS"], CancellationToken.None);

        Assert.Equal("dhcp", p["Network.BootProto"]);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/axis-cgi/param.cgi?action=list&group=Network.BootProto,HTTPS", request.Uri.PathAndQuery);
    }

    [Fact]
    public async Task ListParametersRequiresAGroup()
    {
        using var client = Client(new FakeHttpMessageHandler((_, _) => Fixtures.Text(string.Empty)));
        await Assert.ThrowsAsync<ArgumentException>(() => client.ListParametersAsync([], CancellationToken.None));
    }

    [Fact]
    public async Task GetNetworkInfoRequestsTheFourParameters()
    {
        var handler = new FakeHttpMessageHandler((_, _) => Fixtures.Text(Fixtures.Read("param-list-networkinfo.txt")));
        using var client = Client(handler);

        var info = await client.GetNetworkInfoAsync(CancellationToken.None);

        Assert.Equal(new NetworkInfo(true, true, false, "AXIS P3265-V - B8A44F631339"), info);
        Assert.Equal(
            "/axis-cgi/param.cgi?action=list&group=Network.BootProto,Network.UPnP.FriendlyName,Network.Interface.I0.dot1x.Enabled,HTTPS.Enabled",
            handler.Requests[0].Uri.PathAndQuery);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task AuthFailuresBecomeVapixAuthenticationException(HttpStatusCode status)
    {
        using var client = Client(new FakeHttpMessageHandler((_, _) => Fixtures.Unauthorized("401-https-headers.txt").WithStatus(status)));

        var ex = await Assert.ThrowsAsync<VapixAuthenticationException>(() => client.GetBasicDeviceInfoAsync(CancellationToken.None));
        Assert.Equal(status, ex.StatusCode);
    }

    [Fact]
    public async Task ServerErrorBecomesVapixException()
    {
        using var client = Client(new FakeHttpMessageHandler((_, _) => Fixtures.Text("boom", HttpStatusCode.InternalServerError)));

        var ex = await Assert.ThrowsAsync<VapixException>(() => client.GetBasicDeviceInfoAsync(CancellationToken.None));
        Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
    }

    [Fact]
    public async Task RestartCallsRestartCgi()
    {
        var handler = new FakeHttpMessageHandler((_, _) => Fixtures.Text("Restarting..."));
        using var client = Client(handler);

        await client.RestartAsync(CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/axis-cgi/restart.cgi", request.Uri.AbsolutePath);
    }

    [Fact]
    public async Task SendAsyncResolvesRelativeUriAndReturnsRawResponse()
    {
        var handler = new FakeHttpMessageHandler((_, _) => Fixtures.Text("x", HttpStatusCode.NotFound));
        using var client = Client(handler);

        using var request = new HttpRequestMessage(HttpMethod.Get, "axis-cgi/whatever.cgi");
        using var response = await client.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("https://10.0.0.48/axis-cgi/whatever.cgi", handler.Requests[0].Uri.ToString());
    }

    [Fact]
    public async Task SetInitialRootPasswordPostsFormBodyNotUrl()
    {
        var handler = new FakeHttpMessageHandler((req, _) => req.RequestUri!.AbsolutePath.EndsWith("systemready.cgi", StringComparison.Ordinal)
            ? Fixtures.Json(NeedSetupYes)
            : Fixtures.Text("Created account root."));
        using var client = Client(handler);

        await client.SetInitialRootPasswordAsync("S3cret!pass", CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count);
        var (method, uri, body) = handler.Requests[1];
        Assert.Equal(HttpMethod.Post, method);
        Assert.Equal("/axis-cgi/pwdgrp.cgi", uri.PathAndQuery);
        Assert.Equal("action=add&user=root&pwd=S3cret%21pass&grp=root&sgrp=admin%3Aoperator%3Aviewer%3Aptz", body);
    }

    [Fact]
    public async Task SetInitialRootPasswordRefusesPlainHttpByDefault()
    {
        var handler = new FakeHttpMessageHandler((_, _) => Fixtures.Json(NeedSetupYes));
        using var client = Client(handler, new Uri("http://10.0.0.48/"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SetInitialRootPasswordAsync("pass", CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task SetInitialRootPasswordRefusesWhenDeviceAlreadySetUp()
    {
        var handler = new FakeHttpMessageHandler((_, _) => Fixtures.Json(Fixtures.Read("systemready.json")));
        using var client = Client(handler);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SetInitialRootPasswordAsync("pass", CancellationToken.None));
        Assert.Single(handler.Requests); // only systemready, never pwdgrp
    }

    [Fact]
    public async Task SetInitialRootPasswordReportsDeviceError()
    {
        var handler = new FakeHttpMessageHandler((req, _) => req.RequestUri!.AbsolutePath.EndsWith("systemready.cgi", StringComparison.Ordinal)
            ? Fixtures.Json(NeedSetupYes)
            : Fixtures.Text("# Error: bad password"));
        using var client = Client(handler);

        await Assert.ThrowsAsync<VapixException>(() => client.SetInitialRootPasswordAsync("pass", CancellationToken.None));
    }

    [Theory]
    [InlineData("")]
    [InlineData("tab\there")]
    [InlineData("umlaut-ä")]
    public void ValidatePasswordRejectsInvalid(string password)
    {
        Assert.Throws<ArgumentException>(() => VapixClient.ValidatePassword(password));
    }

    [Fact]
    public void ValidatePasswordRejectsTooLong()
    {
        Assert.Throws<ArgumentException>(() => VapixClient.ValidatePassword(new string('a', 65)));
        VapixClient.ValidatePassword(new string('a', 64));
        VapixClient.ValidatePassword(" !~");
    }

    [Theory]
    [InlineData("https", "10.0.0.48", "https://10.0.0.48/")]
    [InlineData("http", "cam.local:8080", "http://cam.local:8080/")]
    [InlineData("https", "fe80::1", "https://[fe80::1]/")]
    public void BuildBaseAddress(string scheme, string address, string expected)
    {
        Assert.Equal(expected, VapixClient.BuildBaseAddress(scheme, address).ToString());
    }

    [Fact]
    public void BuildBaseAddressRejectsOtherSchemes()
    {
        Assert.Throws<ArgumentException>(() => VapixClient.BuildBaseAddress("ftp", "10.0.0.48"));
    }

    [Fact]
    public void HandlerNeverOffersBasicOverHttp()
    {
        var credentials = new NetworkCredential("root", "pw");
        using var http = VapixClient.CreateHandler(new Uri("http://10.0.0.48/"), credentials, new CertificatePinning());
        using var https = VapixClient.CreateHandler(new Uri("https://10.0.0.48/"), credentials, new CertificatePinning());

        var httpCache = Assert.IsType<CredentialCache>(http.Credentials);
        var httpsCache = Assert.IsType<CredentialCache>(https.Credentials);
        var target = new Uri("http://10.0.0.48/axis-cgi/param.cgi");
        var secureTarget = new Uri("https://10.0.0.48/axis-cgi/param.cgi");

        Assert.Null(httpCache.GetCredential(target, "Basic"));
        Assert.NotNull(httpCache.GetCredential(target, "Digest"));
        Assert.NotNull(httpsCache.GetCredential(secureTarget, "Basic"));
        Assert.NotNull(httpsCache.GetCredential(secureTarget, "Digest"));
        Assert.False(http.AllowAutoRedirect);
    }

    [Fact]
    public void BasicOverHttpOnlyWhenTheAddPageAllowsItForAVerifiedAxisDevice()
    {
        var credentials = new NetworkCredential("root", "pw");
        using var http = VapixClient.CreateHandler(new Uri("http://10.0.0.48/"), credentials, new CertificatePinning(), allowBasicOverHttp: true);

        var cache = Assert.IsType<CredentialCache>(http.Credentials);
        var target = new Uri("http://10.0.0.48/axis-cgi/basicdeviceinfo.cgi");
        Assert.NotNull(cache.GetCredential(target, "Digest")); // preferred when the device offers it
        Assert.NotNull(cache.GetCredential(target, "Basic"));
        Assert.False(new VapixConnectionOptions { Address = "10.0.0.48" }.AllowBasicOverHttp);
    }

    [Fact]
    public async Task PinMismatchBecomesCertificateChangedException()
    {
        using var pinned = CertificatePinningTests.CreateCertificate("CN=pinned");
        using var presented = CertificatePinningTests.CreateCertificate("CN=other");
        var pinning = new CertificatePinning(CertificatePinning.ComputeFingerprint(pinned));

        // Simulates what HttpClientHandler does: run the validation callback, fail the request.
        var handler = new FakeHttpMessageHandler((_, _) =>
        {
            Assert.False(pinning.Validate(presented));
            throw new HttpRequestException("The SSL connection could not be established.");
        });
        using var client = Client(handler, pinning: pinning);

        var ex = await Assert.ThrowsAsync<CertificateChangedException>(() => client.GetBasicDeviceInfoAsync(CancellationToken.None));
        Assert.Equal(CertificatePinning.ComputeFingerprint(pinned), ex.ExpectedFingerprint);
        Assert.Equal(CertificatePinning.ComputeFingerprint(presented), ex.ActualFingerprint);
    }

    [Fact]
    public async Task TransportErrorWithoutMismatchIsNotCertificateChanged()
    {
        var handler = new FakeHttpMessageHandler((_, _) => throw new HttpRequestException(HttpRequestError.ConnectionError, "refused"));
        using var client = Client(handler, pinning: new CertificatePinning());

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetBasicDeviceInfoAsync(CancellationToken.None));
    }
}

internal static class ResponseExtensions
{
    public static HttpResponseMessage WithStatus(this HttpResponseMessage response, HttpStatusCode status)
    {
        response.StatusCode = status;
        return response;
    }
}

public class CertificatePinningTests
{
    internal static X509Certificate2 CreateCertificate(string subject)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    [Fact]
    public void TrustOnFirstUseAcceptsAndRecordsFingerprint()
    {
        using var cert = CreateCertificate("CN=camera");
        var pinning = new CertificatePinning();

        Assert.True(pinning.Validate(cert));
        Assert.Equal(CertificatePinning.ComputeFingerprint(cert), pinning.ObservedFingerprint);
        Assert.Null(pinning.MismatchFingerprint);
        Assert.Equal(64, pinning.ObservedFingerprint!.Length);
    }

    [Fact]
    public void PinnedFingerprintAcceptsSameCertificate()
    {
        using var cert = CreateCertificate("CN=camera");
        var fingerprint = CertificatePinning.ComputeFingerprint(cert);
        var colonLower = string.Join(':', Enumerable.Range(0, 32).Select(i => fingerprint.Substring(i * 2, 2))).ToLowerInvariant();

        var pinning = new CertificatePinning(colonLower);

        Assert.Equal(fingerprint, pinning.PinnedFingerprint);
        Assert.True(pinning.Validate(cert));
    }

    [Fact]
    public void PinnedFingerprintRejectsDifferentCertificate()
    {
        using var pinned = CreateCertificate("CN=camera");
        using var other = CreateCertificate("CN=camera");
        var pinning = new CertificatePinning(CertificatePinning.ComputeFingerprint(pinned));

        Assert.False(pinning.Validate(other));
        Assert.Equal(CertificatePinning.ComputeFingerprint(other), pinning.MismatchFingerprint);
        Assert.Equal(CertificatePinning.ComputeFingerprint(other), pinning.ObservedFingerprint);
    }

    [Fact]
    public void MissingCertificateIsRejected()
    {
        Assert.False(new CertificatePinning().Validate(null));
    }
}
