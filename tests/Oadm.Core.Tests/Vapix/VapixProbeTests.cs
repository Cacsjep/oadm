using System.Net;

using Oadm.Core.Vapix;
using Oadm.Sdk.Devices;

namespace Oadm.Core.Tests.Vapix;

public class VapixProbeTests
{
    private const string UnrestrictedJson = """
        {"apiVersion": "1.3", "data": {"propertyList": {"ProdNbr": "P3265-V", "Version": "12.11.77", "SerialNumber": "B8A44F631339"}}}
        """;

    private const string NeedSetupYes = """{"apiVersion":"1.5","method":"systemready","data":{"systemready":"yes","needsetup":"yes"}}""";

    /// <summary>Routes requests the way the recorded P3265-V answers them anonymously.</summary>
    private static HttpResponseMessage RecordedDevice(HttpRequestMessage request, string? body, string headerFile, string systemReady)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path.EndsWith("systemready.cgi", StringComparison.Ordinal))
        {
            return Fixtures.Json(systemReady);
        }

        if (path.EndsWith("basicdeviceinfo.cgi", StringComparison.Ordinal))
        {
            return body!.Contains("getAllUnrestrictedProperties", StringComparison.Ordinal)
                ? Fixtures.Json(UnrestrictedJson)
                : Fixtures.Unauthorized(headerFile);
        }

        return Fixtures.Text("not found", HttpStatusCode.NotFound);
    }

    private static VapixProbe Probe(Func<Uri, Func<HttpRequestMessage, string?, HttpResponseMessage>> byBase)
    {
        return new VapixProbe(TimeSpan.FromSeconds(5), (uri, _) => new FakeHttpMessageHandler(byBase(uri)));
    }

    [Fact]
    public async Task HttpsDeviceRequiringCredentials()
    {
        var probe = Probe(_ => (req, body) => RecordedDevice(req, body, "401-https-headers.txt", Fixtures.Read("systemready.json")));

        var result = await probe.ProbeAsync("10.0.0.48", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("https", result.Scheme);
        Assert.Equal("B8A44F631339", result.Serial);
        Assert.Equal("P3265-V", result.Model);
        Assert.Equal("12.11.77", result.FirmwareVersion);
        Assert.True(result.AuthenticationRequired);
        Assert.False(result.IsFactoryDefault);
        Assert.Equal(DeviceStatus.CredentialsRequired, result.Status);
        Assert.Equal(new Uri("https://10.0.0.48/"), result.BaseAddress);
    }

    [Fact]
    public async Task FallsBackToHttpWhenHttpsIsClosed()
    {
        var probe = Probe(uri => uri.Scheme == "https"
            ? (_, _) => throw new HttpRequestException(HttpRequestError.ConnectionError, "Connection refused")
            : (req, body) => RecordedDevice(req, body, "401-http-headers.txt", Fixtures.Read("systemready.json")));

        var result = await probe.ProbeAsync("10.0.0.48", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("http", result.Scheme);
        Assert.Equal("B8A44F631339", result.Serial);
        Assert.Null(result.CertificateFingerprint);
    }

    [Fact]
    public async Task FallsBackToHttpOnTimeout()
    {
        var probe = Probe(uri => uri.Scheme == "https"
            ? (_, _) => throw new TaskCanceledException("timeout", new TimeoutException())
            : (req, body) => RecordedDevice(req, body, "401-http-headers.txt", Fixtures.Read("systemready.json")));

        var result = await probe.ProbeAsync("10.0.0.48", CancellationToken.None);

        Assert.Equal("http", result?.Scheme);
    }

    [Fact]
    public async Task NothingAnsweringReturnsNull()
    {
        var probe = Probe(_ => (_, _) => throw new HttpRequestException(HttpRequestError.ConnectionError, "Connection refused"));

        Assert.Null(await probe.ProbeAsync("10.0.0.99", CancellationToken.None));
    }

    [Fact]
    public async Task NonAxisRealmReturnsNull()
    {
        var probe = Probe(_ => (_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Unauthorized);
            response.Headers.TryAddWithoutValidation("WWW-Authenticate", "Basic realm=\"Router\"");
            return response;
        });

        Assert.Null(await probe.ProbeAsync("10.0.0.1", CancellationToken.None));
    }

    [Fact]
    public async Task NotFoundReturnsNull()
    {
        var probe = Probe(_ => (_, _) => Fixtures.Text("nope", HttpStatusCode.NotFound));

        Assert.Null(await probe.ProbeAsync("10.0.0.1", CancellationToken.None));
    }

    [Fact]
    public async Task FactoryDefaultDeviceIsPasswordNotSet()
    {
        var probe = Probe(_ => (req, body) => RecordedDevice(req, body, "401-https-headers.txt", NeedSetupYes));

        var result = await probe.ProbeAsync("10.0.0.48", CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result.IsFactoryDefault);
        Assert.Equal(DeviceStatus.PasswordNotSet, result.Status);
    }

    [Fact]
    public async Task AnonymousAccessParsesPropertiesDirectly()
    {
        var probe = Probe(_ => (req, _) => req.RequestUri!.AbsolutePath.EndsWith("systemready.cgi", StringComparison.Ordinal)
            ? Fixtures.Text("not found", HttpStatusCode.NotFound)
            : Fixtures.Json(Fixtures.Read("basicdeviceinfo-getAllProperties.json")));

        var result = await probe.ProbeAsync("10.0.0.48", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(DeviceStatus.Ok, result.Status);
        Assert.False(result.AuthenticationRequired);
        Assert.Equal("P3265-V", result.Model);
    }

    [Fact]
    public async Task CallerCancellationPropagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var probe = Probe(_ => (_, _) => Fixtures.Text(string.Empty));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe.ProbeAsync("10.0.0.48", cts.Token));
    }
}
