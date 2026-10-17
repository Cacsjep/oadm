using System.Net;
using System.Text;

using Google.Protobuf;

using Grpc.Core;

using Oadm.Core.Auth;
using Oadm.Server.DeviceWeb;
using Oadm.Server.Tests.Support;

using Proto = Oadm.Contracts.V1;
using SdkDeviceStatus = Oadm.Sdk.Devices.DeviceStatus;

namespace Oadm.Server.Tests;

/// <summary>DeviceWebService: the web interface of a device through the server, with the stored login (Web View).</summary>
public sealed class DeviceWebTests
{
    private static Proto.DeviceWebService.DeviceWebServiceClient Client(TestServerHost host) => new(host.Invoker);

    [Fact]
    public async Task OpenRefusesDevicesWhoseStatusBlocksTheLoginAndAuditsTheOthers()
    {
        var network = new FakeAxisNetwork();
        network.Add("10.9.0.1", FakeSerials.Make(1), "pw");
        await using var host = await TestServerHost.StartAsync(network);
        var ok = await DeviceServiceTests.AddDeviceAsync(host, "10.9.0.1", 1);
        var changed = await DeviceServiceTests.AddDeviceAsync(host, "10.9.0.2", 2, status: SdkDeviceStatus.CertificateChanged);
        var credentials = await DeviceServiceTests.AddDeviceAsync(host, "10.9.0.3", 3, status: SdkDeviceStatus.CredentialsRequired);

        var reply = await Client(host).OpenAsync(new Proto.OpenDeviceWebRequest { DeviceId = ok.Id.ToString() });
        Assert.True(reply.Ok);
        Assert.False((await Client(host).OpenAsync(new Proto.OpenDeviceWebRequest { DeviceId = changed.Id.ToString() })).Ok);
        var refused = await Client(host).OpenAsync(new Proto.OpenDeviceWebRequest { DeviceId = credentials.Id.ToString() });
        Assert.False(refused.Ok);
        Assert.Equal(Oadm.Sdk.Devices.DeviceMessages.CredentialsRequired, refused.Message);
        var missing = await Assert.ThrowsAsync<RpcException>(async () =>
            await Client(host).OpenAsync(new Proto.OpenDeviceWebRequest { DeviceId = Guid.NewGuid().ToString() }));
        Assert.Equal(StatusCode.NotFound, missing.StatusCode);

        var audit = (await host.Audit.ListAsync(new Proto.ListAuditRequest())).Entries;
        var opened = Assert.Single(audit, e => e.Action == AuditActions.WebInterfaceOpened);
        Assert.Equal("10.9.0.1", opened.Target);
    }

    [Fact]
    public async Task ARequestReachesTheDeviceWithTheStoredLoginAndThePageGetsTheAnswer()
    {
        var network = new FakeAxisNetwork();
        var camera = network.Add("10.9.0.1", FakeSerials.Make(1), "pw");
        HttpRequestMessage? seen = null;
        var seenBody = string.Empty;
        var authorized = false;
        camera.WebPage = (request, body, login) =>
        {
            seen = request;
            seenBody = body;
            authorized = login;
            var response = new HttpResponseMessage(HttpStatusCode.Found) { Content = new StringContent("<p>moved</p>", Encoding.UTF8, "text/html") };
            response.Headers.Location = new Uri("http://10.9.0.1/camera/index.html#/live");
            response.Headers.Add("Set-Cookie", "session=1; Path=/");
            return response;
        };
        await using var host = await TestServerHost.StartAsync(network);
        var device = await DeviceServiceTests.AddDeviceAsync(host, "10.9.0.1", 1);

        var head = Request(device.Id, "POST", "/camera/login?x=1", hasBody: true);
        head.Headers.Add(new Proto.WebHeader { Name = "Content-Type", Value = "application/x-www-form-urlencoded" });
        head.Headers.Add(new Proto.WebHeader { Name = "Origin", Value = "/" });
        head.Headers.Add(new Proto.WebHeader { Name = "Referer", Value = "/camera/index.html" });
        head.Headers.Add(new Proto.WebHeader { Name = "Authorization", Value = "Basic Zm9vOmJhcg==" });
        var frames = await ExchangeAsync(host, head, Encoding.UTF8.GetBytes("a=1&b=2"));

        Assert.NotNull(seen);
        Assert.True(authorized); // the server's stored login, not the page's header
        Assert.Equal("a=1&b=2", seenBody);
        Assert.Equal("/camera/login", seen!.RequestUri!.AbsolutePath);
        Assert.Equal("?x=1", seen.RequestUri.Query);
        Assert.Equal("http://10.9.0.1", seen.Headers.GetValues("Origin").Single());
        Assert.Equal("http://10.9.0.1/camera/index.html", seen.Headers.Referrer!.ToString());
        Assert.False(seen.Headers.Contains("Authorization"));

        var response = frames[0].Response;
        Assert.Equal(302, response.Status);
        Assert.Equal("/camera/index.html#/live", response.Headers.Single(h => h.Name == "Location").Value);
        Assert.Contains(response.Headers, h => h.Name == "Set-Cookie" && h.Value == "session=1; Path=/");
        Assert.Equal("<p>moved</p>", Encoding.UTF8.GetString(Body(frames)));
        Assert.Equal(Proto.DeviceWebFrame.KindOneofCase.End, frames[^1].KindCase);
    }

    [Fact]
    public async Task AnUnreachableDeviceAnswersWithAnErrorPageAndPathsMustStayOnTheDevice()
    {
        var network = new FakeAxisNetwork();
        network.Add("10.9.0.1", FakeSerials.Make(1), "pw").Offline = true;
        await using var host = await TestServerHost.StartAsync(network);
        var device = await DeviceServiceTests.AddDeviceAsync(host, "10.9.0.1", 1);

        var frames = await ExchangeAsync(host, Request(device.Id, "GET", "/"));
        Assert.Equal(502, frames[0].Response.Status);
        Assert.Contains("Unreachable", Encoding.UTF8.GetString(Body(frames)), StringComparison.Ordinal);

        foreach (var path in new[] { "//evil.example.com/x", "http://evil.example.com/", "/a\\b", "relative" })
        {
            var ex = await Assert.ThrowsAsync<RpcException>(() => ExchangeAsync(host, Request(device.Id, "GET", path)));
            Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
        }
    }

    [Fact]
    public async Task ARefusedDeviceGetsNoRequestAndRequestsWithoutOpenAreAuditedOnce()
    {
        var network = new FakeAxisNetwork();
        var camera = network.Add("10.9.0.1", FakeSerials.Make(1), "pw");
        var requests = 0;
        camera.WebPage = (_, _, _) =>
        {
            Interlocked.Increment(ref requests);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") };
        };
        await using var host = await TestServerHost.StartAsync(network);
        var refused = await DeviceServiceTests.AddDeviceAsync(host, "10.9.0.2", 2, status: SdkDeviceStatus.CredentialsRequired);
        var ex = await Assert.ThrowsAsync<RpcException>(() => ExchangeAsync(host, Request(refused.Id, "GET", "/")));
        Assert.Equal(StatusCode.FailedPrecondition, ex.StatusCode);
        Assert.Equal(Oadm.Sdk.Devices.DeviceMessages.CredentialsRequired, ex.Status.Detail);

        // A client that never called Open is still in the audit log, once for its requests.
        var device = await DeviceServiceTests.AddDeviceAsync(host, "10.9.0.1", 1);
        await ExchangeAsync(host, Request(device.Id, "GET", "/"));
        await ExchangeAsync(host, Request(device.Id, "GET", "/x"));
        Assert.Equal(2, requests);
        var audit = (await host.Audit.ListAsync(new Proto.ListAuditRequest())).Entries;
        Assert.Single(audit, e => e.Action == AuditActions.WebInterfaceOpened && e.Target.StartsWith("10.9.0.1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ADeviceWithAChangedCertificateIsRefused()
    {
        await using var host = await TestServerHost.StartAsync();
        var device = await DeviceServiceTests.AddDeviceAsync(host, "10.9.0.1", 1, status: SdkDeviceStatus.CertificateChanged);

        var ex = await Assert.ThrowsAsync<RpcException>(() => ExchangeAsync(host, Request(device.Id, "GET", "/")));
        Assert.Equal(StatusCode.FailedPrecondition, ex.StatusCode);
    }

    [Theory]
    [InlineData("Origin", "/", "http://10.0.0.48")]
    [InlineData("Origin", "https://other.example.com", "https://other.example.com")]
    [InlineData("Referer", "/camera/index.html", "http://10.0.0.48/camera/index.html")]
    [InlineData("Referer", "//other/x", "//other/x")]
    [InlineData("Accept", "/", "/")]
    public void PageHeadersNameTheDevice(string name, string value, string expected) =>
        Assert.Equal(expected, DeviceWebGrpcService.DeviceValue(new Uri("http://10.0.0.48/"), name, value));

    [Theory]
    [InlineData("http://10.0.0.48/camera/index.html?a=1#x", "/camera/index.html?a=1#x")]
    [InlineData("https://10.0.0.48/x", "/x")] // the device on another scheme: still the relay
    [InlineData("http://10.0.0.48:8080/y", "/y")]
    [InlineData("http://other/x", "http://other/x")]
    [InlineData("/already/relative", "/already/relative")]
    public void ALocationOnTheDeviceBecomesAPath(string location, string expected) =>
        Assert.Equal(expected, DeviceWebGrpcService.PageValue(new Uri("http://10.0.0.48/"), "Location", location));

    private static Proto.DeviceWebRequestHead Request(Guid device, string method, string path, bool hasBody = false) =>
        new() { DeviceId = device.ToString(), Method = method, PathAndQuery = path, HasBody = hasBody };

    private static async Task<List<Proto.DeviceWebFrame>> ExchangeAsync(TestServerHost host, Proto.DeviceWebRequestHead head, byte[]? body = null)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var call = Client(host).Exchange(cancellationToken: timeout.Token);
        await call.RequestStream.WriteAsync(new Proto.DeviceWebFrame { Request = head });
        if (head.HasBody)
        {
            await call.RequestStream.WriteAsync(new Proto.DeviceWebFrame { Body = ByteString.CopyFrom(body ?? []) });
            await call.RequestStream.WriteAsync(new Proto.DeviceWebFrame { End = true });
        }

        await call.RequestStream.CompleteAsync();
        var frames = new List<Proto.DeviceWebFrame>();
        while (await call.ResponseStream.MoveNext(timeout.Token))
        {
            frames.Add(call.ResponseStream.Current);
        }

        return frames;
    }

    private static byte[] Body(List<Proto.DeviceWebFrame> frames) =>
        [.. frames.Where(f => f.KindCase == Proto.DeviceWebFrame.KindOneofCase.Body).SelectMany(f => f.Body.ToByteArray())];
}
