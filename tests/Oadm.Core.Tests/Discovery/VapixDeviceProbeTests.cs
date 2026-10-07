using System.Net;
using System.Text;

using Oadm.Core.Discovery;

namespace Oadm.Core.Tests.Discovery;

public class VapixDeviceProbeTests
{
    private static readonly IPAddress Address = IPAddress.Parse("10.0.0.48");
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Https401BasicRealmIsAxisDeviceWithModelFromUnrestrictedProperties()
    {
        var handler = new FakeHandler(req => (req.RequestUri!.Scheme, req.RequestUri.AbsolutePath, Body(req)) switch
        {
            ("https", "/axis-cgi/basicdeviceinfo.cgi", var b) when b.Contains("getAllProperties", StringComparison.Ordinal)
                => Unauthorized("Basic realm=\"AXIS_B8A44F631339\""),
            ("https", "/axis-cgi/basicdeviceinfo.cgi", var b) when b.Contains("getAllUnrestrictedProperties", StringComparison.Ordinal)
                => Json(Fixture.Text("basicdeviceinfo-unrestricted-p3265v.json")),
            ("https", "/axis-cgi/systemready.cgi", _) => Json(Fixture.Text("systemready-configured-p3265v.json")),
            _ => throw new InvalidOperationException($"unexpected {req.RequestUri}"),
        });
        using var probe = new VapixDeviceProbe(handler);

        var r = await probe.ProbeAsync(Address, Timeout, CancellationToken.None);

        Assert.NotNull(r);
        Assert.Equal("B8A44F631339", r.Serial);
        Assert.Equal("https", r.Scheme);
        Assert.Equal("P3265-V", r.Model);
        Assert.Equal("AXIS P3265-V Dome Camera", r.ProductName);
        Assert.Equal("12.11.77", r.FirmwareVersion);
        Assert.Equal(DiscoveredDeviceStatus.CredentialsRequired, r.Status);
        Assert.False(r.AnonymousFullAccess);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task FallsBackToHttpWhenHttpsIsRefused()
    {
        var handler = new FakeHandler(req => req.RequestUri!.Scheme == "https"
            ? throw new HttpRequestException("refused")
            : req.RequestUri.AbsolutePath == "/axis-cgi/basicdeviceinfo.cgi" && Body(req).Contains("getAllProperties", StringComparison.Ordinal)
                ? Unauthorized("Digest realm=\"AXIS_B8A44F3B34BB\", nonce=\"abc\", algorithm=MD5, qop=\"auth\"")
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        using var probe = new VapixDeviceProbe(handler);

        var r = await probe.ProbeAsync(Address, Timeout, CancellationToken.None);

        Assert.NotNull(r);
        Assert.Equal("http", r.Scheme);
        Assert.Equal("B8A44F3B34BB", r.Serial);
        Assert.Null(r.Model);
        Assert.Equal(DiscoveredDeviceStatus.CredentialsRequired, r.Status);
        Assert.Equal("https://10.0.0.48/axis-cgi/basicdeviceinfo.cgi", handler.Requests[0].Uri);
        Assert.Equal("http://10.0.0.48/axis-cgi/basicdeviceinfo.cgi", handler.Requests[1].Uri);
    }

    [Fact]
    public async Task NeedsetupYesMeansPasswordNotSet()
    {
        var handler = new FakeHandler(req => req.RequestUri!.AbsolutePath == "/axis-cgi/systemready.cgi"
            ? Json("""{"apiVersion":"1.5","method":"systemready","data":{"systemready":"yes","needsetup":"yes"}}""")
            : Body(req).Contains("getAllProperties", StringComparison.Ordinal)
                ? Unauthorized("Digest realm=\"AXIS_ACCC8E0A1B2C\"")
                : Json(Fixture.Text("basicdeviceinfo-unrestricted-p3265v.json")));
        using var probe = new VapixDeviceProbe(handler);

        var r = await probe.ProbeAsync(Address, Timeout, CancellationToken.None);

        Assert.Equal(DiscoveredDeviceStatus.PasswordNotSet, r!.Status);
    }

    [Fact]
    public async Task Anonymous200ParsesPropertiesDirectly()
    {
        var handler = new FakeHandler(req => req.RequestUri!.AbsolutePath == "/axis-cgi/systemready.cgi"
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : Json(Fixture.Text("basicdeviceinfo-unrestricted-p3265v.json")));
        using var probe = new VapixDeviceProbe(handler);

        var r = await probe.ProbeAsync(Address, Timeout, CancellationToken.None);

        Assert.NotNull(r);
        Assert.Equal("B8A44F631339", r.Serial);
        Assert.Equal("P3265-V", r.Model);
        Assert.Equal(DiscoveredDeviceStatus.AnonymousAccess, r.Status);
        Assert.True(r.AnonymousFullAccess);
        Assert.Equal(2, handler.Requests.Count); // no unrestricted call needed
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, null)]
    [InlineData(HttpStatusCode.Unauthorized, "Digest realm=\"router\"")]
    [InlineData(HttpStatusCode.Unauthorized, null)]
    [InlineData(HttpStatusCode.OK, null)]
    public async Task NonAxisAnswersAreNotDevices(HttpStatusCode status, string? challenge)
    {
        var handler = new FakeHandler(_ =>
        {
            var resp = new HttpResponseMessage(status) { Content = new StringContent("<html>hello</html>") };
            if (challenge is not null)
            {
                resp.Headers.TryAddWithoutValidation("WWW-Authenticate", challenge);
            }

            return resp;
        });
        using var probe = new VapixDeviceProbe(handler);

        Assert.Null(await probe.ProbeAsync(Address, Timeout, CancellationToken.None));
        Assert.Equal(2, handler.Requests.Count); // https and http tried
    }

    [Fact]
    public async Task TimeoutOnBothSchemesIsNotADevice()
    {
        var handler = new FakeHandler(async (_, ct) =>
        {
            await Task.Delay(System.Threading.Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var probe = new VapixDeviceProbe(handler);

        Assert.Null(await probe.ProbeAsync(Address, TimeSpan.FromMilliseconds(50), CancellationToken.None));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task CallerCancellationPropagates()
    {
        var handler = new FakeHandler(async (_, ct) =>
        {
            await Task.Delay(System.Threading.Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var probe = new VapixDeviceProbe(handler);
        using var cts = new CancellationTokenSource(50);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe.ProbeAsync(Address, Timeout, cts.Token));
    }

    [Fact]
    public async Task NeverSendsCredentialsAndUsesExactRequestBody()
    {
        var handler = new FakeHandler(req => Body(req).Contains("getAllProperties", StringComparison.Ordinal)
            ? Unauthorized("Digest realm=\"AXIS_ACCC8E0A1B2C\"")
            : new HttpResponseMessage(HttpStatusCode.NotFound));
        using var probe = new VapixDeviceProbe(handler);

        await probe.ProbeAsync(Address, Timeout, CancellationToken.None);

        Assert.All(handler.Requests, r => Assert.False(r.HadAuthorization));
        Assert.All(handler.Requests, r => Assert.Equal(HttpMethod.Post, r.Method));
        Assert.Equal("""{"apiVersion":"1.0","method":"getAllProperties"}""", handler.Requests[0].Body);
    }

    [Theory]
    [InlineData("systemready-configured-p3265v.json", false)]
    [InlineData("systemready-configured-m3206lve.json", false)]
    public void ParseNeedSetupReadsCapturedResponses(string fixture, bool expected)
        => Assert.Equal(expected, VapixDeviceProbe.ParseNeedSetup(Fixture.Text(fixture)));

    [Theory]
    [InlineData("""{"data":{"needsetup":"yes"}}""", true)]
    [InlineData("""{"data":{"systemready":"yes"}}""", null)]
    [InlineData("""{"error":{"code":4002}}""", null)]
    [InlineData("not json", null)]
    [InlineData("[]", null)]
    public void ParseNeedSetupEdgeCases(string json, bool? expected)
        => Assert.Equal(expected, VapixDeviceProbe.ParseNeedSetup(json));

    [Theory]
    [InlineData("""{"apiVersion":"1.0","error":{"code":2002,"message":"x"}}""")]
    [InlineData("""{"data":{}}""")]
    [InlineData("<html/>")]
    public void ParsePropertiesRejectsNonPropertyBodies(string json)
        => Assert.Null(VapixDeviceProbe.ParseProperties(json));

    private static string Body(HttpRequestMessage req)
        => req.Content is null ? string.Empty : req.Content.ReadAsStringAsync().GetAwaiter().GetResult();

    private static HttpResponseMessage Unauthorized(string challenge)
    {
        var resp = new HttpResponseMessage(HttpStatusCode.Unauthorized);
        resp.Headers.TryAddWithoutValidation("WWW-Authenticate", challenge);
        return resp;
    }

    private static HttpResponseMessage Json(string json)
        => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    internal sealed record RecordedRequest(HttpMethod Method, string Uri, string Body, bool HadAuthorization);

    internal sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;

        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
            : this((r, _) => Task.FromResult(respond(r)))
        {
        }

        public FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) => _respond = respond;

        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (Requests)
            {
                Requests.Add(new RecordedRequest(request.Method, request.RequestUri!.ToString(), body, request.Headers.Authorization is not null));
            }

            return await _respond(request, cancellationToken);
        }
    }
}
