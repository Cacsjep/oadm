using System.Net;
using System.Text;

using Microsoft.Extensions.Logging;

using Oadm.Core.Discovery;
using Oadm.Core.Vapix;
using Oadm.Sdk.Devices;

using static Oadm.Core.Tests.Discovery.VapixDeviceProbeTests;

namespace Oadm.Core.Tests.Discovery;

/// <summary>
/// Firmware without basicdeviceinfo (AXIS OS before 8.x, the A1001 line): the probes, the anonymous Axis check and the
/// device information fall back to param.cgi. The answers are recorded from an AXIS Q2901-E (6.50.5.18) and an AXIS A1001
/// (1.65.6): basicdeviceinfo, systemready and apidiscovery 404, param.cgi 401 with the realm AXIS_&lt;serial&gt;.
/// </summary>
public class LegacyFirmwareProbeTests
{
    private static readonly IPAddress Address = IPAddress.Parse("10.0.0.60");
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private const string Realm = "Digest realm=\"AXIS_ACCC8E263B09\", nonce=\"abc\", algorithm=MD5, qop=\"auth\"";

    private static HttpResponseMessage NotFound() => new(HttpStatusCode.NotFound);

    private static HttpResponseMessage Unauthorized(string challenge)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Unauthorized);
        response.Headers.TryAddWithoutValidation("WWW-Authenticate", challenge);
        return response;
    }

    private static HttpResponseMessage Text(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "text/plain") };

    /// <summary>The legacy device: HTTP only, basicdeviceinfo 404, param.cgi as <paramref name="param"/> answers.</summary>
    private static FakeHandler Device(Func<HttpResponseMessage> param) => new(req => req.RequestUri!.Scheme == "https"
        ? throw new HttpRequestException("refused")
        : req.RequestUri.AbsolutePath == "/axis-cgi/param.cgi" ? param() : NotFound());

    [Theory]
    [InlineData("param-deviceinfo-q2901e.txt", "ACCC8E263B09", "Q2901-E", "Temperature Alarm Camera", "6.50.5.18", "mips")]
    [InlineData("param-deviceinfo-a1001.txt", "ACCC8E2477F4", "A1001", "Network Door Controller", "1.65.6", "mips")]
    public void TheRecordedParametersBecomeTheDeviceInformation(string fixture, string serial, string model, string type, string version, string architecture)
    {
        var info = VapixParsers.ParseDeviceInfoParameters(Fixture.Text(fixture));

        Assert.Equal(serial, info.SerialNumber);
        Assert.Equal(model, info.ProdNbr);
        Assert.Equal(type, info.ProdType);
        Assert.Equal(version, info.Version);
        Assert.Equal(architecture, info.Architecture);
        Assert.Equal("AXIS " + model, info.ProdShortName);
        Assert.Null(info.Soc);
    }

    [Fact]
    public void ParametersWithoutASerialNumberAreNoDeviceInformation() =>
        Assert.Throws<VapixException>(() => VapixParsers.ParseDeviceInfoParameters("root.Brand.Brand=AXIS\nroot.Brand.ProdNbr=Q2901-E\n"));

    [Fact]
    public async Task DiscoveryFindsTheSerialInTheParameterRealm()
    {
        var handler = Device(() => Unauthorized(Realm));
        using var probe = new VapixDeviceProbe(handler);

        var result = await probe.ProbeAsync(Address, Timeout, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("ACCC8E263B09", result.Serial);
        Assert.Equal("http", result.Scheme);
        Assert.Equal(DiscoveredDeviceStatus.CredentialsRequired, result.Status);
        Assert.Equal(
            ["http://10.0.0.60/axis-cgi/basicdeviceinfo.cgi", "http://10.0.0.60/axis-cgi/param.cgi?action=list&group=root.Brand,root.Properties.Firmware,root.Properties.System"],
            handler.Requests.Where(r => r.Uri.StartsWith("http:", StringComparison.Ordinal)).Select(r => r.Uri));
        Assert.All(handler.Requests, r => Assert.False(r.HadAuthorization));
    }

    [Fact]
    public async Task DiscoveryWithAnonymousParametersHasTheModel()
    {
        using var probe = new VapixDeviceProbe(Device(() => Text(Fixture.Text("param-deviceinfo-a1001.txt"))));

        var result = await probe.ProbeAsync(Address, Timeout, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("ACCC8E2477F4", result.Serial);
        Assert.Equal("A1001", result.Model);
        Assert.Equal("1.65.6", result.FirmwareVersion);
        Assert.Equal("Network Door Controller", result.ProductType);
        Assert.Equal(DiscoveredDeviceStatus.AnonymousAccess, result.Status);
    }

    [Theory]
    [InlineData(404, null)]
    [InlineData(401, "Digest realm=\"device\", nonce=\"abc\"")]
    [InlineData(200, null)]
    public async Task AnythingElseIsNoAxisDeviceAndIsLogged(int status, string? challenge)
    {
        var logger = new ListLogger();
        var handler = Device(() => status switch
        {
            401 => Unauthorized(challenge!),
            200 => Text("<html>a router</html>"),
            _ => NotFound(),
        });
        using var probe = new VapixDeviceProbe(handler, logger: logger);

        Assert.Null(await probe.ProbeAsync(Address, Timeout, CancellationToken.None));

        var line = Assert.Single(logger.Lines, l => l.Level == LogLevel.Information);
        Assert.Contains("10.0.0.60", line.Text, StringComparison.Ordinal);
        Assert.Contains($"http param.cgi {status}", line.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddManuallyFindsTheSerialInTheParameterRealm()
    {
        var probe = new VapixProbe(Timeout, (_, _) => Device(() => Unauthorized(Realm))); // one handler per scheme

        var result = await probe.ProbeAsync("10.0.0.60", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("ACCC8E263B09", result.Serial);
        Assert.Equal(DeviceStatus.CredentialsRequired, result.Status);
        Assert.True(result.AuthenticationRequired);
    }

    [Fact]
    public async Task TheClientReadsTheParametersAndRemembersIt()
    {
        var handler = new FakeHandler(req => req.RequestUri!.AbsolutePath == "/axis-cgi/param.cgi"
            ? Text(Fixture.Text("param-deviceinfo-q2901e.txt"))
            : NotFound());
        using var client = new VapixClient(new Uri("http://10.0.0.60/"), handler, null, Timeout);

        var first = await client.GetBasicDeviceInfoAsync(CancellationToken.None);
        var second = await client.GetBasicDeviceInfoAsync(CancellationToken.None);

        Assert.Equal("Q2901-E", first.ProdNbr);
        Assert.Equal(first, second);
        Assert.True(client.UsesDeviceInfoParameters);
        Assert.Equal(3, handler.Requests.Count); // basicdeviceinfo once, then the parameters twice

        var unrestricted = await client.GetUnrestrictedPropertiesAsync(CancellationToken.None);
        Assert.Equal("ACCC8E263B09", unrestricted["SerialNumber"]);
        Assert.Equal("Q2901-E", unrestricted["ProdNbr"]);
    }

    [Fact]
    public async Task TheAnonymousCheckGetsTheRealmFromTheParameters()
    {
        var handler = Device(() => Unauthorized(Realm));
        using var client = new VapixClient(new Uri("http://10.0.0.60/"), handler, null, Timeout);

        var refused = await Assert.ThrowsAsync<VapixAuthenticationException>(() => client.GetUnrestrictedPropertiesAsync(CancellationToken.None));

        Assert.Equal("ACCC8E263B09", refused.RealmSerial);
    }

    private sealed class ListLogger : ILogger<VapixDeviceProbe>
    {
        public List<(LogLevel Level, string Text)> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Lines)
            {
                Lines.Add((logLevel, formatter(state, exception)));
            }
        }
    }
}
