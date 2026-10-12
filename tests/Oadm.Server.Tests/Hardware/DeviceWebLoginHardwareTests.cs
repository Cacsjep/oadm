using System.Text;

using Google.Protobuf;

using Oadm.Core.Tests.Hardware;
using Oadm.Server.Tests.Support;

using Xunit.Abstractions;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests.Hardware;

/// <summary>
/// The Web View relay logs in like a browser (read-only on the real camera): a SOAP read (POST: the body is sent again
/// after a refused first try) and login.cgi, which hands out a session cookie with every 401 and accepts only the login
/// together with that cookie.
/// </summary>
[Trait("Category", "Hardware")]
public sealed class DeviceWebLoginHardwareTests(ITestOutputHelper output)
{
    [HardwareFact]
    public async Task SoapPostsAndTheLoginPageWorkThroughTheRelay()
    {
        var camera = DevCameras.All.First(c => c.Address == "10.0.0.202");
        await using var host = await TestServerHost.StartAsync(useRealNetwork: true);
        var (session, discovered) = await TestHelpers.ScanAsync(host, camera.Address, camera.Address);
        var found = Assert.Single(discovered);
        var reply = await host.AddDevices.CommitAsync(new Proto.CommitRequest
        {
            SessionId = session,
            DiscoveredIds = { found.DiscoveredId },
            Credentials = { new Proto.DeviceCredentials { UserName = camera.User, Password = camera.Password } },
        });
        var deviceId = Assert.Single(reply.DeviceIds);
        var client = new Proto.DeviceWebService.DeviceWebServiceClient(host.Invoker);

        const string soap = "<?xml version=\"1.0\" encoding=\"utf-8\"?><soap:Envelope xmlns:soap=\"http://www.w3.org/2003/05/soap-envelope\" xmlns:aweb=\"http://www.axis.com/vapix/ws/webserver\"><soap:Body><aweb:GetWebServerTlsConfiguration/></soap:Body></soap:Envelope>";
        Assert.Equal(200, (await DumpAsync(client, deviceId, "POST", "/vapix/services", Encoding.UTF8.GetBytes(soap), "application/soap+xml; charset=utf-8")).Status);
        var (status, cookie) = await DumpAsync(client, deviceId, "GET", "/axis-cgi/login.cgi", null, null);
        Assert.Equal(200, status);
        Assert.StartsWith("_axis=", cookie, StringComparison.Ordinal);
        Assert.Equal(200, (await DumpAsync(client, deviceId, "GET", "/axis-cgi/login.cgi", null, null, cookie)).Status);
    }

    private async Task<(int Status, string? Cookie)> DumpAsync(Proto.DeviceWebService.DeviceWebServiceClient client, string deviceId, string method, string path, byte[]? body, string? contentType, string? cookie = null)
    {
        string? setCookie = null;
        var status = 0;
        using var call = client.Exchange();
        var head = new Proto.DeviceWebRequestHead { DeviceId = deviceId, Method = method, PathAndQuery = path, HasBody = body is not null };
        if (body is not null)
        {
            head.Headers.Add(new Proto.WebHeader { Name = "Content-Type", Value = contentType });
            head.Headers.Add(new Proto.WebHeader { Name = "Content-Length", Value = body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        }

        if (cookie is not null)
        {
            head.Headers.Add(new Proto.WebHeader { Name = "Cookie", Value = cookie });
        }

        await call.RequestStream.WriteAsync(new Proto.DeviceWebFrame { Request = head });
        if (body is not null)
        {
            await call.RequestStream.WriteAsync(new Proto.DeviceWebFrame { Body = ByteString.CopyFrom(body) });
            await call.RequestStream.WriteAsync(new Proto.DeviceWebFrame { End = true });
        }

        await call.RequestStream.CompleteAsync();
        var text = new StringBuilder();
        while (await call.ResponseStream.MoveNext(CancellationToken.None))
        {
            var frame = call.ResponseStream.Current;
            if (frame.KindCase == Proto.DeviceWebFrame.KindOneofCase.Response)
            {
                status = frame.Response.Status;
                output.WriteLine($"{method} {path} -> {status}");
                setCookie = frame.Response.Headers.LastOrDefault(h => h.Name == "Set-Cookie")?.Value.Split(';')[0] ?? setCookie;
            }
            else if (frame.KindCase == Proto.DeviceWebFrame.KindOneofCase.Body)
            {
                text.Append(frame.Body.ToStringUtf8());
            }
        }

        output.WriteLine(text.Length > 200 ? text.ToString(0, 200) : text.ToString());
        return (status, setCookie);
    }
}
